using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using DeskCanvas.Core.Interfaces;
namespace Folders.Views;

public partial class Folder : UserControl
{
    private FolderModel model;
    private readonly IWidgetLayoutProvider widgetLayoutProvider;

    public Folder(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new FolderModel([]), widgetLayoutProvider) { }

    public Folder(FolderModel model, IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;

        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Refresh();
    }

    public int Columns => model.Columns;
    public int RowSpacing => model.RowSpacing;
    public bool ShowScrollbar => model.ShowScrollbar;
    public bool ShowTitle => model.ShowTitle;
    public string Title => model.Title;
    public double IconSize => model.IconSize;
    public new double FontSize => model.FontSize;
    public double TitleOffsetX => model.TitleOffsetX;
    public bool BoldTitle => model.BoldTitle;
    public bool BoldNames => model.BoldNames;
    public bool IsListMode => model.LayoutMode == "List";
    public bool IsFixedRows => model.RowLayout == "Fixed";
    public bool IsWatchFolderMode => !string.IsNullOrWhiteSpace(model.WatchFolder) && Directory.Exists(model.WatchFolder);
    public string? WatchFolderPath => model.WatchFolder;

    /// <summary>
    /// Explicit grid height for "Fixed" vertical layout: rows × a constant pitch, so an
    /// icon's vertical position depends only on its row index (taller than the viewport
    /// → the ScrollViewer scrolls). NaN in "Auto" mode → the UniformGrid stretches its
    /// rows to divide the available height evenly (the historical behavior).
    /// </summary>
    public static readonly StyledProperty<double> GridHeightProperty =
        AvaloniaProperty.Register<Folder, double>(nameof(GridHeight), double.NaN);

    public double GridHeight
    {
        get => GetValue(GridHeightProperty);
        set => SetValue(GridHeightProperty, value);
    }

    /// <summary>
    /// Top when rows are fixed (so the grid takes its content height and the
    /// ScrollViewer can scroll), Stretch otherwise (rows divide the height evenly).
    /// </summary>
    public Avalonia.Layout.VerticalAlignment GridVerticalAlignment =>
        IsFixedRows ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Stretch;

    private FileSystemWatcher? watcher;
    private DateTime lastWatcherEvent;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Refresh();
        StartWatcher();
        if (VisualRoot is TopLevel topLevel)
        {
            DragDrop.SetAllowDrop(topLevel, true);
            if (topLevel.TryGetPlatformHandle()?.Handle is { } handle)
            {
                WidgetOleDropTarget.Register(handle, OnOleDrop, OnOleDragActive);
                WidgetOleDropTarget.RegisterWmDropFiles(handle, OnOleDrop, OnOleDragActive);
            }
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        StopWatcher();
        if (VisualRoot is TopLevel topLevel && topLevel.TryGetPlatformHandle()?.Handle is { } handle)
        {
            WidgetOleDropTarget.Unregister(handle);
            WidgetOleDropTarget.UnregisterWmDropFiles(handle);
        }
    }

    /// <summary>
    /// Watch the target folder so content changes refresh the widget live.
    /// </summary>
    private void StartWatcher()
    {
        StopWatcher();
        if (string.IsNullOrWhiteSpace(model.WatchFolder) || !Directory.Exists(model.WatchFolder)) return;

        watcher = new FileSystemWatcher(model.WatchFolder)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
        };
        watcher.Created += OnWatcherEvent;
        watcher.Deleted += OnWatcherEvent;
        watcher.Renamed += OnWatcherEvent;
        watcher.Changed += OnWatcherEvent;
        watcher.EnableRaisingEvents = true;
    }

    private void StopWatcher()
    {
        if (watcher == null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Created -= OnWatcherEvent;
        watcher.Deleted -= OnWatcherEvent;
        watcher.Renamed -= OnWatcherEvent;
        watcher.Changed -= OnWatcherEvent;
        watcher.Dispose();
        watcher = null;
    }

    private void OnWatcherEvent(object sender, FileSystemEventArgs e)
    {
        // Debounce bursts of events (e.g. copying a folder fires many changes).
        if ((DateTime.Now - lastWatcherEvent).TotalMilliseconds < 300) return;
        lastWatcherEvent = DateTime.Now;
        Dispatcher.UIThread.Post(Refresh);
    }

    private void OnOleDrop(List<string> paths)
    {
        DropHint.IsVisible = false;
        if (IsWatchFolderMode) return;
        var valid = paths.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct().ToList();
        if (valid.Count == 0) return;
        UpdateModel(model with { Items = model.Items.Concat(valid).Distinct().ToList() });
    }

    private void OnOleDragActive(bool active)
    {
        // Native OLE drag events target the entire widget window. Do not tint the
        // widget because that overlay can remain visible after a successful drop.
        DropHint.IsVisible = false;
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;
        // Ctrl + left drag moves the widget window (host handles it); never open the file.
        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || (GetKeyState(0x11) & 0x8000) != 0;
        if (point.Properties.IsLeftButtonPressed && isCtrl) return;
        if (sender is Control { DataContext: FolderItem item })
            Open(item);
    }

    /// <summary>
    /// In watched-folder mode the title acts as a link that opens the folder in Explorer.
    /// </summary>
    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (string.IsNullOrWhiteSpace(model.WatchFolder)) return;
        Open(new FolderItem(model.WatchFolder, "", null, false));
    }

    private const double HoverScale = 1.12;

    private void OnItemPointerEntered(object? sender, PointerEventArgs e)
    {
        // Fixed rows use an exact per-row pitch, so scaling the whole row would
        // push the file name below the cell and get clipped — scale only the icon.
        if (!IsListMode && IsFixedRows && sender is Border border
            && border.Child is StackPanel panel)
        {
            var icon = panel.Children.OfType<Image>().FirstOrDefault();
            if (icon != null)
            {
                AnimateScale(icon, HoverScale);
                return;
            }
        }

        AnimateScale(sender, HoverScale);
    }

    private void OnItemPointerExited(object? sender, PointerEventArgs e)
    {
        if (!IsListMode && IsFixedRows && sender is Border border
            && border.Child is StackPanel panel)
        {
            var icon = panel.Children.OfType<Image>().FirstOrDefault();
            if (icon != null)
            {
                AnimateScale(icon, 1.0);
                return;
            }
        }

        AnimateScale(sender, 1.0);
    }

    private static void AnimateScale(object? sender, double scale)
    {
        if (sender is not Control control) return;
        // Scale from the center of the element, not the top-left corner.
        control.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        if (control.RenderTransform is not ScaleTransform transform) return;
        transform.Transitions ??= new Transitions
        {
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleXProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut()
            },
            new DoubleTransition
            {
                Property = ScaleTransform.ScaleYProperty,
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut()
            }
        };
        transform.ScaleX = scale;
        transform.ScaleY = scale;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (IsWatchFolderMode) return;
        if (e.Data.Contains(DataFormats.FileNames))
        {
            e.DragEffects = DragDropEffects.Copy;
            DropHint.IsVisible = true;
            e.Handled = true;
        }
    }

    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        DropHint.IsVisible = false;
        if (IsWatchFolderMode) return;
        if (!e.Data.Contains(DataFormats.FileNames)) return;

        var paths = e.Data.GetFiles()?.Select(f => f.Path.LocalPath).Where(path => File.Exists(path) || Directory.Exists(path)).ToList() ?? [];
        if (paths.Count == 0) return;

        var newItems = model.Items.Concat(paths).Distinct().ToList();
        UpdateModel(model with { Items = newItems });
    }

    private void UpdateModel(FolderModel newModel)
    {
        model = newModel;
        Refresh();
        var newSettings = JsonSerializer.SerializeToElement(newModel);
        var newLayout = widgetLayoutProvider.Get() with { Settings = newSettings };

        widgetLayoutProvider.Save(newLayout);
    }

    private void Refresh()
    {
        RootBorder.Padding = new Thickness(model.Padding);

        var items = GetItemPaths()
            .Select(path => CreateFolderItem(path))
            .ToList();

        if (IsListMode)
        {
            GridItems.ItemsSource = null;
            ListItems.ItemsSource = items;
            GridItems.IsVisible = false;
            ListItems.IsVisible = true;
        }
        else
        {
            ListItems.ItemsSource = null;
            GridItems.ItemsSource = items;
            GridItems.IsVisible = true;
            ListItems.IsVisible = false;
        }
        EmptyHint.IsVisible = items.Count == 0;
        EmptyText.Text = items.Count == 0 && IsWatchFolderMode
            ? Locale.Folders_Empty_Folder
            : Locale.Folders_Empty;

        UpdateGridHeight(items.Count);

        // In watched-folder mode the title becomes a clickable link to the folder.
        TitleText.IsHitTestVisible = IsWatchFolderMode && ShowTitle;
        TitleText.Cursor = IsWatchFolderMode && ShowTitle
            ? new Cursor(StandardCursorType.Hand)
            : Cursor.Default;
    }

    /// <summary>
    /// Fixed mode: pin the grid to rows × a constant row pitch so an icon's vertical
    /// position depends only on its row index (never on how many rows there are).
    /// Auto mode clears the height (NaN) so the UniformGrid stretches and divides the
    /// available height evenly across however many rows the items make (previous
    /// behavior).
    /// </summary>
    private void UpdateGridHeight(int count)
    {
        if (IsListMode || !IsFixedRows || count == 0)
        {
            GridHeight = double.NaN;
            return;
        }

        var columns = Math.Max(1, model.Columns);
        var rows = (int)Math.Ceiling(count / (double)columns);
        // Constant per-row height: icon + name lines + fixed padding + row spacing.
        var pitch = model.IconSize
                    + (model.ShowNames ? model.FontSize * Math.Max(1, model.MaxNameLines) + 16 : 12)
                    + model.RowSpacing;
        GridHeight = rows * pitch;
    }

    private FolderItem CreateFolderItem(string path)
    {
        var name = IsWatchFolderMode && model.HideExtensions && File.Exists(path)
            ? Path.GetFileNameWithoutExtension(path)
            : model.GetDisplayName(path);
        var nameText = WrapName(name, model.MaxNameLines, model.MaxNameChars, model.CamelCaseWrap);
        var lineCount = Math.Max(1, nameText.Count(c => c == '\n') + 1);
        return new FolderItem(
            path, name, FolderIconService.GetIcon(path), model.ShowNames, model.IconSize,
            model.FontSize, model.BoldNames, nameText, model.MaxNameLines, lineCount);
    }

    /// <summary>
    /// Break a name into at most <paramref name="maxLines"/> lines. When
    /// <paramref name="maxChars"/> is set, lines are cut at that width
    /// (CJK characters count double). <paramref name="camelCase"/> adds breaks
    /// before capital letters so long camel-case names wrap nicely.
    /// </summary>
    public static string WrapName(string name, int maxLines, int maxChars, bool camelCase)
    {
        var text = name;
        if (camelCase)
            text = InsertCamelBreaks(text);

        var lines = new List<string>();
        if (maxChars > 0)
        {
            foreach (var segment in text.Split('\n'))
                lines.AddRange(SplitByCharWidth(segment, maxChars));
        }
        else
        {
            lines.AddRange(text.Split('\n'));
        }

        if (maxLines > 0 && lines.Count > maxLines)
        {
            var keep = lines.Take(maxLines).ToList();
            keep[maxLines - 1] += "…";
            return string.Join('\n', keep);
        }
        return string.Join('\n', lines);
    }

    private static string InsertCamelBreaks(string text)
    {
        var sb = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            var prev = i > 0 ? text[i - 1] : '\0';
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(prev) && !char.IsDigit(prev)
                && prev != '\n' && prev != ' ' && c <= 0x2E7F)
                sb.Append('\n');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static List<string> SplitByCharWidth(string text, int maxWidth)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        var width = 0;
        foreach (var c in text)
        {
            var w = CharWidth(c);
            if (width + w > maxWidth && sb.Length > 0)
            {
                result.Add(sb.ToString());
                sb.Clear();
                width = 0;
            }
            sb.Append(c);
            width += w;
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    private static int CharWidth(char c) => c > 0x2E7F ? 2 : 1;

    /// <summary>
    /// Source of displayed paths: the watched folder's live contents, or the pinned items.
    /// </summary>
    private IEnumerable<string> GetItemPaths()
    {
        if (!string.IsNullOrWhiteSpace(model.WatchFolder) && Directory.Exists(model.WatchFolder))
        {
            IEnumerable<string> entries = Directory.EnumerateFileSystemEntries(model.WatchFolder);
            if (!model.ShowHiddenFiles)
                entries = entries.Where(path => !IsHiddenOrSystem(path));
            if (model.HideSubfolders)
                entries = entries.Where(path => !Directory.Exists(path));

            var dirKey = model.DirectoriesFirst ? 0 : 1;
            var fileKey = model.DirectoriesFirst ? 1 : 0;

            return entries
                .GroupBy(path => Directory.Exists(path) ? dirKey : fileKey)
                .OrderBy(group => group.Key)
                .SelectMany(SortGroup);
        }

        return model.Items
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Distinct();
    }

    private IEnumerable<string> SortGroup(IEnumerable<string> group)
    {
        var descending = model.SortDescending;
        return model.WatchSortBy switch
        {
            "Created" => descending
                ? group.OrderByDescending(File.GetCreationTimeUtc).ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                : group.OrderBy(File.GetCreationTimeUtc).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            "Modified" => descending
                ? group.OrderByDescending(File.GetLastWriteTimeUtc).ThenByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                : group.OrderBy(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase),
            _ => descending
                ? group.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                : group.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static void Open(FolderItem? item)
    {
        if (item == null) return;
        try
        {
            Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Failed to open {item.Path}: {e.Message}");
        }
    }
}

public record FolderItem(string Path, string Name, Bitmap? Icon, bool ShowName, double IconSize = 40, double FontSize = 12, bool BoldName = false, string NameText = "", int MaxLines = 2, int NameLines = 1)
{
    public double ItemHeight => ShowName ? IconSize + FontSize * Math.Max(1, NameLines) + 22 : IconSize + 12;
}

public class SpacingConverter : IValueConverter
{
    public static readonly SpacingConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spacing = value is int i ? i : 4;
        return new Thickness(3, spacing / 2.0, 3, spacing / 2.0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class RowSpacingConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var spacing = value is int i ? i : 4;
        return new Thickness(0, spacing / 2.0, 0, spacing / 2.0);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToScrollBarVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class BoolToFontWeightConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public class TitleOffsetConverter : IMultiValueConverter
{
    /// <summary>
    /// Converts (offsetPercent, widgetWidth) into a pixel offset: 0 = left-aligned, 100 = full widget width.
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var percent = values.Count > 0 && values[0] is double d ? d : 0.0;
        var width = values.Count > 1 && values[1] is double w ? w : 0.0;
        return width * Math.Clamp(percent, 0, 100) / 100.0;
    }

    public object? ConvertBack(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}