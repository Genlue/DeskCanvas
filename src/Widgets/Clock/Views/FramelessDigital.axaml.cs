using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Clock.Models;
using Clock.Services;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace Clock.Views;

/// <summary>
/// The 无边框时钟: a clock whose numerals ARE the widget — no card, no plate; the glyphs render
/// straight onto the desktop in the global material.
/// <para>
/// Architecture contract — the part that must survive every future edit. This widget's history is
/// a chain of "stopped following the global theme" regressions, each one an event handler that
/// re-implemented a hand-picked subset of the rematerialize steps and missed one:
/// <list type="number">
/// <item><see cref="material"/> is the ONE snapshot of the resolved global material. It is written
/// in exactly one place — <see cref="ApplyCurrentMaterial"/> — and only read everywhere else. A
/// single frame therefore can never mix two materials, and a widget stuck on an old material
/// means "the event never fired", a single debuggable failure, not "two methods disagreed".</item>
/// <item>There are exactly TWO invalidation entries, and a new event picks one of them instead of
/// re-implementing steps inline:
/// <see cref="ApplyCurrentMaterial"/> for anything that can change the material (theme change,
/// light/dark variant change, model refresh, suspend/resume, load) — the full rebuild; and
/// <see cref="RefreshBackdrop"/> for content-only changes (wallpaper advanced, size, position) —
/// which never touches transparency, window region ownership or the resolved material.</item>
/// </list>
/// </para>
/// <para>
/// The clock has <b>no per-widget material override</b>: its surface always follows the global
/// app theme (see <see cref="FramelessThemeResolver"/>) and its glass optics always follow the
/// global liquid glass settings.
/// </para>
/// </summary>
public partial class FramelessDigital : UserControl, IFramelessWidget, IFramelessGlassMask, IWidgetSelfRefreshing, IWidgetSuspendable
{
    private FramelessClockModel model;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    /// <summary>The resolved global material, snapshotted by <see cref="ApplyCurrentMaterial"/>.</summary>
    private FramelessMaterial material;

    private UpdateTimer? currentTimer;
    private Window? window;
    private bool IsDesktopWidget => window is DeskCanvas.Views.Widget;

    // The frame currently composited on screen, plus the pre-rendered frames around it.
    // The field name is part of the widget's checked contract (tests/ClockThemeChecks reads it).
    private Bitmap? liquidGlassBitmap;
    private readonly FramelessGlassFrameCache frameCache = new();

    // Per-second Acrylic region recompute buffers (see UpdateWindowRegion).
    private RenderTargetBitmap? regionBitmap;
    private byte[]? regionBuffer;

    // Sidebar glass mask (see IFramelessGlassMask): the glyph spans a sidebar host unions into the
    // sidebar window's region, so the 毛玻璃 stays inside the numerals instead of flooding the whole
    // grid cell the widget occupies. Kept separate from the region buffers above because the two are
    // different things — the desktop path owns its window's region and rewrites it once per digit,
    // while a sidebar host pulls this mask on demand — and because the sizes differ (a sidebar cell
    // is far smaller than a stretched desktop clock).
    private RenderTargetBitmap? maskBitmap;
    private byte[]? maskBuffer;
    private string? maskCacheKey;
    private List<(int Left, int Top, int Right, int Bottom)> maskSpans = [];
    private string? lastAnnouncedMask;

    private readonly HashSet<string> inFlightRenders = new();
    private CancellationTokenSource? preRenderCts;

    // A newer wallpaper frame arrived while a glyph render was in flight; one follow-up render is
    // queued once the in-flight one finishes (see OnWallpaperInvalidated).
    private bool wallpaperStale;

    /// <summary>
    /// Memory budget for the pre-rendered frame cache (see <c>EvictExpiredCacheEntries</c>).
    /// 48 MB comfortably holds the current frame plus the whole seconds lookahead at any normal
    /// widget size while bounding a full-screen one.
    /// </summary>
    private const long CacheBudgetBytes = 48L * 1024 * 1024;

    private string? lastRegionKey;
    private bool hasRegionSet;

    /// <summary>True while every screen is covered by a fullscreen application (host suspended).</summary>
    private bool suspended;

    public FramelessDigital() : this(new FramelessClockModel(), null, null) { }

    public FramelessDigital(FramelessClockModel model) : this(model, null, null) { }

    public FramelessDigital(IWidgetLayoutProvider widgetLayoutProvider)
        : this(new FramelessClockModel(), widgetLayoutProvider, null) { }

    public FramelessDigital(FramelessClockModel model, IWidgetLayoutProvider? widgetLayoutProvider)
        : this(model, widgetLayoutProvider, null) { }

    public FramelessDigital(FramelessClockModel model, IWidgetLayoutProvider? widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;

        // Initial snapshot: previews and the checks resolve a material before the window ever
        // loads, and it must already be the global theme's — not a blank default.
        material = FramelessThemeResolver.Resolve(appSettingsProvider?.Get().Theme);

        InitializeComponent();
        Classes.Add("Frameless");
        Margin = new Thickness(0);
        Padding = new Thickness(0);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => OnSizeChanged();

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged += OnAppSettingsChanged;
        }

        SetupTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LiquidGlassWallpaper.WallpaperInvalidated -= OnWallpaperInvalidated;
        LiquidGlassWallpaper.WallpaperInvalidated += OnWallpaperInvalidated;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        LiquidGlassWallpaper.WallpaperInvalidated -= OnWallpaperInvalidated;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        window = TopLevel.GetTopLevel(this) as Window;
        if (window != null && IsDesktopWidget)
        {
            window.PositionChanged -= OnWindowPositionChanged;
            window.PositionChanged += OnWindowPositionChanged;
        }

        LiquidGlassWallpaper.WallpaperInvalidated -= OnWallpaperInvalidated;
        LiquidGlassWallpaper.WallpaperInvalidated += OnWallpaperInvalidated;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;
            appSettingsProvider.DataChanged += OnAppSettingsChanged;
        }

        lastRegionKey = null;
        hasRegionSet = false;
        SetupTimer();
        ApplyCurrentMaterial();
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        LiquidGlassWallpaper.WallpaperInvalidated -= OnWallpaperInvalidated;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        if (window != null)
        {
            // Deregistered before the field is cleared — the service identifies the consumer by the
            // window itself.
            UpdateCaptureDemand(wanted: false);
            if (IsDesktopWidget)
            {
                window.PositionChanged -= OnWindowPositionChanged;
                if (hasRegionSet)
                {
                    InteropService.ClearWidgetRegion(window);
                    hasRegionSet = false;
                }
            }
            window = null;
        }

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;
        }

        currentTimer?.Unsubscribe(OnTimerTick);
        currentTimer = null;

        ClearLiquidGlassCache();
        SetDisplayedFrame(null);

        regionBitmap?.Dispose();
        regionBitmap = null;
        regionBuffer = null;
    }

    private void OnWallpaperInvalidated()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(OnWallpaperInvalidated);
            return;
        }

        lastRegionKey = null;
        UpdateTransparencyLevel();

        // Live sampling raises this once per sampling round — far faster than one glyph render
        // (tens of ms). Clearing the cache and cancelling the in-flight render every round made
        // the first frame after attach lose that race for seconds (the flat glyph wash) and kept
        // the pre-rendered lookahead permanently burned. Let the running render finish and queue
        // exactly one follow-up with the newer wallpaper instead.
        if (inFlightRenders.Count > 0)
        {
            wallpaperStale = true;
            InvalidateVisual();
            return;
        }

        RefreshBackdrop(clearCache: false, resetRegionKey: false);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        // The variant changes how every glyph is rendered (dark/light material), so all cached
        // frames are wrong — a full rebuild, not the wallpaper-stale deferral above.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnActualThemeVariantChanged(sender, e));
            return;
        }

        ApplyCurrentMaterial();
    }

    private void OnSizeChanged()
    {
        // Every cached frame was rendered for the old pixel size; the window region is shaped for
        // the old glyph box too. A full backdrop refresh, but the material is untouched.
        RefreshBackdrop(clearCache: true);
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!IsDesktopWidget || !material.IsRenderedGlass) return;

        // The glass backdrop samples the wallpaper by desktop position: when the window moves, the
        // sample offsets move with it, so the cached frames — and any render in flight against the
        // old offsets — are all stale. The glyph shapes do not depend on position: no region reset,
        // and no InvalidateVisual — the new frame posts its own when it lands.
        RefreshBackdrop(clearCache: true, invalidate: false, resetRegionKey: false);
    }

    /// <summary>
    /// Release the pre-rendered liquid glass frames while every attached screen is covered by a
    /// fullscreen or maximized application (see <see cref="IWidgetSuspendable"/>). The cache is
    /// the heaviest thing this widget owns, and none of it is visible behind a fullscreen app, so
    /// it is rebuilt on demand on resume.
    /// <para>
    /// The frame that is currently on screen is <b>kept</b>: the widget is no longer hidden while
    /// suspended, so dropping it would flash a bare fallback for the length of a full optical
    /// re-render the moment the user returns to the desktop.
    /// </para>
    /// </summary>
    public void Suspend()
    {
        suspended = true;
        ClearLiquidGlassCache();

        // A widget that has not produced a frame yet (its first render was cancelled by the clear
        // above, or had not started) would otherwise sit on the flat fallback wash for as long as
        // the desktop stays covered — which reads as "the clock never became liquid glass". While
        // suspended the cache is allowed to hold exactly this one frame.
        if (liquidGlassBitmap == null) RequestBackdropRender();
    }

    /// <summary>Rebuild the material after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        suspended = false;
        SetupTimer();
        ApplyCurrentMaterial();
    }

    private void OnAppSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        ApplyCurrentMaterial();
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings.HasValue)
        {
            try
            {
                var json = layout.Settings.Value.GetRawText();
                var updated = JsonSerializer.Deserialize<FramelessClockModel>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
                if (updated != null)
                {
                    model = updated;
                    ApplyCurrentMaterial();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh FramelessClockModel: {ex.Message}");
            }
        }
    }

    private void SetupTimer()
    {
        var targetTimer = IsDesktopWidget && model.ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;
        if (currentTimer != targetTimer)
        {
            currentTimer?.Unsubscribe(OnTimerTick);
            currentTimer = targetTimer;
            currentTimer.Subscribe(OnTimerTick);
        }
    }

    /// <summary>
    /// The material the widget currently renders with — the snapshot taken by the last
    /// <see cref="ApplyCurrentMaterial"/>, not a fresh resolution. Every read site (the render
    /// branches, the window transparency, the verification loop) sees the same answer, so one
    /// frame can never straddle a theme change.
    /// </summary>
    private FramelessMaterial CurrentMaterial => material;

    /// <summary>
    /// The material as the (acrylic, rendered-glass, solid) triple the render branches switch on.
    /// Kept as its own member: <c>tests/ClockThemeChecks</c> drives it by reflection to pin the
    /// theme-following contract.
    /// </summary>
    private (bool IsAcrylic, bool IsLiquidGlass, bool IsSolid) ResolveEffectiveTheme()
        => (material.IsAcrylic, material.IsRenderedGlass, material.IsSolid);

    /// <summary>
    /// The ONE path that (re)materializes the clock, and the ONLY writer of
    /// <see cref="material"/>. Every material-affecting event — window load, a global theme
    /// change, a light/dark variant change, a model refresh, suspend/resume — funnels through
    /// here, so the side effects (material snapshot, glyph cache, window transparency, glyph
    /// window region, backdrop re-render) can never drift out of sync with the resolved material.
    /// This used to be re-implemented per handler with hand-picked subsets of the steps, which is
    /// exactly how "the frameless clock stopped following the global theme" kept regressing: one
    /// missed step in one handler left the widget stuck on the previous material.
    /// <para>
    /// The pre-rendered frames are always rebuilt: beyond the material, global theme edits
    /// (accent, optics, 染色强度, font) all reach the glyph renderer too, and diffing every
    /// one of them is exactly the fragility this method exists to remove. Rebuilding is
    /// cheap and only happens on a user-initiated settings change. The window-region
    /// teardown and transparency hint are idempotent and run unconditionally as well.
    /// </para>
    /// </summary>
    private void ApplyCurrentMaterial()
    {
        // Snapshot first: everything below (branches, region keys, verification) reads this one
        // answer, so a theme change mid-pipeline can never produce a half-old half-new widget.
        material = FramelessThemeResolver.Resolve(appSettingsProvider?.Get().Theme);

        lastRegionKey = null;
        ClearLiquidGlassCache();

        // The sampling demand follows the material: 纯色 needs no live frame at all, while 毛玻璃
        // (the glyph-span region) and 液态玻璃 (the raytraced glyphs) both do. A frameless widget
        // shows no card, so it has no LiquidGlassSurface to register with and registers directly.
        UpdateCaptureDemand();

        // Transparency first, glyph region second — the order is load-bearing when leaving 毛玻璃.
        // The acrylic backdrop is confined by the glyph region, so clearing the region while the
        // backdrop is still live is precisely the "the whole grid cell is covered with frosted
        // glass" state. Killing the backdrop first means the region clear can never expose it.
        UpdateTransparencyLevel(force: true);
        ClearGlyphRegionIfNotAcrylic();

        RequestBackdropRender();
        InvalidateVisual();

        // The platform does not always honour a level switch in the same turn (and the backdrop
        // can outlive the material that asked for it), so verify what actually happened instead
        // of trusting the assignment.
        VerifyMaterialWindowState();
    }

    /// <summary>
    /// The content-only refresh path: the wallpaper advanced, the widget was resized or moved.
    /// The resolved material is untouched — no transparency changes, no region ownership
    /// changes — only the rendered backdrop is brought up to date. Material-affecting events must
    /// go to <see cref="ApplyCurrentMaterial"/> instead.
    /// </summary>
    /// <param name="clearCache">Drop the pre-rendered frames (size/position-dependent content).</param>
    /// <param name="invalidate">Repaint now; false when the new frame posts its own invalidation.</param>
    /// <param name="resetRegionKey">Force the glyph window region to be recomputed on the next
    /// acrylic render; false when the glyph shapes cannot have changed (a pure window move).</param>
    private void RefreshBackdrop(bool clearCache, bool invalidate = true, bool resetRegionKey = true)
    {
        if (resetRegionKey) lastRegionKey = null;
        if (clearCache) ClearLiquidGlassCache();
        RequestBackdropRender();
        if (invalidate) InvalidateVisual();
    }

    /// <summary>Drop the window's glyph region once the material is no longer 毛玻璃 (see the ordering note in <see cref="ApplyCurrentMaterial"/>).</summary>
    private void ClearGlyphRegionIfNotAcrylic()
    {
        if (window == null || !IsDesktopWidget) return;
        if (material.IsAcrylic || !hasRegionSet) return;
        InteropService.ClearWidgetRegion(window);
        hasRegionSet = false;
    }

    /// <summary>
    /// Check that the window really ended up in the state the resolved material needs, and heal
    /// it if not: a non-毛玻璃 material must not keep the OS acrylic backdrop (with the glyph
    /// region gone that backdrop covers the whole cell), and 毛玻璃 must have a region and a live
    /// backdrop. Bounded to a few passes, so a platform that genuinely refuses a level cannot
    /// turn this into an endless backdrop rebuild.
    /// </summary>
    private void VerifyMaterialWindowState(int attemptsLeft = 3)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (window == null || !IsDesktopWidget) return;

            var wantAcrylic = material.IsAcrylic;
            var level = window.ActualTransparencyLevel;
            var levelOk = wantAcrylic
                ? level == WindowTransparencyLevel.AcrylicBlur
                : level != WindowTransparencyLevel.AcrylicBlur;

            if (levelOk)
            {
                ClearGlyphRegionIfNotAcrylic();
                return;
            }

            Debug.WriteLine($"[FramelessClock] material {(wantAcrylic ? "毛玻璃" : "非毛玻璃")} but the window's backdrop is {level} — re-asserting");
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            window.TransparencyLevelHint = wantAcrylic ? [WindowTransparencyLevel.AcrylicBlur] : [WindowTransparencyLevel.Transparent];
            ClearGlyphRegionIfNotAcrylic();

            if (attemptsLeft > 1)
                DispatcherTimer.RunOnce(() => VerifyMaterialWindowState(attemptsLeft - 1), TimeSpan.FromMilliseconds(120));
        }, DispatcherPriority.Background);
    }

    // Cached hint arrays: UpdateTransparencyLevel runs on every wallpaper invalidation (every
    // live-sampling round), and re-assigning the hint makes the Win32 impl re-apply the window
    // transparency each time — a per-round window-attribute poke that used to come with a fresh
    // array literal whose reference never compared equal to the previous one.
    private static readonly WindowTransparencyLevel[] AcrylicHint = [WindowTransparencyLevel.AcrylicBlur];
    private static readonly WindowTransparencyLevel[] TransparentHint = [WindowTransparencyLevel.Transparent];

    private void UpdateTransparencyLevel(bool force = false)
    {
        if (window == null || !IsDesktopWidget) return;
        var hint = material.IsAcrylic ? AcrylicHint : TransparentHint;
        if (!force && hint.SequenceEqual(window.TransparencyLevelHint)) return;

        // Toggle through None instead of assigning the target directly: the Win32 impl
        // short-circuits an assignment whose content it already holds, and a backdrop that
        // outlives its material has no other way back. None → target is a real level change the
        // platform always re-applies — the same idiom as the settings window's
        // ForceTransparencyReapply.
        window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        window.TransparencyLevelHint = hint;
    }

    private void OnTimerTick()
    {
        if (material.IsRenderedGlass)
        {
            var now = GetCurrentTime();
            var timeStr = FormatTime(now);

            // 1. Automatic Cleanup: Evict expired cache entries before current time
            EvictExpiredCacheEntries(now);

            // 2. Check if current frame was already pre-cached in background
            if (frameCache.TryGet(timeStr, out var cached))
            {
                SetDisplayedFrame(cached);
            }
            else
            {
                // Cache miss (e.g. immediately after resize or clock jump): render now
                SchedulePreRender(now, isImmediate: true);
            }

            // 3. Pre-cache next minute / upcoming seconds in background
            ScheduleUpcomingPreRenders(now);
        }

        InvalidateVisual();
    }

    private void RequestBackdropRender()
    {
        if (!material.IsRenderedGlass)
        {
            SetDisplayedFrame(null);
            return;
        }

        var now = GetCurrentTime();
        SchedulePreRender(now, isImmediate: true);
        ScheduleUpcomingPreRenders(now);
    }

    private void ScheduleUpcomingPreRenders(DateTime now)
    {
        if (!IsDesktopWidget) return;

        // Lookahead is the only thing the suspend actually gives up: while the desktop is covered
        // the widget keeps the single frame it is showing, so there is nothing to pre-render for.
        if (suspended) return;

        if (model.ShowSeconds)
        {
            // Rolling lookahead buffer for upcoming seconds in the next minute
            for (int s = 1; s <= 5; s++)
            {
                var upcoming = now.AddSeconds(s);
                var key = FormatTime(upcoming);
                if (!frameCache.Contains(key) && !inFlightRenders.Contains(key))
                {
                    SchedulePreRender(upcoming, isImmediate: false);
                }
            }
        }
        else
        {
            // Pre-cache the next minute frame
            var nextMinute = now.AddMinutes(1);
            var key = FormatTime(nextMinute);
            if (!frameCache.Contains(key) && !inFlightRenders.Contains(key))
            {
                SchedulePreRender(nextMinute, isImmediate: false);
            }
        }
    }

    private void SchedulePreRender(DateTime targetTime, bool isImmediate)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        if (!material.IsRenderedGlass) return;

        var key = FormatTime(targetTime);
        if (frameCache.Contains(key) || inFlightRenders.Contains(key)) return;

        var scaling = window?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));

        var theme = appSettingsProvider?.Get().Theme;
        var stretchedGeometry = FramelessGlyphGeometry.BuildStretch(
            key, Bounds.Width, Bounds.Height, model.FontFamily, model.FontWeight, model.StretchFill, theme);
        if (stretchedGeometry == null) return;

        byte[] glyphMask = FramelessGlyphGeometry.ExtractMask(stretchedGeometry, Bounds.Width, Bounds.Height, scaling, width, height);

        // The material is whatever the global theme says — the clock carries no per-widget theme
        // override, and no widget-level optics override either: the edge tint and the lens width
        // both come from the global liquid glass settings. 液态玻璃 therefore renders the current
        // merged optics (with the global 柔光晕 / 光谱弥散 knobs selecting the soft recipe).
        var effectiveTheme = theme ?? FallbackTheme;

        if (model.EnableOverlay)
        {
            var overlay = ResolveOverlayColor(model, effectiveTheme);
            var overlayHex = $"#{overlay.R:X2}{overlay.G:X2}{overlay.B:X2}";
            effectiveTheme = effectiveTheme with { AccentColor = overlayHex };
        }
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var screen = window?.Screens.ScreenFromWindow(window);
        var screenPos = window != null ? this.PointToScreen(default) : default;

        // The sidebar slides its content with a render transform, which PointToScreen folds in: the
        // glyphs would be measured a whole window-width to the right while the panel moves — and, on
        // a static screen, would stay there (nothing republishes to correct them). See
        // SidebarWindow.SlideOffsetDip.
        if (window is DeskCanvas.Views.SidebarWindow sliding)
        {
            var slideOffset = sliding.SlideOffsetDip;
            if (Math.Abs(slideOffset) > 0.01)
                screenPos = new PixelPoint(screenPos.X - (int)Math.Round(slideOffset * scaling), screenPos.Y);
        }

        var screens = window?.Screens.All;
        var left = screens?.Min(s => s.Bounds.X) ?? 0;
        var top = screens?.Min(s => s.Bounds.Y) ?? 0;
        var desktopWidth = (screens?.Max(s => s.Bounds.Right) ?? 1920) - left;
        var desktopHeight = (screens?.Max(s => s.Bounds.Bottom) ?? 1080) - top;

        var widget = window as DeskCanvas.Views.Widget;
        var (cols, rows) = widget?.CurrentSpan ?? (0, 0);

        var frame = new LiquidGlassRenderer.Frame(
            width, height, (float)scaling, 0f,
            screenPos.X - left, screenPos.Y - top,
            desktopWidth, desktopHeight,
            (screen?.Bounds.X ?? 0) - left, (screen?.Bounds.Y ?? 0) - top,
            screen?.Bounds.Width ?? 1920, screen?.Bounds.Height ?? 1080,
            effectiveTheme, isDark,
            Columns: cols, Rows: rows);

        // The one live-sampling pipeline: this monitor's composited screen, or the wallpaper when no
        // frame has been published for it yet (see ScreenCaptureService). Taken *before* the frame is
        // built because it also decides the coordinate space the frame is expressed in — the same
        // thing the card glass does — and its reference is handed to the worker below.
        var live = ScreenCaptureService.TryGetSnapshot(window);
        if (live?.CachedBitmap is { Width: > 0 } captured)
        {
            var bounds = screen?.Bounds ?? new PixelRect(left, top, desktopWidth, desktopHeight);
            frame = frame with
            {
                DesktopX = screenPos.X - bounds.X,
                DesktopY = screenPos.Y - bounds.Y,
                DesktopWidth = bounds.Width,
                DesktopHeight = bounds.Height,
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = bounds.Width,
                ScreenHeight = bounds.Height,
                // Undo the downscale the capturer applied, so a card pixel samples the texel it should.
                PixelScale = (float)(bounds.Width / (double)captured.Width)
            };
        }

        inFlightRenders.Add(key);
        preRenderCts ??= new CancellationTokenSource();
        var token = preRenderCts.Token;

        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested) { live?.Dispose(); return; }
            // The snapshot carries a reference the caller owns; hold it for the whole render.
            using var wallpaper = live ?? LiquidGlassWallpaper.Get();
            if (token.IsCancellationRequested) return;
            // No widget-level lens override: the adaptive lens derived from the global optics is
            // the only path the clock renders with (null == adaptive in ResolveLens).
            var pngBytes = GlyphLiquidGlassRenderer.Render(frame, wallpaper, glyphMask);
            if (token.IsCancellationRequested || pngBytes == null || pngBytes.Length == 0) return;

            Dispatcher.UIThread.Post(() =>
            {
                inFlightRenders.Remove(key);
                if (token.IsCancellationRequested) return;

                try
                {
                    using var ms = new MemoryStream(pngBytes);
                    var bmp = new Bitmap(ms);

                    frameCache.Store(key, targetTime, bmp);

                    var currentNow = GetCurrentTime();
                    if (key == FormatTime(currentNow))
                    {
                        SetDisplayedFrame(bmp);
                        InvalidateVisual();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to cache pre-rendered liquid glass frame: {ex.Message}");
                }

                // The render that was in flight when the wallpaper advanced has finished; catch
                // up with one render against the newer frame (OnWallpaperInvalidated deferred).
                if (inFlightRenders.Count == 0 && wallpaperStale)
                {
                    wallpaperStale = false;
                    RequestBackdropRender();
                }
            });
        }, token).ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                Dispatcher.UIThread.Post(() => inFlightRenders.Remove(key));
            }
        });
    }

    /// <summary>Renderer-side fallback when no settings carry a theme (previews): keeps the
    /// pipeline fed with valid optics instead of null-checking every field downstream.</summary>
    private static readonly Theme FallbackTheme = new(null, null, 0.8, false, false, "Segoe UI");

    private void EvictExpiredCacheEntries(DateTime now)
    {
        // Capacity safeguard. Every entry is a full-window bitmap, so the cap is a memory budget
        // rather than a frame count: 6 frames cover the current minute plus the rolling lookahead,
        // and the hard ceiling keeps a full-screen frameless clock from parking hundreds of
        // megabytes of pre-rendered frames (the old flat cap of 70 allowed ~580 MB at 1080p).
        var scaling = window?.RenderScaling ?? 1.0;
        var frameBytes = Math.Max(1L, (long)(Math.Ceiling(Bounds.Width * scaling) * Math.Ceiling(Bounds.Height * scaling) * 4));
        var maxFrames = (int)Math.Clamp(CacheBudgetBytes / frameBytes, 6, 12);

        frameCache.EvictExpired(now, model.ShowSeconds ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(2), maxFrames);
    }

    private void ClearLiquidGlassCache()
    {
        preRenderCts?.Cancel();
        preRenderCts?.Dispose();
        preRenderCts = new CancellationTokenSource();
        inFlightRenders.Clear();

        frameCache.Clear();
    }

    /// <summary>
    /// Swap the frame composited on screen. The old bitmap is disposed only if the cache no
    /// longer holds it — it may be stored under another key (the lookahead) and must survive.
    /// </summary>
    private void SetDisplayedFrame(Bitmap? value)
    {
        if (ReferenceEquals(liquidGlassBitmap, value)) return;
        if (liquidGlassBitmap != null && !frameCache.Holds(liquidGlassBitmap))
        {
            liquidGlassBitmap.Dispose();
        }
        liquidGlassBitmap = value;
        frameCache.SetDisplayed(value);
    }

    private string FormatTime(DateTime dt)
    {
        var hh = model.Use24Hours ? "HH" : "hh";
        var ss = model.ShowSeconds ? ":ss" : "";
        return dt.ToString($"{hh}:mm{ss}", CultureInfo.InvariantCulture);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var targetW = Bounds.Width;
        var targetH = Bounds.Height;
        if (targetW <= 1 || targetH <= 1) return;

        var now = GetCurrentTime();
        var timeStr = FormatTime(now);

        var theme = appSettingsProvider?.Get().Theme;
        var stretchedGeometry = FramelessGlyphGeometry.BuildStretch(
            timeStr, targetW, targetH, model.FontFamily, model.FontWeight, model.StretchFill, theme);
        if (stretchedGeometry == null) return;

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var isAcrylic = material.IsAcrylic;
        var isLiquidGlass = material.IsRenderedGlass;

        // Theme 1: OS-level Acrylic (Real-time hardware DWM blur behind the glyphs)
        if (isAcrylic)
        {
            if (IsDesktopWidget)
            {
                // Material + variant are part of the key so a theme switch can never leave a
                // glyph region shaped for the previous material on the window.
                var regionKey = $"{timeStr}_{targetW}_{targetH}_{model.FontFamily}_{model.FontWeight}_{model.StretchFill}_{isDark}_{isAcrylic}_{isLiquidGlass}";
                if (regionKey != lastRegionKey || !hasRegionSet)
                {
                    lastRegionKey = regionKey;
                    UpdateWindowRegion(stretchedGeometry, targetW, targetH);
                }
            }

            // A sidebar host cannot shape this widget's window region — the sidebar's window is
            // shared by every card in the strip, so no single widget may own it. The host instead
            // unions the glyph spans published here into its own region (see IFramelessGlassMask),
            // which is what keeps the frost on the numerals instead of on the whole grid cell.
            AnnounceGlassMask();

            using (context.PushGeometryClip(stretchedGeometry))
            {
                // Acrylic surface wash: only a preview gets the stronger wash (it has to read at
                // 45×45 against a placeholder scene). A real surface uses the desktop alpha — that
                // is what makes the sidebar's clock read as glass rather than as a plain
                // translucent plate.
                var alpha = IsPreviewSurface ? (isDark ? 160 : 180) : (isDark ? 70 : 48);
                var tintWash = isDark
                    ? Color.FromArgb((byte)alpha, 60, 60, 60)
                    : Color.FromArgb((byte)alpha, 240, 240, 240);
                context.DrawRectangle(new SolidColorBrush(tintWash), null, new Rect(0, 0, targetW, targetH));

                if (model.EnableOverlay)
                {
                    var overlayColor = ResolveOverlayColor(model, theme);
                    context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
                }
            }

            DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, theme);
            return;
        }

        // Liquid glass and solid render through clean 32-bit alpha — no native region, ever.
        ClearGlyphRegionIfNotAcrylic();

        // Theme 2: Optical Liquid Glass (Per-pixel raymarched refraction inside numerals)
        if (isLiquidGlass)
        {
            if (frameCache.TryGet(timeStr, out var cached))
            {
                SetDisplayedFrame(cached);
            }

            if (liquidGlassBitmap != null)
            {
                context.DrawImage(liquidGlassBitmap, new Rect(0, 0, targetW, targetH));
            }
            else
            {
                // High-clarity fallback while liquid glass is raymarching
                using (context.PushGeometryClip(stretchedGeometry))
                {
                    var fallbackHex = isDark ? "#282828" : "#F0F0F0";
                    var c = Color.TryParse(fallbackHex, out var parsed) ? parsed : Colors.Gray;
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B)), null, new Rect(0, 0, targetW, targetH));
                }
                SchedulePreRender(now, isImmediate: true);
                DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, theme);
            }

            if (model.EnableOverlay)
            {
                using (context.PushGeometryClip(stretchedGeometry))
                {
                    var overlayColor = ResolveOverlayColor(model, theme);
                    context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
                }
            }

            return;
        }

        // Theme 3: Solid (Pure vector solid color fill with transparency and overlay support)
        using (context.PushGeometryClip(stretchedGeometry))
        {
            if (model.EnableOverlay)
            {
                var overlayColor = ResolveOverlayColor(model, theme);
                context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
            }
            else
            {
                var solidHex = isDark
                    ? (theme?.EffectiveSolidBackgroundDark ?? "#2E2E2E")
                    : (theme?.EffectiveSolidBackgroundLight ?? "#FFFFFF");
                var baseSolid = Color.TryParse(solidHex, out var parsed) ? parsed : (isDark ? Colors.Black : Colors.White);
                var opacity = theme != null ? (float)Math.Clamp(theme.OpacityLevel, 0.05, 1.0) : 0.9f;
                var brushColor = Color.FromArgb((byte)(opacity * 255), baseSolid.R, baseSolid.G, baseSolid.B);
                context.DrawRectangle(new SolidColorBrush(brushColor), null, new Rect(0, 0, targetW, targetH));
            }
        }
    }

    // ---------- Glass mask for a sidebar host (IFramelessGlassMask) ----------

    /// <summary>
    /// True when this control is a preview (the gallery's 45×45 miniature) rather than a real
    /// surface. A preview renders against a fixed placeholder scene, so its wash is strengthened to
    /// make the recipe readable at that size; a real surface — a desktop widget or a sidebar card —
    /// must use the desktop alpha, which is what makes the sidebar's clock read as glass instead of
    /// a plain translucent plate.
    /// </summary>
    private bool IsPreviewSurface => window is not DeskCanvas.Views.Widget and not DeskCanvas.Views.SidebarWindow;

    /// <summary>
    /// Tell the one sampling pipeline that this window needs a live frame. Only 液态玻璃 reads one:
    /// it raytraces the glyphs against the captured screen. 毛玻璃 must <b>not</b> register — its
    /// frost is the OS backdrop, not a capture — and a registration there would run the whole
    /// sampling loop for a material that never looks at a frame. The window is passed through either
    /// way: a removal is identified by the window, so nulling it here would leave the registration
    /// behind.
    /// </summary>
    private void UpdateCaptureDemand(bool wanted = true) =>
        ScreenCaptureService.SetDirectConsumer(window, wanted && material.IsRenderedGlass);

    /// <inheritdoc />
    public event Action? GlassMaskChanged;

    /// <inheritdoc />
    public string GlassMaskKey =>
        $"{FormatTime(GetCurrentTime())}|{Bounds.Width:0.##}x{Bounds.Height:0.##}"
        + $"|{model.FontFamily}|{model.FontWeight}|{model.StretchFill}|{ActualThemeVariant}";

    /// <inheritdoc />
    public IReadOnlyList<(int Left, int Top, int Right, int Bottom)> GetGlassSpans(double scaling)
    {
        var pixelW = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var pixelH = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));
        var key = $"{GlassMaskKey}|{pixelW}x{pixelH}";
        if (key == maskCacheKey) return maskSpans;

        maskSpans = BuildGlassSpans(pixelW, pixelH, scaling);
        maskCacheKey = key;
        return maskSpans;
    }

    /// <summary>
    /// Tell a sidebar host that the shape moved on (a new digit, a resize, a font change). Cheap: the
    /// signature is compared against the last one announced, and a host that does not care never
    /// subscribes.
    /// </summary>
    private void AnnounceGlassMask()
    {
        var key = GlassMaskKey;
        if (key == lastAnnouncedMask) return;
        lastAnnouncedMask = key;
        try { GlassMaskChanged?.Invoke(); }
        catch (Exception ex) { Debug.WriteLine($"Failed to announce the glass mask: {ex.Message}"); }
    }

    /// <summary>
    /// Rasterise the current glyphs and read the covered spans back out. The same scanline-span
    /// extraction the desktop region uses — a GDI region is built from spans, and a sidebar host
    /// unions the very same spans into its own region.
    /// </summary>
    private List<(int Left, int Top, int Right, int Bottom)> BuildGlassSpans(int pixelW, int pixelH, double scaling)
    {
        var stretchedGeometry = FramelessGlyphGeometry.BuildStretch(
            FormatTime(GetCurrentTime()), Bounds.Width, Bounds.Height,
            model.FontFamily, model.FontWeight, model.StretchFill, appSettingsProvider?.Get().Theme);
        if (stretchedGeometry == null) return [];

        var size = new PixelSize(pixelW, pixelH);
        var rtb = maskBitmap;
        if (rtb == null || rtb.PixelSize != size)
        {
            rtb?.Dispose();
            rtb = maskBitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        }

        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawGeometry(Brushes.Black, null, stretchedGeometry);
        }

        var needed = pixelW * pixelH * 4;
        var buffer = maskBuffer;
        if (buffer == null || buffer.Length < needed) buffer = maskBuffer = new byte[needed];

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), handle.AddrOfPinnedObject(), needed, pixelW * 4);
            return ExtractSpans(buffer, pixelW, pixelH);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to build the glass mask spans: {ex.Message}");
            return [];
        }
        finally
        {
            handle.Free();
        }
    }

    private void UpdateWindowRegion(Geometry geometry, double width, double height)
    {
        if (window == null || !IsDesktopWidget) return;
        var scaling = window.RenderScaling;
        var pixelW = Math.Max(1, (int)Math.Ceiling(width * scaling));
        var pixelH = Math.Max(1, (int)Math.Ceiling(height * scaling));

        // Both buffers are reused across calls: with ShowSeconds this runs once per second and a
        // fresh RenderTargetBitmap plus a fresh full-window byte[] (2.5-8 MB, straight onto the
        // LOH) per call kept gen2 collections running all day.
        var size = new PixelSize(pixelW, pixelH);
        var rtb = regionBitmap;
        if (rtb == null || rtb.PixelSize != size)
        {
            rtb?.Dispose();
            rtb = regionBitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        }

        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawGeometry(Brushes.Black, null, geometry);
        }

        var bytesNeeded = pixelW * pixelH * 4;
        var buffer = regionBuffer;
        if (buffer == null || buffer.Length < bytesNeeded)
        {
            buffer = regionBuffer = new byte[bytesNeeded];
        }

        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), handle.AddrOfPinnedObject(), bytesNeeded, pixelW * 4);
            var spans = ExtractSpans(buffer, pixelW, pixelH);
            InteropService.SetWindowRegionFromSpans(window, spans);
            hasRegionSet = spans.Count > 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set window region from spans: {ex.Message}");
        }
        finally
        {
            handle.Free();
        }
    }

    private static List<(int Left, int Top, int Right, int Bottom)> ExtractSpans(byte[] bgra, int width, int height)
    {
        var spans = new List<(int Left, int Top, int Right, int Bottom)>();
        var stride = width * 4;

        for (int y = 0; y < height; y++)
        {
            int rowStart = y * stride;
            int? spanStart = null;
            for (int x = 0; x < width; x++)
            {
                byte a = bgra[rowStart + x * 4 + 3];
                if (a > 60)
                {
                    if (!spanStart.HasValue) spanStart = x;
                }
                else
                {
                    if (spanStart.HasValue)
                    {
                        spans.Add((spanStart.Value, y, x, y + 1));
                        spanStart = null;
                    }
                }
            }
            if (spanStart.HasValue)
            {
                spans.Add((spanStart.Value, y, width, y + 1));
            }
        }
        return spans;
    }

    private static void DrawSpecularRim(DrawingContext context, Geometry geometry, double targetW, double targetH, bool isDark, FramelessClockModel model, Theme? theme)
    {
        // The rim dye strength follows the global 边缘染色强度 (LiquidGlassSettings.EdgeTint): the
        // clock has no widget-level 染色强度 override any more, so the global optics are the only
        // source for how strongly the accent / overlay colour bleeds into the specular rim.
        var edgeTint = (float)Math.Clamp(
            (theme?.EffectiveLiquidGlass.EdgeTint ?? LiquidGlassSettings.DefaultEdgeTint) / 100.0, 0.0, 1.0);

        Color dye = isDark ? Color.FromRgb(200, 220, 245) : Color.FromRgb(240, 240, 245);
        bool hasDye = false;

        // 1. If model overlay is active, dye with overlay color
        if (model.EnableOverlay)
        {
            var oc = ResolveOverlayColor(model, theme);
            dye = Color.FromRgb(oc.R, oc.G, oc.B);
            hasDye = true;
        }
        // 2. Otherwise if Theme Accent is configured, dye with accent
        else if (!string.IsNullOrEmpty(theme?.AccentColor) && Color.TryParse(theme.AccentColor, out var accent))
        {
            dye = accent;
            hasDye = true;
        }

        var tintFactor = hasDye ? edgeTint : 0f;

        static Color Blend(Color baseC, Color tintC, float weight)
        {
            byte r = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.R + weight * tintC.R), 0, 255);
            byte g = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.G + weight * tintC.G), 0, 255);
            byte b = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.B + weight * tintC.B), 0, 255);
            return Color.FromArgb(baseC.A, r, g, b);
        }

        // Luminous dyed specular glass rim stops
        var stop0Base = isDark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(220, 255, 255, 255);
        var stop1Base = isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(50, 255, 255, 255);
        var stop2Base = isDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(160, 255, 255, 255);

        var stop0 = Blend(stop0Base, dye, 0.45f * tintFactor);
        var stop1 = Blend(stop1Base, dye, 0.85f * tintFactor);
        var stop2 = Blend(stop2Base, dye, 0.35f * tintFactor);

        var rimBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(stop0, 0.0),
                new GradientStop(stop1, 0.45),
                new GradientStop(stop2, 1.0)
            }
        };

        var rimThickness = Math.Clamp(Math.Min(targetW, targetH) * 0.009, 0.8, 2.2);
        var rimPen = new Pen(rimBrush, rimThickness);
        context.DrawGeometry(null, rimPen, geometry);
    }

    private DateTime GetCurrentTime()
    {
        var now = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(model.TimeZoneId))
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(model.TimeZoneId);
                return TimeZoneInfo.ConvertTimeFromUtc(now, tz);
            }
            catch
            {
                return DateTime.Now;
            }
        }
        return DateTime.Now;
    }

    /// <summary>Last-resort overlay colour, used only when neither the accent nor a custom hex resolves.</summary>
    private static readonly Color DefaultOverlayColor = Color.FromRgb(0, 120, 215);

    /// <summary>
    /// The overlay wash colour, already carrying the model's opacity. A static member on purpose:
    /// <c>tests/ClockThemeChecks</c> drives it by reflection to pin the accent-following contract.
    /// </summary>
    private static Color ResolveOverlayColor(FramelessClockModel model, Theme? theme)
    {
        var baseColor = model.FollowAccentColor
            ? ResolveAccentColor(theme) ?? DefaultOverlayColor
            : ParseHex(model.OverlayColor) ?? DefaultOverlayColor;

        var opacity = Math.Clamp(model.OverlayOpacity, 0.0, 1.0);
        return Color.FromArgb((byte)(opacity * 255), baseColor.R, baseColor.G, baseColor.B);
    }

    /// <summary>
    /// The accent colour actually in effect, which is a two-step resolution:
    /// <list type="number">
    /// <item>an explicitly picked accent (<see cref="Theme.AccentColor"/>) wins;</item>
    /// <item>跟随系统强调色 — the default, where <c>AccentColor</c> is <c>null</c> — falls through to
    /// the <c>SystemAccentColor</c> resource. <c>ThemeService.ApplyAccent</c> writes a hand-picked
    /// accent there, and Avalonia's Fluent theme seeds the same key (plus its shade ramp) from the
    /// OS accent when the app leaves the choice to the system.</item>
    /// </list>
    /// Reading only <see cref="Theme.AccentColor"/> made the overlay a hard-coded Windows blue for
    /// every user who left the accent on 跟随系统, whatever their actual accent was. This is the same
    /// lookup the other accent-driven widgets use (<c>Calendar/Views/Month.axaml.cs</c>,
    /// <c>Notes/Views/Note.axaml.cs</c>, <c>Fixed/ViewModels/AggregateViewModel</c>).
    /// </summary>
    private static Color? ResolveAccentColor(Theme? theme)
    {
        if (ParseHex(theme?.AccentColor) is { } picked)
            return picked;

        if (Application.Current is { } app &&
            app.TryFindResource("SystemAccentColor", out var value) &&
            value is Color systemAccent)
            return systemAccent;

        return null;
    }

    /// <summary>Parse a <c>#hex</c> colour, or <c>null</c> when absent or malformed.</summary>
    private static Color? ParseHex(string? hex) =>
        !string.IsNullOrEmpty(hex) && Color.TryParse(hex, out var parsed) ? parsed : null;
}
