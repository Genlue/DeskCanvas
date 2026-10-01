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

        PassedColorPicker.Color = Color.TryParse(model.PassedColor, out var passed) ? passed : Color.Parse("#7B61FF");
        PassedColorHexBox.Text = PassedColorPicker.Color.ToString();

        CurrentColorPicker.Color = Color.TryParse(model.CurrentColor, out var cur) ? cur : Color.Parse("#FF2A85");
        CurrentColorHexBox.Text = CurrentColorPicker.Color.ToString();

        RemainingColorPicker.Color = Color.TryParse(model.RemainingColor, out var rem) ? rem : Color.FromArgb(50, 255, 255, 255);
        RemainingColorHexBox.Text = RemainingColorPicker.Color.ToString();

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

    private void OnColorPickerChanged(object? sender, ColorChangedEventArgs e)
    {
        if (isInitializing) return;

        if (sender == PassedColorPicker)
        {
            PassedColorHexBox.Text = e.NewColor.ToString();
            model = model with { PassedColor = e.NewColor.ToString() };
        }
        else if (sender == CurrentColorPicker)
        {
            CurrentColorHexBox.Text = e.NewColor.ToString();
            model = model with { CurrentColor = e.NewColor.ToString() };
        }
        else if (sender == RemainingColorPicker)
        {
            RemainingColorHexBox.Text = e.NewColor.ToString();
            model = model with { RemainingColor = e.NewColor.ToString() };
        }

        Save();
    }

    private void OnColorHexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        if (sender == PassedColorHexBox && Color.TryParse(PassedColorHexBox.Text, out var c1))
        {
            PassedColorPicker.Color = c1;
            model = model with { PassedColor = c1.ToString() };
            Save();
        }
        else if (sender == CurrentColorHexBox && Color.TryParse(CurrentColorHexBox.Text, out var c2))
        {
            CurrentColorPicker.Color = c2;
            model = model with { CurrentColor = c2.ToString() };
            Save();
        }
        else if (sender == RemainingColorHexBox && Color.TryParse(RemainingColorHexBox.Text, out var c3))
        {
            RemainingColorPicker.Color = c3;
            model = model with { RemainingColor = c3.ToString() };
            Save();
        }
    }

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
