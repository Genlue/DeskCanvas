using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Views.Controls;

#pragma warning disable CA1416

namespace Folders.Views.Settings;

public partial class FolderSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private FolderModel model;
    private Point? dragStart;
    private string? dragPath;
    private PointerPressedEventArgs? dragTrigger;
    // Avalonia 12 drag-drop: custom payloads use a typed application format instead of DataObject strings.
    private static readonly DataFormat<string> ItemPathFormat =
        DataFormat.CreateStringApplicationFormat("FoldersItemPath");
    private bool syncing;

    public FolderSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        model = ReadModel(widgetLayoutProvider.Get()) ?? new FolderModel([]);

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(DragDrop.DropEvent, OnRowDrop);
        widgetLayoutProvider.DataChanged += OnDataChanged;

        SetupColumns();
        SetupLayoutMode();
        SetupRowLayout();
        SetupWatchSort();
        SetupNumericInputs();
        ShowNamesToggle.IsChecked = model.ShowNames;
        ShowScrollbarToggle.IsChecked = model.ShowScrollbar;
        ShowTitleToggle.IsChecked = model.ShowTitle;
        BoldTitleToggle.IsChecked = model.BoldTitle;
        BoldNamesToggle.IsChecked = model.BoldNames;
        CamelCaseWrapToggle.IsChecked = model.CamelCaseWrap;
        HideSubfoldersToggle.IsChecked = model.HideSubfolders;
        TitleBox.Text = model.Title == "文件夹" && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Folder"
            : model.Title;
        TitleOffsetSlider.Value = model.TitleOffsetX;
        LayoutModeBox.SelectedItem = model.LayoutMode;
        UpdateWatchFolderState();
        RefreshList();
    }

    private void UpdateWatchFolderState()
    {
        var watching = !string.IsNullOrWhiteSpace(model.WatchFolder);
        WatchFolderText.Text = model.WatchFolder ?? "—";
        WatchFolderHint.IsVisible = watching;
        WatchSortSetting.IsVisible = watching;
        // The five watch toggles each live inside their own Setting row: the row shell must
        // collapse with the checkbox, otherwise "not watching" would leave five title-only rows.
        DirsFirstSetting.IsVisible = watching;
        ShowHiddenSetting.IsVisible = watching;
        HideExtSetting.IsVisible = watching;
        SortDescSetting.IsVisible = watching;
        HideSubfoldersSetting.IsVisible = watching;
        DirsFirstToggle.IsChecked = model.DirectoriesFirst;
        ShowHiddenToggle.IsChecked = model.ShowHiddenFiles;
        HideExtToggle.IsChecked = model.HideExtensions;
        SortDescToggle.IsChecked = model.SortDescending;
        HideSubfoldersToggle.IsChecked = model.HideSubfolders;
        WatchSortBox.SelectedIndex = Math.Max(0, Array.IndexOf(SortKeys, model.WatchSortBy));
        ItemsEditor.IsVisible = !watching;
        ItemsGroupHeader.IsVisible = !watching;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is TopLevel topLevel)
            DragDrop.SetAllowDrop(topLevel, true);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        widgetLayoutProvider.DataChanged -= OnDataChanged;
    }

    private void OnDataChanged(object? sender, WidgetLayout? oldData, WidgetLayout newData)
    {
        model = ReadModel(newData) ?? new FolderModel([]);
        syncing = true;
        ShowNamesToggle.IsChecked = model.ShowNames;
        ShowScrollbarToggle.IsChecked = model.ShowScrollbar;
        ShowTitleToggle.IsChecked = model.ShowTitle;
        BoldTitleToggle.IsChecked = model.BoldTitle;
        BoldNamesToggle.IsChecked = model.BoldNames;
        CamelCaseWrapToggle.IsChecked = model.CamelCaseWrap;
        HideSubfoldersToggle.IsChecked = model.HideSubfolders;
        TitleBox.Text = model.Title == "文件夹" && !System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "Folder"
            : model.Title;
        TitleOffsetSlider.Value = model.TitleOffsetX;
        ColumnsBox.SelectedItem = model.Columns;
        RowLayoutBox.SelectedIndex = RowLayoutIndex(model.RowLayout);
        RowSpacingInput.Value = model.RowSpacing;
        IconSizeInput.Value = (decimal)model.IconSize;
        FontSizeInput.Value = (decimal)model.FontSize;
        MaxNameLinesInput.Value = model.MaxNameLines;
        MaxNameCharsInput.Value = model.MaxNameChars;
        PaddingInput.Value = model.Padding;
        LayoutModeBox.SelectedItem = model.LayoutMode;
        UpdateColumnState();
        UpdateWatchFolderState();
        syncing = false;
        RefreshList();
    }

    private void SetupColumns()
    {
        ColumnsBox.ItemsSource = Enumerable.Range(1, 12).ToList();
        ColumnsBox.SelectedItem = model.Columns;
    }

    private void SetupNumericInputs()
    {
        syncing = true;
        RowSpacingInput.Value = model.RowSpacing;
        IconSizeInput.Value = (decimal)model.IconSize;
        FontSizeInput.Value = (decimal)model.FontSize;
        MaxNameLinesInput.Value = model.MaxNameLines;
        MaxNameCharsInput.Value = model.MaxNameChars;
        PaddingInput.Value = model.Padding;
        syncing = false;
    }

    private static readonly string[] RowLayoutKeys = { "Auto", "Fixed" };

    private void SetupLayoutMode()
    {
        LayoutModeBox.ItemsSource = new List<string> { "Grid", "List" };
        LayoutModeBox.SelectedItem = model.LayoutMode;
        UpdateColumnState();
    }

    private static int RowLayoutIndex(string? key)
    {
        var index = Array.IndexOf(RowLayoutKeys, key);
        return index < 0 ? 0 : index;
    }

    private void SetupRowLayout()
    {
        RowLayoutBox.ItemsSource = new List<string>
        {
            Locale.Folders_RowLayout_Auto,
            Locale.Folders_RowLayout_Fixed,
        };
        RowLayoutBox.SelectedIndex = RowLayoutIndex(model.RowLayout);
    }

    private void OnRowLayoutChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        var index = RowLayoutBox.SelectedIndex;
        if (index >= 0 && index < RowLayoutKeys.Length && RowLayoutKeys[index] != model.RowLayout)
            Save(model with { RowLayout = RowLayoutKeys[index] });
    }

    private static readonly string[] SortKeys = { "Name", "Created", "Modified" };

    private void SetupWatchSort()
    {
        WatchSortBox.ItemsSource = new[]
        {
            Locale.Folders_SortName,
            Locale.Folders_SortCreated,
            Locale.Folders_SortModified
        };
        WatchSortBox.SelectedIndex = Math.Max(0, Array.IndexOf(SortKeys, model.WatchSortBy));
    }

    private void UpdateColumnState()
    {
        if (ColumnsBox != null && ColumnsSetting != null)
        {
            var enabled = model.LayoutMode != "List";
            ColumnsBox.IsEnabled = enabled;
            ColumnsSetting.IsEnabled = enabled;
        }

        // Row layout only affects the grid (list rows already have fixed height).
        if (RowLayoutSetting != null)
            RowLayoutSetting.IsVisible = model.LayoutMode != "List";
    }

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control { DataContext: FolderItem item })
        {
            dragStart = e.GetPosition(this);
            dragPath = item.Path;
            dragTrigger = e;
        }
    }

    private async void OnItemPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragPath == null || dragStart == null || dragTrigger == null) return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - dragStart.Value.X) + Math.Abs(position.Y - dragStart.Value.Y) < 8) return;

        // DoDragDropAsync starts from the press event; the 8px threshold above keeps clicks from
        // accidentally starting a drag, so the saved press args are the trigger.
        var trigger = dragTrigger;
        dragTrigger = null;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(ItemPathFormat, dragPath));
        await DragDrop.DoDragDropAsync(trigger, data, DragDropEffects.Move);
        dragStart = null;
        dragPath = null;
    }

    private void OnRowDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(ItemPathFormat) || e.Source is not Control { DataContext: FolderItem target }) return;

        var dragged = e.DataTransfer.TryGetValue(ItemPathFormat);
        if (string.IsNullOrEmpty(dragged)) return;

        var items = model.Items.ToList();
        var from = items.IndexOf(dragged);
        var to = items.IndexOf(target.Path);
        if (from < 0 || to < 0 || from == to) return;

        items.RemoveAt(from);
        items.Insert(to, dragged);
        Save(model with { Items = items });
    }

    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: FolderItem item } box) return;

        var names = new Dictionary<string, string>(model.CustomNames ?? new Dictionary<string, string>());
        var text = box.Text?.Trim() ?? "";
        var defaultName = DisplayName(item.Path);

        if (string.IsNullOrEmpty(text) || text == defaultName)
        {
            if (names.Remove(item.Path))
                Save(model with { CustomNames = names.Count == 0 ? null : names });
            return;
        }

        names[item.Path] = text;
        Save(model with { CustomNames = names });
    }

    private void OnNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void OnRemove(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FolderItem item })
        {
            var items = model.Items.Where(path => path != item.Path).ToList();
            var names = model.CustomNames is { } n ? new Dictionary<string, string>(n) : null;
            names?.Remove(item.Path);
            Save(model with { Items = items, CustomNames = names });
        }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var path = PathBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(path) || !(File.Exists(path) || Directory.Exists(path))) return;

        var items = model.Items.Append(path).Distinct().ToList();
        Save(model with { Items = items });
        PathBox.Text = "";
    }

    private async void OnPickFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Const.AppName,
            AllowMultiple = true
        });

        if (folder.Count == 0) return;

        var items = model.Items.Concat(folder.Select(f => f.Path.LocalPath)).Distinct().ToList();
        Save(model with { Items = items });
    }

    private async void OnPickFile(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        var hwnd = topLevel?.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        var files = ShellFilePicker.PickFilesNoDereference(hwnd, Const.AppName, allowMultiple: true);

        if (files.Count == 0 && topLevel?.StorageProvider != null)
        {
            var storageFiles = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Const.AppName,
                AllowMultiple = true
            });
            files = storageFiles.Select(f => f.Path.LocalPath).ToList();
        }

        if (files.Count == 0) return;

        var items = model.Items.Concat(files).Distinct().ToList();
        Save(model with { Items = items });
    }

    private async void OnPickWatchFolder(object? sender, RoutedEventArgs e)
    {
        var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storageProvider == null) return;

        var folder = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Const.AppName,
            AllowMultiple = false
        });

        if (folder.Count == 0) return;
        var path = folder[0].Path.LocalPath;
        if (!string.IsNullOrWhiteSpace(path) && path.Length > 3)
            path = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        Save(model with { WatchFolder = path });
        UpdateWatchFolderState();
    }

    private void OnClearWatchFolder(object? sender, RoutedEventArgs e)
    {
        Save(model with { WatchFolder = null });
        UpdateWatchFolderState();
    }

    private void OnToggleNames(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { ShowNames = ShowNamesToggle.IsChecked == true });
    }

    private void OnToggleScrollbar(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { ShowScrollbar = ShowScrollbarToggle.IsChecked == true });
    }

    private void OnToggleTitle(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { ShowTitle = ShowTitleToggle.IsChecked == true });
    }

    private void OnToggleBoldTitle(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { BoldTitle = BoldTitleToggle.IsChecked == true });
    }

    private void OnToggleBoldNames(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { BoldNames = BoldNamesToggle.IsChecked == true });
    }

    private void OnToggleCamelCaseWrap(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { CamelCaseWrap = CamelCaseWrapToggle.IsChecked == true });
    }

    private void OnMaxNameLinesChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var lines = Math.Max(1, (int)Math.Round(val));
        if (lines != model.MaxNameLines)
            Save(model with { MaxNameLines = lines });
    }

    private void OnMaxNameCharsChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var chars = Math.Max(0, (int)Math.Round(val));
        if (chars != model.MaxNameChars)
            Save(model with { MaxNameChars = chars });
    }

    private void OnPaddingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var padding = Math.Max(0, (int)Math.Round(val));
        if (padding != model.Padding)
            Save(model with { Padding = padding });
    }

    private void OnWatchSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        var index = WatchSortBox.SelectedIndex;
        if (index >= 0 && index < SortKeys.Length && SortKeys[index] != model.WatchSortBy)
            Save(model with { WatchSortBy = SortKeys[index] });
    }

    private void OnToggleDirsFirst(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { DirectoriesFirst = DirsFirstToggle.IsChecked == true });
    }

    private void OnToggleShowHidden(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { ShowHiddenFiles = ShowHiddenToggle.IsChecked == true });
    }

    private void OnToggleHideExt(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { HideExtensions = HideExtToggle.IsChecked == true });
    }

    private void OnToggleSortDesc(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { SortDescending = SortDescToggle.IsChecked == true });
    }

    private void OnToggleHideSubfolders(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        Save(model with { HideSubfolders = HideSubfoldersToggle.IsChecked == true });
    }

    /// <summary>
    /// The shared LabeledSlider commits once per completed drag/key edit, so the model sees a
    /// single rounded write instead of one per pixel; the value column is rendered by the
    /// control itself (the page no longer keeps a mirror TextBlock).
    /// </summary>
    private void OnTitleOffsetCommitted(object? sender, LabeledSliderValueCommittedEventArgs e)
    {
        if (syncing) return;
        var value = Math.Round(e.Value);
        if (Math.Abs(value - model.TitleOffsetX) > 0.001)
            Save(model with { TitleOffsetX = value });
    }

    private void OnLayoutModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        if (LayoutModeBox.SelectedItem is string mode && mode != model.LayoutMode)
        {
            Save(model with { LayoutMode = mode });
            UpdateColumnState();
        }
    }

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        if (syncing) return;
        var text = TitleBox.Text?.Trim() ?? "";
        if (text != model.Title)
            Save(model with { Title = text });
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
    }

    private void OnColumnsChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (syncing) return;
        if (ColumnsBox.SelectedItem is int columns && columns != model.Columns)
            Save(model with { Columns = columns });
    }

    private void OnRowSpacingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var spacing = Math.Max(0, (int)Math.Round(val));
        if (spacing != model.RowSpacing)
            Save(model with { RowSpacing = spacing });
    }

    private void OnIconSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var size = (double)val;
        if (size > 0 && Math.Abs(size - model.IconSize) > 0.001)
            Save(model with { IconSize = size });
    }

    private void OnFontSizeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (syncing || e.NewValue is not { } val) return;
        var size = (double)val;
        if (size > 0 && Math.Abs(size - model.FontSize) > 0.001)
            Save(model with { FontSize = size });
    }

    private void RefreshList()
    {
        var items = model.Items
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct()
            .Select(path => new FolderItem(path, model.GetDisplayName(path), FolderIconService.GetIcon(path), true))
            .ToList();

        ItemList.ItemsSource = items;
        EmptyHint.IsVisible = items.Count == 0;
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// Deserialize the model directly from the layout's Settings JSON element.
    /// Does not rely on host-provided model helpers, so it works across host versions.
    /// </summary>
    private static FolderModel? ReadModel(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<FolderModel>();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Save(FolderModel newModel)
    {
        model = newModel;
        RefreshList();
        widgetLayoutProvider.Save(widgetLayoutProvider.Get() with
        {
            Settings = JsonSerializer.SerializeToElement(newModel)
        });
    }
}
