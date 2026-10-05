using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Clock.Models;
using Clock.Services;
using Clock.Views;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace ClockThemeChecks;

/// <summary>
/// Regression checks for the frameless clock's material resolution and its glyph liquid glass
/// output.
///
/// The clock carries <b>no per-widget theme override</b> any more: it always follows the global
/// app theme. The historical <c>ThemeMode</c> setting was removed when the old separate
/// 液态玻璃 / 柔光玻璃 materials were merged into the single current 液态玻璃, where 柔光 is reached
/// through the 柔光晕 / 光谱弥散 optics instead of a second material.
///
/// Part 1 (activation): the desktop widget is activated by
/// <c>WidgetFactory.CreateWidgetControl</c> as <c>Activate(typeof(FramelessDigital), layoutProvider, model)</c>,
/// which goes through <c>ActivatorUtilities.CreateInstance</c>. That type has five public
/// constructors, two of which accept those two arguments, so which one wins decides whether
/// the widget ever receives <see cref="IAppSettingsProvider"/> — and therefore whether it can
/// resolve the global material at all.
///
/// Part 2 (rendering): renders the control under global 液态玻璃 and global 毛玻璃 themes and saves
/// PNGs, so the two materials can be compared pixel by pixel (and inspected by eye).
/// </summary>
class Program
{
    private static int failures;
    private static string outputDir = "dist/clock-theme-checks";

    /// <summary>Global material used by the checks (the clock resolves its material against this).</summary>
    private static readonly SurfaceStyle GlobalSurface = SurfaceStyle.LiquidGlass;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) outputDir = args[0];
        Directory.CreateDirectory(outputDir);

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        var settings = new StubSettings(BuildSettings(GlobalSurface));
        var services = new ServiceCollection();
        services.AddSingleton<IAppSettingsProvider>(settings);
        var provider = services.BuildServiceProvider();
        var layout = new StubLayout();

        Console.WriteLine("=== Frameless clock theme checks ===");
        Console.WriteLine($"global surface = {GlobalSurface}");
        Console.WriteLine();

        // ---- Part 1: activation / material resolution ----
        var clock = (FramelessDigital)ActivatorUtilities.CreateInstance(
            provider, typeof(FramelessDigital), layout, new FramelessClockModel());

        var injected = ReadField(clock, "appSettingsProvider");
        Console.WriteLine("ctor args = [IWidgetLayoutProvider, FramelessClockModel]");
        Console.WriteLine($"  injected IAppSettingsProvider = {(injected == null ? "NULL" : injected.GetType().Name)}");
        Check("the clock receives IAppSettingsProvider", injected != null);
        Check("a global LiquidGlass theme resolves LiquidGlass",
            ResolveTheme(clock) == (false, true, false));

        // ---- Part 2: rendering follows the global material ----
        Console.WriteLine();
        Console.WriteLine($"Rendering into {Path.GetFullPath(outputDir)} …");

        var glass = Render(settings, layout, outputDir, "global-liquidglass", expectFrame: true);
        var acrylic = Render(new StubSettings(BuildSettings(SurfaceStyle.Acrylic)), layout, outputDir,
            "global-acrylic", expectFrame: false);

        Check("a global 液态玻璃 theme drives the glyph glass pipeline", glass.ProducedFrame);
        Check("a global 毛玻璃 theme does not (it uses the OS acrylic backdrop)", !acrylic.ProducedFrame);

        byte[]? glassPixels = glass.Pixels, acrylicPixels = acrylic.Pixels;
        if (glassPixels != null && acrylicPixels != null)
        {
            var materialDelta = MeanAbsoluteDifference(glassPixels, acrylicPixels);
            Console.WriteLine($"  mean |delta| 液态玻璃 vs 毛玻璃: {materialDelta:F2}/255");
            Check("the clock renders visibly different numerals per global material", materialDelta > 1.0);
        }
        else
        {
            Check("both global material renders produced pixels", false);
        }

        // ---- Part 3: glyph optics — the meniscus must exist on the adaptive path ----
        // Regression: refractionWidth 0 used to mean "no lens", so the numerals were only a
        // blurred, tinted fill and read as 毛玻璃 while the rest of the desktop was 液态玻璃.
        // The clock no longer exposes a 边缘折射宽度 override, so <c>null</c> (what the widget
        // passes) is the production path and must resolve the same adaptive lens as 0.
        Console.WriteLine();
        Console.WriteLine("--- glyph lens optics ---");

        var globalOptics = new LiquidGlassSettings(
            Blur: 30, Refraction: 60, EdgeWidth: 7, Highlight: 50, Dispersion: 100, LightAngle: 225, EdgeTint: 10);
        const float probeScale = 2f, probeStrokeRadius = 26f;

        var auto = GlyphLiquidGlassRenderer.ResolveLens(globalOptics, probeScale, probeStrokeRadius, 1f, 1f, 0.0);
        Console.WriteLine($"  refractionWidth = 0 (default) → lens {auto.LensWidth:F2}px, bend {auto.LensShift:F2}px, dispersion {auto.Dispersion:F2}");
        Check("default refractionWidth=0 still produces a real lens", auto.LensWidth > 2f && auto.LensShift > 0.5f);

        var fromNull = GlyphLiquidGlassRenderer.ResolveLens(globalOptics, probeScale, probeStrokeRadius, 1f, 1f, null);
        Check("null refractionWidth resolves the same adaptive lens as 0",
            Math.Abs(fromNull.LensWidth - auto.LensWidth) < 0.001f);

        var manual = GlyphLiquidGlassRenderer.ResolveLens(globalOptics, probeScale, probeStrokeRadius, 1f, 1f, 12.0);
        Console.WriteLine($"  refractionWidth = 12 (manual)  → lens {manual.LensWidth:F2}px, bend {manual.LensShift:F2}px");
        Check("an explicit refractionWidth still overrides the adaptive lens (optics sweep aid)", manual.LensWidth > auto.LensWidth + 1f);

        var strong = GlyphLiquidGlassRenderer.ResolveLens(globalOptics with { Refraction = 100 }, probeScale, probeStrokeRadius, 1f, 1f, 0.0);
        Check("the global refraction slider drives the glyph bend", strong.LensShift > auto.LensShift);

        // A mask with no interior pixels used to invert a Math.Clamp range and throw, which
        // silently killed the background render (the widget then kept its flat fallback wash).
        var degenerate = GlyphLiquidGlassRenderer.ResolveLens(globalOptics, probeScale, 2.0f * probeScale, 1f, 1f, 0.0);
        Check("a hairline glyph mask cannot abort the render", degenerate.LensWidth >= 0f);

        // ---- Part 4: the live desktop capture must actually be sampled ----
        // A live capture carries only CachedBitmap (ImageBytes is null). Reading ImageBytes
        // instead — as the 1.8.0 build did — dropped the capture entirely and painted the
        // numerals over a flat colour, losing both the wallpaper and the refraction.
        Console.WriteLine();
        Console.WriteLine("--- live desktop capture sampling ---");

        var glassTheme = new Theme(
            DarkMode: true, AccentColor: null, OpacityLevel: 0.18, Monochrome: false, UseNativeFrame: false,
            FontFamily: "Segoe UI", Surface: SurfaceStyle.LiquidGlass, LiquidGlass: globalOptics);

        using var syntheticWallpaper = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(syntheticWallpaper))
        {
            canvas.Clear(new SKColor(220, 40, 40));
            using var blue = new SKPaint { Color = new SKColor(40, 80, 220) };
            canvas.DrawRect(new SKRect(32, 0, 64, 64), blue);
        }
        syntheticWallpaper.SetImmutable();

        var liveWallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(0, 0, 0), syntheticWallpaper, live: true);
        var liveFrame = new LiquidGlassRenderer.Frame(
            64, 64, 1f, 0f, 0, 0, 64, 64, 0, 0, 64, 64, glassTheme, Dark: true);

        // A filled numeral bar with real interior depth (a glyph-like mask).
        var barMask = new byte[64 * 64];
        for (var y = 12; y < 52; y++)
        for (var x = 12; x < 52; x++)
            barMask[y * 64 + x] = 255;

        var livePng = GlyphLiquidGlassRenderer.Render(liveFrame, liveWallpaper, barMask, 0.0);
        var livePixels = DecodePixels(livePng);
        var (red, blue2) = CountDominant(livePixels);
        Console.WriteLine($"  numeral pixels: {red} wallpaper-red, {blue2} wallpaper-blue");
        Check("live capture reaches the numerals (wallpaper colours survive)",
            livePixels != null && red > 100 && blue2 > 100);
        liveWallpaper.Dispose();

        // ---- Part 5: visual preview against a synthetic wallpaper ----
        // The harness has no real desktop capture, so the lens is previewed against generated
        // vertical stripes: the displacement shows up as bent, locally compressed lines near
        // the stroke edges (a plain frosted fill leaves them uniformly blurred instead).
        Console.WriteLine();
        Console.WriteLine("--- optical preview ---");

        const int pw = 736, ph = 368;
        using var stripes = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(stripes))
        {
            canvas.Clear(new SKColor(16, 22, 36));
            // Wide stripes (32 px period) so the displacement stays readable after the blur.
            for (var i = 0; i < 24; i++)
            {
                using var paint = new SKPaint { Color = SKColor.FromHsl(i * 15f, 78f, 55f) };
                canvas.DrawRect(new SKRect(i * 32, 0, i * 32 + 18, ph), paint);
            }
        }
        stripes.SetImmutable();

        var previewWallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(16, 22, 36), stripes, live: true);
        var previewFrame = new LiquidGlassRenderer.Frame(
            pw, ph, 2f, 0f, 0, 0, pw, ph, 0, 0, pw, ph, glassTheme, Dark: true, Columns: 4, Rows: 2);

        var previewMask = new byte[pw * ph];
        for (var y = 60; y < ph - 60; y++)
        for (var x = 90; x < pw - 90; x++)
            previewMask[y * pw + x] = 255;

        var lensPng = GlyphLiquidGlassRenderer.Render(previewFrame, previewWallpaper, previewMask, 0.0);
        var flatPng = GlyphLiquidGlassRenderer.Render(previewFrame, previewWallpaper, previewMask, 0.1);
        var lensPixels = DecodePixels(lensPng);
        var flatPixels = DecodePixels(flatPng);
        var lensDelta = lensPixels != null && flatPixels != null
            ? MeanAbsoluteDifference(lensPixels, flatPixels)
            : 0.0;
        Console.WriteLine($"  mean |delta| lens (auto) vs hairline lens: {lensDelta:F2}/255");
        Check("the default lens visibly bends the backdrop", lensDelta > 2.0);

        var previewPath = Path.Combine(outputDir, "glyph-lens-preview.png");
        File.WriteAllBytes(previewPath, lensPng);
        Console.WriteLine($"  lensed numerals: saved {previewPath}");
        previewWallpaper.Dispose();

        // ---- Part 6: live theme switch on one running instance ----
        // The user changes 外观 → 应用主题 while the clock is already on the desktop: the same
        // widget instance has to follow the new global material without being recreated. This
        // pins that path (settings change → resolve → re-render), which a fresh start does not
        // exercise.
        Console.WriteLine();
        Console.WriteLine("--- live theme switch (acrylic → liquid glass) ---");

        var switchSettings = new StubSettings(BuildSettings(SurfaceStyle.Acrylic));
        var switchServices = new ServiceCollection();
        switchServices.AddSingleton<IAppSettingsProvider>(switchSettings);
        var switchProvider = switchServices.BuildServiceProvider();

        var switched = (FramelessDigital)ActivatorUtilities.CreateInstance(
            switchProvider, typeof(FramelessDigital), layout, new FramelessClockModel());
        switched.Width = 368;
        switched.Height = 184;
        switched.Measure(new Size(368, 184));
        switched.Arrange(new Rect(0, 0, 368, 184));
        switched.UpdateLayout();

        Console.WriteLine($"  before: {Describe(ResolveTheme(switched))}");
        Check("the clock starts on the acrylic global theme", ResolveTheme(switched) == (true, false, false));

        switchSettings.Save(BuildSettings(SurfaceStyle.LiquidGlass));

        Console.WriteLine($"  after : {Describe(ResolveTheme(switched))}");
        Check("the same instance follows the switch to liquid glass", ResolveTheme(switched) == (false, true, false));
        Check("and it renders a liquid glass frame afterwards", WaitForFrame(switched));

        for (var round = 1; round <= 2; round++)
        {
            switchSettings.Save(BuildSettings(SurfaceStyle.Acrylic));
            Check($"round {round}: the same instance returns to acrylic",
                ResolveTheme(switched) == (true, false, false));
            Check($"round {round}: acrylic releases the previous liquid glass frame",
                ReadField(switched, "liquidGlassBitmap") == null);
            switchSettings.Save(BuildSettings(SurfaceStyle.LiquidGlass));
            Check($"round {round}: the same instance returns to liquid glass",
                ResolveTheme(switched) == (false, true, false) && WaitForFrame(switched));
        }

        CheckNativeMaterialRoundTrip();

        // ---- Part 7: material resolution is global-only ----
        // The clock used to carry its own 视觉主题 override (ThemeMode, which also offered the old
        // separate 液态玻璃 and 柔光玻璃). It was removed when those two were merged into one
        // material: the widget now always follows the global theme, and 柔光 is reached through the
        // global 柔光晕 / 光谱弥散 optics instead of a second material. This pins the mapping — and
        // that a ThemeMode left over in an existing layout.json is simply ignored.
        Console.WriteLine();
        Console.WriteLine("--- global material resolution ---");

        Theme ThemeFor(SurfaceStyle surface) => BuildSettings(surface).Theme;

        Check("global 毛玻璃 resolves Acrylic",
            FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.Acrylic)).IsAcrylic);
        Check("global 纯色 resolves Solid",
            FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.Solid)).IsSolid);
        Check("global 多彩 falls back to a filled surface (not acrylic)",
            FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.Colorful)).IsSolid
            && !FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.Colorful)).IsRenderedGlass);

        var liquidGlobal = FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.LiquidGlass));
        Check("global 液态玻璃 resolves rendered glass without the soft recipe",
            liquidGlobal is { IsRenderedGlass: true, IsSoftGlow: false, IsAcrylic: false, IsSolid: false });

        // A stored 柔光玻璃 surface (legacy configurations) still reaches the soft recipe…
        var legacySoft = FramelessThemeResolver.Resolve(ThemeFor(SurfaceStyle.SoftGlow));
        Check("a stored 柔光玻璃 theme still resolves the rendered (soft) glass",
            legacySoft is { IsRenderedGlass: true, IsSoftGlow: true, IsAcrylic: false, IsSolid: false });

        // …and so does the merged form: plain 液态玻璃 with the soft optics turned up.
        var mergedSoft = ThemeFor(SurfaceStyle.LiquidGlass) with
        {
            LiquidGlass = new LiquidGlassSettings(Glow: 70, Spectrum: 100)
        };
        Check("液态玻璃 with 柔光晕 / 光谱弥散 up resolves the soft recipe",
            FramelessThemeResolver.Resolve(mergedSoft) is { IsRenderedGlass: true, IsSoftGlow: true });

        Check("no global theme yet falls back to acrylic (unchanged historic behaviour)",
            FramelessThemeResolver.Resolve(null).IsAcrylic);

        // The model must no longer be able to carry a per-widget material at all, and a ThemeMode
        // left over in a user's layout.json must not break deserialization.
        Check("FramelessClockModel no longer declares a ThemeMode override",
            typeof(FramelessClockModel).GetProperty("ThemeMode") == null);
        var stale = JsonSerializer.Deserialize<FramelessClockModel>(
            "{\"Use24Hours\":false,\"ThemeMode\":4}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("a stale ThemeMode in stored settings is ignored, not fatal",
            stale is { Use24Hours: false });

        var softSettings = new StubSettings(BuildSettings(SurfaceStyle.SoftGlow));
        var softServices = new ServiceCollection();
        softServices.AddSingleton<IAppSettingsProvider>(softSettings);
        var softFollow = (FramelessDigital)ActivatorUtilities.CreateInstance(
            softServices.BuildServiceProvider(), typeof(FramelessDigital), layout, new FramelessClockModel());
        Check("a running instance on a soft global theme resolves rendered glass",
            ResolveTheme(softFollow) == (false, true, false));

        Console.WriteLine();
        Console.WriteLine("--- soft glyph optics ---");
        var softOptics = new LiquidGlassSettings(
            Blur: 30, Refraction: 50, EdgeWidth: 24, Highlight: 46, Dispersion: 30,
            LightAngle: 225, EdgeTint: 40, Glow: 70, Spectrum: 100);

        var crispLens = GlyphLiquidGlassRenderer.ResolveLens(softOptics, probeScale, probeStrokeRadius, 1f, 1f, 0.0, soft: false);
        var softLens = GlyphLiquidGlassRenderer.ResolveLens(softOptics, probeScale, probeStrokeRadius, 1f, 1f, 0.0, soft: true);
        Console.WriteLine($"  lens: 液态 {crispLens.LensWidth:F2}px/{crispLens.LensShift:F2}px → 柔光 {softLens.LensWidth:F2}px/{softLens.LensShift:F2}px");
        Check("柔光玻璃 widens the glyph lens band", softLens.LensWidth > crispLens.LensWidth);
        Check("柔光玻璃 makes the glyph bend shallower", softLens.LensShift < crispLens.LensShift);

        var noLens = GlyphLiquidGlassRenderer.ResolveLens(softOptics with { EdgeWidth = 0 }, probeScale, probeStrokeRadius, 1f, 1f, 0.0);
        Console.WriteLine($"  EdgeWidth=0 → lens {noLens.LensWidth:F2}px, bend {noLens.LensShift:F2}px, dispersion {noLens.Dispersion:F2} kept");
        Check("EdgeWidth=0 switches the glyph lens off without touching dispersion",
            noLens.LensWidth == 0f && noLens.LensShift == 0f && noLens.Dispersion > 0f);

        // Same optics, same mask, same wallpaper — only the material differs.
        // Same optics, same mask — only the material differs. (Part 5 disposed its preview
        // wallpaper, so build a fresh one: rendering through a disposed bitmap is an AV.)
        using var softStripes = new SKBitmap(new SKImageInfo(pw, ph, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(softStripes))
        {
            canvas.Clear(new SKColor(16, 22, 36));
            for (var i = 0; i < 24; i++)
            {
                using var paint = new SKPaint { Color = SKColor.FromHsl(i * 15f, 78f, 55f) };
                canvas.DrawRect(new SKRect(i * 32, 0, i * 32 + 18, ph), paint);
            }
        }
        softStripes.SetImmutable();
        var softWallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(16, 22, 36), softStripes, live: true);

        var softTheme = glassTheme with { Surface = SurfaceStyle.SoftGlow, LiquidGlass = softOptics };
        // The crisp side has to zero both soft-recipe ingredients: since 柔光玻璃 was merged into
        // 液态玻璃, 柔光晕 or 光谱弥散 above 0 selects the soft look on any surface, and without
        // this the two themes would render identically.
        var crispTheme = glassTheme with
        {
            Surface = SurfaceStyle.LiquidGlass,
            LiquidGlass = softOptics with { Glow = 0, Spectrum = 0 }
        };
        var softFrame = previewFrame with { Theme = softTheme };
        var crispFrame = previewFrame with { Theme = crispTheme };
        var softPng = GlyphLiquidGlassRenderer.Render(softFrame, softWallpaper, previewMask, 0.0);
        var crispPng = GlyphLiquidGlassRenderer.Render(crispFrame, softWallpaper, previewMask, 0.0);
        // Compare decoded pixels: PNG bytes differ in length whenever the content differs at all,
        // so a byte-wise comparison of the encoded images says nothing about the material.
        var softPixels = DecodePixels(softPng);
        var crispPixels = DecodePixels(crispPng);
        var softDelta = softPixels != null && crispPixels != null
            ? MeanAbsoluteDifference(softPixels, crispPixels)
            : 0.0;
        Console.WriteLine($"  numerals: mean |delta| 柔光 vs 液态 at identical optics: {softDelta:F2}/255");
        Check("the soft recipe reaches the numerals (柔光玻璃 differs from 液态玻璃)", softDelta > 1.0);
        File.WriteAllBytes(Path.Combine(outputDir, "glyph-soft-glow.png"), softPng);
        softWallpaper.Dispose();

        // ---- Part 8: 跟随强调色 must resolve the accent actually in effect ----
        // Regression: the overlay read only Theme.AccentColor, so a user who left the accent on
        // 跟随系统强调色 (AccentColor == null) got a hard-coded Windows blue for the overlay no
        // matter what accent the rest of the app was using.
        Console.WriteLine();
        Console.WriteLine("--- overlay accent resolution ---");

        var overlayMethod = typeof(FramelessDigital).GetMethod(
            "ResolveOverlayColor", BindingFlags.NonPublic | BindingFlags.Static)!;

        Color Overlay(int opacityPercent, bool followAccent, string? customHex, string? themeAccent)
        {
            var overlayModel = new FramelessClockModel(
                EnableOverlay: true,
                FollowAccentColor: followAccent,
                OverlayColor: customHex ?? "#000000",
                OverlayOpacity: opacityPercent / 100.0);
            var overlayTheme = BuildSettings(SurfaceStyle.LiquidGlass).Theme with { AccentColor = themeAccent };
            return (Color)overlayMethod.Invoke(null, [overlayModel, overlayTheme])!;
        }

        static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // The host publishes the accent in effect here (ThemeService.ApplyAccent for a picked one,
        // Avalonia's Fluent theme for 跟随系统). Set it to a colour that is unmistakably not blue;
        // this is the last part of the run, so the fixture is left in place like the other checks
        // that drive the accent resource (CalendarHollowChecks, AccentPersistenceChecks).
        Application.Current!.Resources["SystemAccentColor"] = Color.Parse("#12C46A");

        var systemAccent = Overlay(100, true, null, null);
        Console.WriteLine($"  跟随系统强调色 → {Hex(systemAccent)}");
        Check("跟随强调色 with no picked accent uses the SystemAccentColor resource",
            systemAccent == Color.Parse("#12C46A"));

        var pickedAccent = Overlay(100, true, null, "#FF3B30");
        Console.WriteLine($"  手选强调色 → {Hex(pickedAccent)}");
        Check("跟随强调色 with a picked accent uses the picked colour",
            pickedAccent == Color.Parse("#FF3B30"));

        var custom = Overlay(100, false, "#FFCC00", "#FF3B30");
        Check("a custom 遮罩颜色 ignores the accent", custom == Color.Parse("#FFCC00"));

        var faint = Overlay(40, true, null, "#FF3B30");
        Check("the overlay carries the model's opacity",
            faint == Color.FromArgb((byte)(0.40 * 255), 0xFF, 0x3B, 0x30));

        Console.WriteLine();
        CheckWidgetGlyphsMatchTimeText();
        CheckV2GlyphMaterial();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckV2GlyphMaterial()
    {
        Console.WriteLine("--- V2 numeral optics and coverage ---");
        var theme = BuildSettings(SurfaceStyle.LiquidGlassV2).Theme with
        {
            OpacityLevel = 1,
            LiquidGlassV2 = new(Blur: 0, Refraction: 0, Highlight: 0, Vibrancy: 0, Dispersion: 0)
        };
        using var backdrop = new SKBitmap(96, 64);
        for (var y = 0; y < 64; y++)
        for (var x = 0; x < 96; x++)
            backdrop.SetPixel(x, y, new SKColor((byte)(40 + x * 2), (byte)(50 + x), (byte)(60 + x)));
        using var wallpaper = WallpaperSnapshot.FromBitmap(null, SKColors.Black, backdrop.Copy(), live: true);
        var frame = new LiquidGlassRenderer.Frame(96, 64, 1, 0, 0, 0, 96, 64, 0, 0, 96, 64, theme, true);
        var mask = new byte[96 * 64];
        for (var y = 8; y < 56; y++)
        for (var x = 8; x < 88; x++) mask[y * 96 + x] = 255;
        var png = GlyphLiquidGlassRenderer.Render(frame, wallpaper, mask);
        using var output = SKBitmap.Decode(png);
        Check("frameless clock opacity defaults to 100%", new FramelessClockModel().LiquidGlassOpacity == 100);
        var opacity0 = GlyphLiquidGlassRenderer.Render(frame with { GlyphOpacity = 0 }, wallpaper, mask);
        var opacity100 = GlyphLiquidGlassRenderer.Render(frame with { GlyphOpacity = 1 }, wallpaper, mask);
        var opacity200 = GlyphLiquidGlassRenderer.Render(frame with { GlyphOpacity = 2 }, wallpaper, mask);
        Check("V2 component opacity 0/100/200 produces distinct glyph output",
            !opacity0.SequenceEqual(opacity100) && !opacity100.SequenceEqual(opacity200));
        // Distinct bytes are not enough: a flat multiple of the tiny V2 baseline shifts the
        // coating by a few percent and the slider reads as dead. 200% must be visibly more
        // coated than 100% (roughly a quarter of the numerals' full range, not ~2%).
        Check("V2 opacity 200% coats visibly more than 100%",
            MeanAbsoluteDifference(DecodePixels(opacity100), DecodePixels(opacity200)) > 8);
        var legacyFrame = frame with { Theme = theme with { Surface = SurfaceStyle.LiquidGlass }, GlyphOpacity = 0 };
        var legacyDefault = GlyphLiquidGlassRenderer.Render(legacyFrame with { GlyphOpacity = 1 }, wallpaper, mask);
        Check("legacy glass ignores the component opacity", legacyDefault.SequenceEqual(GlyphLiquidGlassRenderer.Render(legacyFrame, wallpaper, mask)));
        Check("maximum V2 opacity keeps wallpaper contrast in the numeral centre",
            output.GetPixel(70, 32).Red - output.GetPixel(30, 32).Red > 55);
        var contaminated = frame with { Theme = theme with { LiquidGlass = new(Blur: 100, Refraction: 100, EdgeWidth: 80, Highlight: 100, Dispersion: 100, EdgeTint: 100, Glow: 100, Spectrum: 100), AccentColor = "#FFFF00" } };
        Check("V2 ignores all retained legacy and soft optics", png.SequenceEqual(GlyphLiquidGlassRenderer.Render(contaminated, wallpaper, mask)));
        var refracted = GlyphLiquidGlassRenderer.Render(frame with { Theme = theme with { LiquidGlassV2 = theme.EffectiveLiquidGlassV2 with { Refraction = 80 } } }, wallpaper, mask);
        Check("V2 refraction zero removes displacement; its own slider responds", !png.SequenceEqual(refracted));
        foreach (var control in new[] { "Blur", "Vibrancy", "Dispersion" })
        {
            var settings = theme.EffectiveLiquidGlassV2 with { Refraction = 80 };
            var low = frame with { Theme = theme with { LiquidGlassV2 = settings } };
            var highSettings = control switch
            {
                "Blur" => settings with { Blur = 100 },
                "Vibrancy" => settings with { Vibrancy = 100 },
                _ => settings with { Dispersion = 100 }
            };
            var high = frame with { Theme = theme with { LiquidGlassV2 = highSettings } };
            Check($"V2 glyph responds to its own {control} control",
                !GlyphLiquidGlassRenderer.Render(low, wallpaper, mask).SequenceEqual(GlyphLiquidGlassRenderer.Render(high, wallpaper, mask)));
        }
        var highlighted = GlyphLiquidGlassRenderer.Render(frame with { Theme = theme with { LiquidGlassV2 = theme.EffectiveLiquidGlassV2 with { Highlight = 100 } } }, wallpaper, mask);
        using var lit = SKBitmap.Decode(highlighted);
        Check("V2 highlight reaches inward and fades toward the centre",
            lit.GetPixel(12, 32).Red - output.GetPixel(12, 32).Red > 2
            && lit.GetPixel(48, 32).Red - output.GetPixel(48, 32).Red < 2);
        using var neutral = new WallpaperSnapshot(null, new SKColor(60, 90, 120));
        var alphaFrame = frame with { Width = 16, Height = 16, Theme = theme with { OpacityLevel = 0 } };
        foreach (byte coverage in new byte[] { 64, 128, 192, 255 })
        {
            using var alphaOutput = SKBitmap.Decode(GlyphLiquidGlassRenderer.Render(alphaFrame, neutral, Enumerable.Repeat(coverage, 256).ToArray()));
            var pixel = alphaOutput.GetPixel(8, 8);
            Check($"V2 {coverage}/255 coverage preserves straight RGB without a bright fringe",
                pixel.Alpha == coverage && Math.Abs(pixel.Red - 64) < 5 && Math.Abs(pixel.Green - 93) < 5 && Math.Abs(pixel.Blue - 122) < 5);
        }
        foreach (byte coverage in new byte[] { 1, 8, 16 })
        {
            using var alphaOutput = SKBitmap.Decode(GlyphLiquidGlassRenderer.Render(alphaFrame, neutral, Enumerable.Repeat(coverage, 256).ToArray()));
            Check($"V2 retains low antialias coverage {coverage}/255", alphaOutput.GetPixel(8, 8).Alpha == coverage);
            using var composite = new SKBitmap(16, 16);
            using (var canvas = new SKCanvas(composite))
            {
                canvas.Clear(SKColors.Black);
                canvas.DrawBitmap(alphaOutput, 0, 0);
            }
            Check($"V2 low coverage {coverage}/255 cannot brighten the contour",
                composite.GetPixel(8, 8).Red <= coverage * 0.3f + 1);
        }
        Check("V2 leaves pixels outside the exact numeral mask transparent", output.GetPixel(0, 0).Alpha == 0);
    }

    /// <summary>
    /// The pre-render pipeline must lay glyphs out from the bare time text: the frame cache key
    /// additionally carries the glass-opacity suffix, and feeding it to the geometry renders the
    /// suffix as extra characters on the desktop. The check rebuilds the time-text mask with the
    /// widget's own geometry pipeline and requires the produced frame to be empty outside it.
    /// </summary>
    private static void CheckWidgetGlyphsMatchTimeText()
    {
        Console.WriteLine();
        Console.WriteLine("--- widget glyphs follow the time text, not the cache key ---");
        var settings = new StubSettings(BuildSettings(SurfaceStyle.LiquidGlassV2));
        const double width = 368, height = 184;
        var model = new FramelessClockModel(Use24Hours: true, FontFamily: "Impact", FontWeight: 800, StretchFill: true);
        var view = new FramelessDigital(model, new StubLayout(), settings) { Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && ReadField(view, "liquidGlassBitmap") == null)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
        Dispatcher.UIThread.RunJobs();

        var frames = ReadField(ReadField(view, "frameCache"), "frames") as System.Collections.IDictionary;
        Check("the widget pre-rendered keyed frames", frames != null && frames.Count > 0);
        if (frames == null || frames.Count == 0) return;

        var key = (string)frames.Keys.Cast<object>().First()!;
        var separator = key.IndexOf('|');
        Check("the frame cache key carries the glass-opacity suffix",
            separator > 0 && key.Contains("glassOpacity:"));
        if (separator <= 0) return;
        var timeText = key[..separator];

        // No window in this harness, so the widget rendered at scaling 1 — the exact mask it was
        // supposed to fill can be rebuilt with the same geometry calls and compared pixel-wise.
        var geometryType = typeof(FramelessDigital).Assembly.GetType("Clock.Services.FramelessGlyphGeometry")!;
        var geometry = geometryType.InvokeMember("BuildStretch", BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod,
            null, null, [timeText, width, height, model.FontFamily, model.FontWeight, model.StretchFill, settings.Get().Theme]);
        Check("the bare time text lays out a glyph geometry", geometry != null);
        if (geometry == null) return;
        var mask = (byte[]?)geometryType.InvokeMember("ExtractMask", BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod,
            null, null, [geometry, width, height, 1.0, (int)width, (int)height]);
        Check("the time text mask matches the frame size", mask != null && mask.Length == (int)(width * height));
        if (mask == null) return;

        var path = Path.Combine(outputDir, "widget-time-text.png");
        ((Bitmap)ReadField(view, "liquidGlassBitmap")!).Save(path);
        using var rendered = SKBitmap.Decode(path);
        Check("the widget's liquid glass frame decodes at the mask size",
            rendered != null && rendered.Width == (int)width && rendered.Height == (int)height);
        if (rendered == null) return;

        var strayInk = 0;
        for (var y = 0; y < rendered.Height; y++)
        for (var x = 0; x < rendered.Width; x++)
        {
            if (mask[y * rendered.Width + x] == 0 && rendered.GetPixel(x, y).Alpha != 0) strayInk++;
        }
        Check($"no glyph ink outside the time text ({strayInk} stray pixels) — the cache key suffix must never render",
            strayInk == 0);
    }

    private static string Describe((bool IsAcrylic, bool IsLiquidGlass, bool IsSolid) theme) =>
        theme switch
        {
            (true, _, _) => "acrylic",
            (_, true, _) => "liquid glass",
            _ => "solid"
        };

    private static void CheckNativeMaterialRoundTrip()
    {
        Console.WriteLine();
        Console.WriteLine("--- native host transparency round trip ---");
        var host = new Window { Width = 368, Height = 184, ShowInTaskbar = false };
        var apply = typeof(FramelessDigital).GetMethod("ApplyWindowTransparency",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var platform = host.PlatformImpl!;
        var surface = platform.GetType().GetProperty("CompositionEffectsSurface",
            BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(platform);
        var effect = surface?.GetType().GetField("_blurEffect",
            BindingFlags.NonPublic | BindingFlags.Instance);

        if (surface == null || effect == null)
        {
            Console.WriteLine("  SKIP: WinUI composition blur probe is unavailable on this platform.");
            host.Close();
            return;
        }

        void Apply(SurfaceStyle style) => apply.Invoke(null,
            [host, FramelessThemeResolver.Resolve(BuildSettings(style).Theme).IsAcrylic]);

        Apply(SurfaceStyle.LiquidGlass);
        for (var round = 1; round <= 2; round++)
        {
            Apply(SurfaceStyle.Acrylic);
            Check($"native round {round}: acrylic activates the composition brush",
                host.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                && effect.GetValue(surface)?.ToString() == "Acrylic");
            Apply(SurfaceStyle.LiquidGlass);
            Check($"native round {round}: liquid glass clears the composition brush",
                host.ActualTransparencyLevel == WindowTransparencyLevel.Transparent
                && effect.GetValue(surface)?.ToString() == "None");
        }
        host.Close();
    }

    /// <summary>Pump the dispatcher until the widget produced a liquid glass frame.</summary>
    private static bool WaitForFrame(FramelessDigital view)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && ReadField(view, "liquidGlassBitmap") == null)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }

        Dispatcher.UIThread.RunJobs();
        return ReadField(view, "liquidGlassBitmap") != null;
    }

    /// <summary>Pixels of a rendered widget snapshot, plus whether it produced a glyph glass frame.</summary>
    private sealed record RenderResult(byte[]? Pixels, bool ProducedFrame);

    /// <summary>
    /// Render the widget off-screen at the real 4×2 grid size under <paramref name="provider"/>'s
    /// global theme. The liquid glass material is produced by a background task, so the dispatcher
    /// is pumped until the widget's pre-rendered bitmap lands (or the timeout elapses) — a
    /// non-liquid-glass theme produces none, which is what <see cref="RenderResult.ProducedFrame"/>
    /// reports.
    /// </summary>
    private static RenderResult Render(IAppSettingsProvider provider, StubLayout layout, string dir,
        string name, bool expectFrame)
    {
        const double width = 368, height = 184;

        var model = new FramelessClockModel(
            Use24Hours: true, FontFamily: "Impact", FontWeight: 800, StretchFill: true);

        var view = new FramelessDigital(model, layout, provider) { Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        // Wait long enough for the optical render when a frame is expected; a theme that never
        // starts the pipeline only needs a short grace period before it is declared absent.
        var deadline = DateTime.UtcNow.AddSeconds(expectFrame ? 20 : 2);
        while (DateTime.UtcNow < deadline && ReadField(view, "liquidGlassBitmap") == null)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(50);
        }
        Dispatcher.UIThread.RunJobs();

        var produced = ReadField(view, "liquidGlassBitmap") != null;
        Console.WriteLine($"  {name}: liquidGlassBitmap={(produced ? "ready" : "NOT PRODUCED")}");

        const double scale = 2.0;
        var pixelSize = new PixelSize((int)(width * scale), (int)(height * scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(view);

        var path = Path.Combine(dir, $"{name}.png");
        bitmap.Save(path);
        Console.WriteLine($"  {name}: saved {path}");

        return new RenderResult(LoadPixels(path), produced);
    }

    /// <summary>Decode a PNG into BGRA bytes (SkiaSharp, so no unsafe pointer juggling).</summary>
    private static byte[]? LoadPixels(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;

        var pixels = new byte[decoded.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>Decode a PNG byte buffer into BGRA bytes.</summary>
    private static byte[]? DecodePixels(byte[] png)
    {
        using var decoded = SKBitmap.Decode(png);
        if (decoded == null) return null;
        var pixels = new byte[decoded.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>Count pixels whose red / blue channel dominates (BGRA order).</summary>
    private static (int Red, int Blue) CountDominant(byte[]? bgra)
    {
        if (bgra == null) return (0, 0);
        int red = 0, blue = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            int b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            if (r > g + 40 && r > b + 40) red++;
            else if (b > g + 40 && b > r + 40) blue++;
        }
        return (red, blue);
    }

    private static double MeanAbsoluteDifference(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return double.MaxValue;
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    private static AppSettings BuildSettings(SurfaceStyle surface) => new(
        new Theme(
            DarkMode: true,
            AccentColor: null,
            OpacityLevel: 0.8,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Segoe UI",
            Surface: surface),
        Templates: [],
        new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(72, 8, 16),
        new Region("zh-Hans"),
        RunOnStartup: false,
        IgnoreUpdate: null);

    private static object? ReadField(object target, string name) => target
        .GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(target);

    private static (bool IsAcrylic, bool IsLiquidGlass, bool IsSolid) ResolveTheme(object clock)
    {
        var method = clock.GetType().GetMethod("ResolveEffectiveTheme", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((bool, bool, bool))method.Invoke(clock, null)!;
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    private sealed class StubSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging;
        public event DataChangedEvent<AppSettings>? DataChanged;

        public AppSettings Get() => value;

        public void Save(AppSettings data)
        {
            var old = value;
            DataChanging?.Invoke(this, old, data);
            value = data;
            DataChanged?.Invoke(this, old, data);
        }
    }

    private sealed class StubLayout : IWidgetLayoutProvider
    {
        public event DataChangedEvent<WidgetLayout>? DataChanging;
        public event DataChangedEvent<WidgetLayout>? DataChanged;

        public string ScreenId { get; set; } = "probe";

        public WidgetLayout Get() => new("Clock", "FramelessDigital", 0, 0, 368, 184, null);

        public void Save(WidgetLayout data)
        {
            DataChanging?.Invoke(this, data, data);
            DataChanged?.Invoke(this, data, data);
        }

        public void Remove() { }
    }
}
