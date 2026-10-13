using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Progress.Locales;
using Progress.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Views.Controls;

namespace Progress.Views.Settings;

public partial class ProgressSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private ProgressModel model;
    private bool isInitializing = true;

    public ProgressSettings() : this(null!) { }

    public ProgressSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new ProgressModel()) : new ProgressModel();

        InitializeComponent();

        InitCombos();
        LoadFromModel();
        isInitializing = false;
    }

    private void InitCombos()
    {
        ModeCombo.ItemsSource = new List<string>
        {
            Locale.Progress_Mode_Year,
            Locale.Progress_Mode_Month,
            Locale.Progress_Mode_Week,
            Locale.Progress_Mode_Day,
            Locale.Progress_Mode_Life
        };

        DayGranularityCombo.ItemsSource = new List<string>
        {
            Locale.Progress_DayGranularity_5Min,
            Locale.Progress_DayGranularity_10Min,
            Locale.Progress_DayGranularity_15Min,
            Locale.Progress_DayGranularity_30Min,
            Locale.Progress_DayGranularity_Hour
        };

        DotShapeCombo.ItemsSource = new List<string>
        {
            Locale.Progress_DotShape_Circle,
            Locale.Progress_DotShape_Squircle
        };
    }

    private void LoadFromModel()
    {
        ModeCombo.SelectedIndex = (int)model.Mode;
        DayGranularityCombo.SelectedIndex = (int)model.DayGranularity;
        DotShapeCombo.SelectedIndex = (int)model.DotShape;

        FollowAccentSwitch.IsChecked = model.FollowAccentColor;
        ShowHeaderSwitch.IsChecked = model.ShowHeader;
        ShowPercentageSwitch.IsChecked = model.ShowPercentage;
        ShowCountSwitch.IsChecked = model.ShowCount;

        CustomTitleBox.Text = model.CustomTitle ?? "";

        // Fallback on unreadable stored data only for display: the model keeps its value until
        // the user actually edits the row (a load never commits).
        PassedColorInput.Value = NormalizeColor(model.PassedColor, "#FF7B61FF");
        CurrentColorInput.Value = NormalizeColor(model.CurrentColor, "#FFFF2A85");
        RemainingColorInput.Value = NormalizeColor(model.RemainingColor, "#32FFFFFF");

        BirthDateBox.Text = model.BirthDate ?? "2000-01-01";
        LifeExpectancyInput.Value = model.LifeExpectancyYears;

        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        DayGranularitySetting.IsVisible = ModeCombo.SelectedIndex == (int)ProgressMode.Day;
        BirthDateSetting.IsVisible = ModeCombo.SelectedIndex == (int)ProgressMode.Life;
        LifeExpectancySetting.IsVisible = ModeCombo.SelectedIndex == (int)ProgressMode.Life;
        PassedColorSetting.IsVisible = FollowAccentSwitch.IsChecked != true;
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            Mode = (ProgressMode)Math.Max(0, ModeCombo.SelectedIndex),
            DayGranularity = (DayGranularity)Math.Max(0, DayGranularityCombo.SelectedIndex),
            DotShape = (DotShape)Math.Max(0, DotShapeCombo.SelectedIndex)
        };

        UpdateVisibility();
        Save();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            FollowAccentColor = FollowAccentSwitch.IsChecked ?? true,
            ShowHeader = ShowHeaderSwitch.IsChecked ?? true,
            ShowPercentage = ShowPercentageSwitch.IsChecked ?? true,
            ShowCount = ShowCountSwitch.IsChecked ?? true
        };

        UpdateVisibility();
        Save();
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        var title = CustomTitleBox.Text?.Trim();
        model = model with { CustomTitle = string.IsNullOrWhiteSpace(title) ? null : title };
        Save();
    }

    private void OnBirthDateLostFocus(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        var b = BirthDateBox.Text?.Trim();
        model = model with { BirthDate = string.IsNullOrWhiteSpace(b) ? null : b };
        Save();
    }

    private void OnLifeExpectancyChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (isInitializing) return;
        model = model with { LifeExpectancyYears = (int)(LifeExpectancyInput.Value ?? 80) };
        Save();
    }

    /// <summary>One handler for all three colour rows: the control already normalised and committed.</summary>
    private void OnColorCommitted(object? sender, ColorInputValueCommittedEventArgs e)
    {
        if (isInitializing) return;

        if (sender == PassedColorInput) model = model with { PassedColor = e.Value };
        else if (sender == CurrentColorInput) model = model with { CurrentColor = e.Value };
        else if (sender == RemainingColorInput) model = model with { RemainingColor = e.Value };
        else return;

        Save();
    }

    /// <summary>Display-only normalisation: an unreadable stored value shows the documented fallback.</summary>
    private static string NormalizeColor(string? value, string fallback)
        => !string.IsNullOrWhiteSpace(value) && Color.TryParse(value, out _) ? value : fallback;

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout == null) return;
            widgetLayoutProvider.Save(layout with
            {
                Settings = JsonSerializer.SerializeToElement(model)
            });
        }
        catch
        {
            // Ignored
        }
    }

    private static ProgressModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<ProgressModel>();
        }
        catch
        {
            return null;
        }
    }
}
