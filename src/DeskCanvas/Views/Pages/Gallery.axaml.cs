using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;

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
    public CornerRadius Radius => new(appSettingsProvider.Get().Dimensions.Radius / (VisualRoot?.RenderScaling ?? 1.0));

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
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e) => ReleasePreviews();

    private void ReleasePreviews()
    {
        if (widgets == null) return;

        var released = widgets;
        widgets = null;
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
                        locale?.GetString(widgetInfo.Title ?? string.Empty) ?? widgetInfo.Title,
                        locale?.GetString(widgetInfo.Subtitle ?? string.Empty) ?? widgetInfo.Subtitle,
                        widgetInfo.DefaultColumns,
                        widgetInfo.DefaultRows
                    ));

                result.AddRange(items);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Gallery] Failed to load {info.AssemblyName}: {ex.Message}");
            }
        }

        return result;
    }

    private void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var button = sender as Button;
        var preview = button!.DataContext as WidgetPreviewViewModel;
        if (preview == null) return;

        var settingsWindow = VisualRoot as Window;
        var attached = settingsWindow != null ? displayMonitor.Find(settingsWindow) : null;
        if (attached == null)
        {
            // Monitor not ready (edge case): legacy primary placement.
            var legacy = layoutProvider.Get().FindById(ScreensLayout.LegacyPrimaryId)
                         ?? new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, []);
            var (defaultW, defaultH) = DefaultSize(settingsWindow, preview.DefaultColumns, preview.DefaultRows);
            var pointer = button.PointToScreen(new Point(0, 0));
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
        var (x, y, w, h) = ComputePlacement(screenConfig, attached, preview.DefaultColumns, preview.DefaultRows);
        var widgetLayout = new WidgetLayout(preview.Type, preview.Subtype, x, y, w, h, null);
        widgetFactory.Add(screenConfig, widgetLayout).Show();
    }

    /// <summary>
    /// 「添加到侧栏」: create an independent sidebar instance (fresh instance id) on the screen the
    /// settings window is on, sized to the widget's default footprint and clamped to the sidebar.
    /// </summary>
    private void SidebarButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var preview = (sender as Control)?.DataContext as WidgetPreviewViewModel;
        if (preview == null) return;

        var sidebar = App.Services?.GetService(typeof(SidebarService)) as SidebarService;
        if (sidebar == null) return;

        var window = VisualRoot as Window;
        var attached = window != null ? displayMonitor.Find(window) : null;
        if (attached == null)
        {
            sidebar.ShowForCursorScreen();
            return;
        }

        // The sidebar is a grid: the widget's declared default span is what decides its size, not
        // a pixel size computed for the desktop grid (that size, divided by the widget margin,
        // is what used to come back as an 11×6 aggregate or a 6×6 square).
        var layout = new WidgetLayout(preview.Type, preview.Subtype, 0, 0, 0, 0, null);
        sidebar.AddWidget(attached.Screen, layout, preview.DefaultColumns, preview.DefaultRows);
    }

    /// <summary>
    /// Compute the initial placement (position relative to the owning screen's
    /// working area + size) for a new widget on the target screen.
    /// <para>
    /// Widgets live on the bottom desktop band (below every application window), so
    /// dropping a new card at the clicked position would bury it under the settings
    /// window and the widgets already there — the user then has to drag windows away
    /// just to grab it. Instead the card goes into the FIRST UNOCCUPIED slot,
    /// scanning the screen's grid left → right, top → bottom. Only when no cell can
    /// fit the widget's span does it fall back to the grid origin (top-left corner),
    /// ignoring occupancy and covering whatever sits there.
    /// </para>
    /// </summary>
    private (int X, int Y, int Width, int Height) ComputePlacement(
        ScreenLayout screenConfig, AttachedScreen attached, int defaultCols = 2, int defaultRows = 2)
    {
        var settings = appSettingsProvider.Get();
        var screen = attached.Screen;
        var area = screen.WorkingArea;
        var scaling = screen.Scaling;

        int width;
        int height;

        if (settings.Layout.GridMode != GridMode.Manual)
        {
            // Free placement (no grid): tile the working area with the widget's own
            // footprint (left → right, top → bottom), first slot without overlap wins.
            width = (int) (defaultCols * settings.Dimensions.Size + (defaultCols - 1) * settings.Dimensions.Margin);
            height = (int) (defaultRows * settings.Dimensions.Size + (defaultRows - 1) * settings.Dimensions.Margin);
            var (freeX, freeY) = FindFreeSpot(screenConfig.Layout, area, scaling, width, height);
            return (freeX, freeY, width, height);
        }

        var grid = screenConfig.Grid ?? settings.Grid ?? DeskCanvas.Core.Models.Settings.Grid.Default;
        var (cell, gridX, gridY) = GridMetrics.Resolve(grid, area.X, area.Y, area.Width, area.Height);
        width = (int) Math.Round(defaultCols * cell / scaling);
        height = (int) Math.Round(defaultRows * cell / scaling);
        var widthPhys = (int) Math.Round(width * scaling);
        var heightPhys = (int) Math.Round(height * scaling);

        // Candidate origins are only those where the whole span stays inside the grid.
        var columns = Math.Max(1, grid.Columns);
        var rows = Math.Max(1, grid.Rows);
        var spanCols = Math.Clamp(defaultCols, 1, columns);
        var spanRows = Math.Clamp(defaultRows, 1, rows);

        for (var row = 0; row + spanRows <= rows; row++)
        {
            for (var col = 0; col + spanCols <= columns; col++)
            {
                var x = gridX + col * cell;
                var y = gridY + row * cell;
                if (!IntersectsAnyWidget(screenConfig.Layout, area, scaling, x, y, widthPhys, heightPhys))
                    return (x - area.X, y - area.Y, width, height);
            }
        }

        // Grid full (or widget larger than the grid): start at the top-left corner,
        // ignoring occupancy — the new card covers whatever is there.
        return (gridX - area.X, gridY - area.Y, width, height);
    }

    /// <summary>
    /// Scan the working area with the widget's own footprint as the step
    /// (left → right, top → bottom) for the first position that does not intersect
    /// any widget already on the screen; the working-area origin is the "full"
    /// fallback. Returns coordinates relative to the working area.
    /// </summary>
    private (int X, int Y) FindFreeSpot(
        IReadOnlyList<WidgetLayout> layout, PixelRect area, double scaling, int widthDip, int heightDip)
    {
        var widthPhys = Math.Max(1, (int) Math.Round(widthDip * scaling));
        var heightPhys = Math.Max(1, (int) Math.Round(heightDip * scaling));
        var columns = Math.Max(1, area.Width / widthPhys);
        var rows = Math.Max(1, area.Height / heightPhys);

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var x = area.X + col * widthPhys;
                var y = area.Y + row * heightPhys;
                if (!IntersectsAnyWidget(layout, area, scaling, x, y, widthPhys, heightPhys))
                    return (x - area.X, y - area.Y);
            }
        }

        return (0, 0);
    }

    /// <summary>Whether a candidate rectangle (physical pixels) intersects any widget
    /// already stored on this screen (stored positions are working-area-relative).</summary>
    private static bool IntersectsAnyWidget(
        IReadOnlyList<WidgetLayout> layout, PixelRect area, double scaling,
        int x, int y, int widthPhys, int heightPhys)
    {
        foreach (var widget in layout)
        {
            var wx = widget.X + area.X;
            var wy = widget.Y + area.Y;
            var ww = (int) Math.Round(widget.Width * scaling);
            var wh = (int) Math.Round(widget.Height * scaling);
            if (x < wx + ww && wx < x + widthPhys && y < wy + wh && wy < y + heightPhys)
                return true;
        }

        return false;
    }

    private (int Width, int Height) DefaultSize(Window? settingsWindow, int defaultCols = 2, int defaultRows = 2)
    {
        var settings = appSettingsProvider.Get();
        if (settings.Layout.GridMode == GridMode.Manual)
        {
            var screen = settingsWindow?.Screens.Primary;
            var area = screen?.WorkingArea;
            var (cell, _, _) = GridMetrics.Resolve(settings.Grid, area?.X ?? 0, area?.Y ?? 0, area?.Width ?? 1920, area?.Height ?? 1080);
            var scaling = screen?.Scaling ?? 1.0;
            return ((int) Math.Round(defaultCols * cell / scaling), (int) Math.Round(defaultRows * cell / scaling));
        }
        var w = (int) (defaultCols * settings.Dimensions.Size + (defaultCols - 1) * settings.Dimensions.Margin);
        var h = (int) (defaultRows * settings.Dimensions.Size + (defaultRows - 1) * settings.Dimensions.Margin);
        return (w, h);
    }
}