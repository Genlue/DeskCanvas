using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Views;

/// <summary>
/// The右侧小组件侧栏 window for one screen: a transparent, topmost, tool-window strip pinned to
/// the right edge of that screen's work area. It hosts the shared <see cref="SidebarWidgetHost"/>
/// cards in a square-cell grid and owns the sidebar-only interactions — slide animation,
/// focus-loss/Esc auto-close, the left-edge width drag, and Ctrl-drag re-ordering.
/// <para>
/// Geometry is the grid's: the window is <see cref="WidthDip"/> wide and its content is
/// <see cref="GridColumns"/> equal columns, so one cell is <c>width / columns</c> (360 / 4 = 90).
/// </para>
/// </summary>
public partial class SidebarWindow : Window
{
    private const double SlideInMs = 260;
    private const double SlideOutMs = 180;
    private const double DragThresholdDip = 4;

    private readonly SidebarService service;
    private readonly AttachedScreen screen;
    private readonly List<SidebarWidgetHost> hosts = [];
    private readonly TranslateTransform slide = new();

    private double widthDip;

    /// <summary>How far the window extends past the card strip for the shadow's tail (DIPs).</summary>
    private double shadowExtentDip;
    private bool cardRegionApplied;

    /// <summary>Signature of the last applied region, so an unchanged one is not re-applied.</summary>
    private long cardRegionSignature;
    private bool closing;
    private bool draggingWidth;
    private double cursorStartX;
    private double widthStartDip;

    /// <summary>
    /// Set the moment this window is closed, before anything else reacts to it.
    /// <para>
    /// Once closed the platform window is gone, so every question that goes through it — which
    /// screen is this window on, what is its handle, set a window region — throws
    /// <c>ObjectDisposedException</c>. That is not theoretical: the sidebar service tears its hosts
    /// down from the <c>Closed</c> handler, and recomputing the 毛玻璃 region there (which resolves
    /// the window's screen) crashed the whole app with "Cannot access a disposed object". This flag
    /// is subscribed <b>first</b> in the constructor, and handlers run in subscription order, so it
    /// is already set by the time the service's handler runs.
    /// </para>
    /// </summary>
    private bool tornDown;

    private SidebarWidgetHost? dragHost;
    private string? dragInstanceId;
    private Point dragStart;
    private bool reordering;
    private long openedTicks;
    private DispatcherTimer? slideTween;
    private DispatcherTimer? closeTimer;

    public SidebarWindow(SidebarService service, AttachedScreen screen, double widthDip)
    {
        this.service = service;
        this.screen = screen;
        this.widthDip = widthDip;

        InitializeComponent();

        Root.RenderTransform = slide;

        // The transparency level follows the active material — see ApplyTransparencyHint. It is a
        // local value (not a style) so a runtime material switch reconfigures the same window.
        ApplyTransparencyHint();
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;

        Opened += OnOpened;
        Deactivated += OnDeactivated;
        KeyDown += OnKeyDown;
        // Subscribed before the sidebar service attaches its own handlers, so every teardown path
        // already sees the torn-down state (see tornDown).
        Closed += (_, _) =>
        {
            tornDown = true;
            SidebarWheelHook.Release();
        };

        WidthHandle.PointerPressed += OnWidthHandlePressed;
        WidthHandle.PointerMoved += OnWidthHandleMoved;
        WidthHandle.PointerReleased += OnWidthHandleReleased;
        WidthHandle.PointerCaptureLost += (_, _) => draggingWidth = false;

        EmptyOpenGallery.Click += (_, _) => OpenGallery();

        HostPanel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        HostPanel.AddHandler(PointerMovedEvent, OnPanelPointerMoved, RoutingStrategies.Tunnel);
        HostPanel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The wheel scrolls the sidebar wherever the pointer is inside it — the gaps between the
        // cards and the strip's own edges included, not just the widgets themselves. Ctrl+wheel
        // overrides a widget's own scroller and always scrolls the sidebar.
        Root.AddHandler(PointerWheelChangedEvent, OnRootWheelChanged, RoutingStrategies.Tunnel);

        // Everything that depends on where the cards actually landed — the shadow's fade anchor and
        // the 毛玻璃 window region — has to be recomputed after a layout pass. Rebuild() runs before
        // the cards have been measured, so their bounds (and therefore the region) are still zero
        // there; without this the region was never applied at all and the whole window frosted.
        HostPanel.LayoutUpdated += (_, _) => SyncOverlayGeometry();

        // Scrolling moves the cards with a render transform, which is not a layout change, so the
        // region would keep pointing at where the cards used to be.
        Scroller.ScrollChanged += (_, _) => SyncOverlayGeometry();

        // A material / dimension change while the sidebar is open must reconfigure the native
        // window (acrylic backdrop or not) instead of waiting for the next summon.
        service.AppSettings.DataChanged += OnAppSettingsChanged;

        // Position and size the window before it is ever shown, so the first frame already sits on
        // the screen edge instead of flashing at the platform default position.
        ApplyBounds();
    }

    /// <summary>
    /// Native transparency of the sidebar window.
    /// <para>
    /// 毛玻璃 (<see cref="SurfaceStyle.Acrylic"/>) is the OS-level acrylic backdrop, which is what
    /// makes the frosted theme frost at all — the hint has to be <c>AcrylicBlur</c> as the
    /// <b>first</b> choice, because the hint is a preference list and Windows supports
    /// <c>Transparent</c> too: listing Transparent first (as this window used to) meant the acrylic
    /// level was silently never selected and the whole theme did nothing while the sidebar was open.
    /// </para>
    /// <para>
    /// Every rendered material (液态玻璃 / 新液态玻璃) must keep <c>Transparent</c> first so the
    /// card draws its own per-pixel-alpha glass; acrylic stays listed behind it purely so a platform
    /// without per-pixel transparency degrades to a visible frosted card instead of an invisible
    /// window.
    /// </para>
    /// </summary>
    private void ApplyTransparencyHint() =>
        TransparencyLevelHint = UsesNativeBlur
            ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Transparent]
            : [WindowTransparencyLevel.Transparent, WindowTransparencyLevel.AcrylicBlur];

    /// <summary>True while the active material is the OS-level frosted glass.</summary>
    private bool UsesNativeBlur => service.AppSettings.Get().Theme.UsesNativeBlur;

    /// <summary>The live shadow settings of this sidebar.</summary>
    private SidebarShadowSettings ShadowSettings => service.SidebarSettings.EffectiveShadow;

    private void OnAppSettingsChanged(object? sender, AppSettings? oldSettings, AppSettings newSettings)
    {
        if (tornDown) return;

        if (oldSettings?.Theme.UsesNativeBlur != newSettings.Theme.UsesNativeBlur)
        {
            ApplyTransparencyHint();
            if (!IsVisible) return;
        }

        // Dimensions/margins feed the card radius; a shadow edit changes the window's own width.
        ApplyBounds();
        RelayoutGrid();
    }

    /// <summary>
    /// This window's screen configuration, read live from the layout provider (the captured
    /// <see cref="AttachedScreen"/> is a snapshot and would restore just-removed entries).
    /// </summary>
    public ScreenLayout? ScreenConfig => service.ScreenConfigFor(ScreenId) ?? screen.Config;

    /// <summary>Stable id of the screen this sidebar belongs to.</summary>
    public string ScreenId => screen.Config?.Id ?? ScreensLayout.LegacyPrimaryId;

    /// <summary>Current DPI scaling of the screen.</summary>
    public double Scaling => LiveScreen.Scaling;

    /// <summary>
    /// The screen this sidebar currently sits on, resolved <b>live</b>.
    /// <para>
    /// The <see cref="AttachedScreen"/> this window was built from is a snapshot: a resolution, DPI
    /// or arrangement change makes its rectangle stale, and a stale rectangle does not just
    /// misplace the strip — the glass sampler would capture one region while the frame mapping
    /// pointed at another, so the cards would refract the wrong part of the screen (or a piece of
    /// the desktop that no longer exists).
    /// </para>
    /// <para>
    /// The query goes through the platform window, which throws once the window is closed, so a
    /// torn-down (or not-yet-created) window falls back to the snapshot instead of taking the app
    /// down.
    /// </para>
    /// </summary>
    private Screen LiveScreen
    {
        get
        {
            if (tornDown) return screen.Screen;
            try
            {
                return Screens.ScreenFromWindow(this) ?? screen.Screen;
            }
            catch
            {
                // No usable platform window (closing, disposed, not created yet): the snapshot is
                // the only answer available, and being slightly stale beats crashing.
                return screen.Screen;
            }
        }
    }

    /// <summary>This sidebar's screen rectangle in physical pixels (the glass sampler's target).</summary>
    public PixelRect ScreenBoundsPx => LiveScreen.Bounds;

    /// <summary>Whether auto-close on focus loss is currently suppressed (menu/edit/drag open).</summary>
    public bool AutoCloseSuppressed { get; set; }

    /// <summary>
    /// How many secondary panels opened from this sidebar's cards are currently alive. While one is
    /// open the sidebar must not follow focus out: a panel needs the sidebar to stay where it is
    /// (it spawns anchored on a card, and the card has to be there to fold back into).
    /// </summary>
    private int hostedPanels;

    /// <summary>True while nothing may auto-close the sidebar on focus loss.</summary>
    private bool SuppressedAutoClose => AutoCloseSuppressed || hostedPanels > 0;

    /// <summary>
    /// A secondary panel opened from a card in this sidebar. Until the matching
    /// <see cref="EndHostedPanel"/> the sidebar holds itself open.
    /// </summary>
    public void BeginHostedPanel() => hostedPanels++;

    /// <summary>The hosted panel is gone. The sidebar returns to its normal focus-loss behaviour.</summary>
    public void EndHostedPanel()
    {
        if (hostedPanels > 0) hostedPanels--;

        // A sidebar that is already sliding out (or gone) has nothing left to follow out — and
        // asking a torn-down window whether it is active goes through the platform, which throws
        // once the window is closed.
        if (tornDown || closing) return;

        // The panel is dismissed, so the click (or Escape) that closed it belongs to whatever
        // comes next — follow it out unless the sidebar itself is what the user is now on.
        if (!SuppressedAutoClose && !IsActive) service.Hide("panel-closed");
    }

    /// <summary>The attached screen this sidebar is on.</summary>
    public AttachedScreen Screen => screen;

    /// <summary>Column count of this screen's sidebar grid.</summary>
    public int GridColumns => ScreenConfig?.EffectiveSidebar.EffectiveColumns ?? SidebarLayout.DefaultColumns;

    /// <summary>Side of one square grid cell in DIPs (sidebar width ÷ columns).</summary>
    public double CellSideDip => SidebarRules.CellSide(widthDip, GridColumns);

    /// <summary>
    /// Total gap the grid leaves between a card and its cell — twice the configured per-side inset
    /// (see <see cref="SidebarSettings.EffectiveWidgetMarginDip"/>).
    /// </summary>
    public double GutterDip => service.SidebarSettings.EffectiveWidgetMarginDip * 2;

    /// <summary>
    /// Base corner radius of this sidebar's cards: the sidebar's own override when the user set
    /// one, otherwise the desktop chain for this screen (per-screen radius, then the global one) —
    /// which is what every sidebar used before the override existed. Still passed through the
    /// adaptive per-span heuristic by <see cref="SidebarWidgetHost"/>.
    /// </summary>
    public double WidgetRadiusDip => service.SidebarSettings.EffectiveWidgetRadiusDip(
        (int)Math.Round(ScreenConfig?.Radius ?? service.AppSettings.Get().Dimensions.Radius));

    /// <summary>The current width in DIPs.</summary>
    public double WidthDip => widthDip;

    /// <summary>True while the panel is sliding out (a toggle in this window must reopen it).</summary>
    public bool IsClosing => closing;

    /// <summary>
    /// The slide animation's current offset, in DIPs. Glass geometry must <b>not</b> see it: the
    /// cards are laid out at their settled place and only the window's content is translated for the
    /// animation, but a position read through the visual tree (<c>PointToScreen</c>,
    /// <c>TranslatePoint</c>) folds the transform in — so a glass surface that measured its position
    /// mid-slide sampled a strip a whole window-width to the right. With a static screen nothing
    /// re-publishes afterwards, so the wrongness stuck.
    /// </summary>
    public double SlideOffsetDip => slide.X;

    /// <summary>
    /// Whether Windows currently keeps this window out of screen captures
    /// (<c>WDA_EXCLUDEFROMCAPTURE</c>). When it does, the glass sampler must <b>not</b> overwrite
    /// this window's rectangle with a stale frame: the capture it takes already contains the real
    /// desktop behind the sidebar, which is exactly the backdrop the glass is supposed to refract.
    /// </summary>
    public bool CaptureExclusionActive { get; private set; }

    /// <summary>
    /// Ask Windows to leave this window out of screen captures and remember whether it agreed.
    /// Best effort: an unsupported system simply returns <c>false</c> (see
    /// <see cref="CaptureExclusionActive"/> for what depends on it).
    /// </summary>
    public bool ApplyCaptureExclusion()
    {
        CaptureExclusionActive = SidebarZOrder.TryExcludeFromCapture(this);
        return CaptureExclusionActive;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        openedTicks = Environment.TickCount64;
        SidebarZOrder.MakeToolWindow(this);
        ApplyCaptureExclusion();
        ApplyBounds();
        Rebuild();
        // The wheel over a card reaches the window normally; over the gaps it never does in 毛玻璃
        // (the window region clips those out of hit-testing, see ApplyCardRegion), so a hook picks
        // them up instead. One reference per open sidebar.
        SidebarWheelHook.Acquire(IsWheelGap, ScrollFromHook);        SlideTo(Math.Max(1, Width), animate: false);
        Dispatcher.UIThread.Post(() =>
        {
            SlideTo(0, animate: true);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Whether the physical-pixel point is inside this sidebar's strip but over none of its cards —
    /// exactly the places the window's own wheel handling never hears about. Read from the wheel
    /// hook, so it must stay cheap and must never throw.
    /// </summary>
    private bool IsWheelGap(int x, int y)
    {
        if (tornDown || !IsVisible || closing) return false;

        // The window spans one monitor, so physical px → window DIP is one division. PointToClient
        // is not reachable here on every Avalonia build, and the arithmetic is all it would do.
        var scaling = Scaling <= 0 ? 1.0 : Scaling;
        var local = new Point((x - Position.X) / scaling, (y - Position.Y) / scaling);

        if (local.X < 0 || local.Y < 0 || local.X > Bounds.Width || local.Y > Bounds.Height) return false;

        foreach (var host in hosts)
        {
            if (!host.IsVisible) continue;
            if (host.TranslatePoint(default, this) is not { } origin) continue;
            if (new Rect(origin, host.Bounds.Size).Contains(local)) return false;
        }

        return true;
    }

    /// <summary>Wheel over a gap: scroll this sidebar. Called from the hook, on the UI thread.</summary>
    private void ScrollFromHook(int delta)
    {
        if (tornDown) return;
        ScrollSidebarBy(delta);
    }

    private void OnDeactivated(object? sender, EventArgs e)
    {
        // Clicking any other window (or the desktop) brings the sidebar back in, unless a menu,
        // the widget editor, a drag or a secondary panel opened from one of the cards is open.
        // The grace period swallows the activation race that happens while the window is still
        // being shown — without it the sidebar closed itself the instant it appeared.
        if (SuppressedAutoClose) return;
        if (Environment.TickCount64 - openedTicks < 500) return;
        service.Hide("deactivated");
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            service.Hide("escape");
        }
    }

    // ---------- Layout / width ----------

    private void ApplyBounds()
    {
        if (tornDown) return;

        ApplyShadowGeometry();

        var scaling = Scaling <= 0 ? 1.0 : Scaling;
        var workArea = LiveScreen.WorkingArea;
        // The visible strip is still exactly widthDip wide and flush with the screen's right edge;
        // the extra shadowExtentDip is the tail overhang on its left.
        var windowWidthDip = widthDip + shadowExtentDip;
        var widthPx = (int)Math.Round(windowWidthDip * scaling);

        Width = windowWidthDip;
        Height = Math.Max(1, workArea.Height / scaling);
        Position = new PixelPoint(workArea.Right - widthPx, workArea.Y);

        // The cell size follows the width, so the whole grid has to be re-measured (and every
        // card's radius with it).
        HostPanel.Relayout();
        ApplyCardRegion();

        // The sampler keeps this window's own rectangle out of the image, so it has to be told
        // whenever that rectangle moves or resizes (the width drag changes it every frame).
        service.RefreshCaptureTargets();
    }

    /// <summary>
    /// Re-read the shadow settings and lay the window out around them: the card strip keeps
    /// <see cref="WidthDip"/> (so the grid's cell size never changes) and is offset to the right by
    /// the shadow's reach, which is the extra space the tail fades out in.
    /// <para>
    /// The width-drag handle is placed at the leftmost card's <b>own</b> left edge at the same time.
    /// It cannot sit in the gutter next to the strip's edge: outside the cards the 毛玻璃 window
    /// region is empty, so the handle would be unclickable, and any region patch added to keep it
    /// clickable frosts a visible vertical line there (see <see cref="ApplyCardRegion"/>). Sitting on
    /// the card puts it inside the frosted rectangle — clickable in every material — while still
    /// reading as "the sidebar's left edge".
    /// </para>
    /// </summary>
    private void ApplyShadowGeometry()
    {
        var shadow = ShadowSettings;
        shadowExtentDip = SidebarRules.ClampShadowExtent(
            shadow.IsVisible ? shadow.EffectiveExtendDip : 0, widthDip, ScreenWidthDip());
        ContentRoot.Margin = new Thickness(shadowExtentDip, 0, 0, 0);

        // ContentRoot starts at the strip's left edge (its margin is the shadow overhang above) and
        // the grid insets the first card by half the gutter it actually drew, so that is where the
        // handle goes. The drawn gutter — not the configured one — because a narrow sidebar caps it
        // at half a cell (SidebarRules.EffectiveGutter): following the configured value there would
        // push the handle back out of the card, and out of the frosted region with it.
        WidthHandle.Margin = new Thickness(
            SidebarRules.EffectiveGutter(CellSideDip, GutterDip) / 2, 0, 0, 0);

        UpdateShadowAnchor();
    }

    /// <summary>Width of this sidebar's screen in DIPs (fallback: the sidebar's own width).</summary>
    private double ScreenWidthDip()
    {
        var scaling = Scaling <= 0 ? 1.0 : Scaling;
        return LiveScreen.Bounds.Width / scaling;
    }

    /// <summary>
    /// Recompute everything that is anchored to where the cards actually landed: the shadow's fade
    /// start and the 毛玻璃 window region. Both are cheap no-ops when nothing moved, and both are
    /// wrong while the cards are still unmeasured — which is why this is driven by the layout and
    /// scroll notifications rather than only by <see cref="Rebuild"/>.
    /// </summary>
    private void SyncOverlayGeometry()
    {
        if (tornDown) return;
        UpdateShadowAnchor();
        ApplyCardRegion();
    }

    /// <summary>
    /// Aim the shadow's fade at the leftmost card. The fade ends a little to the left of that card
    /// (which is why the window is wider than the strip), so an empty sidebar falls back to the
    /// strip's own left edge.
    /// <para>
    /// The coordinate space is <see cref="Root"/>, <b>not</b> the window: the layer is a child of
    /// Root and paints in Root's own space, while Root carries the slide animation's render
    /// transform. Measuring against the window folded that transform into the anchor, so during the
    /// open/close slide the fade was aimed a whole window-width off and collapsed into a
    /// half-invisible gradient — which is exactly why the shadow only showed up once the panel had
    /// already landed. In Root's space the anchor is stable while the panel slides, and the layer
    /// (also a child of Root) travels with it, so the shadow arrives together with the sidebar.
    /// </para>
    /// </summary>
    private void UpdateShadowAnchor()
    {
        if (tornDown) return;

        var leftmost = double.MaxValue;
        foreach (var host in hosts)
        {
            if (!host.IsVisible) continue;
            if (host.TranslatePoint(default, Root) is not { } origin) continue;
            if (origin.X < leftmost) leftmost = origin.X;
        }

        ShadowLayer.Configure(ShadowSettings, leftmost < double.MaxValue ? leftmost : shadowExtentDip);
    }

    /// <summary>
    /// Clip the native window to its <b>cards</b> while the material is 毛玻璃.
    /// <para>
    /// The OS acrylic backdrop covers the whole HWND, so the region is the only thing that decides
    /// where the frost appears — and it has to be the cards and nothing else: the gaps between them,
    /// the strip beside them and the shadow's tail are all meant to stay clear glass-free space (the
    /// desktop widget clips its single card rectangle for the same reason, see
    /// <c>Widget.ApplyWidgetRegion</c>).
    /// </para>
    /// <para>
    /// Every other material composites through per-pixel alpha, so the region is <b>cleared</b>
    /// there instead: a GDI region is 1-bit and would truncate the anti-aliased curve of a card
    /// corner.
    /// </para>
    /// </summary>
    private void ApplyCardRegion()
    {
        // Nothing to clip any more: the window (and its region) is already gone, and asking the
        // platform anything about it now throws (see tornDown).
        if (tornDown || !OperatingSystem.IsWindows()) return;

        // Every rendered material composites through per-pixel alpha, so the region is cleared for
        // them: a GDI region is 1-bit and would truncate the anti-aliased curve of a card corner.
        if (!UsesNativeBlur)
        {
            if (!cardRegionApplied) return;
            InteropService.ClearWidgetRegion(this);
            cardRegionApplied = false;
            cardRegionSignature = 0;
            return;
        }

        var scaling = Scaling <= 0 ? 1.0 : Scaling;
        var viewport = ViewportInWindow();
        var rects = new List<(int X, int Y, int Width, int Height, int Radius)>(hosts.Count + 1);
        var signature = (long)Math.Round(Bounds.Width) * 31 + (long)Math.Round(Bounds.Height);

        if (hosts.Count == 0)
        {
            // An empty sidebar has no cards; the placeholder is the only thing that may frost.
            if (EmptyState.IsVisible && EmptyState.Bounds.Width > 0 &&
                EmptyState.TranslatePoint(default, this) is { } empty)
            {
                var box = Clamp(empty, EmptyState.Bounds.Size, viewport);
                if (box.HasValue)
                {
                    rects.Add(Scale(box.Value, EmptyState.CornerRadius.TopLeft, scaling));
                    signature = Mix(signature, box.Value);
                }
            }
        }
        else
        {
            // Only the cards frost — nothing else.
            //
            // A notch used to join the region so the width-drag handle stayed grabbable, but it was
            // placed at window x = 0 while the card strip actually starts at shadowExtentDip inside
            // the window: with the diffuse shadow enabled (the default, 36 DIP of overhang) the
            // notch landed in the transparent shadow tail, some 40 DIP to the left of the leftmost
            // card, and read as a stray vertical frosted line at the far left of the sidebar.
            //
            // It was never needed either: the handle no longer sits in that gutter — it is pinned to
            // the leftmost card's own left edge (see ApplyShadowGeometry) and therefore already
            // inside the card rectangle that follows. Keeping it there is what keeps a 7 DIP wide,
            // full-height sliver out of the region.
            foreach (var host in hosts)
            {
                if (!host.IsVisible) continue;
                if (host.TranslatePoint(default, this) is not { } origin) continue;

                // A frameless widget (the 无边框时钟) draws its material inside its own glyphs: frost
                // the glyph spans it publishes and nothing else. Frosting the card rectangle instead
                // is what turned a sidebar clock into one slab of frosted glass filling its whole
                // grid cell — the same "whole cell is covered" state the widget avoids on the desktop
                // by owning its window region, which a window shared by every card cannot grant it.
                if (host.GlassMask is { } frameless)
                {
                    var spans = FramelessSpans(frameless, host, origin, scaling, viewport);
                    if (spans.Count > 0)
                    {
                        rects.AddRange(spans);
                        signature = signature * 31 + frameless.GlassMaskKey.GetHashCode();
                        continue;
                    }

                    // Empty spans: the widget is on a fallback with no glyphs to frost. Falling back
                    // to the card rectangle here would be exactly the bug above, so the widget is
                    // simply left out of the region.
                    continue;
                }

                // A card scrolled half out of view is drawn clipped by the scroll viewer, so its
                // region must be clipped the same way — otherwise the hidden part would frost an
                // empty band above or below it.
                var box = Clamp(origin, host.Bounds.Size, viewport);
                if (!box.HasValue) continue;

                rects.Add(Scale(box.Value, host.Radius.TopLeft, scaling));
                signature = Mix(signature, box.Value);
            }
        }

        // Not laid out yet (nothing measures more than 0): leave whatever region is in place — the
        // next layout/scroll notification re-applies with real rectangles, and clipping to nothing
        // right now would blank the sidebar.
        if (rects.Count == 0) return;

        // Layout and scroll notifications both land here and either may repeat unchanged; the
        // signature keeps SetWindowRgn off the window manager for those.
        if (cardRegionApplied && signature == cardRegionSignature) return;

        InteropService.SetWindowRegionFromRoundedRects(this, rects);
        cardRegionApplied = true;
        cardRegionSignature = signature;
    }

    /// <summary>The card strip's visible rectangle, in window-relative DIPs.</summary>
    private Rect ViewportInWindow()
    {
        var origin = Scroller.TranslatePoint(default, this) ?? new Point(0, 0);
        return new Rect(origin, Scroller.Bounds.Size);
    }

    /// <summary>Fold one rectangle into the region signature.</summary>
    private static long Mix(long signature, Rect box) =>
        ((signature * 31 + (long)Math.Round(box.X)) * 31 + (long)Math.Round(box.Y)) * 31
        + (long)Math.Round(box.Width) * 131 + (long)Math.Round(box.Height);

    /// <summary>A DIP rectangle clipped to the visible strip (null when nothing of it is visible).</summary>
    private static Rect? Clamp(Point origin, Size size, Rect viewport)
    {
        if (size.Width <= 0 || size.Height <= 0) return null;

        var box = new Rect(origin, size).Intersect(viewport);
        return box.Width > 0.5 && box.Height > 0.5 ? box : null;
    }

    /// <summary>A DIP rectangle plus radius, converted to the physical pixels GDI regions use.</summary>
    private static (int X, int Y, int Width, int Height, int Radius) Scale(Rect box, double radius, double scaling) =>
        ((int)Math.Round(box.X * scaling), (int)Math.Round(box.Y * scaling),
         (int)Math.Round(box.Width * scaling), (int)Math.Round(box.Height * scaling),
         (int)Math.Round(radius * scaling));

    /// <summary>
    /// A frameless widget's glyph spans, mapped into window pixels. The widget measures them in its
    /// own physical-pixel space, so they are offset by the card's position, re-scaled about the
    /// content's centre when a per-widget content scale is set, and clipped to the visible strip.
    /// </summary>
    private List<(int X, int Y, int Width, int Height, int Radius)> FramelessSpans(
        IFramelessGlassMask mask, SidebarWidgetHost host, Point origin, double scaling, Rect viewport)
    {
        var result = new List<(int X, int Y, int Width, int Height, int Radius)>();
        var scale = host.ContentScale;

        // The content is scaled about its own centre (RenderTransformOrigin 0.5/0.5), so a span edge
        // moves towards, or away from, the centre by the same proportion.
        var centerX = host.Bounds.Width * scaling / 2;
        var centerY = host.Bounds.Height * scaling / 2;
        var uniform = Math.Abs(scale - 1.0) < 0.001;

        var clipLeft = (int)Math.Round(viewport.X * scaling);
        var clipTop = (int)Math.Round(viewport.Y * scaling);
        var clipRight = (int)Math.Round((viewport.X + viewport.Width) * scaling);
        var clipBottom = (int)Math.Round((viewport.Y + viewport.Height) * scaling);

        var offsetX = origin.X * scaling;
        var offsetY = origin.Y * scaling;

        foreach (var (left, top, right, bottom) in mask.GetGlassSpans(scaling))
        {
            double x1 = left, y1 = top, x2 = right, y2 = bottom;
            if (!uniform)
            {
                x1 = centerX + (x1 - centerX) * scale;
                y1 = centerY + (y1 - centerY) * scale;
                x2 = centerX + (x2 - centerX) * scale;
                y2 = centerY + (y2 - centerY) * scale;
            }

            var l = Math.Max((int)Math.Round(offsetX + x1), clipLeft);
            var t = Math.Max((int)Math.Round(offsetY + y1), clipTop);
            var r = Math.Min((int)Math.Round(offsetX + x2), clipRight);
            var b = Math.Min((int)Math.Round(offsetY + y2), clipBottom);
            if (r - l <= 0 || b - t <= 0) continue;

            result.Add((l, t, r - l, b - t, 0));
        }

        return result;
    }

    /// <summary>
    /// Re-pack the grid and re-measure every card. Called when the column count changes, when a
    /// card's span changes and after a re-order.
    /// </summary>
    public void RelayoutGrid()
    {
        if (tornDown) return;

        HostPanel.Columns = GridColumns;
        HostPanel.Gutter = GutterDip;
        HostPanel.Relayout();
        EmptyState.IsVisible = hosts.Count == 0;
        UpdateShadowAnchor();
        ApplyCardRegion();
    }

    private void OnWidthHandlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        draggingWidth = true;
        AutoCloseSuppressed = true;
        widthStartDip = widthDip;
        cursorStartX = this.PointToScreen(e.GetPosition(this)).X;
        e.Pointer.Capture(WidthHandle);
        e.Handled = true;
    }

    private void OnWidthHandleMoved(object? sender, PointerEventArgs e)
    {
        if (!draggingWidth) return;

        // The left edge moves with the cursor while the right edge stays pinned to the screen edge.
        var cursorX = this.PointToScreen(e.GetPosition(this)).X;
        var deltaDip = (cursorX - cursorStartX) / (Scaling <= 0 ? 1.0 : Scaling);
        widthDip = SidebarRules.ClampWidth(widthStartDip - deltaDip, service.SidebarSettings, ScreenWidthDip());
        ApplyBounds();
        e.Handled = true;
    }

    private void OnWidthHandleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!draggingWidth) return;
        draggingWidth = false;
        AutoCloseSuppressed = false;
        service.UpdateWidth(ScreenId, widthDip);
        e.Handled = true;
    }

    // ---------- Wheel scrolling ----------

    /// <summary>DIPs scrolled per wheel notch (close to a ScrollViewer's own three-line step).</summary>
    private const double WheelStepDip = 56;

    /// <summary>
    /// Route a wheel event to the sidebar's own scroller.
    /// <para>
    /// The sidebar scrolls wherever the pointer is inside it — over the gaps between cards and the
    /// strip's edges, not only over a widget — because the wheel no longer has to reach the
    /// <see cref="Scroller"/> through hit-testing: this handler runs on the way <b>down</b> the tree
    /// (tunnel), before the pointer's own target sees the event.
    /// </para>
    /// <para>
    /// A widget that scrolls its own content keeps the wheel for itself (that is what its own
    /// scroll viewer is for), but only while it can actually move that way; at either end the wheel
    /// hands over to the sidebar, so a fully scrolled note does not trap the wheel. Holding
    /// <b>Ctrl</b> bypasses the widget entirely — the sidebar scrolls no matter what the pointer is
    /// over, which is the keyboard escape hatch for a widget with a busy inner scroller.
    /// </para>
    /// </summary>
    private void OnRootWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (Math.Abs(e.Delta.Y) < 0.001) return;

        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            var inner = NearestInnerScroller(e.Source as Visual);
            if (inner != null && CanScrollFurther(inner, e.Delta.Y)) return;
        }

        ScrollSidebarBy(e.Delta.Y);
        e.Handled = true;
    }

    /// <summary>
    /// The nearest scroll viewer between the event's source and the sidebar's own <see cref="Scroller"/>
    /// (<c>null</c> when the pointer is not over a widget that scrolls itself).
    /// </summary>
    private ScrollViewer? NearestInnerScroller(Visual? source)
    {
        for (var visual = source; visual != null && !ReferenceEquals(visual, Scroller); visual = visual.GetVisualParent())
            if (visual is ScrollViewer viewer) return viewer;
        return null;
    }

    /// <summary>Whether <paramref name="viewer"/> can still move in the wheel's direction.</summary>
    private static bool CanScrollFurther(ScrollViewer viewer, double delta)
    {
        const double epsilon = 0.5;
        var reach = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        return delta > 0 ? viewer.Offset.Y > epsilon : viewer.Offset.Y < reach - epsilon;
    }

    /// <summary>Scroll the sidebar's own scroller by <paramref name="delta"/> notches.</summary>
    private void ScrollSidebarBy(double delta)
    {
        var reach = Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height);
        var y = Math.Clamp(Scroller.Offset.Y - delta * WheelStepDip, 0, reach);
        if (Math.Abs(y - Scroller.Offset.Y) < 0.001) return;
        Scroller.Offset = new Vector(Scroller.Offset.X, y);
    }

    // ---------- Cards ----------

    /// <summary>Rebuild every card from the screen's stored sidebar.</summary>
    public void Rebuild()
    {
        if (tornDown) return;

        foreach (var host in hosts)
        {
            if (host.GlassMask is { } frameless) frameless.GlassMaskChanged -= OnGlassMaskChanged;
            host.Detach();
        }
        hosts.Clear();
        HostPanel.Children.Clear();

        HostPanel.Columns = GridColumns;
        HostPanel.Gutter = GutterDip;

        var entries = service.EntriesFor(ScreenId);
        foreach (var entry in entries)
        {
            SidebarWidgetHost host;
            try
            {
                host = service.CreateHost(ScreenId, entry, this);
            }
            catch
            {
                // A widget whose assembly/view cannot be loaded must not take the whole sidebar
                // (or the app) down — skip that entry and keep the rest.
                continue;
            }

            // A frameless widget announces new glyph spans as its digits advance; without this the
            // sidebar's frost would keep the shape of the first frame it ever saw.
            if (host.GlassMask is { } frameless) frameless.GlassMaskChanged += OnGlassMaskChanged;
            host.Classes.Add("SidebarHost");
            hosts.Add(host);
            HostPanel.Children.Add(host);
        }

        EmptyState.IsVisible = hosts.Count == 0;
        HostPanel.Relayout();
        UpdateShadowAnchor();
        ApplyCardRegion();
    }

    /// <summary>Open the settings window on the gallery page (empty-state / add-widget entry).</summary>
    private void OpenGallery()
    {
        if (App.Services?.GetService(typeof(Settings)) is Settings settings)
        {
            settings.ShowAndActivate();
            settings.SelectPage(typeof(DeskCanvas.Views.Pages.Gallery));
        }
    }

    /// <summary>Reorder the card for an entry without rebuilding its content (drag re-order).</summary>
    public void ApplyReorder(string instanceId, int targetIndex)
    {
        var host = hosts.FirstOrDefault(h => h.InstanceId == instanceId);
        if (host == null) return;

        var from = hosts.IndexOf(host);
        hosts.RemoveAt(from);
        HostPanel.Children.Remove(host);

        var index = Math.Clamp(targetIndex, 0, hosts.Count);
        hosts.Insert(index, host);
        HostPanel.Children.Insert(index, host);

        HostPanel.Relayout();
    }

    // ---------- Ctrl-drag reorder ----------

    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;
        if (IsInteractive(e.Source as Visual)) return;

        var host = (e.Source as Visual)?.FindAncestorOfType<SidebarWidgetHost>(includeSelf: true);
        if (host == null) return;

        dragHost = host;
        dragInstanceId = host.InstanceId;
        dragStart = e.GetPosition(HostPanel);
        e.Pointer.Capture(HostPanel);
        e.Handled = true;
    }

    private void OnPanelPointerMoved(object? sender, PointerEventArgs e)
    {
        if (dragHost == null) return;

        var point = e.GetPosition(HostPanel);
        if (!reordering && Math.Abs(point.Y - dragStart.Y) < DragThresholdDip && Math.Abs(point.X - dragStart.X) < DragThresholdDip)
            return;

        reordering = true;
        AutoCloseSuppressed = true;
        InsertionLine.IsVisible = true;

        var index = InsertionIndexFor(point.Y);
        InsertionLine.Margin = new Thickness(12, InsertionLineY(index), 12, 0);
        e.Handled = true;
    }

    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (dragHost == null || dragInstanceId == null) return;

        var instanceId = dragInstanceId;
        var insertIndex = reordering ? InsertionIndexFor(e.GetPosition(HostPanel).Y) : -1;
        var fromIndex = SidebarRules.IndexOf(hosts.Select(h => h.Entry).ToList(), instanceId);

        dragHost = null;
        dragInstanceId = null;
        reordering = false;
        InsertionLine.IsVisible = false;
        AutoCloseSuppressed = false;

        if (insertIndex >= 0 && fromIndex >= 0)
        {
            // Convert the "insert before" index into the entry's final index after removal.
            var target = insertIndex > fromIndex ? insertIndex - 1 : insertIndex;
            if (target != fromIndex)
                service.Reorder(ScreenId, instanceId, target);
        }

        e.Handled = true;
    }

    /// <summary>
    /// The insertion index for a drag, read off the packed grid: cards are compared by their row
    /// band first (the grid flows top→bottom), then left → right inside that row.
    /// </summary>
    private int InsertionIndexFor(double y)
    {
        var centers = new List<double>(hosts.Count);
        foreach (var host in hosts)
        {
            if (!host.IsVisible) continue;
            var top = host.Bounds.Y;
            centers.Add(top + host.Bounds.Height / 2);
        }
        return SidebarRules.InsertionIndex(centers, y);
    }

    private double InsertionLineY(int index)
    {
        var visible = hosts.Where(h => h.IsVisible).ToList();
        if (visible.Count == 0) return 0;
        if (index <= 0) return Math.Max(0, visible[0].Bounds.Y - 4);
        if (index >= visible.Count) return visible[^1].Bounds.Bottom + 4;
        return visible[index].Bounds.Y - 4;
    }

    private static bool IsInteractive(Visual? source)
    {
        if (source == null) return false;
        return source.FindAncestorOfType<Button>(includeSelf: true) != null
               || source.FindAncestorOfType<TextBox>(includeSelf: true) != null
               || source.FindAncestorOfType<NumericUpDown>(includeSelf: true) != null
               || source.FindAncestorOfType<Slider>(includeSelf: true) != null
               || source.FindAncestorOfType<ScrollBar>(includeSelf: true) != null;
    }

    // ---------- Slide animation ----------

    /// <summary>
    /// Move the panel. The animation is driven by a timer tween rather than by
    /// <c>Transitions</c> on the transform: the panel starts off-screen, so any environment where a
    /// transition does not run would leave the sidebar permanently outside the window (invisible).
    /// A tween always ends exactly at the target, whatever happens.
    /// <para>
    /// Every step re-applies the 毛玻璃 window region. The slide is a <b>render</b> transform on
    /// <see cref="Root"/>, which raises no layout pass at all, while the region is expressed in
    /// window coordinates and therefore tracks the cards as they move. Without the per-step update
    /// the region stayed frozen where the cards were at the instant the animation started — with the
    /// panel still a whole width off-screen — so the acrylic window was clipped down to nothing for
    /// the entire tween and then snapped into place the moment something finally recomputed it. That
    /// is the "no transition, just a delayed jump" the frosted theme showed. Following the cards
    /// makes the acrylic panel slide in exactly like a rendered one: as much of it as is on screen
    /// is frosted, and no more.
    /// </para>
    /// </summary>
    private void SlideTo(double target, bool animate)
    {
        slideTween?.Stop();
        slideTween = null;

        var from = slide.X;
        if (!animate)
        {
            slide.X = target;
            ApplyCardRegion();
            return;
        }

        if (Math.Abs(target - from) < 0.5)
        {
            slide.X = target;
            ApplyCardRegion();
            return;
        }

        var durationMs = (double)(target > from ? SlideOutMs : SlideInMs);
        var started = Environment.TickCount64;
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((Environment.TickCount64 - started) / durationMs, 0, 1);
            var eased = 1 - Math.Pow(1 - progress, 3);   // ease-out cubic
            slide.X = from + (target - from) * eased;
            ApplyCardRegion();

            if (progress < 1) return;

            timer.Stop();
            slide.X = target;
            ApplyCardRegion();
            if (ReferenceEquals(slideTween, timer)) slideTween = null;
        };

        slideTween = timer;
        timer.Start();
    }

    /// <summary>Slide the panel in from the right edge.</summary>
    public void PlayOpen()
    {
        // A close that is still sliding out has to be cancelled here: its timer would otherwise
        // close the window a moment after it was just reopened, which is exactly the flicker (and
        // the swallowed second press) of mashing the hotkey.
        closeTimer?.Stop();
        closeTimer = null;
        closing = false;

        openedTicks = Environment.TickCount64;
        SlideTo(Math.Max(1, Width), animate: false);

        // The sidebar must be able to take focus, otherwise neither Esc nor "click elsewhere to
        // dismiss" can ever fire (a window that never activates never reports Deactivated).
        try { Activate(); } catch { /* activation is best-effort */ }

        // The panel came back at the same width; re-pack in case the column count changed while
        // the sidebar was closed.
        RelayoutGrid();
        Dispatcher.UIThread.Post(() => SlideTo(0, animate: true), DispatcherPriority.Loaded);
    }

    /// <summary>Slide out and then close; <paramref name="afterHide"/> runs once it is gone.</summary>
    public void PlayClose(Action? afterHide = null)
    {
        if (closing) { afterHide?.Invoke(); return; }
        closing = true;

        SlideTo(Math.Max(1, Width), animate: true);

        closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SlideOutMs + 20) };
        closeTimer.Tick += (_, _) =>
        {
            closeTimer?.Stop();
            closeTimer = null;
            closing = false;

            // Close, never Hide: a hidden Avalonia window keeps its HWND (and its window class)
            // alive, so every summon used to leak another top-level window for the rest of the
            // session — seventeen of them after seventeen presses, all stacked on the same edge.
            Close();
            afterHide?.Invoke();
        };
        closeTimer.Start();
    }

    /// <summary>
    /// Release (or rebuild) what every hosted widget's content holds. Called when a fullscreen
    /// application takes the screen and when it goes away — the same reaction the desktop widgets
    /// get, so the sidebar's widgets hand their timers and caches back too.
    /// </summary>
    public void SetContentSuspended(bool suspended)
    {
        foreach (var host in hosts)
        {
            if (suspended) host.SuspendContent();
            else host.ResumeContent();
        }
    }

    /// <summary>
    /// Release every host subscription before the window is discarded. Reached from the
    /// <c>Closed</c> handler, i.e. after the platform window is gone, so this must stay purely
    /// managed: it used to re-apply the 毛玻璃 region here, which resolves the window's screen and
    /// threw <c>ObjectDisposedException</c> straight out of the window's own teardown.
    /// </summary>
    public void DetachHosts()
    {
        // Always detached, even for an empty sidebar — dropping the handler here is what keeps the
        // settings store from holding a closed window alive.
        service.AppSettings.DataChanged -= OnAppSettingsChanged;

        if (hosts.Count == 0) return;
        foreach (var host in hosts)
        {
            if (host.GlassMask is { } frameless) frameless.GlassMaskChanged -= OnGlassMaskChanged;
            host.Detach();
        }
        hosts.Clear();
        HostPanel.Children.Clear();
    }

    /// <summary>
    /// A frameless widget's glyphs moved on (a new digit, a resize): the frosted area of the window
    /// has to follow them.
    /// </summary>
    private void OnGlassMaskChanged()
    {
        if (tornDown) return;
        ApplyCardRegion();
    }
}
