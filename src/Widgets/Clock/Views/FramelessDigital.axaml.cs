using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
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
/// straight onto the desktop, <b>locked to the 毛玻璃 material</b>.
/// <para>
/// The clock used to follow the global app theme through four materials (毛玻璃 / 液态玻璃 /
/// 新液态玻璃 / 纯色), which made it the one widget whose look silently changed with 外观
/// settings and the historic source of "stopped following the global theme" regressions. It is
/// now locked to 毛玻璃: the glyphs are always a wash over the OS's native acrylic backdrop,
/// confined to the glyph outline by the window region, whatever the global surface is — the
/// rendered-glass pipeline (theme resolver, glyph glass renderer, pre-render frame cache and the
/// wallpaper sampling) is gone entirely. The global theme is still read, but only for the
/// appearance inputs the 毛玻璃 look itself uses: the light/dark wash, the accent behind
/// 跟随强调色 and the global font family.
/// </para>
/// <para>
/// Architecture contract — the part that must survive every future edit:
/// <list type="number">
/// <item>There is ONE invalidation entry, <see cref="InvalidateGlyphs"/>: it resets the window
/// region key and repaints. Every look-affecting event (load, light/dark variant change, model
/// refresh, size change, a global settings change) funnels through it. No event re-implements
/// steps inline — re-implemented hand-picked subsets of steps are exactly how the old
/// theme-following regressions kept happening.</item>
/// <item>The OS acrylic backdrop is asserted once per load (<see cref="VerifyAcrylicBackdrop"/>,
/// bounded self-healing) and never reconfigured afterwards — with the lock there is no second
/// material to switch to, so a frame can never straddle two materials.</item>
/// </list>
/// </para>
/// </summary>
public partial class FramelessDigital : UserControl, IFramelessWidget, IWidgetSelfRefreshing
{
    private FramelessClockModel model;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;

    private UpdateTimer? currentTimer;
    private Window? window;
    private bool IsDesktopWidget => window is DeskCanvas.Views.Widget;

    // Per-tick window region recompute buffers (see UpdateWindowRegion).
    private RenderTargetBitmap? regionBitmap;
    private byte[]? regionBuffer;

    private string? lastRegionKey;
    private bool hasRegionSet;

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
        SizeChanged += (_, _) => InvalidateGlyphs();

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged += OnAppSettingsChanged;
        }

        SetupTimer();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        window = TopLevel.GetTopLevel(this) as Window;

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;
            appSettingsProvider.DataChanged += OnAppSettingsChanged;
        }

        lastRegionKey = null;
        hasRegionSet = false;
        SetupTimer();

        if (window != null && IsDesktopWidget)
        {
            // The one place the OS backdrop is configured: the lock means it is never switched
            // again, only re-asserted if the platform dropped it.
            ApplyWindowTransparency(window);
            VerifyAcrylicBackdrop(attemptsLeft: 3);
        }

        InvalidateGlyphs();
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        if (window != null)
        {
            if (IsDesktopWidget && hasRegionSet)
            {
                InteropService.ClearWidgetRegion(window);
            }
            window = null;
        }
        hasRegionSet = false;

        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;
        }

        currentTimer?.Unsubscribe(OnTimerTick);
        currentTimer = null;

        regionBitmap?.Dispose();
        regionBitmap = null;
        regionBuffer = null;
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        // The variant changes the wash and the specular rim, so the cached region key is stale.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnActualThemeVariantChanged(sender, e));
            return;
        }

        InvalidateGlyphs();
    }

    private void OnAppSettingsChanged(object sender, AppSettings? oldData, AppSettings newData)
    {
        InvalidateGlyphs();
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
                    InvalidateGlyphs();
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
    /// The ONE invalidation entry: everything that can change the rendered look (load,
    /// light/dark variant change, model refresh, size change, a global settings change) funnels
    /// through here. It only resets the region key and repaints — the glyph geometry, the wash
    /// and the specular rim are all recomputed in the next <see cref="Render"/> pass.
    /// </summary>
    private void InvalidateGlyphs()
    {
        lastRegionKey = null;
        InvalidateVisual();
    }

    /// <summary>
    /// Check that the window really ended up on the acrylic backdrop the lock requires, and
    /// re-assert it if not: the platform does not always honour the hint in the same turn, and a
    /// plain Transparent window would put the wash on the wallpaper with no blur behind it.
    /// Bounded to a few passes, so a platform that genuinely refuses the level cannot turn this
    /// into an endless loop.
    /// </summary>
    private void VerifyAcrylicBackdrop(int attemptsLeft)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (window == null || !IsDesktopWidget) return;
            if (window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur) return;

            Debug.WriteLine($"[FramelessClock] expected the acrylic backdrop but the window is at {window.ActualTransparencyLevel} — re-asserting");
            ApplyWindowTransparency(window);

            if (attemptsLeft > 1)
                DispatcherTimer.RunOnce(() => VerifyAcrylicBackdrop(attemptsLeft - 1), TimeSpan.FromMilliseconds(120));
        }, DispatcherPriority.Background);
    }

    // Cached hint array: re-assigning the hint makes the Win32 impl re-apply the window
    // transparency each time.
    private static readonly WindowTransparencyLevel[] AcrylicHint = [WindowTransparencyLevel.AcrylicBlur];

    /// <summary>
    /// Assert the OS acrylic backdrop. A static member on purpose:
    /// <c>tests/ClockThemeChecks</c> drives it to pin the native backdrop contract.
    /// </summary>
    private static void ApplyWindowTransparency(Window target)
    {
        target.TransparencyLevelHint = AcrylicHint;
    }

    private void OnTimerTick()
    {
        // The region key carries the time text, so a tick that changes the digits also moves the
        // window region inside the Render pass; the tick itself only has to repaint.
        InvalidateVisual();
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

        if (IsDesktopWidget)
        {
            // Time text, size, font and variant are part of the key so a variant switch can
            // never leave a glyph region shaped for the previous wash on the window.
            var regionKey = $"{timeStr}_{targetW}_{targetH}_{model.FontFamily}_{model.FontWeight}_{model.StretchFill}_{isDark}";
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
        // The rim dye strength is the historic shipping default. The rim used to follow the
        // global 边缘染色强度 (LiquidGlassSettings.EdgeTint) — a 液态玻璃 optic — but with the
        // clock locked to 毛玻璃 that cross-material coupling is gone: the look no longer
        // changes when the global surface (or its optics) is not 毛玻璃's.
        const float edgeTint = (float)(LiquidGlassSettings.DefaultEdgeTint / 100.0);

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
        // 黑白/背景色 monochrome: the accent resource is ThemeService's monochrome ramp
        // (pure white/black in 黑白, the inverted background in 背景色) — the hand-picked
        // accent hex must not bypass it, so it is only honored in the 强调色 variant and
        // outside monochrome.
        var achromaticMonochrome = theme is { Monochrome: true }
            && theme.EffectiveMonochromeVariant != MonochromeStyle.Accent;
        if (!achromaticMonochrome && ParseHex(theme?.AccentColor) is { } picked)
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
