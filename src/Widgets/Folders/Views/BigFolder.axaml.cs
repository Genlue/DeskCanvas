using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Folders.Locales;
using Folders.Models;
using Folders.Services;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;

namespace Folders.Views;

public partial class BigFolder : UserControl, IWidgetSelfRefreshing
{
    private BigFolderModel model;
    private readonly IWidgetLayoutProvider widgetLayoutProvider;

    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<BigFolder, int>(nameof(Columns), 3);

    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly StyledProperty<int> RowsProperty =
        AvaloniaProperty.Register<BigFolder, int>(nameof(Rows), 3);

    public int Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<BigFolder, double>(nameof(Spacing), 8.0);

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    public static readonly StyledProperty<double> ItemSizeProperty =
        AvaloniaProperty.Register<BigFolder, double>(nameof(ItemSize), 40.0);

    public double ItemSize
    {
        get => GetValue(ItemSizeProperty);
        set => SetValue(ItemSizeProperty, value);
    }

    public static readonly StyledProperty<double> GridWidthProperty =
        AvaloniaProperty.Register<BigFolder, double>(nameof(GridWidth), 0.0);

    public double GridWidth
    {
        get => GetValue(GridWidthProperty);
        set => SetValue(GridWidthProperty, value);
    }

    public static readonly StyledProperty<double> GridHeightProperty =
        AvaloniaProperty.Register<BigFolder, double>(nameof(GridHeight), 0.0);

    public double GridHeight
    {
        get => GetValue(GridHeightProperty);
        set => SetValue(GridHeightProperty, value);
    }

    public static readonly StyledProperty<Thickness> PaddingThicknessProperty =
        AvaloniaProperty.Register<BigFolder, Thickness>(nameof(PaddingThickness), new Thickness(10));

    public Thickness PaddingThickness
    {
        get => GetValue(PaddingThicknessProperty);
        set => SetValue(PaddingThicknessProperty, value);
    }

    public BigFolder() : this(new BigFolderModel(), null!) { }

    public BigFolder(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new BigFolderModel(), widgetLayoutProvider) { }

    public BigFolder(BigFolderModel model, IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        AddHandler(DragDrop.DropEvent, OnDrop);

        RecomputeAndPopulate();
    }

    public BigFolder(IWidgetLayoutProvider widgetLayoutProvider, BigFolderModel model)
        : this(model, widgetLayoutProvider) { }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings.HasValue)
        {
            try
            {
                var json = layout.Settings.Value.GetRawText();
                var newModel = JsonSerializer.Deserialize<BigFolderModel>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (newModel != null)
                    model = newModel;
            }
            catch { }
        }

        Dispatcher.UIThread.Post(RecomputeAndPopulate);
        SchedulePreRender(100);
    }

    private DispatcherTimer? preRenderDebounceTimer;

    private void SchedulePreRender(int delayMs = 150)
    {
        if (preRenderDebounceTimer == null)
        {
            preRenderDebounceTimer = new DispatcherTimer();
            preRenderDebounceTimer.Tick += (_, _) =>
            {
                preRenderDebounceTimer.Stop();
                TriggerLiquidGlassPreRender();
            };
        }
        preRenderDebounceTimer.Stop();
        preRenderDebounceTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
        preRenderDebounceTimer.Start();
    }

    private void OnWallpaperChanged()
    {
        SchedulePreRender(50);
    }

    /// <summary>
    /// Single stable delegate instance for the static <c>WallpaperInvalidated</c> event, so a
    /// load/unload cycle can always detach exactly what it attached.
    /// </summary>
    private Action? wallpaperInvalidatedHandler;

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RecomputeAndPopulate();

        if (VisualRoot is TopLevel topLevel)
        {
            DragDrop.SetAllowDrop(topLevel, true);
            if (topLevel.TryGetPlatformHandle()?.Handle is { } handle)
            {
                WidgetOleDropTarget.Register(handle, OnOleDrop, OnOleDragActive);
                WidgetOleDropTarget.RegisterWmDropFiles(handle, OnOleDrop, OnOleDragActive);
            }
        }

        if (VisualRoot is Window win)
        {
            win.PositionChanged += OnWindowPositionChanged;
        }

        wallpaperInvalidatedHandler ??= OnWallpaperChanged;
        LiquidGlassBridge.UnsubscribeWallpaperInvalidated(wallpaperInvalidatedHandler);
        LiquidGlassBridge.SubscribeWallpaperInvalidated(wallpaperInvalidatedHandler);
        SchedulePreRender(200);
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        SchedulePreRender(150);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        preRenderDebounceTimer?.Stop();

        // WallpaperInvalidated is a static event: without this detach the view (and its whole
        // item/icon graph) stays rooted for the lifetime of the process.
        if (wallpaperInvalidatedHandler != null)
            LiquidGlassBridge.UnsubscribeWallpaperInvalidated(wallpaperInvalidatedHandler);

        if (VisualRoot is Window win)
        {
            win.PositionChanged -= OnWindowPositionChanged;
        }

        if (VisualRoot is TopLevel topLevel && topLevel.TryGetPlatformHandle()?.Handle is { } handle)
        {
            WidgetOleDropTarget.Unregister(handle);
            WidgetOleDropTarget.UnregisterWmDropFiles(handle);
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1 ||
                Math.Abs(e.NewSize.Height - e.PreviousSize.Height) > 1)
            {
                RecomputeAndPopulate();
                SchedulePreRender(150);
            }
        }
    }

    /// <summary>
    /// Converts widget width and height (which may be pixels e.g. 200x200 or direct grid units e.g. 2x2)
    /// to the grid span (Columns, Rows).
    /// </summary>
    public static (int SpanCols, int SpanRows) ResolveSpan(double width, double height)
    {
        if (width <= 0) width = 200;
        if (height <= 0) height = 200;

        // If direct grid units (e.g. from tests or explicit layout: width <= 10)
        if (width <= 10 && height <= 10)
            return ((int)Math.Max(1, Math.Round(width)), (int)Math.Max(1, Math.Round(height)));

        // In uWidgets: unit = ~95px (Size 80 + Margin 15).
        // 1x1: ~80-100px -> 1
        // 2x2: ~170-210px -> 2
        // 4x2: ~350-420px x ~170-210px -> 4 x 2
        // 4x4: ~350-420px x ~350-420px -> 4 x 4
        int cols = Math.Max(1, (int)Math.Round((width + 15.0) / 95.0));
        int rows = Math.Max(1, (int)Math.Round((height + 15.0) / 95.0));
        return (cols, rows);
    }

    public static (int Cols, int Rows) CalculateGridDimensions(int spanCols, int spanRows, bool dense)
    {
        spanCols = Math.Max(1, spanCols);
        spanRows = Math.Max(1, spanRows);

        if (spanCols == 2 && spanRows == 2)
            return dense ? (4, 4) : (3, 3);
        if (spanCols == 4 && spanRows == 2)
            return dense ? (6, 3) : (5, 2);
        if (spanCols == 2 && spanRows == 4)
            return dense ? (3, 6) : (2, 5);
        if (spanCols == 1 && spanRows == 1)
            return dense ? (3, 3) : (2, 2);
        if (spanCols == 2 && spanRows == 1)
            return dense ? (4, 2) : (3, 1);
        if (spanCols == 1 && spanRows == 2)
            return dense ? (2, 4) : (1, 3);
        if (spanCols == 4 && spanRows == 4)
            return dense ? (6, 6) : (5, 5);

        int cols = dense ? Math.Max(2, (int)Math.Round(spanCols * 1.5) + (spanCols >= 2 ? 1 : 0))
                         : Math.Max(1, (int)Math.Round(spanCols * 1.25) + (spanCols >= 2 ? 1 : 0));
        int rows = dense ? Math.Max(2, (int)Math.Round(spanRows * 1.5) + (spanRows >= 2 ? 1 : 0))
                         : Math.Max(1, (int)Math.Round(spanRows * 1.25) + (spanRows >= 2 ? 1 : 0));
        return (cols, rows);
    }

    public void RecomputeAndPopulate()
    {
        var layout = widgetLayoutProvider?.Get();
        double w = Bounds.Width;
        double h = Bounds.Height;

        if (w <= 0 || h <= 0)
        {
            w = layout?.Width ?? 200;
            h = layout?.Height ?? 200;
        }

        // If dimensions were passed as small grid units (e.g. 2x2), convert to pixels for measurement
        if (w <= 10) w *= 95;
        if (h <= 10) h *= 95;

        double spanRefW = layout?.Width > 0 ? layout.Width : w;
        double spanRefH = layout?.Height > 0 ? layout.Height : h;
        var (spanCols, spanRows) = ResolveSpan(spanRefW, spanRefH);

        int cols, rows;
        if (model.EffectiveDensity == BigFolderDensity.Custom)
        {
            cols = Math.Clamp(model.CustomColumns, 1, 12);
            rows = Math.Clamp(model.CustomRows, 1, 12);
        }
        else
        {
            var dims = CalculateGridDimensions(spanCols, spanRows, model.EffectiveDensity == BigFolderDensity.Dense);
            cols = dims.Cols;
            rows = dims.Rows;
        }

        Columns = cols;
        Rows = rows;

        int pad = Math.Max(0, model.Padding);
        int spacing = Math.Max(0, model.Spacing);
        Spacing = spacing;
        PaddingThickness = new Thickness(pad);

        double availW = Math.Max(10, w - 2 * pad);
        double availH = Math.Max(10, h - 2 * pad);

        double gapsW = Math.Max(0, cols - 1) * spacing;
        double gapsH = Math.Max(0, rows - 1) * spacing;

        double availForIconsW = Math.Max(8, availW - gapsW);
        double availForIconsH = Math.Max(8, availH - gapsH);

        // Icon/Item size jointly determined by spacing, padding, and sparse/dense/custom
        double itemSize = Math.Floor(Math.Min(availForIconsW / cols, availForIconsH / rows));
        if (itemSize < 8) itemSize = 8;
        ItemSize = itemSize;

        GridWidth = cols * itemSize + gapsW;
        GridHeight = rows * itemSize + gapsH;

        double imageInset = itemSize >= 48 ? 3 : (itemSize >= 32 ? 2 : 1);
        double imageSize = Math.Max(8, itemSize - 2 * imageInset);
        var cornerRadius = new CornerRadius(Math.Clamp(Math.Round(itemSize * 0.22), 4, 14));

        var allPaths = GetItemPaths().ToList();
        int capacity = cols * rows;
        var items = new List<BigFolderItem>(capacity);

        if (allPaths.Count <= capacity)
        {
            foreach (var p in allPaths)
            {
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = p;
                var bmp = FolderIconService.GetIcon(p);
                items.Add(new BigFolderItem(p, name, bmp, itemSize, imageSize, cornerRadius));
            }
        }
        else
        {
            // Capacity exceeded: show capacity - 1 items and a special "More" overflow tile at bottom-right
            int regularCount = capacity - 1;
            for (int i = 0; i < regularCount; i++)
            {
                var p = allPaths[i];
                var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (string.IsNullOrEmpty(name)) name = p;
                var bmp = FolderIconService.GetIcon(p);
                items.Add(new BigFolderItem(p, name, bmp, itemSize, imageSize, cornerRadius));
            }

            var overflowPaths = allPaths.Skip(regularCount).ToList();
            var miniIcons = overflowPaths.Take(4).Select(FolderIconService.GetIcon).ToList();
            while (miniIcons.Count < 4) miniIcons.Add(null);

            // Proportional mini spacing and padding derived from model settings
            double padRatio = itemSize > 0 ? (double)pad / itemSize : 0.15;
            double spacingRatio = itemSize > 0 ? (double)spacing / itemSize : 0.12;

            // 2 * miniSize + spacingRatio * miniSize + 2 * padRatio * miniSize = imageSize
            double denom = 2.0 + spacingRatio + 2.0 * padRatio;
            double miniSize = Math.Max(8.0, Math.Floor(imageSize / (denom > 0 ? denom : 2.0)));
            double miniSpacing = Math.Max(1.0, Math.Round(miniSize * spacingRatio));
            double miniPad = Math.Max(1.0, Math.Round(miniSize * padRatio));
            var miniPaddingThickness = new Thickness(miniPad);

            var miniItemCornerRadius = new CornerRadius(Math.Clamp(Math.Round(miniSize * 0.22), 2, 8));
            var miniBadgeCornerRadius = miniItemCornerRadius;
            double miniFontSize = Math.Clamp(Math.Round(miniSize * 0.52), 9, 16);

            int remainingBeyondThree = overflowPaths.Count - 3;
            bool showBadge = remainingBeyondThree > 1;
            string badgeText = "+";

            string viewAllTooltip = string.Format(Locale.Folders_BigFolder_ViewAll, allPaths.Count);
            items.Add(new BigFolderItem(
                Path: "",
                Name: viewAllTooltip,
                Icon: null,
                ItemSize: itemSize,
                ImageSize: imageSize,
                CornerRadius: cornerRadius,
                IsMoreButton: true,
                MiniIcon0: miniIcons[0],
                MiniIcon1: miniIcons[1],
                MiniIcon2: miniIcons[2],
                MiniIcon3: miniIcons[3],
                MiniIconSize: miniSize,
                ShowBadge: showBadge,
                BadgeText: badgeText,
                MiniSpacing: miniSpacing,
                MiniPaddingThickness: miniPaddingThickness,
                MiniItemCornerRadius: miniItemCornerRadius,
                MiniBadgeCornerRadius: miniBadgeCornerRadius,
                MiniFontSize: miniFontSize
            ));
        }

        IconGrid.ItemsSource = null;
        IconGrid.ItemsSource = items;
        IconGrid.IsVisible = items.Count > 0;
        EmptyHint.IsVisible = items.Count == 0;
    }

    private IEnumerable<string> GetItemPaths()
    {
        return model.SafeItems
            .Where(path => !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path)))
            .Distinct();
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private void OnItemPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsRightButtonPressed) return;

        var isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || (GetKeyState(0x11) & 0x8000) != 0;
        if (point.Properties.IsLeftButtonPressed && isCtrl) return;

        if (sender is Control { DataContext: BigFolderItem item })
        {
            if (item.IsMoreButton)
            {
                Dispatcher.UIThread.Post(OpenFullPopupWindow);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo { FileName = item.Path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open {item.Path}: {ex.Message}");
            }
        }
    }

    private void OpenFullPopupWindow()
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        BigFolderPopupWindow.ShowPopup(model, screenCenter, owner, UpdateModel, SpawnCornerRadius);
    }

    /// <summary>
    /// The host widget's visual corner radius, so the popup's corners and its pre-rendered
    /// glass match the widget card exactly. Null keeps the popup's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as uWidgets.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var pixelPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(pixelPoint.X, pixelPoint.Y), topLevel);
            }
            return (null, topLevel);
        }
        return (null, null);
    }

    private void TriggerLiquidGlassPreRender()
    {
        try
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0)
            {
                Dispatcher.UIThread.Post(TriggerLiquidGlassPreRender, DispatcherPriority.Loaded);
                return;
            }

            var appSettings = new uWidgets.Core.Services.AppSettingsProvider().Get();
            if (appSettings.Theme.UsesRenderedGlass)
            {
                var (screenCenter, topLevel) = GetScreenCenterAndTopLevel();
                var window = VisualRoot as Window ?? topLevel as Window;
                var screen = (screenCenter.HasValue && window != null
                    ? window.Screens.ScreenFromPoint(new PixelPoint((int)Math.Round(screenCenter.Value.X), (int)Math.Round(screenCenter.Value.Y)))
                    : null) ?? window?.Screens.Primary;

                bool isDark = topLevel?.ActualThemeVariant == ThemeVariant.Dark || (appSettings.Theme.DarkMode ?? true);
                LiquidGlassPreRenderService.RequestPreRender(
                    screenCenter,
                    logicalWidth: 440,
                    logicalHeight: 480,
                    cornerRadius: SpawnCornerRadius ?? 18,
                    theme: appSettings.Theme,
                    isDark: isDark,
                    targetScreen: screen,
                    allScreens: window?.Screens.All);
            }
        }
        catch { }
    }

    private const double HoverScale = 1.15;

    private void OnItemPointerEntered(object? sender, PointerEventArgs e)
    {
        AnimateScale(sender, HoverScale);
    }

    private void OnItemPointerExited(object? sender, PointerEventArgs e)
    {
        AnimateScale(sender, 1.0);
    }

    private static void AnimateScale(object? sender, double scale)
    {
        if (sender is not Control control) return;
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
        if (e.Data.Contains(DataFormats.Files) || e.Data.Contains(DataFormats.FileNames))
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
        var paths = ExtractDroppedPaths(e.Data);
        if (paths.Count == 0) return;

        e.Handled = true;
        AddItems(paths);
    }

    private void OnOleDrop(List<string> paths)
    {
        DropHint.IsVisible = false;
        var valid = paths.Where(path => File.Exists(path) || Directory.Exists(path)).Distinct().ToList();
        if (valid.Count == 0) return;
        AddItems(valid);
    }

    private void OnOleDragActive(bool active)
    {
        DropHint.IsVisible = active;
    }

    private static List<string> ExtractDroppedPaths(IDataObject data)
    {
        var paths = new List<string>();
        if (data.GetFiles() is { } files)
        {
            foreach (var f in files)
            {
                var p = f.TryGetLocalPath() ?? f.Path.LocalPath;
                if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    paths.Add(p);
            }
        }
        else if (data.Get(DataFormats.Files) is IEnumerable<IStorageItem> storageItems)
        {
            foreach (var f in storageItems)
            {
                var p = f.TryGetLocalPath() ?? f.Path.LocalPath;
                if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    paths.Add(p);
            }
        }
        // GetFileNames() is obsolete ("Use GetFiles") but still the only API that yields local
        // paths for a plain string-list 'Files' drop; GetFiles() above already consumed the
        // IStorageItem form. Kept, with a narrow suppression, because swapping it for GetFiles()
        // would silently drop that drop-source. Migrate when Avalonia exposes the replacement.
#pragma warning disable CS0618
        else if (data.GetFileNames() is { } fileNames)
#pragma warning restore CS0618
        {
            foreach (var p in fileNames)
            {
                if (!string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p)))
                    paths.Add(p);
            }
        }
        return paths.Distinct().ToList();
    }

    private void AddItems(List<string> newPaths)
    {
        var updated = model.SafeItems.Concat(newPaths).Distinct().ToList();
        UpdateModel(model with { Items = updated });
    }

    private void UpdateModel(BigFolderModel newModel)
    {
        model = newModel;
        RecomputeAndPopulate();
        if (widgetLayoutProvider == null) return;
        var newSettings = JsonSerializer.SerializeToElement(newModel);
        var currentLayout = widgetLayoutProvider.Get();
        if (currentLayout != null)
        {
            var newLayout = currentLayout with { Settings = newSettings };
            widgetLayoutProvider.Save(newLayout);
        }
    }
}

public record BigFolderItem(
    string Path,
    string Name,
    Bitmap? Icon,
    double ItemSize,
    double ImageSize,
    CornerRadius CornerRadius,
    bool IsMoreButton = false,
    Bitmap? MiniIcon0 = null,
    Bitmap? MiniIcon1 = null,
    Bitmap? MiniIcon2 = null,
    Bitmap? MiniIcon3 = null,
    double MiniIconSize = 0.0,
    bool ShowBadge = false,
    string? BadgeText = null,
    double MiniSpacing = 0.0,
    Thickness MiniPaddingThickness = default,
    CornerRadius MiniItemCornerRadius = default,
    CornerRadius MiniBadgeCornerRadius = default,
    double MiniFontSize = 9.0);
