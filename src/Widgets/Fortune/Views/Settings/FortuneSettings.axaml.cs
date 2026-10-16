using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using Fortune.Models;

namespace Fortune.Views.Settings;

public partial class FortuneSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private FortuneModel currentModel;
    private bool isInitializing = true;

    public FortuneSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        currentModel = LoadModel();

        InitializeComponent();
        InitializeControls();
        isInitializing = false;
    }

    private FortuneModel LoadModel()
    {
        try
        {
            var layout = widgetLayoutProvider.Get();
            if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
            {
                var parsed = settings.Deserialize<FortuneModel>();
                if (parsed != null) return parsed;
            }
        }
        catch { }

        return new FortuneModel();
    }

    private void InitializeControls()
    {
        PoolComboBox.SelectedIndex = (int)currentModel.PoolType;
        FreeDrawToggle.IsChecked = currentModel.FreeDrawMode;
    }

    private void OnSettingChanged(object? sender, SelectionChangedEventArgs e)
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
        FortunePoolType pool = (FortunePoolType)PoolComboBox.SelectedIndex;
        bool freeDraw = FreeDrawToggle.IsChecked ?? false;

        currentModel = new FortuneModel(pool, true, freeDraw);
        try
        {
            var layout = widgetLayoutProvider.Get();
            layout = layout with { Settings = JsonSerializer.SerializeToElement(currentModel) };
            widgetLayoutProvider.Save(layout);
        }
        catch { }
    }
}
