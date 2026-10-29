using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Views;

public partial class EditWidget : HostDialogWindow
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    public UserControl Control { get; }
    public string WidgetType => widgetLayoutProvider.Get().Type;
    public string WidgetSubType => widgetLayoutProvider.Get().SubType;

    /// <summary>Persisted-geometry slot of this window (settings view type, stable across runs).</summary>
    private readonly string geometryKey;

    /// <summary>True once the first-show geometry pass ran; later openings must not re-place the window.</summary>
    private bool geometryChecked;

    /// <summary>True when stored geometry was applied (skips the plain visibility rescue path).</summary>
    private bool geometryRestored;

    public EditWidget(IWidgetLayoutProvider widgetLayoutProvider, UserControl control)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        DataContext = this;
        Control = control;
        InitializeComponent();

        geometryKey = WindowGeometryStore.ComposeKey(control.GetType());
        ApplyScrollOwnership(control);
        Opened += OnOpened;
        Closing += OnClosing;

        // Restore before the first show when the platform window already reports screens
        // (no center-then-jump flash); otherwise the first show retries below.
        TryRestoreGeometry();
    }

    /// <summary>
    /// Consumes the settings page's scroll contract: Host pages are scrolled by this window's
    /// single content viewport; LegacySelf pages keep their internal scroller and the viewport
    /// only supplies the finite box (no double scrollbar either way).
    /// </summary>
    private void ApplyScrollOwnership(UserControl control)
    {
        var hostScrolls = SettingsPage.GetScrollOwnership(control) == SettingsPageScrollOwnership.Host;
        ContentViewport.VerticalScrollBarVisibility = hostScrolls
            ? ScrollBarVisibility.Auto
            : ScrollBarVisibility.Disabled;
        ContentViewport.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        // Only the FIRST show may touch the position. The old code clamped X/Y to 0 on every
        // activation, which destroyed the legitimate negative coordinates of monitors placed
        // left of / above the primary — and re-placing on each activation also fought the user
        // moving the window between screens.
        if (geometryChecked) return;
        geometryChecked = true;
        if (!geometryRestored) geometryRestored = TryRestoreGeometry();
        RescueIfOffScreen();
    }

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        // Save the NORMAL geometry on close; best-effort, never blocks closing.
        try
        {
            SaveGeometry();
        }
        catch
        {
            // UI state only.
        }
    }

    // ---- stored geometry ------------------------------------------------------------------

    /// <summary>
    /// Applies the stored geometry (DIP size + working-area-relative center) when an entry
    /// exists: resolves the target screen (stored signature first, primary fallback), converts
    /// the normalized center to physical pixels with the screen's CURRENT scaling, clamps the
    /// window into that screen's working area (negative coordinates preserved) and shrinks the
    /// effective minimum size when the working area is smaller than the design minimum, so the
    /// title row and close button stay reachable while the content viewport scrolls.
    /// </summary>
    private bool TryRestoreGeometry()
    {
        if (geometryRestored) return true;

        WindowGeometryEntry? entry;
        Screens? screens;
        try
        {
            entry = WindowGeometryStore.TryGet(geometryKey);
            if (entry is null) return false;
            screens = Screens;
        }
        catch
        {
            return false; // platform window not ready yet — retried on first show
        }

        if (screens is null || screens.All.Count == 0) return false;

        var target = MatchScreen(screens, entry.MonitorId) ?? screens.Primary ?? screens.All[0];
        var restored = ResolveRestoredGeometry(entry, target.WorkingArea, target.Scaling, MinWidth, MinHeight);

        MinWidth = restored.MinWidth;
        MinHeight = restored.MinHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = restored.Width;
        Height = restored.Height;
        Position = restored.Position;

        geometryRestored = true;
        return true;
    }

    /// <summary>The applied window geometry of a restore.</summary>
    public readonly record struct RestoredWindowGeometry(
        double Width, double Height, double MinWidth, double MinHeight, PixelPoint Position);

    /// <summary>
    /// Pure restore math (exposed for the foundation checks): stored DIP size + normalized
    /// working-area center + the CURRENT screen scaling → DIP size, effective minimum size and
    /// physical top-left position. The position is clamped inside the working area (negative
    /// coordinates preserved). When the working area is smaller than the design minimum, the
    /// effective minimum shrinks to the area so the title row / close button stay reachable —
    /// the content viewport scrolls the rest.
    /// </summary>
    public static RestoredWindowGeometry ResolveRestoredGeometry(
        WindowGeometryEntry entry, PixelRect area, double scaling, double minWidth, double minHeight)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1.0;

        var areaWidthDip = area.Width / scaling;
        var areaHeightDip = area.Height / scaling;
        var effectiveMinWidth = Math.Min(minWidth, Math.Max(200, areaWidthDip));
        var effectiveMinHeight = Math.Min(minHeight, Math.Max(160, areaHeightDip));

        var width = Math.Clamp(entry.WidthDip, effectiveMinWidth, Math.Max(effectiveMinWidth, areaWidthDip));
        var height = Math.Clamp(entry.HeightDip, effectiveMinHeight, Math.Max(effectiveMinHeight, areaHeightDip));

        var sizePx = new PixelSize(
            Math.Max(1, (int)Math.Round(width * scaling)),
            Math.Max(1, (int)Math.Round(height * scaling)));
        var centerX = area.X + entry.CenterXRatio * area.Width;
        var centerY = area.Y + entry.CenterYRatio * area.Height;

        var position = ClampIntoArea(
            new PixelPoint(
                (int)Math.Round(centerX - sizePx.Width / 2.0),
                (int)Math.Round(centerY - sizePx.Height / 2.0)),
            sizePx, area);

        return new RestoredWindowGeometry(width, height, effectiveMinWidth, effectiveMinHeight, position);
    }

    private void SaveGeometry()
    {
        if (WindowState == WindowState.Minimized) return;
        if (!IsVisible) return;
        if (!double.IsFinite(Width) || !double.IsFinite(Height) || Width <= 0 || Height <= 0) return;

        var screens = Screens;
        if (screens is null || screens.All.Count == 0) return;

        var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        var sizePx = new PixelSize(
            Math.Max(1, (int)Math.Round(Width * scaling)),
            Math.Max(1, (int)Math.Round(Height * scaling)));
        var center = new PixelPoint(Position.X + sizePx.Width / 2, Position.Y + sizePx.Height / 2);

        var screen = ScreenContaining(screens, center) ?? screens.Primary ?? screens.All[0];
        var area = screen.WorkingArea;

        WindowGeometryStore.Save(geometryKey, new WindowGeometryEntry
        {
            WidthDip = Width,
            HeightDip = Height,
            MonitorId = ScreenId(screen),
            CenterXRatio = Math.Clamp((center.X - area.X) / (double)area.Width, 0, 1),
            CenterYRatio = Math.Clamp((center.Y - area.Y) / (double)area.Height, 0, 1),
        });
    }

    /// <summary>
    /// Keeps a first-show window reachable: if its rectangle intersects no screen's working
    /// area (e.g. the screen it was placed on is gone), pull it inside the primary working
    /// area. Legal negative coordinates are preserved untouched.
    /// </summary>
    private void RescueIfOffScreen()
    {
        var screens = Screens;
        if (screens is null || screens.All.Count == 0) return;

        var areas = new PixelRect[screens.All.Count];
        for (var i = 0; i < screens.All.Count; i++) areas[i] = screens.All[i].WorkingArea;
        var primary = (screens.Primary ?? screens.All[0]).WorkingArea;

        var size = PixelSize.FromSize(ClientSize, RenderScaling);
        Position = ResolveVisiblePosition(Position, size, areas, primary);
    }

    /// <summary>
    /// Pure visibility rule (exposed for the foundation checks): a window rectangle that
    /// intersects ANY working area keeps its position exactly — negative coordinates of
    /// monitors left of / above the primary included; otherwise it is clamped inside the
    /// primary working area so at least a meaningful part stays reachable.
    /// </summary>
    public static PixelPoint ResolveVisiblePosition(PixelPoint position, PixelSize size,
        IReadOnlyList<PixelRect> workingAreas, PixelRect primary)
    {
        var rect = new PixelRect(position, size);
        foreach (var area in workingAreas)
        {
            if (area.Intersects(rect)) return position;
        }

        var x = Math.Clamp(position.X, primary.X, Math.Max(primary.X, primary.Right - size.Width));
        var y = Math.Clamp(position.Y, primary.Y, Math.Max(primary.Y, primary.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    private static PixelPoint ClampIntoArea(PixelPoint position, PixelSize size, PixelRect area)
    {
        var x = Math.Clamp(position.X, area.X, Math.Max(area.X, area.Right - size.Width));
        var y = Math.Clamp(position.Y, area.Y, Math.Max(area.Y, area.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    private static Screen? MatchScreen(Screens screens, string? monitorId)
    {
        if (string.IsNullOrEmpty(monitorId)) return null;
        foreach (var screen in screens.All)
        {
            if (ScreenId(screen) == monitorId) return screen;
        }
        return null;
    }

    private static Screen? ScreenContaining(Screens screens, PixelPoint point)
    {
        foreach (var screen in screens.All)
        {
            if (screen.WorkingArea.Contains(point)) return screen;
        }
        return null;
    }

    /// <summary>
    /// Best-effort stable screen signature (11.1.3 exposes no device id): the primary flag,
    /// otherwise the bounds — restored by exact match with a primary fallback.
    /// </summary>
    private static string ScreenId(Screen screen)
        => screen.IsPrimary
            ? "primary"
            : $"{screen.Bounds.Width}x{screen.Bounds.Height}@{screen.Bounds.X},{screen.Bounds.Y}";

    private void Close(object? sender, RoutedEventArgs e) => Close();
}
