using System;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Tools.Models;
using Tools.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Tools.Views.Settings;

public partial class ClipboardSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private ClipboardModel model;
    private bool isInitializing = true;

    public ClipboardSettings() : this(null!) { }

    public ClipboardSettings(IWidgetLayoutProvider? widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider!;
        model = widgetLayoutProvider != null ? (ReadModel(widgetLayoutProvider.Get()) ?? new ClipboardModel()) : new ClipboardModel();

        InitializeComponent();

        MaxCountSlider.Value = model.MaxHistoryCount;
        MaxCountValueText.Text = $"{model.MaxHistoryCount}";
        AutoCopyToggle.IsChecked = model.AutoCopyOnClick;
        CaptureTextToggle.IsChecked = model.CaptureText;
        CaptureImagesToggle.IsChecked = model.CaptureImages;
        CaptureFilesToggle.IsChecked = model.CaptureFiles;

        isInitializing = false;
    }

    private void OnMaxCountChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (isInitializing) return;
        int val = (int)Math.Round(e.NewValue);
        MaxCountValueText.Text = $"{val}";
        model = model with { MaxHistoryCount = val };
        Save();
        ClipboardMonitorService.Instance.UpdateSettings(model);
    }

    private void OnToggleChanged(object? sender, RoutedEventArgs e)
    {
        if (isInitializing) return;
        model = model with
        {
            AutoCopyOnClick = AutoCopyToggle.IsChecked ?? true,
            CaptureText = CaptureTextToggle.IsChecked ?? true,
            CaptureImages = CaptureImagesToggle.IsChecked ?? true,
            CaptureFiles = CaptureFilesToggle.IsChecked ?? true
        };
        Save();
        ClipboardMonitorService.Instance.UpdateSettings(model);
    }

    private void OnClearAllClicked(object? sender, RoutedEventArgs e)
    {
        ClipboardMonitorService.Instance.ClearAll();
    }

    private static ClipboardModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<ClipboardModel>();
        }
        catch
        {
            return null;
        }
    }

    private void Save()
    {
        if (widgetLayoutProvider == null) return;
        widgetLayoutProvider.Save(widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(model)
        });
    }
}
