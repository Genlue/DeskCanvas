using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

#pragma warning disable CA1416

namespace Folders.Views.Settings;

public partial class BigFolderSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private BigFolderModel model;
    private Point? dragStart;
    private string? dragPath;
    private bool syncing;

    public BigFolderSettings() : this(null!) { }

    public BigFolderSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = widgetLayoutProvider != null ? ReadModel(widgetLayoutProvider.Get()) ?? new BigFolderModel() : new BigFolderModel();

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(DragDrop.DropEvent, OnRowDrop);
        if (widgetLayoutProvider != null)
            widgetLayoutProvider.DataChanged += OnDataChanged;

        SetupDensityBox();
        SetupNumericInputs();
        RefreshList();
    }

    private void SetupDensityBox()
    {
        DensityBox.ItemsSource = new[]
        {
            Locale.Folders_BigFolder_Sparse,
            Locale.Folders_BigFolder_Dense,
            Locale.Folders_BigFolder_Custom
        };
        int index = model.EffectiveDensity switch
        {
            BigFolderDensity.Dense => 1,
            BigFolderDensity.Custom => 2,
            _ => 0
        };
        DensityBox.SelectedIndex = index;
        CustomDimensionsPanel.IsVisible = index == 2;
    }

    private void SetupNumericInputs()
    {
        SpacingInput.Value = model.Spacing;
        PaddingInput.Value = model.Padding;
        CustomColumnsInput.Value = model.CustomColumns;
        CustomRowsInput.Value = model.CustomRows;
        FolderNameBox.Text = model.FolderName ?? "";
        PopupColumnsInput.Value = model.PopupColumns;
        PopupIconSizeInput.Value = model.PopupIconSize;
        PopupSpacingInput.Value = model.PopupSpacing;
        PopupPaddingInput.Value = model.PopupPadding;
        PopupShowNamesToggle.IsChecked = model.PopupShowNames;
        PopupShowExtensionsToggle.IsChecked = model.PopupShowExtensions;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (VisualRoot is TopLevel topLevel)
            DragDrop.SetAllowDrop(topLevel, true);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (widgetLayoutProvider != null)
            widgetLayoutProvider.DataChanged -= OnDataChanged;
    }

    private void OnDataChanged(object? sender, WidgetLayout? oldData, WidgetLayout newData)
    {
        model = ReadModel(newData) ?? new BigFolderModel();
        syncing = true;
        int index = model.EffectiveDensity switch
        {
            BigFolderDensity.Dense => 1,
            BigFolderDensity.Custom => 2,
            _ => 0
        };
        DensityBox.SelectedIndex = index;
        CustomDimensionsPanel.IsVisible = index == 2;
        SpacingInput.Value = model.Spacing;
        PaddingInput.Value = model.Padding;
        CustomColumnsInput.Value = model.CustomColumns;
        CustomRowsInput.Value = model.CustomRows;
        FolderNameBox.Text = model.FolderName ?? "";
        PopupColumnsInput.Value = model.PopupColumns;
        PopupIconSizeInput.Value = model.PopupIconSize;
        PopupSpacingInput.Value = model.PopupSpacing;
        PopupPaddingInput.Value = model.PopupPadding;
        PopupShowNamesToggle.IsChecked = model.PopupShowNames;
        PopupShowExtensionsToggle.IsChecked = model.PopupShowExtensions;
        RefreshList();
        syncing = false;
    }

    private void OnDensityChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        var density = DensityBox.SelectedIndex switch
        {
            1 => BigFolderDensity.Dense,
            2 => BigFolderDensity.Custom,
            _ => BigFolderDensity.Sparse
        };
        CustomDimensionsPanel.IsVisible = density == BigFolderDensity.Custom;
        if (density != model.EffectiveDensity)
            Save(model with { Density = density, DenseMode = (density == BigFolderDensity.Dense) });
    }

    private void OnCustomColumnsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var cols = Math.Clamp((int)Math.Round(val), 1, 12);
        if (cols != model.CustomColumns)
            Save(model with { CustomColumns = cols });
    }

    private void OnCustomRowsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var rows = Math.Clamp((int)Math.Round(val), 1, 12);
        if (rows != model.CustomRows)
            Save(model with { CustomRows = rows });
    }

    private void OnSpacingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var spacing = Math.Max(0, (int)Math.Round(val));
        if (spacing != model.Spacing)
            Save(model with { Spacing = spacing });
    }

    private void OnPaddingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var pad = Math.Max(0, (int)Math.Round(val));
        if (pad != model.Padding)
            Save(model with { Padding = pad });
    }

    private void OnPopupFolderNameChanged(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        var name = string.IsNullOrWhiteSpace(FolderNameBox.Text) ? null : FolderNameBox.Text.Trim();
        if (name != model.FolderName)
            Save(model with { FolderName = name });
    }

    private void OnPopupFolderNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            OnPopupFolderNameChanged(sender, e);
    }

    private void OnPopupColumnsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var cols = Math.Clamp((int)Math.Round(val), 2, 8);
        if (cols != model.PopupColumns)
            Save(model with { PopupColumns = cols });
    }

    private void OnPopupIconSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var size = Math.Clamp((int)Math.Round(val), 24, 80);
        if (size != model.PopupIconSize)
            Save(model with { PopupIconSize = size });
    }

    private void OnPopupSpacingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var spacing = Math.Clamp((int)Math.Round(val), 0, 32);
        if (spacing != model.PopupSpacing)
            Save(model with { PopupSpacing = spacing });
    }

    private void OnPopupPaddingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var pad = Math.Clamp((int)Math.Round(val), 6, 36);
        if (pad != model.PopupPadding)
            Save(model with { PopupPadding = pad });
    }

    private void OnPopupShowNamesChanged(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        var show = PopupShowNamesToggle.IsChecked ?? true;
        if (show != model.PopupShowNames)
            Save(model with { PopupShowNames = show });
    }

    private void OnPopupShowExtensionsChanged(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        var show = PopupShowExtensionsToggle.IsChecked ?? false;
        if (show != model.PopupShowExtensions)
            Save(model with { PopupShowExtensions = show });
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            && sender is Control { DataContext: BigFolderSettingRow item })
        {
            dragStart = e.GetPosition(this);
            dragPath = item.Path;
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragPath == null || dragStart == null) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - dragStart.Value.X) + Math.Abs(position.Y - dragStart.Value.Y) < 8) return;

        var data = new DataObject();
        data.Set("BigFoldersItemPath", dragPath);
        await DragDrop.DoDragDrop(e, data, DragDropEffects.Move);
        dragStart = null;
        dragPath = null;
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (!e.Data.Contains("BigFoldersItemPath") || e.Source is not Control { DataContext: BigFolderSettingRow target }) return;

        var dragged = e.Data.Get("BigFoldersItemPath") as string;
        if (string.IsNullOrEmpty(dragged)) return;

        var items = model.SafeItems.ToList();
        var from = items.IndexOf(dragged);
        var to = items.IndexOf(target.Path);
        if (from < 0 || to < 0 || from == to) return;

        items.RemoveAt(from);
        items.Insert(to, dragged);
        Save(model with { Items = items });
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: BigFolderSettingRow item })
        {
            var items = model.SafeItems.Where(path => path != item.Path).ToList();
            Save(model with { Items = items });
        }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) return;

        var items = model.SafeItems.Append(path).Distinct().ToList();
        Save(model with { Items = items });
        PathBox.Text = "";
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "DeskCanvas",
            AllowMultiple = true
        });

        if (folder.Count == 0) return;

        var items = model.SafeItems.Concat(folder.Select(f => f.Path.LocalPath)).Distinct().ToList();
        Save(model with { Items = items });
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "DeskCanvas",
            AllowMultiple = true
        });

        if (files.Count == 0) return;

        var items = model.SafeItems.Concat(files.Select(f => f.Path.LocalPath)).Distinct().ToList();
        Save(model with { Items = items });
    }

    private void RefreshList()
    {
        var items = model.SafeItems
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct()
            .Select(path =>
            {
                var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = path;
                return new BigFolderSettingRow(path, name, FolderIconService.GetIcon(path));
            })
            .ToList();

        ItemList.ItemsSource = items;
        EmptyHint.IsVisible = items.Count == 0;
    }

    private static BigFolderModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return JsonSerializer.Deserialize<BigFolderModel>(settings.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(BigFolderModel newModel)
    {
        model = newModel;
        RefreshList();
        if (widgetLayoutProvider == null) return;
        var currentLayout = widgetLayoutProvider.Get();
        if (currentLayout != null)
        {
            widgetLayoutProvider.Save(currentLayout with
            {
                Settings = JsonSerializer.SerializeToElement(newModel)
            });
        }
    }
}

public record BigFolderSettingRow(string Path, string Name, Bitmap? Icon);
