using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

var output = Path.GetFullPath(args.FirstOrDefault() ?? "dist/glass-checks");
Directory.CreateDirectory(output);

if (args.ElementAtOrDefault(0) == "wallpaper-layout")
{
    WallpaperLayoutCheck.Run(args.ElementAtOrDefault(1) ?? "dist/pw-Progman.png",
        args.ElementAtOrDefault(2) ?? "dist/glass-layout",
        args.ElementAtOrDefault(3) ?? "22", args.ElementAtOrDefault(4) == "1");
    return;
}

if (args.ElementAtOrDefault(0) == "layout-scan")
{
    WallpaperLayoutCheck.LayoutScan(args.ElementAtOrDefault(1)!, args.ElementAtOrDefault(2)!,
        args.ElementAtOrDefault(3) ?? "dist/glass-layout");
    return;
}

if (args.ElementAtOrDefault(0) == "verify-widget")
{
    WallpaperLayoutCheck.VerifyWidget(args.ElementAtOrDefault(1)!,
        args.ElementAtOrDefault(2)!, args.ElementAtOrDefault(3)!, args.ElementAtOrDefault(4) == "live");
    return;
}

var oldTheme = JsonSerializer.Deserialize<Theme>("""
    {"DarkMode":null,"AccentColor":null,"OpacityLevel":0.4,"Monochrome":true,"UseNativeFrame":false,"FontFamily":"Inter"}
    """)!;
Check(oldTheme.EffectiveSurface == SurfaceStyle.Acrylic && oldTheme.UsesNativeBlur, "old acrylic config migration");
Check((oldTheme with { OpacityLevel = 1 }).EffectiveSurface == SurfaceStyle.Solid, "old solid config migration");
Check(oldTheme.EffectiveLiquidGlass == new LiquidGlassSettings(), "old config receives glass defaults");
var invalid = new LiquidGlassSettings(double.NaN, 300, -1, double.PositiveInfinity, -9, 900).Normalize();
Check(invalid == new LiquidGlassSettings(100, 100, 0, 50, 0, 360), "invalid optics are clamped");
Check(new LiquidGlassSettings(12, 28, 0, 65, 18, 225).Normalize().EdgeWidth == 0,
    "EdgeWidth 0 survives validation (no lens ring is a legal setting)");
var offsetClamp = new LiquidGlassSettings(0, 0, 4, 0, 0, 0, EdgeTint: 0, WallpaperOffsetX: 2500, WallpaperOffsetY: -9999).Normalize();
Check(offsetClamp.WallpaperOffsetX == LiquidGlassSettings.WallpaperOffsetLimit && offsetClamp.WallpaperOffsetY == -LiquidGlassSettings.WallpaperOffsetLimit,
    "wallpaper offsets are clamped");
Check(new LiquidGlassSettings(double.NaN, 300, -1, double.PositiveInfinity, -9, 900, 400, 2500, -9999).Normalize()
        == new LiquidGlassSettings(100, 100, 0, 50, 0, 360, 100, 1000, -1000),
    "edge tint and offsets are clamped together");

// Live sampling: the floor is 3 ms ("as fast as this machine can go"), and the sampler is
// demand-driven, so a very small interval asks for frames rather than grabbing the desktop
// hundreds of times a second.
Check(LiquidGlassSettings.MinLiveSamplingInterval == 3, "the fastest sampling interval is 3 ms");
Check(LiquidGlassSettings.MaxLiveSamplingInterval == 1000, "the slowest sampling interval is 1000 ms");
Check(new LiquidGlassSettings(LiveSamplingInterval: 1).Normalize().LiveSamplingInterval == 3,
    "a sub-3 ms sampling interval clamps up to 3 ms");
Check(new LiquidGlassSettings(LiveSamplingInterval: 99999).Normalize().LiveSamplingInterval == 1000,
    "an absurd sampling interval clamps down to 1000 ms");
Check(new LiquidGlassSettings().LiveSamplingInterval == LiquidGlassSettings.DefaultLiveSamplingInterval,
    "the default sampling interval is unchanged (5 ms — as fast as this machine goes)");
Check(!new LiquidGlassSettings().LiveSampling, "live sampling is off by default (the static material)");

// 背景清晰度 is the knob that trades the sampling round's cost against backdrop detail, and it is
// the user's own setting — so it must survive validation untouched across its whole range and only
// be clamped at the documented floor. Silently capping it is the defect it exists to fix.
Check(new LiquidGlassSettings().BackdropClarity == LiquidGlassSettings.DefaultBackdropClarity,
    "backdrop clarity defaults to 25% (cheap backdrop builds, blur hides the reduced grid)");
Check(new LiquidGlassSettings(BackdropClarity: 100).Normalize().BackdropClarity == 100,
    "backdrop clarity 100% (native resolution) survives validation");
Check(new LiquidGlassSettings(BackdropClarity: 25).Normalize().BackdropClarity == 25,
    "backdrop clarity 25% (the floor) survives validation");
Check(new LiquidGlassSettings(BackdropClarity: 1).Normalize().BackdropClarity == LiquidGlassSettings.MinBackdropClarity,
    "a sub-floor backdrop clarity clamps up to the minimum");
Check(new LiquidGlassSettings(BackdropClarity: 9999).Normalize().BackdropClarity == 100,
    "an absurd backdrop clarity clamps down to native");
Check(double.IsFinite(new LiquidGlassSettings(BackdropClarity: double.NaN).Normalize().BackdropClarity),
    "a non-finite backdrop clarity falls back to the default");
Check(JsonSerializer.Deserialize<LiquidGlassSettings>(JsonSerializer.Serialize(
        new LiquidGlassSettings(BackdropClarity: 80)))!.BackdropClarity == 80,
    "backdrop clarity survives a JSON round trip");
var theme = oldTheme with { Surface = SurfaceStyle.LiquidGlass, OpacityLevel = 0.18, LiquidGlass = new(7, 55, 32, 80, 40, 225) };
Check(theme.IsGlass && theme.IsLiquidGlass && !theme.UsesNativeBlur, "liquid glass disables fixed native blur");
var restored = JsonSerializer.Deserialize<Theme>(JsonSerializer.Serialize(theme))!;
Check(restored == theme, "all glass parameters survive JSON round trip");

var defaultAttr = new DeskCanvas.Core.Models.Attributes.WidgetInfoAttribute(typeof(object));
Check(defaultAttr.DefaultColumns == 2 && defaultAttr.DefaultRows == 2, "WidgetInfoAttribute default 2x2");
var customAttr = new DeskCanvas.Core.Models.Attributes.WidgetInfoAttribute(typeof(object), defaultColumns: 1, defaultRows: 1);
Check(customAttr.DefaultColumns == 1 && customAttr.DefaultRows == 1, "WidgetInfoAttribute custom 1x1");

var toolsDll = Path.GetFullPath("src/DeskCanvas/bin/Debug/net8.0/Widgets/Tools.dll");
if (File.Exists(toolsDll))
{
    AppDomain.CurrentDomain.AssemblyResolve += (_, ea) =>
    {
        var name = new System.Reflection.AssemblyName(ea.Name).Name;
        var cand1 = Path.GetFullPath($"src/DeskCanvas/bin/Debug/net8.0/{name}.dll");
        if (File.Exists(cand1)) return System.Reflection.Assembly.LoadFrom(cand1);
        var cand2 = Path.GetFullPath($"src/DeskCanvas/bin/Debug/net8.0/Widgets/{name}.dll");
        if (File.Exists(cand2)) return System.Reflection.Assembly.LoadFrom(cand2);
        return null;
    };
    var toolsAsm = System.Reflection.Assembly.LoadFrom(toolsDll);
    var localeAttr = toolsAsm.GetCustomAttributes(typeof(DeskCanvas.Core.Models.Attributes.LocaleAttribute), false).FirstOrDefault() as DeskCanvas.Core.Models.Attributes.LocaleAttribute;
    Check(localeAttr != null && localeAttr.DisplayName == "Tools", "Tools assembly defines LocaleAttribute with Tools category");

    var widgetInfos = System.Reflection.CustomAttributeData.GetCustomAttributes(toolsAsm)
        .Where(cad => cad.AttributeType.Name == "WidgetInfoAttribute")
        .ToList();
    Check(widgetInfos.Count == 2, "Tools assembly exports exactly 2 widgets (Clipboard & Translator)");
    Check(widgetInfos.Any(w => w.ConstructorArguments[0].Value?.ToString()?.Contains("ClipboardView") == true), "Tools exports ClipboardView");
    Check(widgetInfos.Any(w => w.ConstructorArguments[0].Value?.ToString()?.Contains("TranslatorView") == true), "Tools exports TranslatorView");

    var youdaoType = toolsAsm.GetType("Tools.Services.Translation.YoudaoTranslationEngine");
    if (youdaoType != null)
    {
        var engine = Activator.CreateInstance(youdaoType);
        var method = youdaoType.GetMethod("TranslateAsync");
        if (engine != null && method != null)
        {
            var taskZh = method.Invoke(engine, new object?[] { "hello", "auto", "zh", default(CancellationToken) }) as Task<string>;
            var resZh = taskZh?.GetAwaiter().GetResult();
            Check(!string.IsNullOrWhiteSpace(resZh), $"Youdao translation (auto->zh) works and returned '{resZh?.Trim()}'");

            var taskJa = method.Invoke(engine, new object?[] { "你好", "auto", "ja", default(CancellationToken) }) as Task<string>;
            var resJa = taskJa?.GetAwaiter().GetResult();
            Check(resJa?.Contains("こんにちは") == true, $"Youdao translation (auto->ja) works and returned '{resJa?.Trim()}'");

            var taskEn = method.Invoke(engine, new object?[] { "你好", "auto", "en", default(CancellationToken) }) as Task<string>;
            var resEn = taskEn?.GetAwaiter().GetResult();
            Check(resEn?.IndexOf("hello", StringComparison.OrdinalIgnoreCase) >= 0, $"Youdao translation (auto->en) works and returned '{resEn?.Trim()}'");

            // Verify pivot translation for foreign-to-foreign language pairs (e.g. English -> Japanese)
            var taskEnToJa = method.Invoke(engine, new object?[] { "Good morning", "auto", "ja", default(CancellationToken) }) as Task<string>;
            var resEnToJa = taskEnToJa?.GetAwaiter().GetResult();
            Check(resEnToJa?.Contains("おはよう") == true || resEnToJa?.Contains("こんにちは") == true,
                $"Youdao pivot translation (auto(en)->ja) works and returned Japanese: '{resEnToJa?.Trim()}'");

            var taskExplicitEnToJa = method.Invoke(engine, new object?[] { "hello", "en", "ja", default(CancellationToken) }) as Task<string>;
            var resExplicitEnToJa = taskExplicitEnToJa?.GetAwaiter().GetResult();
            Check(resExplicitEnToJa?.Contains("こんにちは") == true,
                $"Youdao pivot translation (en->ja) works and returned Japanese: '{resExplicitEnToJa?.Trim()}'");

            var taskExplicitEnToFr = method.Invoke(engine, new object?[] { "hello", "en", "fr", default(CancellationToken) }) as Task<string>;
            var resExplicitEnToFr = taskExplicitEnToFr?.GetAwaiter().GetResult();
            Check(resExplicitEnToFr?.IndexOf("Bonjour", StringComparison.OrdinalIgnoreCase) >= 0,
                $"Youdao pivot translation (en->fr) works and returned French: '{resExplicitEnToFr?.Trim()}'");
        }
    }
}

var searchDll = Path.GetFullPath("src/DeskCanvas/bin/Debug/net8.0/Widgets/Search.dll");
if (File.Exists(searchDll))
{
    var searchAsm = System.Reflection.Assembly.LoadFrom(searchDll);
    var localeAttr = searchAsm.GetCustomAttributes(typeof(DeskCanvas.Core.Models.Attributes.LocaleAttribute), false).FirstOrDefault() as DeskCanvas.Core.Models.Attributes.LocaleAttribute;
    Check(localeAttr != null && localeAttr.DisplayName == "Search", "Search assembly defines LocaleAttribute with Search category");

    var widgetInfos = System.Reflection.CustomAttributeData.GetCustomAttributes(searchAsm)
        .Where(cad => cad.AttributeType.Name == "WidgetInfoAttribute")
        .ToList();
    Check(widgetInfos.Count == 1, "Search assembly exports exactly 1 widget (SearchView)");
    Check(widgetInfos[0].ConstructorArguments[0].Value?.ToString()?.Contains("SearchView") == true, "Search exports SearchView");

    var launcherType = searchAsm.GetType("Search.Services.SearchLauncher");
    var engineType = searchAsm.GetType("Search.Models.SearchEngine");
    if (launcherType != null && engineType != null)
    {
        var buildUrlMethod = launcherType.GetMethod("BuildUrl");
        var engInstance = Activator.CreateInstance(engineType);
        engineType.GetProperty("UrlTemplate")?.SetValue(engInstance, "https://www.google.com/search?q={q}");
        var urlResult = buildUrlMethod?.Invoke(null, new[] { engInstance, "hello world" }) as string;
        Check(urlResult == "https://www.google.com/search?q=hello%20world", "SearchLauncher correctly builds URL with query encoding");
    }

    var viewType = searchAsm.GetType("Search.Views.SearchView");
    if (viewType != null)
    {
        try
        {
            var inst = Activator.CreateInstance(viewType);
            Check(inst != null, "SearchView default ctor creates instance");

            var asmProvider = new DeskCanvas.Core.Services.AssemblyProvider(new EmptyServiceProvider());

            // Gallery mode: WidgetFactory.CreateControl invokes Activate with empty args
            var instGallery = asmProvider.Activate(viewType);
            Check(instGallery != null, "AssemblyProvider.Activate creates SearchView for Gallery preview (empty args)");

            // Desktop mode: WidgetFactory.CreateInternal invokes Activate with [ model ]
            var modelType = searchAsm.GetType("Search.Models.SearchModel")!;
            var modelInst = Activator.CreateInstance(modelType)!;
            var instDesktop = asmProvider.Activate(viewType, modelInst);
            Check(instDesktop != null, "AssemblyProvider.Activate creates SearchView with model for desktop placement");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL: SearchView ctor check threw: {ex}");
        }
    }
}

System.Reflection.Assembly? clockAsm = null;
var clockDll = Path.GetFullPath("src/DeskCanvas/bin/Debug/net8.0/Widgets/Clock.dll");
if (File.Exists(clockDll))
{
    clockAsm = System.Reflection.Assembly.LoadFrom(clockDll);
    var widgetInfos = System.Reflection.CustomAttributeData.GetCustomAttributes(clockAsm)
        .Where(cad => cad.AttributeType.Name == "WidgetInfoAttribute")
        .ToList();
    var framelessInfo = widgetInfos.FirstOrDefault(w => w.ConstructorArguments[0].Value?.ToString()?.Contains("FramelessDigital") == true);
    Check(framelessInfo != null, "Clock assembly exports FramelessDigital");

    var framelessViewType = clockAsm.GetType("Clock.Views.FramelessDigital");
    Check(framelessViewType != null, "Clock defines Clock.Views.FramelessDigital");
    if (framelessViewType != null)
    {
        Check(typeof(DeskCanvas.Core.Interfaces.IFramelessWidget).IsAssignableFrom(framelessViewType), "FramelessDigital implements IFramelessWidget");
        Check(typeof(DeskCanvas.Core.Interfaces.IWidgetSelfRefreshing).IsAssignableFrom(framelessViewType), "FramelessDigital implements IWidgetSelfRefreshing");

        var modelType = clockAsm.GetType("Clock.Models.FramelessClockModel");
        Check(modelType != null, "Clock defines FramelessClockModel");
        if (modelType != null)
        {
            var modelInst = Activator.CreateInstance(modelType);
            var weightProp = modelType.GetProperty("FontWeight");
            Check(weightProp != null && (int)weightProp.GetValue(modelInst)! == 700, "FramelessClockModel has FontWeight with default 700");
            var stretchProp = modelType.GetProperty("StretchFill");
            Check(stretchProp != null && (bool)stretchProp.GetValue(modelInst)! == true, "FramelessClockModel has StretchFill with default true");
            // The clock is locked to the 毛玻璃 material and carries no glass-parameter overrides:
            // the material itself, the lens width, the edge dye and the coating opacity are not
            // widget settings, so none of these properties may come back.
            Check(modelType.GetProperty("DyeIntensity") == null, "FramelessClockModel no longer declares DyeIntensity");
            Check(modelType.GetProperty("RefractionWidth") == null, "FramelessClockModel no longer declares RefractionWidth");
            Check(modelType.GetProperty("LiquidGlassOpacity") == null, "FramelessClockModel no longer declares LiquidGlassOpacity");
        }

        var asmProvider = new DeskCanvas.Core.Services.AssemblyProvider(new EmptyServiceProvider());
        var instGallery = asmProvider.Activate(framelessViewType);
        Check(instGallery != null, "AssemblyProvider.Activate creates FramelessDigital instance for preview");
    }
}

var foldersDll = Path.GetFullPath("src/DeskCanvas/bin/Debug/net8.0/Widgets/Folders.dll");
if (File.Exists(foldersDll))
{
    var foldersAsm = System.Reflection.Assembly.LoadFrom(foldersDll);
    var iconServiceType = foldersAsm.GetType("Folders.Services.FolderIconService");
    Check(iconServiceType != null, "FolderIconService type exists in Folders.dll");
    if (iconServiceType != null)
    {
        var getIconMethod = iconServiceType.GetMethod("GetIcon", new[] { typeof(string) });
        Check(getIconMethod != null, "FolderIconService.GetIcon method exists");

        // Verify IShellItemImageFactory extracts 256x256 HD icon for system executables
        var factoryCheck = VerifyShellItemImageFactory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
        Check(factoryCheck.Width >= 128 && factoryCheck.Height >= 128,
            $"IShellItemImageFactory extracts high resolution icon (>= 128px): actual is {factoryCheck.Width}x{factoryCheck.Height}");

        var cmdCheck = VerifyShellItemImageFactory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
        Check(cmdCheck.Width >= 128 && cmdCheck.Height >= 128,
            $"IShellItemImageFactory extracts high resolution icon for cmd.exe: actual is {cmdCheck.Width}x{cmdCheck.Height}");
    }
}


using var wallpaperSurface = SKSurface.Create(new SKImageInfo(1200, 800));
var canvas = wallpaperSurface.Canvas;
using var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1200, 800),
    new[] { new SKColor(20, 90, 122), new SKColor(65, 139, 150), new SKColor(220, 161, 112), new SKColor(105, 60, 127) }, null, SKShaderTileMode.Clamp);
using var paint = new SKPaint { Shader = gradient, IsAntialias = true };
canvas.DrawPaint(paint);
paint.Shader = null;
for (var x = -800; x < 1200; x += 90)
{
    paint.Color = new SKColor(240, 249, 250, 55);
    using var path = new SKPath();
    path.MoveTo(x, 0); path.LineTo(x + 30, 0); path.LineTo(x + 830, 800); path.LineTo(x + 800, 800); path.Close();
    canvas.DrawPath(path, paint);
}
paint.Color = new SKColor(250, 222, 178);
canvas.DrawCircle(860, 180, 80, paint);
using var wallpaperImage = wallpaperSurface.Snapshot();
using var wallpaperData = wallpaperImage.Encode(SKEncodedImageFormat.Png, 100);
var wallpaper = new WallpaperSnapshot(wallpaperData.ToArray(), SKColors.Black);
var frame = new LiquidGlassRenderer.Frame(320, 240, 1, 32, 100, 240, 1200, 800, 0, 0, 1200, 800, theme, false);
var timer = Stopwatch.StartNew();
var normalBytes = LiquidGlassRenderer.Render(frame, wallpaper);
using var normal = SKBitmap.Decode(normalBytes);
Check(normal.GetPixel(0, 0).Alpha == 0 && normal.GetPixel(160, 120).Alpha == 255, "rounded clipping and solid center");
using var clear = Decode(theme with { LiquidGlass = new(0, 0, 32, 0, 0, 225, EdgeTint: 0), OpacityLevel = 0 });
using var blurry = Decode(theme with { LiquidGlass = new(60, 0, 32, 0, 0, 225, EdgeTint: 0), OpacityLevel = 0 });
Check(Difference(clear, blurry, false) > 4, "blur changes wallpaper independently of content");
using var refracted = Decode(theme with { LiquidGlass = new(0, 100, 50, 0, 0, 225, EdgeTint: 0), OpacityLevel = 0 });
Check(Difference(clear, refracted, true) > 3, "refraction changes edge image samples");
using var lit = Decode(theme with { LiquidGlass = new(0, 0, 32, 100, 0, 225, EdgeTint: 0), OpacityLevel = 0 });
Console.WriteLine($"Highlight mean RGB delta: {Difference(clear, lit, true):F2}");
Check(Difference(clear, lit, true) > 2, "highlight strength changes edge lighting");
using var dispersed = Decode(theme with { LiquidGlass = new(0, 100, 50, 0, 100, 225, EdgeTint: 0), OpacityLevel = 0 });
Check(Difference(refracted, dispersed, true) > 0.3, "dispersion separates RGB samples");
using var opposite = Decode(theme with { LiquidGlass = new(0, 0, 32, 100, 0, 45, EdgeTint: 0), OpacityLevel = 0 });
Console.WriteLine($"Light direction mean RGB delta: {Difference(lit, opposite, true):F2}");
Check(Difference(lit, opposite, true) > 1, "light direction moves highlights");
using var dyed = Decode(theme with { LiquidGlass = new(0, 0, 32, 0, 0, 225, EdgeTint: 100), OpacityLevel = 0 });
Console.WriteLine($"Edge dye mean RGB delta (border/center): {Difference(clear, dyed, true):F2} / {Difference(clear, dyed, false):F2}");
Check(Difference(clear, dyed, true) > 12, "edge tint stains the border band");
Check(Difference(clear, dyed, false) < Difference(clear, dyed, true) * 0.6, "edge tint is concentrated at the border");
// The rim color must follow the local wallpaper colors: test with a deterministic
// split wallpaper (top half pure blue, bottom half pure red) — the top rim must be
// clearly blue (B-R >> 0) and the bottom rim clearly red (B-R << 0).
{
    using var splitSurface = SKSurface.Create(new SKImageInfo(1200, 800));
    splitSurface.Canvas.Clear(new SKColor(0x00, 0x40, 0xFF));
    using var splitPaint = new SKPaint { Color = new SKColor(0xFF, 0x40, 0x00) };
    splitSurface.Canvas.DrawRect(new SKRect(0, 400, 1200, 800), splitPaint);
    using var splitImage = splitSurface.Snapshot();
    using var splitData = splitImage.Encode(SKEncodedImageFormat.Png, 100);
    using var splitDyed = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with
    {
        Theme = theme with { LiquidGlass = new(0, 0, 32, 0, 0, 225, EdgeTint: 100), OpacityLevel = 0 }
    }, new WallpaperSnapshot(splitData.ToArray(), SKColors.Black)));
    var top = BandColor(splitDyed, 2, 10);
    var bottom = BandColor(splitDyed, splitDyed.Height - 10, splitDyed.Height - 2);
    Console.WriteLine($"Auto rim B−R: top = {top:F1} (blue), bottom = {bottom:F1} (red)");
    Check(top > 30 && bottom < -30, "edge tint derives from the local wallpaper colors");
}
using var shifted = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { DesktopX = 500 }, wallpaper));
Check(Difference(normal, shifted, false) > 5, "moving the card updates wallpaper sampling");
using var offset = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { Theme = theme with { LiquidGlass = theme.EffectiveLiquidGlass with { WallpaperOffsetX = 40 } } }, wallpaper));
Console.WriteLine($"Manual offset mean RGB delta: {Difference(normal, offset, false):F2}");
Check(Difference(normal, offset, false) > 5, "manual wallpaper offset shifts the sample position");
using var fallback = SKBitmap.Decode(LiquidGlassRenderer.Render(frame, new WallpaperSnapshot(null, new SKColor(48, 56, 64))));
Check(fallback.GetPixel(160, 120).Alpha == 255, "solid desktop fallback renders");
foreach (var size in new[] { (1, 1), (24, 800), (800, 24), (640, 480) })
{
    using var image = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { Width = size.Item1, Height = size.Item2, Radius = 90 }, wallpaper));
    Check(image.Width == size.Item1 && image.Height == size.Item2, $"extreme geometry {size}");
}

// 1x1 and 1-grid strip adaptive liquid glass optics verification
var optics1x1 = LiquidGlassRenderer.GetAdaptiveOptics(frame with { Columns = 1, Rows = 1 }, 80, 80);
Check(optics1x1.EdgeScale < 0.65f && optics1x1.EdgeScale > 0.50f, "1x1 adaptive optics edge scale ~0.58");
Check(optics1x1.ShiftScale < 0.75f && optics1x1.ShiftScale > 0.60f, "1x1 adaptive optics shift scale ~0.68");
Check(optics1x1.MaxLensFrac <= 0.30f, "1x1 adaptive optics max lens fraction <= 0.30");

var opticsStrip = LiquidGlassRenderer.GetAdaptiveOptics(frame with { Columns = 4, Rows = 1 }, 80, 320);
Check(opticsStrip.EdgeScale < 0.85f && opticsStrip.EdgeScale > 0.70f, "1xN/Nx1 strip adaptive optics edge scale ~0.75");
Check(opticsStrip.ShiftScale < 0.90f && opticsStrip.ShiftScale > 0.75f, "1xN/Nx1 strip adaptive optics shift scale ~0.82");

var opticsNormal = LiquidGlassRenderer.GetAdaptiveOptics(frame with { Columns = 2, Rows = 2 }, 200, 200);
Check(opticsNormal.EdgeScale == 1.0f && opticsNormal.ShiftScale == 1.0f, "standard widget full optics scale");

using var card1x1 = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { Width = 80, Height = 80, Columns = 1, Rows = 1 }, wallpaper));
Check(card1x1.Width == 80 && card1x1.Height == 80, "1x1 liquid glass card renders cleanly");

using var cardStrip = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { Width = 320, Height = 80, Columns = 4, Rows = 1 }, wallpaper));
Check(cardStrip.Width == 320 && cardStrip.Height == 80, "1-grid strip liquid glass card renders cleanly");

// Dark speckled wallpaper check: verify that a black wallpaper with scattered white spots
// does NOT produce soap-bubble / rainbow chromatic noise on the glass surface.
{
    using var starrySurface = SKSurface.Create(new SKImageInfo(1200, 800));
    starrySurface.Canvas.Clear(new SKColor(2, 2, 3)); // deep dark background with slight 8-bit noise
    using var starPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
    var rng = new Random(42);
    for (int i = 0; i < 80; i++)
    {
        var sx = rng.Next(0, 1200);
        var sy = rng.Next(0, 800);
        var sr = (float)(rng.NextDouble() * 2.5 + 0.5);
        starrySurface.Canvas.DrawCircle(sx, sy, sr, starPaint);
    }
    using var starryImage = starrySurface.Snapshot();
    using var starryData = starryImage.Encode(SKEncodedImageFormat.Png, 100);
    using var starryGlass = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with
    {
        Theme = theme with { LiquidGlass = new(12, 28, 24, 65, 18, 225, EdgeTint: 80), OpacityLevel = 0.18 }
    }, new WallpaperSnapshot(starryData.ToArray(), SKColors.Black)));

    float maxInteriorChroma = 0f;
    float maxRimChroma = 0f;
    int badIntX = 0, badIntY = 0; SKColor badIntColor = default;
    int badRimX = 0, badRimY = 0; SKColor badRimColor = default;

    for (int y = 5; y < starryGlass.Height - 5; y++)
    {
        for (int x = 5; x < starryGlass.Width - 5; x++)
        {
            var p = starryGlass.GetPixel(x, y);
            if (p.Alpha < 255) continue;
            var c = Math.Max(Math.Abs(p.Red - p.Green), Math.Max(Math.Abs(p.Green - p.Blue), Math.Abs(p.Blue - p.Red)));
            bool isInterior = x >= 28 && x <= starryGlass.Width - 28 && y >= 28 && y <= starryGlass.Height - 28;
            if (isInterior)
            {
                if (c > maxInteriorChroma)
                {
                    maxInteriorChroma = c;
                    badIntX = x; badIntY = y; badIntColor = p;
                }
            }
            else
            {
                if (c > maxRimChroma)
                {
                    maxRimChroma = c;
                    badRimX = x; badRimY = y; badRimColor = p;
                }
            }
        }
    }
    Console.WriteLine($"Dark starry glass max interior chroma: {maxInteriorChroma:F2} at ({badIntX},{badIntY}) {badIntColor}; rim chroma: {maxRimChroma:F2} at ({badRimX},{badRimY}) {badRimColor}");
    Check(maxInteriorChroma < 4f, "dark speckled wallpaper produces pristine achromatic interior (no rainbow soap bubbles)");
}

// Export a deterministic optical preview from the production renderer.
using var sheet = SKSurface.Create(new SKImageInfo(1200, 800));
sheet.Canvas.DrawImage(wallpaperImage, 0, 0);
DrawCard(100, "Liquid glass", theme);
DrawCard(460, "Clear · blur 0", theme with { LiquidGlass = new(0, 65, 32, 80, 30, 225) });
DrawCard(820, "Soft · blur 40", theme with { LiquidGlass = new(40, 35, 32, 80, 20, 225) });
using var sheetImage = sheet.Snapshot();
using var sheetData = sheetImage.Encode(SKEncodedImageFormat.Png, 100);
File.WriteAllBytes(Path.Combine(output, "liquid-glass-preview.png"), sheetData.ToArray());
Console.WriteLine($"All optical checks passed in {timer.ElapsedMilliseconds} ms. Preview: {output}");
OpticsProfile.Run(output);
SoftGlowProfile.Run(output);

// The GPU materials are validated last: they can only be compile-checked here (see
// GpuMaterialCheck / LiquidGlassV2Check), and their failure counts have to reach the process
// exit code rather than throw mid-sheet.
var gpuFailures = GpuMaterialCheck.Run();
var v2Failures = LiquidGlassV2Check.Run();
var clarityFailures = BackdropClarityBlurCheck.Run();
if (gpuFailures + v2Failures + clarityFailures > 0) Environment.Exit(1);

SKBitmap Decode(Theme material) => SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { Theme = material }, wallpaper));
void DrawCard(int x, string label, Theme material)
{
    using var card = SKBitmap.Decode(LiquidGlassRenderer.Render(frame with { DesktopX = x, Theme = material }, wallpaper));
    sheet.Canvas.DrawBitmap(card, x, 240);
    using var labelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true, TextSize = 22, Typeface = SKTypeface.FromFamilyName("Segoe UI") };
    sheet.Canvas.DrawText(label, x + 24, 288, labelPaint);
    labelPaint.TextSize = 64;
    sheet.Canvas.DrawText("09:41", x + 24, 375, labelPaint);
    labelPaint.TextSize = 16;
    sheet.Canvas.DrawText("MONDAY, SEPTEMBER 7", x + 26, 416, labelPaint);
}
static double Difference(SKBitmap a, SKBitmap b, bool edge)
{
    double total = 0;
    var count = 0;
    for (var y = 1; y < a.Height - 1; y++)
    for (var x = 1; x < a.Width - 1; x++)
    {
        if (edge && Math.Min(Math.Min(x, y), Math.Min(a.Width - x, a.Height - y)) > 20) continue;
        var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
        if (p.Alpha < 255 || q.Alpha < 255) continue;
        total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
        count += 3;
    }
    return total / Math.Max(1, count);
}

/// <summary>Mean (Blue − Red) of a horizontal band — higher = bluer rim, lower = redder rim.</summary>
static double BandColor(SKBitmap bitmap, int y0, int y1)
{
    double total = 0;
    var count = 0;
    for (var y = y0; y < y1; y++)
    for (var x = 60; x < bitmap.Width - 60; x++)
    {
        var p = bitmap.GetPixel(x, y);
        if (p.Alpha < 255) continue;
        total += p.Blue - p.Red;
        count++;
    }
    return total / Math.Max(1, count);
}
static void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {label}");
    Console.WriteLine($"PASS: {label}");
}

static (int Width, int Height) VerifyShellItemImageFactory(string path)
{
    [DllImport("ole32.dll")]
    static extern int CoInitialize(IntPtr pvReserved);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);
    [DllImport("gdi32.dll")]
    static extern IntPtr GetObject(IntPtr h, int c, out BITMAP b);
    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeleteObject(IntPtr h);

    CoInitialize(IntPtr.Zero);
    var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var factory) != 0 || factory == null)
        return (0, 0);

    var size = new SIZE { cx = 256, cy = 256 };
    // 0x105 = SIIGBF_ICONONLY (0x4) | SIIGBF_BIGGERSIZEOK (0x1) | SIIGBF_SCALEUP (0x100)
    var hr = factory.GetImage(size, 0x105, out var hbm);
    if (hr != 0 || hbm == IntPtr.Zero)
    {
        hr = factory.GetImage(size, 0x5, out hbm);
    }
    if (hr != 0 || hbm == IntPtr.Zero) return (0, 0);

    try
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bmp) != IntPtr.Zero)
        {
            return (Math.Abs(bmp.bmWidth), Math.Abs(bmp.bmHeight));
        }
    }
    finally
    {
        DeleteObject(hbm);
    }
    return (0, 0);
}

[StructLayout(LayoutKind.Sequential)]
struct SIZE { public int cx; public int cy; }

[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellItemImageFactory
{
    [PreserveSig] int GetImage([In] SIZE size, [In] uint flags, out IntPtr phbm);
}

[StructLayout(LayoutKind.Sequential)]
struct BITMAP
{
    public int bmType;
    public int bmWidth;
    public int bmHeight;
    public int bmWidthBytes;
    public ushort bmPlanes;
    public ushort bmBitsPixel;
    public IntPtr bmBits;
}

class DummyWidgetLayoutProvider : DeskCanvas.Core.Interfaces.IWidgetLayoutProvider
{
    public string ScreenId { get; set; } = "default";
    public DeskCanvas.Core.Models.WidgetLayout Get() => null!;
    public void Save(DeskCanvas.Core.Models.WidgetLayout data) { }
    public void Remove() { }
    public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanging;
    public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanged;
}

class EmptyServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}
