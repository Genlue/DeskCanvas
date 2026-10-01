using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

#pragma warning disable CA1416

namespace Folders.Views.Settings;

public partial class SingleFileSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private SingleFileModel model;
    private bool syncing;

    public SingleFileSettings() : this(null!) { }

    public SingleFileSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider != null ? ReadModel(widgetLayoutProvider.Get()) ?? new SingleFileModel() : new SingleFileModel();

        InitializeComponent();

        syncing = true;
        PathBox.Text = model.Path ?? "";
        IconPercentSlider.Value = model.IconPercent;
        IconPercentValue.Text = $"{model.IconPercent:0}%";
        syncing = false;
    }

    private static SingleFileModel? ReadModel(WidgetLayout layout)
    {
        if (!layout.Settings.HasValue) return null;
        try
        {
            return JsonSerializer.Deserialize<SingleFileModel>(layout.Settings.Value.GetRawText());
        }
        catch
        {
            return null;
        }
    }

    private void Save(SingleFileModel newModel)
    {
        model = newModel;
        if (widgetLayoutProvider == null) return;
        var element = JsonSerializer.SerializeToElement(newModel);
        var layout = widgetLayoutProvider.Get() with { Settings = element };
        widgetLayoutProvider.Save(layout);
    }

    private void OnIconPercentChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (syncing) return;
        var value = Math.Round(e.NewValue);
        IconPercentValue.Text = $"{value:0}%";
        Save(model with { IconPercent = value });
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var hwnd = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var path = ShellFilePicker.PickSingleFileNoDereference(hwnd, Locale.Folders_PickFile);

        if (string.IsNullOrEmpty(path) && topLevel?.StorageProvider != null)
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Locale.Folders_PickFile,
                AllowMultiple = false
            });
            if (files.Count > 0 && files[0].Path.LocalPath is { } fallbackPath)
                path = fallbackPath;
        }

        if (!string.IsNullOrEmpty(path))
        {
            PathBox.Text = path;
            Save(model with { Path = path });
        }
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Locale.Folders_PickFolder,
            AllowMultiple = false
        });

        if (folder.Count > 0 && folder[0].Path.LocalPath is { } path)
        {
            PathBox.Text = path;
            Save(model with { Path = path });
        }
    }

    private void OnClearPath(object? sender, RoutedEventArgs e)
    {
        PathBox.Text = "";
        Save(model with { Path = null });
    }
}
