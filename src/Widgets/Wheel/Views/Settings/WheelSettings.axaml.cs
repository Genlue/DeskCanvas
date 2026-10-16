using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using Wheel.Models;
using Wheel.Services;

namespace Wheel.Views.Settings;

public partial class WheelSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private WheelModel currentModel;
    private bool isInitializing = true;

    public WheelSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;
    }

    private WheelModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<WheelModel>();
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return new WheelModel();
    }

    private void InitializeControls()
    {
        var data = WheelStore.GetData();
        TemplateComboBox.Items.Clear();

        int selected = 0;
        for (int i = 0; i < data.Templates.Count; i++)
        {
            var t = data.Templates[i];
            TemplateComboBox.Items.Add(new ComboBoxItem { Content = t.Title, Tag = t.Id });
            if (t.Id == currentModel.CurrentTemplateId) selected = i;
        }

        TemplateComboBox.SelectedIndex = selected;
        DurationNumeric.Value = (decimal)currentModel.SpinDurationSeconds;
        EliminateToggle.IsChecked = currentModel.EliminateOnSelected;
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void OnDurationChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        SaveModel();
    }

    private void SaveModel()
    {
        string? tmplId = (TemplateComboBox.SelectedItem as ComboBoxItem)?.Tag as string;
        double duration = (double)(DurationNumeric.Value ?? (decimal)2.5);
        bool eliminate = EliminateToggle.IsChecked ?? false;

        currentModel = new WheelModel(tmplId, duration, eliminate);
        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}
