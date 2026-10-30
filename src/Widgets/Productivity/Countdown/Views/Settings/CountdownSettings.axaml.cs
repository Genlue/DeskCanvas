using System;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Countdown.Locales;
using Countdown.Models;
using Countdown.Services;
using DeskCanvas.Core.Interfaces;

namespace Countdown.Views.Settings;

public partial class CountdownSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private CountdownModel currentModel;
    private bool isInitializing = true;

    public CountdownSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;
    }

    private CountdownModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<CountdownModel>();
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return new CountdownModel();
    }

    private void InitializeControls()
    {
        StyleComboBox.SelectedIndex = (int)currentModel.Style;
        GranularityComboBox.SelectedIndex = (int)currentModel.Granularity;

        PopulateEventComboBox();
    }

    private void PopulateEventComboBox()
    {
        EventComboBox.Items.Clear();

        // 1. Auto option
        var autoItem = new ComboBoxItem
        {
            Content = Locale.Countdown_TargetEvent_Auto,
            Tag = null
        };
        EventComboBox.Items.Add(autoItem);

        var events = CountdownStore.Get();
        int selectedIndex = 0;

        for (int i = 0; i < events.Count; i++)
        {
            var evt = events[i];
            var item = new ComboBoxItem
            {
                Content = evt.Title,
                Tag = evt.Id
            };
            EventComboBox.Items.Add(item);

            if (evt.Id == currentModel.TargetEventId)
            {
                selectedIndex = i + 1;
            }
        }

        EventComboBox.SelectedIndex = selectedIndex;
    }

    private void OnStyleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        currentModel = currentModel with { Style = (CountdownCardStyle)StyleComboBox.SelectedIndex };
        SaveSettings();
    }

    private void OnGranularitySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        currentModel = currentModel with { Granularity = (CountdownGranularity)GranularityComboBox.SelectedIndex };
        SaveSettings();
    }

    private void OnEventSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        if (EventComboBox.SelectedItem is ComboBoxItem item)
        {
            currentModel = currentModel with { TargetEventId = item.Tag as string };
            SaveSettings();
        }
    }

    private void SaveSettings()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}
