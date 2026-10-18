using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using GridSettings = DeskCanvas.Core.Models.Settings.Grid;

namespace DeskCanvas.Views.Pages;

public partial class Gallery : UserControl, INotifyPropertyChanged
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly ILayoutProvider layoutProvider;
    private readonly IAssemblyProvider assemblyProvider;
    private readonly List<AssemblyInfo> assemblyInfos;
    private readonly IWidgetFactory<Window, UserControl> widgetFactory;
    private readonly DisplayMonitorService displayMonitor;
    private List<WidgetPreviewViewModel>? widgets;
    public List<WidgetPreviewViewModel> Widgets => widgets ??= GetWidgets();
    public CornerRadius Radius => new(appSettingsProvider.Get().Dimensions.Radius / (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0));

    /// <summary>Display order of the gallery sections (top → bottom); keys are
    /// <c>WidgetInfoAttribute.Category</c> values resolved against the main app locale.</summary>
    private static readonly string[] CategoryOrder =
    [
        "WidgetCategory_ClockCalendar",
        "WidgetCategory_Weather",
        "WidgetCategory_SystemStatus",
        "WidgetCategory_Productivity",
        "WidgetCategory_Launcher",
        "WidgetCategory_Lifestyle",
        "WidgetCategory_Utilities",
        "WidgetCategory_Container",
    ];

    /// <summary>Glyph shown in each section header; mirrors the approved 8-category scheme.</summary>
    private static readonly IReadOnlyDictionary<string, string> CategoryIcons = new Dictionary<string, string>
    {
        ["WidgetCategory_ClockCalendar"] = "🕒",
        ["WidgetCategory_Weather"] = "🌤️",
        ["WidgetCategory_SystemStatus"] = "📊",
        ["WidgetCategory_Productivity"] = "✅",
        ["WidgetCategory_Launcher"] = "🚀",
        ["WidgetCategory_Lifestyle"] = "🎵",
        ["WidgetCategory_Utilities"] = "🧰",
        ["WidgetCategory_Container"] = "🧩",
    };

    /// <summary>Bucket for widgets whose assembly declares no category (stale or
    /// third-party DLLs compiled against the pre-category attribute constructor).</summary>
    private const string UncategorizedKey = "WidgetCategory_Uncategorized";

    private List<WidgetCategoryGroup>? categories;
    public List<WidgetCategoryGroup> Categories => categories ??= BuildCategories();

    /// <inheritdoc />
    public new event PropertyChangedEventHandler? PropertyChanged;

    public Gallery(IAppSettingsProvider appSettingsProvider, ILayoutProvider layoutProvider, IAssemblyProvider assemblyProvider, 
        AssemblyInfo assemblyInfo, IWidgetFactory<Window, UserControl> widgetFactory, DisplayMonitorService displayMonitor)
        : this(appSettingsProvider, layoutProvider, assemblyProvider, [assemblyInfo], widgetFactory, displayMonitor)
    {
    }

    public Gallery(IAppSettingsProvider appSettingsProvider, ILayoutProvider layoutProvider, IAssemblyProvider assemblyProvider, 
        IEnumerable<AssemblyInfo> assemblyInfos, IWidgetFactory<Window, UserControl> widgetFactory, DisplayMonitorService displayMonitor)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.layoutProvider = layoutProvider;
        this.assemblyProvider = assemblyProvider;
        this.assemblyInfos = assemblyInfos.ToList();
        this.widgetFactory = widgetFactory;
        this.displayMonitor = displayMonitor;
        DataContext = this;
        
        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// Every card hosts a real, live widget control — that is what makes the preview look exactly
    /// like the desktop widget — so the card list owns timers, WMI/SMTC subscriptions and decoded
    /// bitmaps for all ~28 widget types at once.
    /// <para>
    /// The settings window caches its pages, so a gallery that is navigated away from comes back
    /// later: the previews are released when the page leaves the tree (each control cleans itself
    /// up when it unloads) and rebuilt on the next visit. Keeping the disposed controls in the
    /// card list would show dead previews, and keeping the live ones alive would leak a whole
    /// widget set per visit.
    /// </para>
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (widgets != null) return;
        widgets = GetWidgets();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Widgets)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Categories)));
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => ReleasePreviews();

    private void ReleasePreviews()
    {
        if (widgets == null) return;

        var released = widgets;
        widgets = null;
        // The section view models wrap those same previews, so they must be rebuilt
        // on the next visit too. NO PropertyChanged is raised here on purpose: a
        // Categories refresh would lazily rebuild all previews mid-unload
        // and defeat the release. OnLoaded re-notifies everything after rebuilding.
        categories = null;
        foreach (var preview in released)
        {
            try
            {
                (preview.Control as IDisposable)?.Dispose();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Gallery] Failed to release {preview.Type}/{preview.Subtype}: {ex.Message}");
            }
        }
    }

    private List<WidgetPreviewViewModel> GetWidgets()
    {
        var result = new List<WidgetPreviewViewModel>();

        foreach (var info in assemblyInfos)
        {
            try
            {
                var assembly = assemblyProvider.LoadAssembly(info.AssemblyName);
                var locale = assemblyProvider.GetLocaleResourceManager(assembly);

                var items = assembly
                    .GetCustomAttributes<WidgetInfoAttribute>()
                    .Select(widgetInfo => new WidgetPreviewViewModel(
                        widgetFactory.CreateControl(widgetInfo.ViewType),
                        info.AssemblyName,
                        widgetInfo.ViewType.Name,
                        widgetInfo.ViewType,
                        locale?.GetString(widgetInfo.Title ?? string.Empty) ?? widgetInfo.Title,
                        widgetInfo.DefaultColumns,
                        widgetInfo.DefaultRows,
                        widgetInfo.PresetSpans,
                        widgetInfo.Category));

                result.AddRange(items);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Gallery] Failed to load {info.AssemblyName}: {ex.Message}");
            }
        }

        return result;
    }

    private List<WidgetCategoryGroup> BuildCategories()
    {
        var byKey = Widgets
            .GroupBy(widget => widget.Category ?? UncategorizedKey)
            .ToDictionary(group => group.Key, group => group.ToList());

        var groups = new List<WidgetCategoryGroup>();
        // Known categories first, in the curated display order.
        foreach (var key in CategoryOrder)
        {
            if (byKey.Remove(key, out var items)) groups.Add(MakeGroup(key, items));
        }
        // Categories the host doesn't know (a newer third-party widget DLL) slot in
        // alphabetically before the uncategorized bucket, which always trails last.
        foreach (var key in byKey.Keys.Where(key => key != UncategorizedKey).OrderBy(key => key, StringComparer.Ordinal).ToList())
        {
            if (byKey.Remove(key, out var items)) groups.Add(MakeGroup(key, items));
        }
        if (byKey.Remove(UncategorizedKey, out var others)) groups.Add(MakeGroup(UncategorizedKey, others));
        return groups;
    }

    private static WidgetCategoryGroup MakeGroup(string key, List<WidgetPreviewViewModel> items) =>
        new(key,
            CategoryIcons.GetValueOrDefault(key, "🧩"),
            Locale.ResourceManager.GetString(key, Locale.Culture) ?? key,
            items);

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)!.DataContext is not WidgetPreviewViewModel preview) return;
        PlaceWidget(preview, preview.DefaultColumns, preview.DefaultRows, sender as Control);
    }

    /// <summary>
    /// Right-click on a multi-preset card opens the size panel: a context menu (same
    /// styling and behavior as every other menu in the app) whose entries each carry a
    /// live mini preview at that span's aspect ratio plus the size badge. Choosing an
    /// entry places the widget on the desktop at that size directly. Single-span
    /// widgets declare no presets (<see cref="WidgetInfoAttribute.PresetSpans"/>) and
    /// intentionally get no panel.
    /// </summary>
    private void Card_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if ((sender as Button)!.DataContext is not WidgetPreviewViewModel preview) return;
        if (!preview.HasSizePresets) return;

        var menuItems = new List<MenuItem>();
        var thumbnails = new List<UserControl>();
        foreach (var (columns, rows) in preview.PresetSpans)
        {
            var control = widgetFactory.CreateControl(preview.ViewType);
            thumbnails.Add(control);
            menuItems.Add(BuildSizeMenuItem(preview, columns, rows, control));
        }

        var menu = new ContextMenu { ItemsSource = menuItems, Placement = PlacementMode.Pointer };
        menu.Closed += (_, _) =>
        {
            // Closing the menu detaches the thumbnails (their Unloaded cleanup runs);
            // dispose explicitly, same contract as ReleasePreviews for the page cards.
            foreach (var control in thumbnails)
            {
                try
                {
                    (control as IDisposable)?.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Gallery] Failed to release size thumbnail: {ex.Message}");
                }
            }
        };
        menu.Open((Control)sender!);
        e.Handled = true;
    }

    /// <summary>One entry of the size panel: a live mini preview at the span's aspect
    /// ratio — 100 DIP per grid cell, the same pixel tier heuristic views fall back to
    /// outside the desktop — plus the same size badge the gallery card shows. The
    /// thumbnail scale targets ~64 DIP per grid cell so the preview is actually
    /// legible, capped by a 256×128 outer box (4×1 stays 256 wide, 4×4 shrinks to
    /// 128×128).</summary>
    private MenuItem BuildSizeMenuItem(WidgetPreviewViewModel preview, int columns, int rows, UserControl control)
    {
        const double maxThumbWidth = 256;
        const double maxThumbHeight = 128;
        const double cell = 64;
        var scale = Math.Min(Math.Min(maxThumbWidth / columns, maxThumbHeight / rows), cell);
        var thumbWidth = columns * scale;
        var thumbHeight = rows * scale;

        // The live control is sized to columns*100 DIP (the same pixel tier heuristic
        // views fall back to outside the desktop) and the Viewbox scales it into the
        // thumbnail, so the preview shows exactly what the desktop card will look like.
        control.Width = columns * 100;
        control.Height = rows * 100;

        var thumbnail = new Border
        {
            Width = thumbWidth,
            Height = thumbHeight,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#16808080")),
            BorderBrush = new SolidColorBrush(Color.Parse("#25808080")),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            IsHitTestVisible = false,
            Child = new Viewbox { Stretch = Stretch.Uniform, Child = control }
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(thumbnail);
        header.Children.Add(new TextBlock
        {
            Text = WidgetPreviewViewModel.BadgeFor(columns, rows),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
        });

        var item = new MenuItem { Header = header };
        item.Click += (_, _) => PlaceWidget(preview, columns, rows, item);
        return item;
    }

    private void PlaceWidget(WidgetPreviewViewModel preview, int columns, int rows, Control? anchor)
    {
        displayMonitor.Refresh();
        if (displayMonitor.IsTopologyChanging) return;
        var settingsWindow = TopLevel.GetTopLevel(this) as Window;
        var attached = settingsWindow != null ? displayMonitor.Find(settingsWindow) : null;
        if (attached == null)
        {
            // Monitor not ready (edge case): legacy primary placement. The Avalonia
            // screen list itself (window.Screens) is still valid even while
            // displayMonitor's own reconciliation has not committed yet — resolve the
            // primary working area from it and run the SAME first-fit the regular
            // path uses. The old pointer placement dropped the card at the clicked
            // gallery card's screen position with no occupancy test at all, so every
            // widget placed in this window stacked onto the previous one (the
            // 吞组件 overlap); the fallback below only runs when no screen geometry
            // is resolvable at all.
            var legacy = layoutProvider.Get().FindById(ScreensLayout.LegacyPrimaryId)
                         ?? new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, []);
            var (defaultW, defaultH) = DefaultSize(settingsWindow, columns, rows);
            var primary = settingsWindow?.Screens.Primary;
            if (primary != null)
            {
                var area = primary.WorkingArea;
                var grid = legacy.Grid ?? appSettingsProvider.Get().Grid ?? GridSettings.Default;
                var frame = FrameFor(grid, area, primary.Scaling);
                // The unmigrated legacy entry keeps absolute desktop coordinates; the
                // scan works relative to the primary screen's working area.
                var relative = legacy.Key == null
                    ? legacy.Layout.Select(item => item with { X = item.X - area.X, Y = item.Y - area.Y }).ToList()
                    : legacy.Layout;
                var (col, row) = GridMetrics.FindFirstFitCell(relative, frame, columns, rows);
                // No slot (grid full, card larger than the grid, or no room left in the
                // working area): stop instead of covering whatever sits at the origin.
                if (col < 0)
                {
                    NotifyNoRoom();
                    return;
                }
                var (width, height) = SizeOf(frame, columns, rows);
                var relativeX = frame.GridX + col * frame.Cell;
                var relativeY = frame.GridY + row * frame.Cell;
                // v1 storage is absolute desktop pixels; keyed entries store relative.
                var (storedX, storedY) = legacy.Key == null
                    ? (relativeX + area.X, relativeY + area.Y)
                    : (relativeX, relativeY);
                widgetFactory.Add(legacy, new WidgetLayout(preview.Type, preview.Subtype, storedX, storedY,
                    width, height, null)).Show();
                return;
            }
            var pointer = anchor != null ? anchor.PointToScreen(new Point(0, 0)) : new PixelPoint(0, 0);
            if (legacy.Key != null)
            {
                var area = displayMonitor.FindByConfigId(legacy.Id)?.Screen.WorkingArea;
                if (area == null) return;
                pointer = new PixelPoint(pointer.X - area.Value.X, pointer.Y - area.Value.Y);
            }
            var legacyLayout = new WidgetLayout(preview.Type, preview.Subtype, pointer.X, pointer.Y,
                defaultW, defaultH, null);
            widgetFactory.Add(legacy, legacyLayout).Show();
            return;
        }

        // attached.Config is a SNAPSHOT taken at the last monitor poll (up to ~1.5s
        // stale, see DisplayMonitorService.CurrentConfig): a widget added or removed
        // moments ago is invisible in it — two rapid clicks then reused the same free
        // cell (overlap), and a just-deleted row-0 gap stayed "occupied" so the next
        // widget skipped to row 2. Re-read the entry by id to see the layout as it is
        // right now; EnsureConfig still covers brand-new screens with no entry yet.
        var screenConfig = displayMonitor.CurrentConfig(settingsWindow!)
                           ?? displayMonitor.EnsureConfig(attached);
        var placementConfig = screenConfig;
        if (screenConfig.Key == null)
        {
            var area = attached.Screen.WorkingArea;
            placementConfig = screenConfig with
            {
                Layout = screenConfig.Layout.Select(item => item with { X = item.X - area.X, Y = item.Y - area.Y }).ToList()
            };
        }
        if (ComputePlacement(placementConfig, attached, columns, rows) is not { } slot)
        {
            NotifyNoRoom();
            return;
        }
        var (x, y, w, h) = slot;
        if (screenConfig.Key == null)
        {
            x += attached.Screen.WorkingArea.X;
            y += attached.Screen.WorkingArea.Y;
        }
        var widgetLayout = new WidgetLayout(preview.Type, preview.Subtype, x, y, w, h, null);
        widgetFactory.Add(screenConfig, widgetLayout).Show();
    }

    /// <summary>
    /// No placeable slot for the card. Refuse the placement and say why: the old fallback
    /// dropped the card on the grid origin with occupancy ignored, so it landed exactly on
    /// the first placed widget (visually "swallowed" — the record itself was never lost).
    /// </summary>
    private void NotifyNoRoom()
    {
        if (VisualRoot is not Window owner) return;
        _ = ConfirmDialog.InformAsync(owner, Locale.Gallery_NoRoom, Locale.Gallery_NoRoom_Ok);
    }

    /// <summary>
    /// The placement frame of a screen: its grid lattice plus the working area a card has to
    /// stay inside (the grid itself may hang off the screen, see
    /// <see cref="GridMetrics.PlacementFrame"/>).
    /// </summary>
    private static GridMetrics.PlacementFrame FrameFor(GridSettings grid, PixelRect area, double scaling)
    {
        var (cell, gridX, gridY) = GridMetrics.Resolve(grid, area.X, area.Y, area.Width, area.Height);
        return new GridMetrics.PlacementFrame(cell, scaling, gridX - area.X, gridY - area.Y,
            grid.Columns, grid.Rows, area.Width, area.Height);
    }

    /// <summary>The DIP size of a <paramref name="columns"/>×<paramref name="rows"/> card placed in <paramref name="frame"/>.</summary>
    private static (int Width, int Height) SizeOf(GridMetrics.PlacementFrame frame, int columns, int rows) =>
        ((int) Math.Round(columns * frame.Cell / frame.Scaling),
         (int) Math.Round(rows * frame.Cell / frame.Scaling));

    /// <summary>
    /// Compute the initial placement (position relative to the owning screen's
    /// working area + size) for a new widget on the target screen, or <c>null</c> when the
    /// card does not fit.
    /// <para>
    /// Widgets live on the bottom desktop band (below every application window), so
    /// dropping a new card at the clicked position would bury it under the settings
    /// window and the widgets already there — the user then has to drag windows away
    /// just to grab it. Instead the card goes into the FIRST UNOCCUPIED slot,
    /// scanning the screen's grid left → right, top → bottom
    /// (<see cref="GridMetrics.FindFirstFitCell"/>: a cell occupancy matrix built from
    /// every stored widget's own span, so differently sized widgets pack correctly and
    /// a new card can never overlap an existing one while any free slot remains).
    /// </para>
    /// <para>
    /// "Free" is judged in the same rectangle the window will be shown at: the scan is
    /// limited to the cells that stay inside the working area, and the card keeps its real
    /// span (a 4×2 card on a 2-column grid is refused instead of being placed with a
    /// clamped span while keeping its full width). The old grid-origin fallback is gone —
    /// it is exactly how a card ended up covering the one placed before it.
    /// </para>
    /// </summary>
    private (int X, int Y, int Width, int Height)? ComputePlacement(
        ScreenLayout screenConfig, AttachedScreen attached, int defaultCols = 2, int defaultRows = 2)
    {
        var settings = appSettingsProvider.Get();
        var screen = attached.Screen;
        var grid = screenConfig.Grid ?? settings.Grid ?? GridSettings.Default;
        var frame = FrameFor(grid, screen.WorkingArea, screen.Scaling);

        var (col, row) = GridMetrics.FindFirstFitCell(screenConfig.Layout, frame, defaultCols, defaultRows);
        if (col < 0) return null;

        var (width, height) = SizeOf(frame, defaultCols, defaultRows);
        return (frame.GridX + col * frame.Cell, frame.GridY + row * frame.Cell, width, height);
    }

    private (int Width, int Height) DefaultSize(Window? settingsWindow, int defaultCols = 2, int defaultRows = 2)
    {
        var settings = appSettingsProvider.Get();
        var screen = settingsWindow?.Screens.Primary;
        var area = screen?.WorkingArea;
        var (cell, _, _) = GridMetrics.Resolve(settings.Grid, area?.X ?? 0, area?.Y ?? 0, area?.Width ?? 1920, area?.Height ?? 1080);
        var scaling = screen?.Scaling ?? 1.0;
        return ((int) Math.Round(defaultCols * cell / scaling), (int) Math.Round(defaultRows * cell / scaling));
    }
}
