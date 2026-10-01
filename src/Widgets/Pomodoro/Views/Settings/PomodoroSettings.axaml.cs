using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Pomodoro.Models;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Pomodoro.Views.Settings;

public partial class PomodoroSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private PomodoroModel model;
    private bool isInitializing = true;

    public PomodoroSettings() : this(null!) { }

    public PomodoroSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new PomodoroModel()) : new PomodoroModel();

        InitializeComponent();
        LoadFromModel();
        isInitializing = false;
    }

    private void LoadFromModel()
    {
        FocusMinutesInput.Value = model.FocusMinutes;
        ShortBreakMinutesInput.Value = model.ShortBreakMinutes;
        LongBreakMinutesInput.Value = model.LongBreakMinutes;
        LongBreakIntervalInput.Value = model.LongBreakInterval;
        AutoStartBreaksSwitch.IsChecked = model.AutoStartBreaks;
        AutoStartFocusSwitch.IsChecked = model.AutoStartFocus;
    }

    private void OnNumericSettingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            FocusMinutes = (int)(FocusMinutesInput.Value ?? 25),
            ShortBreakMinutes = (int)(ShortBreakMinutesInput.Value ?? 5),
            LongBreakMinutes = (int)(LongBreakMinutesInput.Value ?? 15),
            LongBreakInterval = (int)(LongBreakIntervalInput.Value ?? 4)
        };

        Save();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;

        model = model with
        {
            AutoStartBreaks = AutoStartBreaksSwitch.IsChecked ?? false,
            AutoStartFocus = AutoStartFocusSwitch.IsChecked ?? false
        };

        Save();
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
        catch { }
    }

    private static PomodoroModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<PomodoroModel>();
        }
        catch
        {
            return null;
        }
    }
}
