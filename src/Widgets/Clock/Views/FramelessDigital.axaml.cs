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
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;

namespace Clock.Views;

/// <summary>
/// <summary>无边框时钟：数字直接绘制在桌面上，材质由 <see cref="ApplyCurrentMaterial"/> 统一解析。</summary>
public partial class FramelessDigital : UserControl, IFramelessWidget, IWidgetSelfRefreshing, IWidgetSuspendable, IFocusThemedContent, IWidgetActivityConsumer
{
    private FramelessClockModel model;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly IWidgetSession? session;

    /// <summary>The resolved material, snapshotted by <see cref="ApplyCurrentMaterial"/>.</summary>
    private FramelessMaterial material;

    private UpdateTimer? currentTimer;
    private Window? window;
    private bool IsDesktopWidget => window is DeskCanvas.Views.Widget;

    // The frame currently composited on screen, plus the pre-rendered frames around it.
    // The field name is part of the widget's checked contract (tests/ClockThemeChecks reads it).
    private Bitmap? liquidGlassBitmap;
    private readonly FramelessGlassFrameCache frameCache = new();

    // Per-tick window region recompute buffers (see UpdateWindowRegion).
    private RenderTargetBitmap? regionBitmap;
    private byte[]? regionBuffer;

    private readonly HashSet<string> inFlightRenders = new();
    private CancellationTokenSource? preRenderCts;
    private bool wallpaperStale;

    /// <summary>Renders currently running on the pool across all frameless clock instances.</summary>
    private static int inFlightRenderCount;

    /// <summary>Concurrent pre-render ceiling, aligned with the glyph renderer's 2-slot pool.</summary>
    private const int MaxConcurrentPreRenders = 2;

    private const long CacheBudgetBytes = 24L * 1024 * 1024;

    private string? lastRegionKey;
    private bool hasRegionSet;
    private bool suspended;

    // Tick gating (the Digital pilot's reconciliation, mirrored on the view because the
    // frameless clock keeps its tick logic in code-behind): the shared timer subscription
    // exists exactly while the control is loaded AND the session renders. Session-less
    // instances keep the historic always-on behaviour.
    private bool isRunning = true;
    private bool renderActive = true;
    private bool isSubscribed;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: whether the shared timer subscription is currently live.</summary>
    public bool IsTickingForChecks => isSubscribed;

    private static readonly Theme FallbackTheme = new(
        DarkMode: null,
        AccentColor: null,
        OpacityLevel: 0.18,
        Monochrome: false,
        UseNativeFrame: false,
        FontFamily: "Segoe UI",
        Surface: SurfaceStyle.LiquidGlass,
        LiquidGlass: new LiquidGlassSettings());

    public FramelessDigital() : this(new FramelessClockModel(), null, null) { }

    public FramelessDigital(FramelessClockModel model) : this(model, null, null) { }

    public FramelessDigital(IWidgetLayoutProvider widgetLayoutProvider)
        : this(widgetLayoutProvider.Get().GetModel<FramelessClockModel>() ?? new FramelessClockModel(), widgetLayoutProvider, null) { }

    public FramelessDigital(IWidgetLayoutProvider widgetLayoutProvider, IAppSettingsProvider appSettingsProvider)
        : this(widgetLayoutProvider.Get().GetModel<FramelessClockModel>() ?? new FramelessClockModel(), widgetLayoutProvider, appSettingsProvider) { }

    public FramelessDigital(FramelessClockModel model, IWidgetLayoutProvider? widgetLayoutProvider)
        : this(model, widgetLayoutProvider, null) { }

    public FramelessDigital(FramelessClockModel model, IWidgetLayoutProvider? widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider, IWidgetSession? session = null)
    {
        this.model = model;
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;

        material = FramelessThemeResolver.Resolve(this.model, appSettingsProvider?.Get().Theme);

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
        isRunning = true;
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

        isRunning = false;
        currentTimer?.Unsubscribe(OnTimerTick);
        currentTimer = null;
        isSubscribed = false;

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

        if (!material.IsRenderedGlass)
        {
            InvalidateVisual();
            return;
        }

        if (inFlightRenders.Count > 0)
        {
            wallpaperStale = true;
            return;
        }

        wallpaperStale = false;
        frameCache.Clear();
        SchedulePreRender(GetCurrentTime(), isImmediate: true);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnActualThemeVariantChanged(sender, e));
            return;
        }

        ApplyCurrentMaterial();
    }

    private void OnSizeChanged()
    {
        RefreshBackdrop(clearCache: true);
    }

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (!IsDesktopWidget || !material.IsRenderedGlass) return;
        RefreshBackdrop(clearCache: true, invalidate: false, resetRegionKey: false);
    }

    public void Suspend()
    {
        suspended = true;
        ClearLiquidGlassCache();
        if (liquidGlassBitmap == null && material.IsRenderedGlass)
            RequestBackdropRender();
    }

    public void Resume()
    {
        suspended = false;
        ApplyCurrentMaterial();
    }

    private void OnAppSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnAppSettingsChanged(sender, oldData, newData));
            return;
        }

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
                    frameCache.Clear();
                    inFlightRenders.Clear();
                    ApplyCurrentMaterial();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to refresh FramelessClockModel: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Host activity push (the <see cref="IWidgetActivityConsumer"/> path, invoked on the UI
    /// thread): ticking follows the snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// Session-less instances ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        SetupTimer();
    }

    /// <summary>Reconcile the shared timer and its 1 s / 1 min tier while loaded and rendering.</summary>
    private void SetupTimer()
    {
        var wanted = isRunning && renderActive;
        var targetTimer = IsDesktopWidget && model.ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;

        if (currentTimer != targetTimer)
        {
            currentTimer?.Unsubscribe(OnTimerTick);
            currentTimer = targetTimer;
            isSubscribed = false;
            if (wanted)
            {
                currentTimer.Subscribe(OnTimerTick);
                isSubscribed = true;
                OnTimerTick();
            }
        }
        else if (wanted && !isSubscribed)
        {
            currentTimer.Subscribe(OnTimerTick);
            isSubscribed = true;
            OnTimerTick();
        }
        else if (!wanted && isSubscribed)
        {
            currentTimer!.Unsubscribe(OnTimerTick);
            isSubscribed = false;
        }
    }

    void IFocusThemedContent.OnFocusThemeChanged() => ApplyCurrentMaterial();

    /// <summary>
    /// The single invalidation entry for all material- and model-affecting events.
    /// </summary>
    private void ApplyCurrentMaterial()
    {
        SetupTimer();
        material = FramelessThemeResolver.Resolve(model, FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get().Theme);

        lastRegionKey = null;
        ClearLiquidGlassCache();

        UpdateTransparencyLevel(force: true);
        ClearGlyphRegionIfNotAcrylic();

        RequestBackdropRender();
        InvalidateVisual();

        VerifyMaterialWindowState();
    }

    private void RefreshBackdrop(bool clearCache, bool invalidate = true, bool resetRegionKey = true)
    {
        if (resetRegionKey) lastRegionKey = null;
        if (clearCache) ClearLiquidGlassCache();
        RequestBackdropRender();
        if (invalidate) InvalidateVisual();
    }

    private void ClearGlyphRegionIfNotAcrylic()
    {
        if (window == null || !IsDesktopWidget) return;
        if (material.IsAcrylic || !hasRegionSet) return;
        InteropService.ClearWidgetRegion(window);
        hasRegionSet = false;
    }

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

            Debug.WriteLine($"[FramelessClock] material {(wantAcrylic ? "毛玻璃" : "非毛玻璃")} but window backdrop is {level} — re-asserting");
            ApplyWindowTransparency(window, wantAcrylic);
            ClearGlyphRegionIfNotAcrylic();

            if (attemptsLeft > 1)
                DispatcherTimer.RunOnce(() => VerifyMaterialWindowState(attemptsLeft - 1), TimeSpan.FromMilliseconds(120));
        }, DispatcherPriority.Background);
    }

    private void UpdateTransparencyLevel(bool force = false)
    {
        if (window == null || !IsDesktopWidget) return;
        var wantAcrylic = material.IsAcrylic;
        var expected = wantAcrylic ? WindowTransparencyLevel.AcrylicBlur : WindowTransparencyLevel.Transparent;
        if (!force && window.TransparencyLevelHint is { Count: 1 } current && current[0] == expected)
            return;

        ApplyWindowTransparency(window, wantAcrylic);
    }

    /// <summary>Assert the OS acrylic backdrop on <paramref name="target"/>.</summary>
    private static void ApplyWindowTransparency(Window target) =>
        ApplyWindowTransparency(target, acrylic: true);

    /// <summary>
    /// Configure window transparency using a fresh array instance so Avalonia's StyledProperty
    /// setter never short-circuits on reference equality.
    /// </summary>
    private static void ApplyWindowTransparency(Window target, bool acrylic)
    {
        target.TransparencyLevelHint = acrylic
            ? new[] { WindowTransparencyLevel.AcrylicBlur }
            : new[] { WindowTransparencyLevel.Transparent };
    }

    private void OnTimerTick()
    {
        if (material.IsRenderedGlass)
        {
            var now = GetCurrentTime();
            var timeStr = CacheKey(now);

            EvictExpiredCacheEntries(now);

            if (frameCache.TryGet(timeStr, out var cached))
            {
                SetDisplayedFrame(cached);
            }
            else
            {
                SchedulePreRender(now, isImmediate: true);
            }

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
        if (!IsDesktopWidget || suspended) return;

        if (model.ShowSeconds)
        {
            for (int s = 1; s <= 5; s++)
            {
                var upcoming = now.AddSeconds(s);
                var key = CacheKey(upcoming);
                if (!frameCache.Contains(key) && !inFlightRenders.Contains(key))
                {
                    SchedulePreRender(upcoming, isImmediate: false);
                }
            }
        }
        else
        {
            var nextMinute = now.AddMinutes(1);
            var key = CacheKey(nextMinute);
            if (!frameCache.Contains(key) && !inFlightRenders.Contains(key))
            {
                SchedulePreRender(nextMinute, isImmediate: false);
            }
        }
    }

    private (LiquidGlassRenderer.Frame Frame, byte[] GlyphMask)? PrepareGlassRenderInputs(DateTime targetTime)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return null;

        var scaling = window?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * scaling));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * scaling));

        var effectiveTheme = FramelessThemeResolver.EffectiveTheme(FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get().Theme, model.SurfaceOverride);
        if (!effectiveTheme.UsesRenderedGlass)
        {
            effectiveTheme = effectiveTheme with { Surface = SurfaceStyle.LiquidGlass };
        }

        var stretchedGeometry = FramelessGlyphGeometry.BuildStretch(
            FormatTime(targetTime), Bounds.Width, Bounds.Height,
            model.FontFamily, model.FontWeight, model.StretchFill, effectiveTheme, model.FixedWidthDigits);
        if (stretchedGeometry == null) return null;

        byte[] glyphMask = FramelessGlyphGeometry.ExtractMask(stretchedGeometry, Bounds.Width, Bounds.Height, scaling, width, height);

        if (model.EnableOverlay)
        {
            var overlay = ResolveOverlayColor(model, effectiveTheme);
            var overlayHex = $"#{overlay.R:X2}{overlay.G:X2}{overlay.B:X2}";
            effectiveTheme = effectiveTheme with { AccentColor = overlayHex };
        }

        var isDark = ClockPerScreenTheme.IsDarkVariant(this);

        // Shared glass geometry: originScale 1 keeps physical pixels; the layout walk avoids
        // PointToScreen composing ancestor scale transforms (which shifted sampling).
        var geometry = GlassSamplingOrigin.FrameDesktopGeometry(window, this, 1.0);

        var widget = window as DeskCanvas.Views.Widget;
        var (cols, rows) = widget?.CurrentSpan ?? (0, 0);

        var frame = new LiquidGlassRenderer.Frame(
            width, height, (float)scaling, 0f,
            geometry.DesktopX, geometry.DesktopY,
            geometry.DesktopWidth, geometry.DesktopHeight,
            geometry.ScreenX, geometry.ScreenY, geometry.ScreenWidth, geometry.ScreenHeight,
            effectiveTheme, isDark,
            Columns: cols, Rows: rows,
            GlyphOpacity: (float)Math.Clamp(model.LiquidGlassOpacity / 100.0, 0, 2));

        return (frame, glyphMask);
    }

    private void RenderGlassFrameSynchronously(DateTime targetTime)
    {
        var key = CacheKey(targetTime);
        if (frameCache.TryGet(key, out var existing))
        {
            SetDisplayedFrame(existing);
            return;
        }

        var inputs = PrepareGlassRenderInputs(targetTime);
        if (inputs == null) return;

        try
        {
            using var wallpaper = LiquidGlassWallpaper.Get();
            var pngBytes = GlyphLiquidGlassRenderer.Render(inputs.Value.Frame, wallpaper, inputs.Value.GlyphMask);
            if (pngBytes is { Length: > 0 })
            {
                using var ms = new MemoryStream(pngBytes);
                var bmp = new Bitmap(ms);
                frameCache.Store(key, targetTime, bmp);
                SetDisplayedFrame(bmp);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Synchronous frameless glass render failed: {ex.Message}");
        }
    }

    private void SchedulePreRender(DateTime targetTime, bool isImmediate)
    {
        if (Bounds.Width < 1 || Bounds.Height < 1) return;
        if (!material.IsRenderedGlass) return;

        var key = CacheKey(targetTime);
        if (frameCache.Contains(key) || inFlightRenders.Contains(key)) return;

        // Speculative future frames yield to the in-flight ceiling (see MaxConcurrentPreRenders):
        // they are retried on the next tick, nearest second first, so nothing is lost — only the
        // concurrency is bounded. The immediate frame (the one the clock is switching to right
        // now) is never held back.
        if (!isImmediate && Volatile.Read(ref inFlightRenderCount) >= MaxConcurrentPreRenders) return;

        var inputs = PrepareGlassRenderInputs(targetTime);
        if (inputs == null) return;
        var (frame, glyphMask) = inputs.Value;

        inFlightRenders.Add(key);
        Interlocked.Increment(ref inFlightRenderCount);
        preRenderCts ??= new CancellationTokenSource();
        var token = preRenderCts.Token;

        _ = Task.Run(() =>
        {
            try
            {
                if (token.IsCancellationRequested) return;
                using var wallpaper = LiquidGlassWallpaper.Get();
                if (token.IsCancellationRequested) return;

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

                        var currentKey = CacheKey(GetCurrentTime());
                        if (isImmediate || key == currentKey || liquidGlassBitmap == null)
                        {
                            SetDisplayedFrame(bmp);
                            InvalidateVisual();
                        }

                        if (wallpaperStale && inFlightRenders.Count == 0)
                        {
                            wallpaperStale = false;
                            frameCache.Clear();
                            SchedulePreRender(GetCurrentTime(), isImmediate: true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Failed to decode pre-rendered frameless glass: {ex.Message}");
                    }
                }, DispatcherPriority.Render);
            }
            finally
            {
                // The scratch workspace is released the moment the render itself ends — the
                // posted decode callback does not hold one. The count must fall on every path,
                // cancellation included.
                Interlocked.Decrement(ref inFlightRenderCount);
            }
        }, token);
    }

    private void EvictExpiredCacheEntries(DateTime now)
    {
        var minAge = model.ShowSeconds ? TimeSpan.FromSeconds(2) : TimeSpan.FromMinutes(2);
        var maxFrames = model.ShowSeconds ? 8 : 3;
        // The declared budget finally participates: eviction checks the displayed-frame pin, the
        // frame-count ceiling and the stored pixel bytes together (see FramelessGlassFrameCache).
        frameCache.EvictExpired(now, minAge, maxFrames, CacheBudgetBytes);
    }

    private void ClearLiquidGlassCache()
    {
        wallpaperStale = false;
        preRenderCts?.Cancel();
        preRenderCts?.Dispose();
        preRenderCts = null;
        inFlightRenders.Clear();
        frameCache.Clear();
    }

    private void SetDisplayedFrame(Bitmap? next)
    {
        if (ReferenceEquals(liquidGlassBitmap, next)) return;

        var previous = liquidGlassBitmap;
        liquidGlassBitmap = next;
        frameCache.SetDisplayed(next);

        if (previous != null && !frameCache.Holds(previous))
        {
            previous.Dispose();
        }
    }

    private string FormatTime(DateTime dt)
    {
        var hh = model.Use24Hours ? "HH" : "hh";
        var ss = model.ShowSeconds ? ":ss" : "";
        return dt.ToString($"{hh}:mm{ss}", CultureInfo.InvariantCulture);
    }

    private string CacheKey(DateTime dt) =>
        $"{FormatTime(dt)}@{Math.Round(model.LiquidGlassOpacity)}_{material.EffectiveSurface}_{model.FontFamily}_{model.FontWeight}_{model.StretchFill}_{model.FixedWidthDigits}_{ClockPerScreenTheme.IsDarkVariant(this)}";

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var targetW = Bounds.Width;
        var targetH = Bounds.Height;
        if (targetW <= 1 || targetH <= 1) return;

        var now = GetCurrentTime();
        var timeStr = FormatTime(now);
        var cacheKey = CacheKey(now);

        var globalTheme = FocusThemeCoordinator.ResolveVisualTheme(this) ?? appSettingsProvider?.Get().Theme;
        var effectiveTheme = FramelessThemeResolver.EffectiveTheme(globalTheme, model.SurfaceOverride);
        material = FramelessThemeResolver.Resolve(model, globalTheme);

        var stretchedGeometry = FramelessGlyphGeometry.BuildStretch(
            timeStr, targetW, targetH,
            model.FontFamily, model.FontWeight, model.StretchFill,
            effectiveTheme, model.FixedWidthDigits);
        if (stretchedGeometry == null) return;

        var isDark = ClockPerScreenTheme.IsDarkVariant(this);

        // Theme 1: 毛玻璃 (OS-level Acrylic blur confined to the glyph region)
        if (material.IsAcrylic)
        {
            if (IsDesktopWidget)
            {
                var contentScale = ResolveHostContentScale();
                var regionKey = $"{timeStr}_{targetW}_{targetH}_{model.FontFamily}_{model.FontWeight}_{model.StretchFill}_{model.FixedWidthDigits}_{isDark}_{contentScale:F3}";
                if (regionKey != lastRegionKey || !hasRegionSet)
                {
                    lastRegionKey = regionKey;
                    UpdateWindowRegion(stretchedGeometry, targetW, targetH);
                }
            }

            using (context.PushGeometryClip(stretchedGeometry))
            {
                var alpha = IsDesktopWidget ? (isDark ? 70 : 48) : (isDark ? 160 : 180);
                var tintWash = isDark
                    ? Color.FromArgb((byte)alpha, 60, 60, 60)
                    : Color.FromArgb((byte)alpha, 240, 240, 240);
                context.DrawRectangle(new SolidColorBrush(tintWash), null, new Rect(0, 0, targetW, targetH));

                if (model.EnableOverlay)
                {
                    var overlayColor = ResolveOverlayColor(model, effectiveTheme);
                    context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
                }
            }

            DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, effectiveTheme);
            return;
        }

        // All non-acrylic materials composite via clean 32-bit per-pixel alpha — no 1-bit GDI window region.
        ClearGlyphRegionIfNotAcrylic();

        // Theme 2: 液态玻璃 / 新液态玻璃 / 柔光玻璃 (Per-pixel refractive optical glass)
        if (material.IsRenderedGlass)
        {
            if (frameCache.TryGet(cacheKey, out var cached))
            {
                SetDisplayedFrame(cached);
            }
            else if (!IsDesktopWidget && liquidGlassBitmap == null)
            {
                RenderGlassFrameSynchronously(now);
            }

            if (liquidGlassBitmap != null)
            {
                context.DrawImage(liquidGlassBitmap, new Rect(0, 0, targetW, targetH));
            }
            else
            {
                using (context.PushGeometryClip(stretchedGeometry))
                {
                    var fallbackHex = isDark ? "#282828" : "#F0F0F0";
                    var c = Color.TryParse(fallbackHex, out var parsed) ? parsed : Colors.Gray;
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(120, c.R, c.G, c.B)), null, new Rect(0, 0, targetW, targetH));
                }
                SchedulePreRender(now, isImmediate: true);
                DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, effectiveTheme);
            }

            if (model.EnableOverlay)
            {
                using (context.PushGeometryClip(stretchedGeometry))
                {
                    var overlayColor = ResolveOverlayColor(model, effectiveTheme);
                    context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
                }
            }

            return;
        }

        // Theme 3: 多彩 (Vibrant gradient numeral fill + luminous specular rim)
        if (material.IsColorful)
        {
            using (context.PushGeometryClip(stretchedGeometry))
            {
                if (model.EnableOverlay)
                {
                    var overlayColor = ResolveOverlayColor(model, effectiveTheme);
                    context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
                }
                else
                {
                    var accent = ResolveAccentColor(effectiveTheme) ?? DefaultOverlayColor;
                    var startColor = BlendColors(accent, Colors.White, isDark ? 0.28f : 0.38f);
                    var midColor = accent;
                    var endColor = BlendColors(accent, isDark ? Color.FromRgb(25, 20, 60) : Color.FromRgb(40, 30, 95), 0.32f);

                    var colorfulBrush = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(startColor, 0.0),
                            new GradientStop(midColor, 0.52),
                            new GradientStop(endColor, 1.0)
                        }
                    };
                    context.DrawRectangle(colorfulBrush, null, new Rect(0, 0, targetW, targetH));
                }
            }

            DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, effectiveTheme);
            return;
        }

        // Theme 4: 纯色 (Pure vector solid color fill with transparency, overlay, and crisp rim)
        using (context.PushGeometryClip(stretchedGeometry))
        {
            if (model.EnableOverlay)
            {
                var overlayColor = ResolveOverlayColor(model, effectiveTheme);
                context.DrawRectangle(new SolidColorBrush(overlayColor), null, new Rect(0, 0, targetW, targetH));
            }
            else
            {
                var solidHex = isDark
                    ? (effectiveTheme.EffectiveSolidBackgroundDark ?? "#2E2E2E")
                    : (effectiveTheme.EffectiveSolidBackgroundLight ?? "#FFFFFF");
                var baseSolid = Color.TryParse(solidHex, out var parsed) ? parsed : (isDark ? Colors.Black : Colors.White);
                var opacity = (float)Math.Clamp(effectiveTheme.OpacityLevel, 0.05, 1.0);
                var brushColor = Color.FromArgb((byte)(opacity * 255), baseSolid.R, baseSolid.G, baseSolid.B);
                context.DrawRectangle(new SolidColorBrush(brushColor), null, new Rect(0, 0, targetW, targetH));
            }
        }

        DrawSpecularRim(context, stretchedGeometry, targetW, targetH, isDark, model, effectiveTheme);
    }

    private double ResolveHostContentScale()
    {
        if (Parent is Visual parent && parent.RenderTransform is ScaleTransform scaleTransform)
        {
            return scaleTransform.ScaleX;
        }
        return 1.0;
    }

    private void UpdateWindowRegion(Geometry geometry, double width, double height)
    {
        if (window == null || !IsDesktopWidget) return;
        var scaling = window.RenderScaling;
        var pixelW = Math.Max(1, (int)Math.Ceiling(width * scaling));
        var pixelH = Math.Max(1, (int)Math.Ceiling(height * scaling));

        var size = new PixelSize(pixelW, pixelH);
        var rtb = regionBitmap;
        if (rtb == null || rtb.PixelSize != size)
        {
            rtb?.Dispose();
            rtb = regionBitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        }

        var contentScale = ResolveHostContentScale();
        using (var ctx = rtb.CreateDrawingContext())
        {
            if (Math.Abs(contentScale - 1.0) > 0.001)
            {
                var cx = width * 0.5;
                var cy = height * 0.5;
                var scaleMatrix = Matrix.CreateTranslation(-cx, -cy)
                    * Matrix.CreateScale(contentScale, contentScale)
                    * Matrix.CreateTranslation(cx, cy);
                using (ctx.PushTransform(scaleMatrix))
                {
                    ctx.DrawGeometry(Brushes.Black, null, geometry);
                }
            }
            else
            {
                ctx.DrawGeometry(Brushes.Black, null, geometry);
            }
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

    private static Color BlendColors(Color baseC, Color tintC, float weight)
    {
        byte r = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.R + weight * tintC.R), 0, 255);
        byte g = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.G + weight * tintC.G), 0, 255);
        byte b = (byte)Math.Clamp((int)Math.Round((1f - weight) * baseC.B + weight * tintC.B), 0, 255);
        return Color.FromArgb(baseC.A, r, g, b);
    }

    private void DrawSpecularRim(DrawingContext context, Geometry geometry, double targetW, double targetH, bool isDark, FramelessClockModel model, Theme? theme)
    {
        var edgeTint = (float)Math.Clamp(
            (theme?.EffectiveLiquidGlass.Highlight ?? 50.0) / 100.0, 0.0, 1.0);

        Color dye = isDark ? Color.FromRgb(200, 220, 245) : Color.FromRgb(240, 240, 245);
        bool hasDye = false;

        if (model.EnableOverlay)
        {
            var oc = ResolveOverlayColor(model, theme);
            dye = Color.FromRgb(oc.R, oc.G, oc.B);
            hasDye = true;
        }
        else if (ResolveAccentColor(theme) is { } accent)
        {
            dye = accent;
            hasDye = !string.IsNullOrEmpty(theme?.AccentColor);
        }

        var tintFactor = hasDye ? edgeTint : 0f;

        var stop0Base = isDark ? Color.FromArgb(200, 255, 255, 255) : Color.FromArgb(220, 255, 255, 255);
        var stop1Base = isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(50, 255, 255, 255);
        var stop2Base = isDark ? Color.FromArgb(140, 255, 255, 255) : Color.FromArgb(160, 255, 255, 255);

        var stop0 = BlendColors(stop0Base, dye, 0.45f * tintFactor);
        var stop1 = BlendColors(stop1Base, dye, 0.85f * tintFactor);
        var stop2 = BlendColors(stop2Base, dye, 0.35f * tintFactor);

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
        var rimPen = new Pen(rimBrush, rimThickness)
        {
            LineJoin = PenLineJoin.Round,
            LineCap = PenLineCap.Round
        };
        context.DrawGeometry(null, rimPen, geometry);
    }

    /// <summary>Local when no id; known ids convert; unknown ids fall back to Utc.</summary>
    private DateTime GetCurrentTime()
    {
        if (string.IsNullOrEmpty(model.TimeZoneId)) return DateTime.Now;
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, SharedTimeZone.Resolve(model.TimeZoneId));
    }

    private static readonly Color DefaultOverlayColor = Color.FromRgb(0, 120, 215);

    private Color ResolveOverlayColor(FramelessClockModel model, Theme? theme)
    {
        var baseColor = model.FollowAccentColor
            ? ResolveAccentColor(theme) ?? DefaultOverlayColor
            : ParseHex(model.OverlayColor) ?? DefaultOverlayColor;

        var opacity = Math.Clamp(model.OverlayOpacity, 0.0, 1.0);
        return Color.FromArgb((byte)(opacity * 255), baseColor.R, baseColor.G, baseColor.B);
    }

    private Color? ResolveAccentColor(Theme? theme)
    {
        var achromaticMonochrome = theme is { Monochrome: true }
            && theme.EffectiveMonochromeVariant != MonochromeStyle.Accent;
        if (!achromaticMonochrome && ParseHex(theme?.AccentColor) is { } picked)
            return picked;

        if (ThemeScopeResolver.TryFindResource<Color>(this, "SystemAccentColor", ClockPerScreenTheme.Resolve(this), out var systemAccent))
            return systemAccent;

        return null;
    }

    private static Color? ParseHex(string? hex) =>
        !string.IsNullOrEmpty(hex) && Color.TryParse(hex, out var parsed) ? parsed : null;
}
