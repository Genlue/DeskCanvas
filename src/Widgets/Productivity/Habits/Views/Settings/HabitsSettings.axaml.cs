using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using Habits.Locales;
using Habits.Models;
using Habits.Services;

namespace Habits.Views.Settings;

public partial class HabitsSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private HabitsModel currentModel;
    private bool isInitializing = true;

    private static readonly (int Hour, string Label)[] ResetHourOptions =
    [
        (0, "00:00 (自然日零点)"),
        (1, "01:00 (凌晨一点)"),
        (2, "02:00 (凌晨两点)"),
        (3, "03:00 (凌晨三点)"),
        (4, "04:00 (夜猫子推荐四点)"),
        (5, "05:00 (清晨五点)")
    ];

    private static readonly string[] ModeOptions =
    [
        Locale.Habits_ViewMode_List,
        Locale.Habits_ViewMode_Focus,
        Locale.Habits_ViewMode_Heatmap
    ];

    public HabitsSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        InitializeComponent();

        currentModel = LoadSettings();

        // Populate Mode Combo
        ModeCombo.ItemsSource = ModeOptions;
        ModeCombo.SelectedIndex = (int)currentModel.ViewMode;

        // Populate Habits Combo
        var data = HabitsStore.Get();
        PopulateHabitsCombo(data);

        // Populate Reset Hour Combo
        DayResetHourCombo.ItemsSource = ResetHourOptions.Select(o => o.Label).ToList();
        var hourIndex = Array.FindIndex(ResetHourOptions, o => o.Hour == data.DayResetHour);
        DayResetHourCombo.SelectedIndex = hourIndex >= 0 ? hourIndex : 0;

        // Toggles
        ShowStreakSwitch.IsChecked = currentModel.ShowStreak;
        ShowTargetProgressSwitch.IsChecked = currentModel.ShowTargetProgress;

        UpdateVisibility();
        isInitializing = false;

        HabitsStore.ModelChanged += OnStoreModelChanged;
        Unloaded += OnUnloaded;
    }

    private void OnStoreModelChanged(HabitsDataModel data, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        isInitializing = true;
        try
        {
            PopulateHabitsCombo(data);
            var hourIndex = Array.FindIndex(ResetHourOptions, o => o.Hour == data.DayResetHour);
            DayResetHourCombo.SelectedIndex = hourIndex >= 0 ? hourIndex : 0;
        }
        finally
        {
            isInitializing = false;
        }
    }

    private void PopulateHabitsCombo(HabitsDataModel data)
    {
        var habitItems = new List<string>(data.Habits.Count);
        for (int i = 0; i < data.Habits.Count; i++)
        {
            var h = data.Habits[i];
            habitItems.Add($"{h.Icon} {h.Title}");
        }
        FocusedHabitCombo.ItemsSource = habitItems;
        var focusedIndex = data.Habits.FindIndex(h => h.Id == currentModel.FocusedHabitId);
        FocusedHabitCombo.SelectedIndex = focusedIndex >= 0 ? focusedIndex : (data.Habits.Count > 0 ? 0 : -1);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Unloaded -= OnUnloaded;
        HabitsStore.ModelChanged -= OnStoreModelChanged;
    }

    private HabitsModel LoadSettings()
    {
        try
        {
            var raw = widgetLayoutProvider.Get().Settings;
            if (raw.HasValue && raw.Value.ValueKind == JsonValueKind.Object)
            {
                var s = raw.Value.Deserialize<HabitsModel>(HabitsStore.SharedOptions);
                if (s != null) return s;
            }
        }
        catch { }
        return new HabitsModel();
    }

    private void SaveSettings()
    {
        if (isInitializing) return;

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel, HabitsStore.SharedOptions) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }

    private void UpdateVisibility()
    {
        FocusedHabitSetting.IsVisible = currentModel.ViewMode == HabitsViewMode.Focus;
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        var mode = (HabitsViewMode)Math.Clamp(ModeCombo.SelectedIndex, 0, 2);
        var data = HabitsStore.Get();
        string? focusedId = null;
        if (FocusedHabitCombo.SelectedIndex >= 0 && FocusedHabitCombo.SelectedIndex < data.Habits.Count)
        {
            focusedId = data.Habits[FocusedHabitCombo.SelectedIndex].Id;
        }

        currentModel = currentModel with
        {
            ViewMode = mode,
            FocusedHabitId = focusedId
        };

        UpdateVisibility();
        SaveSettings();
    }

    private void OnDayResetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;

        int selectedIdx = DayResetHourCombo.SelectedIndex;
        if (selectedIdx >= 0 && selectedIdx < ResetHourOptions.Length)
        {
            int hour = ResetHourOptions[selectedIdx].Hour;
            var data = HabitsStore.Get();
            if (data.DayResetHour != hour)
            {
                var updated = data with { DayResetHour = hour };
                HabitsStore.Save(updated, sender: this);
            }
        }
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        currentModel = currentModel with
        {
            ShowStreak = ShowStreakSwitch.IsChecked == true,
            ShowTargetProgress = ShowTargetProgressSwitch.IsChecked == true
        };

        SaveSettings();
    }
}
