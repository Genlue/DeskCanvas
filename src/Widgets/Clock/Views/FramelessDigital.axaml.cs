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
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Core.Models.Settings;
using uWidgets.Services;

namespace Clock.Views;

public partial class FramelessDigital : UserControl, IFramelessWidget, IWidgetSelfRefreshing, IWidgetSuspendable
{
    private FramelessClockModel model;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    private UpdateTimer? currentTimer;
    private Window? window;
    private bool IsDesktopWidget => window is uWidgets.Views.Widget;
    private Bitmap? liquidGlassBitmap;

    // Per-second Acrylic region recompute buffers (see UpdateWindowRegion).
    private RenderTargetBitmap? regionBitmap;
    private byte[]? regionBuffer;

    // Cache of pre-rendered liquid glass frames keyed by time string
    private readonly Dictionary<string, (DateTime ValidTime, Bitmap Bitmap)> liquidGlassCache = new();
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

    private Geometry? cachedGeometry;
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
        liquidGlassBitmap?.Dispose();
        liquidGlassBitmap = null;

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

        RequestBackdropRender();
        InvalidateVisual();
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
        lastRegionKey = null;
        ClearLiquidGlassCache();
        RequestBackdropRender();
        InvalidateVisual();
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
        cachedGeometry = null;

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
        lastRegionKey = null;
        SetupTimer();
        ApplyCurrentMaterial();
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!IsDesktopWidget) return;
        var (_, isLiquidGlass, _) = ResolveEffectiveTheme();
        if (isLiquidGlass)
        {
            ClearLiquidGlassCache();
            RequestBackdropRender();
        }
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
                    lastRegionKey = null;
                    SetupTimer();
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
    /// The clock's material, resolved from the <b>global</b> theme (see
    /// <see cref="FramelessThemeResolver"/>): the widget has no per-widget theme override, so the
    /// global 液态玻璃 / 柔光 recipe reaches the numerals through the glyph glass pipeline, a global
    /// 毛玻璃 gives the OS acrylic backdrop and a global 纯色 a plain fill.
    /// </summary>
    private FramelessMaterial CurrentMaterial => FramelessThemeResolver.Resolve(appSettingsProvider?.Get().Theme);

    private (bool IsAcrylic, bool IsLiquidGlass, bool IsSolid) ResolveEffectiveTheme()
    {
        var material = CurrentMaterial;
        return (material.IsAcrylic, material.IsRenderedGlass, material.IsSolid);
    }

    /// <summary>
    /// The ONE path that (re)materializes the clock. Every invalidating event — window load,
    /// a global theme change, a light/dark variant change, a model refresh, suspend/resume —
    /// funnels through here, so the side effects (glyph cache, window transparency, glyph
    /// window region, backdrop re-render) can never drift out of sync with the resolved
    /// material again. This used to be re-implemented per handler with hand-picked subsets
    /// of the four steps, which is exactly how "the frameless clock stopped following the
    /// global theme" kept regressing: one missed step in one handler left the widget stuck
    /// on the previous material.
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
        lastRegionKey = null;
        ClearLiquidGlassCache();

        UpdateTransparencyLevel();

        if (window != null && IsDesktopWidget && !CurrentMaterial.IsAcrylic && hasRegionSet)
        {
            InteropService.ClearWidgetRegion(window);
            hasRegionSet = false;
        }

        RequestBackdropRender();
        InvalidateVisual();
    }

    // Cached hint arrays: UpdateTransparencyLevel runs on every wallpaper invalidation (every
    // live-sampling round), and re-assigning the hint makes the Win32 impl re-apply the window
    // transparency each time — a per-round window-attribute poke that used to come with a fresh
    // array literal whose reference never compared equal to the previous one.
    private static readonly WindowTransparencyLevel[] AcrylicHint = [WindowTransparencyLevel.AcrylicBlur];
    private static readonly WindowTransparencyLevel[] TransparentHint = [WindowTransparencyLevel.Transparent];

    private void UpdateTransparencyLevel()
    {
        if (window == null || !IsDesktopWidget) return;
        var (isAcrylic, _, _) = ResolveEffectiveTheme();
        var hint = isAcrylic ? AcrylicHint : TransparentHint;
        if (!hint.SequenceEqual(window.TransparencyLevelHint))
            window.TransparencyLevelHint = hint;
    }

    private void OnTimerTick()
    {
        var (_, isLiquidGlass, _) = ResolveEffectiveTheme();
        if (isLiquidGlass)
        {
            var now = GetCurrentTime();
            var timeStr = FormatTime(now);

            // 1. Automatic Cleanup: Evict expired cache entries before current time
            EvictExpiredCacheEntries(now);

            // 2. Check if current frame was already pre-cached in background
            if (liquidGlassCache.TryGetValue(timeStr, out var cached))
            {
                if (liquidGlassBitmap != cached.Bitmap)
                {
                    if (liquidGlassBitmap != null && !IsBitmapInCache(liquidGlassBitmap))
                    {
                        liquidGlassBitmap.Dispose();
                    }
                    liquidGlassBitmap = cached.Bitmap;
                }
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
        var (_, isLiquidGlass, _) = ResolveEffectiveTheme();
        if (!isLiquidGlass)
        {
            liquidGlassBitmap?.Dispose();
            liquidGlassBitmap = null;
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
                if (!liquidGlassCache.ContainsKey(key) && !inFlightRenders.Contains(key))
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
            if (!liquidGlassCache.ContainsKey(key) && !inFlightRenders.Contains(key))
            {
                SchedulePreRender(nextMinute, isImmediate: false);
            }
        }
    }

    private void SchedulePreRender(DateTime targetTime, bool isImmediate)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        var (_, isLiquidGlass, _) = ResolveEffectiveTheme();
        if (!isLiquidGlass) return;

        var key = FormatTime(targetTime);
        if (liquidGlassCache.ContainsKey(key) || inFlightRenders.Contains(key)) return;

        var scaling = window?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));

        var theme = appSettingsProvider?.Get().Theme;
        var fontFamily = ResolveFontFamily(model.FontFamily ?? theme?.FontFamily);
        var weightVal = Math.Clamp(model.FontWeight, 100, 900);
        var fontWeight = (FontWeight)weightVal;
        var typeface = new Typeface(fontFamily, FontStyle.Normal, fontWeight);

        var formattedText = new FormattedText(
            key,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            100.0,
            Brushes.Black);

        var rawGeometry = formattedText.BuildGeometry(new Point(0, 0));
        if (rawGeometry == null) return;
        var tight = rawGeometry.Bounds;
        if (tight.Width <= 0 || tight.Height <= 0) return;

        Matrix matrix;
        if (model.StretchFill)
        {
            var sx = Bounds.Width / tight.Width;
            var sy = Bounds.Height / tight.Height;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y) * Matrix.CreateScale(sx, sy);
        }
        else
        {
            var scale = Math.Min(Bounds.Width / tight.Width, Bounds.Height / tight.Height);
            var actualW = tight.Width * scale;
            var actualH = tight.Height * scale;
            var ox = (Bounds.Width - actualW) / 2.0;
            var oy = (Bounds.Height - actualH) / 2.0;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y)
                   * Matrix.CreateScale(scale, scale)
                   * Matrix.CreateTranslation(ox, oy);
        }

        var stretchedGeometry = rawGeometry.Clone();
        stretchedGeometry.Transform = new MatrixTransform(matrix);

        byte[] glyphMask = ExtractGlyphMask(stretchedGeometry, Bounds.Width, Bounds.Height, scaling, width, height);

        // The material is whatever the global theme says — the clock carries no per-widget theme
        // override, and no widget-level optics override either: the edge tint and the lens width
        // both come from the global liquid glass settings. 液态玻璃 therefore renders the current
        // merged optics (with the global 柔光晕 / 光谱弥散 knobs selecting the soft recipe).
        var effectiveTheme = theme ?? new Theme(null, null, 0.8, false, false, "Segoe UI");

        if (model.EnableOverlay)
        {
            var overlay = ResolveOverlayColor(model, effectiveTheme);
            var overlayHex = $"#{overlay.R:X2}{overlay.G:X2}{overlay.B:X2}";
            effectiveTheme = effectiveTheme with { AccentColor = overlayHex };
        }
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var screen = window?.Screens.ScreenFromWindow(window);
        var screenPos = window != null ? this.PointToScreen(default) : default;

        var screens = window?.Screens.All;
        var left = screens?.Min(s => s.Bounds.X) ?? 0;
        var top = screens?.Min(s => s.Bounds.Y) ?? 0;
        var desktopWidth = (screens?.Max(s => s.Bounds.Right) ?? 1920) - left;
        var desktopHeight = (screens?.Max(s => s.Bounds.Bottom) ?? 1080) - top;

        var widget = window as uWidgets.Views.Widget;
        var (cols, rows) = widget?.CurrentSpan ?? (0, 0);

        var frame = new LiquidGlassRenderer.Frame(
            width, height, (float)scaling, 0f,
            screenPos.X - left, screenPos.Y - top,
            desktopWidth, desktopHeight,
            (screen?.Bounds.X ?? 0) - left, (screen?.Bounds.Y ?? 0) - top,
            screen?.Bounds.Width ?? 1920, screen?.Bounds.Height ?? 1080,
            effectiveTheme, isDark,
            Columns: cols, Rows: rows);

        inFlightRenders.Add(key);
        preRenderCts ??= new CancellationTokenSource();
        var token = preRenderCts.Token;

        _ = Task.Run(() =>
        {
            if (token.IsCancellationRequested) return;
            // The snapshot carries a reference the caller owns; hold it for the whole render.
            using var wallpaper = LiquidGlassWallpaper.Get();
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

                    if (liquidGlassCache.TryGetValue(key, out var existing))
                    {
                        if (existing.Bitmap != liquidGlassBitmap)
                            existing.Bitmap.Dispose();
                    }
                    liquidGlassCache[key] = (targetTime, bmp);

                    var currentNow = GetCurrentTime();
                    if (key == FormatTime(currentNow))
                    {
                        if (liquidGlassBitmap != null && !IsBitmapInCache(liquidGlassBitmap))
                        {
                            liquidGlassBitmap.Dispose();
                        }
                        liquidGlassBitmap = bmp;
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

    private void EvictExpiredCacheEntries(DateTime now)
    {
        var expiredKeys = new List<string>();
        foreach (var (key, (validTime, _)) in liquidGlassCache)
        {
            bool isExpired = model.ShowSeconds
                ? validTime < now.AddSeconds(-2)
                : validTime < now.AddMinutes(-2);

            if (isExpired)
            {
                expiredKeys.Add(key);
            }
        }

        foreach (var key in expiredKeys)
        {
            if (liquidGlassCache.Remove(key, out var entry))
            {
                if (entry.Bitmap != liquidGlassBitmap)
                {
                    entry.Bitmap.Dispose();
                }
            }
        }

        // Capacity safeguard. Every entry is a full-window bitmap, so the cap is a memory budget
        // rather than a frame count: 6 frames cover the current minute plus the rolling lookahead,
        // and the hard ceiling keeps a full-screen frameless clock from parking hundreds of
        // megabytes of pre-rendered frames (the old flat cap of 70 allowed ~580 MB at 1080p).
        var scaling = window?.RenderScaling ?? 1.0;
        var frameBytes = Math.Max(1L, (long)(Math.Ceiling(Bounds.Width * scaling) * Math.Ceiling(Bounds.Height * scaling) * 4));
        var maxFrames = Math.Clamp(CacheBudgetBytes / frameBytes, 6, 12);

        while (liquidGlassCache.Count > maxFrames)
        {
            var oldest = liquidGlassCache.OrderBy(kv => kv.Value.ValidTime).FirstOrDefault();
            if (oldest.Key != null && liquidGlassCache.Remove(oldest.Key, out var entry))
            {
                if (entry.Bitmap != liquidGlassBitmap)
                {
                    entry.Bitmap.Dispose();
                }
            }
            else break;
        }
    }

    private void ClearLiquidGlassCache()
    {
        preRenderCts?.Cancel();
        preRenderCts?.Dispose();
        preRenderCts = new CancellationTokenSource();
        inFlightRenders.Clear();

        foreach (var entry in liquidGlassCache.Values)
        {
            if (entry.Bitmap != liquidGlassBitmap)
            {
                entry.Bitmap.Dispose();
            }
        }
        liquidGlassCache.Clear();
    }

    private bool IsBitmapInCache(Bitmap bitmap)
    {
        foreach (var entry in liquidGlassCache.Values)
        {
            if (entry.Bitmap == bitmap) return true;
        }
        return false;
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
        var hh = model.Use24Hours ? "HH" : "hh";
        var ss = model.ShowSeconds ? ":ss" : "";
        var timeStr = now.ToString($"{hh}:mm{ss}", CultureInfo.InvariantCulture);

        var theme = appSettingsProvider?.Get().Theme;
        var fontFamily = ResolveFontFamily(model.FontFamily ?? theme?.FontFamily);
        var weightVal = Math.Clamp(model.FontWeight, 100, 900);
        var fontWeight = (FontWeight)weightVal;
        var typeface = new Typeface(fontFamily, FontStyle.Normal, fontWeight);

        var formattedText = new FormattedText(
            timeStr,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            100.0,
            Brushes.Black);

        var rawGeometry = formattedText.BuildGeometry(new Point(0, 0));
        if (rawGeometry == null) return;

        var tight = rawGeometry.Bounds;
        if (tight.Width <= 0 || tight.Height <= 0) return;

        Matrix matrix;
        if (model.StretchFill)
        {
            var sx = targetW / tight.Width;
            var sy = targetH / tight.Height;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y) * Matrix.CreateScale(sx, sy);
        }
        else
        {
            var scale = Math.Min(targetW / tight.Width, targetH / tight.Height);
            var actualW = tight.Width * scale;
            var actualH = tight.Height * scale;
            var ox = (targetW - actualW) / 2.0;
            var oy = (targetH - actualH) / 2.0;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y)
                   * Matrix.CreateScale(scale, scale)
                   * Matrix.CreateTranslation(ox, oy);
        }

        var stretchedGeometry = rawGeometry.Clone();
        stretchedGeometry.Transform = new MatrixTransform(matrix);
        cachedGeometry = stretchedGeometry;

        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        var (isAcrylic, isLiquidGlass, _) = ResolveEffectiveTheme();

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

            using (context.PushGeometryClip(stretchedGeometry))
            {
                // Acrylic surface wash: in preview, provide higher opacity so it looks frosted in the gallery card
                var alpha = IsDesktopWidget ? (isDark ? 70 : 48) : (isDark ? 160 : 180);
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

        // Ensure window region is cleared for Liquid Glass and Solid themes (clean 32-bit alpha)
        if (hasRegionSet && window != null && IsDesktopWidget)
        {
            InteropService.ClearWidgetRegion(window);
            hasRegionSet = false;
        }

        // Theme 2: Optical Liquid Glass (Per-pixel raymarched refraction inside numerals)
        if (isLiquidGlass)
        {
            if (liquidGlassCache.TryGetValue(timeStr, out var cached))
            {
                if (liquidGlassBitmap != cached.Bitmap)
                {
                    if (liquidGlassBitmap != null && !IsBitmapInCache(liquidGlassBitmap))
                    {
                        liquidGlassBitmap.Dispose();
                    }
                    liquidGlassBitmap = cached.Bitmap;
                }
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

    private static byte[] ExtractGlyphMask(Geometry? geometry, double width, double height, double scaling, int pixelW, int pixelH)
    {
        var mask = new byte[pixelW * pixelH];
        if (geometry == null || pixelW <= 0 || pixelH <= 0) return mask;

        using var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96 * scaling, 96 * scaling));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawGeometry(Brushes.Black, null, geometry);
        }

        var buffer = new byte[pixelW * pixelH * 4];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), handle.AddrOfPinnedObject(), buffer.Length, pixelW * 4);
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = buffer[i * 4 + 3];
            }
        }
        finally
        {
            handle.Free();
        }
        return mask;
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

    private static FontFamily ResolveFontFamily(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return new FontFamily("Segoe UI");

        if (fontName.Equals("HarmonyOS Sans Condensed", StringComparison.OrdinalIgnoreCase))
        {
            return new FontFamily("avares://Clock/Assets/Fonts#HarmonyOS Sans Condensed, HarmonyOS Sans Condensed, Segoe UI");
        }

        return new FontFamily(fontName);
    }

    /// <summary>Last-resort overlay colour, used only when neither the accent nor a custom hex resolves.</summary>
    private static readonly Color DefaultOverlayColor = Color.FromRgb(0, 120, 215);

    /// <summary>The overlay wash colour, already carrying the model's opacity.</summary>
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
