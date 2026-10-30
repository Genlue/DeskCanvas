using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using TickClock.Models;

namespace TickClock.Views.Settings;

public partial class TickClockSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private TickClockModel currentModel;
    private bool isInitializing = true;

    public TickClockSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;
    }

    private TickClockModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<TickClockModel>();
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return new TickClockModel();
    }

    private void InitializeControls()
    {
        ShowWeekdayToggle.IsChecked = currentModel.ShowWeekday;
        ShowDateToggle.IsChecked = currentModel.ShowDate;
        Use24HourToggle.IsChecked = currentModel.Use24Hour;

    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void SaveModel()
    {
        currentModel = new TickClockModel(
            ShowWeekdayToggle.IsChecked ?? true,
            ShowDateToggle.IsChecked ?? true,
            Use24HourToggle.IsChecked ?? true);

        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}
