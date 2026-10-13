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
using Clock.ViewModels;
using Clock.Views;
using Clock.Views.Settings;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace ClockThemeChecks;

/// <summary>
/// Regression checks for the frameless clock's multi-theme architecture, per-widget material
/// override, DI constructor resolution, and settings view-model contract.
/// </summary>
class Program
{
    private static int failures;
    private static string outputDir = "dist/clock-theme-checks";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) outputDir = args[0];
        Directory.CreateDirectory(outputDir);

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        var layout = new StubLayout();

        Console.WriteLine("=== Frameless clock multi-theme & architecture checks ===");
        Console.WriteLine();

        // ---- Part 1: DI activation / settings injection across all entry points ----
        var settings = new StubSettings(BuildSettings(SurfaceStyle.Acrylic));
        var services = new ServiceCollection();
        services.AddSingleton<IAppSettingsProvider>(settings);
        var provider = services.BuildServiceProvider();

        var clock = (FramelessDigital)ActivatorUtilities.CreateInstance(
            provider, typeof(FramelessDigital), layout, new FramelessClockModel());

        var injected = ReadField(clock, "appSettingsProvider");
        Console.WriteLine("ctor args = [IWidgetLayoutProvider, FramelessClockModel]");
        Console.WriteLine($"  injected IAppSettingsProvider = {(injected == null ? "NULL" : injected.GetType().Name)}");
        Check("the clock receives IAppSettingsProvider", injected != null);

        // ---- Part 1b: fresh-add / 组件库 preview activation ----
        Console.WriteLine();
        Console.WriteLine("--- fresh-add / preview activation (no stored Settings) ---");

        var defaultModel = WidgetFactory.TryCreateDefaultModel(typeof(FramelessDigital));
        Check("the factory builds a default FramelessClockModel for a Settings-less layout",
            defaultModel is FramelessClockModel);

        var freshAdd = (FramelessDigital)ActivatorUtilities.CreateInstance(
            provider, typeof(FramelessDigital), layout, defaultModel!);
        var freshInjected = ReadField(freshAdd, "appSettingsProvider");
        Console.WriteLine("ctor args = [IWidgetLayoutProvider, default FramelessClockModel]");
        Console.WriteLine($"  injected IAppSettingsProvider = {(freshInjected == null ? "NULL" : freshInjected.GetType().Name)}");
        Check("a freshly added clock receives IAppSettingsProvider", freshInjected != null);

        // ---- Part 1c: FramelessClockSettings DI activation ----
        var settingsControl = (FramelessClockSettings)ActivatorUtilities.CreateInstance(
            provider, typeof(FramelessClockSettings), layout);
        Check("FramelessClockSettings activates with IWidgetLayoutProvider + IAppSettingsProvider",
            settingsControl.DataContext is FramelessClockSettingsViewModel);

        // ---- Part 2: FramelessThemeResolver contract ----
        Console.WriteLine();
        Console.WriteLine("--- FramelessThemeResolver contract (FollowGlobal + SurfaceOverride) ---");

        Check("FollowGlobal under Acrylic resolves Acrylic (IsAcrylic=true, IsRenderedGlass=false)",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.Acrylic).Theme, null)
                is { EffectiveSurface: SurfaceStyle.Acrylic, IsAcrylic: true, IsRenderedGlass: false });

        Check("FollowGlobal under LiquidGlass resolves LiquidGlass (IsRenderedGlass=true)",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.LiquidGlass).Theme, null)
                is { EffectiveSurface: SurfaceStyle.LiquidGlass, IsAcrylic: false, IsRenderedGlass: true });

        Check("FollowGlobal under LiquidGlassV2 resolves LiquidGlassV2 (IsRenderedGlass=true)",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.LiquidGlassV2).Theme, null)
                is { EffectiveSurface: SurfaceStyle.LiquidGlassV2, IsAcrylic: false, IsRenderedGlass: true });

        Check("FollowGlobal under Solid resolves Solid (IsSolid=true)",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.Solid).Theme, null)
                is { EffectiveSurface: SurfaceStyle.Solid, IsSolid: true, IsRenderedGlass: false });

        Check("FollowGlobal under Colorful resolves Colorful (IsColorful=true)",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.Colorful).Theme, null)
                is { EffectiveSurface: SurfaceStyle.Colorful, IsColorful: true, IsRenderedGlass: false });

        Check("Explicit SurfaceOverride=LiquidGlassV2 overrides global Acrylic",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.Acrylic).Theme, SurfaceStyle.LiquidGlassV2)
                is { EffectiveSurface: SurfaceStyle.LiquidGlassV2, IsRenderedGlass: true });

        Check("Explicit SurfaceOverride=Solid overrides global LiquidGlass",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.LiquidGlass).Theme, SurfaceStyle.Solid)
                is { EffectiveSurface: SurfaceStyle.Solid, IsSolid: true, IsRenderedGlass: false });

        Check("Explicit SurfaceOverride=Colorful overrides global Acrylic",
            FramelessThemeResolver.Resolve(BuildSettings(SurfaceStyle.Acrylic).Theme, SurfaceStyle.Colorful)
                is { EffectiveSurface: SurfaceStyle.Colorful, IsColorful: true, IsRenderedGlass: false });

        // ---- Part 3: Live multi-theme rendering & per-widget override ----
        Console.WriteLine();
        Console.WriteLine($"Rendering multi-theme snapshots into {Path.GetFullPath(outputDir)} …");

        byte[]? acrylic = null, liquidGlass = null, liquidGlassV2 = null, solid = null, colorful = null;
        byte[]? overrideSolidOnGlass = null, overrideLg2OnAcrylic = null, acrylicAgain = null;
        Bitmap? bmpOverrideSolid = null, bmpOverrideLg2 = null;

        for (var attempt = 0; attempt < 2; attempt++)
        {
            acrylic = Render(layout, outputDir, "global-acrylic", SurfaceStyle.Acrylic, null, out var bmpAcrylic);
            liquidGlass = Render(layout, outputDir, "global-liquidglass", SurfaceStyle.LiquidGlass, null, out var bmpLg1);
            liquidGlassV2 = Render(layout, outputDir, "global-liquidglass-v2", SurfaceStyle.LiquidGlassV2, null, out var bmpLg2);
            solid = Render(layout, outputDir, "global-solid", SurfaceStyle.Solid, null, out var bmpSolid);
            colorful = Render(layout, outputDir, "global-colorful", SurfaceStyle.Colorful, null, out var bmpColorful);
            overrideSolidOnGlass = Render(layout, outputDir, "override-solid-on-liquidglass",
                SurfaceStyle.LiquidGlass, SurfaceStyle.Solid, out bmpOverrideSolid);
            overrideLg2OnAcrylic = Render(layout, outputDir, "override-lgv2-on-acrylic",
                SurfaceStyle.Acrylic, SurfaceStyle.LiquidGlassV2, out bmpOverrideLg2);
            acrylicAgain = Render(layout, outputDir, "global-acrylic-again", SurfaceStyle.Acrylic, null, out _);

            if (acrylic != null && acrylicAgain != null && MeanAbsoluteDifference(acrylic, acrylicAgain) == 0)
            {
                Check("Acrylic mode does not hold a pre-rendered liquidGlassBitmap", bmpAcrylic == null);
                Check("LiquidGlass mode populates liquidGlassBitmap", bmpLg1 != null);
                Check("LiquidGlassV2 mode populates liquidGlassBitmap", bmpLg2 != null);
                Check("Solid mode does not hold a pre-rendered liquidGlassBitmap", bmpSolid == null);
                Check("Colorful mode does not hold a pre-rendered liquidGlassBitmap", bmpColorful == null);
                break;
            }

            Console.WriteLine("  minute boundary rolled mid-sequence — re-rendering");
        }

        if (acrylic != null && liquidGlass != null && liquidGlassV2 != null && solid != null && colorful != null)
        {
            Check("LiquidGlass renders distinctly from Acrylic", MeanAbsoluteDifference(acrylic, liquidGlass) > 0.5);
            Check("LiquidGlassV2 renders distinctly from Acrylic", MeanAbsoluteDifference(acrylic, liquidGlassV2) > 0.5);
            Check("Solid renders distinctly from Acrylic", MeanAbsoluteDifference(acrylic, solid) > 0.5);
            Check("Colorful renders distinctly from Solid", MeanAbsoluteDifference(solid, colorful) > 0.5);

            Check("Per-widget SurfaceOverride=Solid on global LiquidGlass matches Solid output",
                overrideSolidOnGlass != null && bmpOverrideSolid == null && MeanAbsoluteDifference(solid, overrideSolidOnGlass) < 0.01);

            Check("Per-widget SurfaceOverride=LiquidGlassV2 on global Acrylic renders LiquidGlassV2",
                overrideLg2OnAcrylic != null && bmpOverrideLg2 != null && MeanAbsoluteDifference(liquidGlassV2, overrideLg2OnAcrylic) < 0.01);
        }
        else
        {
            Check("all multi-theme renders completed", false);
        }

        // ---- Part 4: Settings ViewModel & Model contract ----
        Console.WriteLine();
        Console.WriteLine("--- Settings ViewModel & Model contract ---");

        var vm = new FramelessClockSettingsViewModel(layout, settings);
        Check("MaterialOptions includes FollowGlobal + 5 explicit materials (6 total)",
            vm.MaterialOptions.Count == 6 && vm.MaterialOptions[0].Style == null);
        Check("FontSourceOptions separates Artistic (false) and System (true)",
            vm.FontSourceOptions.Count == 2 && !vm.FontSourceOptions[0].IsSystemFont && vm.FontSourceOptions[1].IsSystemFont);
        Check("ArtisticFontOptions exposes all 8 bundled iOS Lock Screen artistic fonts",
            vm.ArtisticFontOptions.Count == 8);
        Check("SystemFontOptions is separated from bundled artistic fonts",
            vm.SystemFontOptions.Count >= 9 && vm.SystemFontOptions[0].FontFamily == null);

        var stale = JsonSerializer.Deserialize<FramelessClockModel>(
            "{\"Use24Hours\":false,\"ThemeMode\":4,\"LiquidGlassOpacity\":85}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("stored settings deserialize cleanly with LiquidGlassOpacity",
            stale is { Use24Hours: false, LiquidGlassOpacity: 85.0 });

        // ---- Part 5: 跟随强调色 must resolve the accent actually in effect ----
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
            var overlayTheme = BuildSettings(SurfaceStyle.Acrylic).Theme with { AccentColor = themeAccent };
            return (Color)overlayMethod.Invoke(null, [overlayModel, overlayTheme])!;
        }

        Application.Current!.Resources["SystemAccentColor"] = Color.Parse("#12C46A");

        var systemAccent = Overlay(100, true, null, null);
        Check("跟随强调色 with no picked accent uses the SystemAccentColor resource",
            systemAccent == Color.Parse("#12C46A"));

        var pickedAccent = Overlay(100, true, null, "#FF3B30");
        Check("跟随强调色 with a picked accent uses the picked colour",
            pickedAccent == Color.Parse("#FF3B30"));

        var custom = Overlay(100, false, "#FFCC00", "#FF3B30");
        Check("a custom 遮罩颜色 ignores the accent", custom == Color.Parse("#FFCC00"));

        var faint = Overlay(40, true, null, "#FF3B30");
        Check("the overlay carries the model's opacity",
            faint == Color.FromArgb((byte)(0.40 * 255), 0xFF, 0x3B, 0x30));

        CheckNativeWindowTransparency();
        CheckAnalogIVDial3Contract();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckAnalogIVDial3Contract()
    {
        Console.WriteLine();
        Console.WriteLine("--- AnalogIV (表盘-3 / Dial 3) widget contract ---");

        var widgetAttr = typeof(AnalogIV).Assembly
            .GetCustomAttributes<DeskCanvas.Core.Models.Attributes.WidgetInfoAttribute>()
            .FirstOrDefault(a => a.ViewType == typeof(AnalogIV));

        Check("AnalogIV is registered via [WidgetInfo] in Clock assembly",
            widgetAttr != null
            && widgetAttr.ModelType == typeof(ClockModel)
            && widgetAttr.EditModelViewType == typeof(AnalogClockSettings)
            && widgetAttr.Title == "Clock_AnalogIV_Title"
            && widgetAttr.Subtitle == "Clock_Analog_Subtitle");

        var prevCulture = Clock.Locales.Locale.Culture;
        try
        {
            Clock.Locales.Locale.Culture = new System.Globalization.CultureInfo("zh-Hans");
            Check("Clock_AnalogIV_Title resolves to '表盘-3' in zh-Hans",
                Clock.Locales.Locale.Clock_AnalogIV_Title == "表盘-3");

            Clock.Locales.Locale.Culture = System.Globalization.CultureInfo.InvariantCulture;
            Check("Clock_AnalogIV_Title resolves to 'Dial 3' in English",
                Clock.Locales.Locale.Clock_AnalogIV_Title == "Dial 3");
        }
        finally
        {
            Clock.Locales.Locale.Culture = prevCulture;
        }

        var dial1 = new AnalogI(new ClockModel());
        var dial3 = new AnalogIV(new ClockModel());

        Check("AnalogIV shares identical PresetSpans and AllowedBaseSpans with AnalogI",
            dial3.PresetSpans.SequenceEqual(dial1.PresetSpans)
            && dial3.AllowedBaseSpans.SequenceEqual(dial1.AllowedBaseSpans)
            && dial3.IsAllowedSpan(2, 2) && !dial3.IsAllowedSpan(2, 1)
            && dial3.SnapSpan(3, 3) == (3, 3));

        // ---- 表盘颜色 (dial color) resolution contract ----
        Check("表盘颜色 defaults to 跟随背景 (null resolution — the theme brush is used)",
            AnalogIV.ResolveDialColor(new ClockModel(), isDarkVariant: false) == null
            && AnalogIV.ResolveDialColor(new ClockModel(), isDarkVariant: true) == null);

        Check("黑白 resolves to white (#FFFFFF) in light mode and black (#1C1C1E) in dark mode",
            AnalogIV.ResolveDialColor(
                new ClockModel(DialColorMode: ClockDialColorMode.BlackWhite), isDarkVariant: false)
                == Color.Parse("#FFFFFF")
            && AnalogIV.ResolveDialColor(
                new ClockModel(DialColorMode: ClockDialColorMode.BlackWhite), isDarkVariant: true)
                == Color.Parse("#1C1C1E"));

        Check("自定义 resolves the picked light/dark colors per variant",
            AnalogIV.ResolveDialColor(new ClockModel(
                DialColorMode: ClockDialColorMode.Custom,
                DialColorLight: "#12C46A",
                DialColorDark: "#FF3B30"), isDarkVariant: false) == Color.Parse("#12C46A")
            && AnalogIV.ResolveDialColor(new ClockModel(
                DialColorMode: ClockDialColorMode.Custom,
                DialColorLight: "#12C46A",
                DialColorDark: "#FF3B30"), isDarkVariant: true) == Color.Parse("#FF3B30"));

        Check("自定义 without picked colors starts from the 黑白 pair",
            AnalogIV.ResolveDialColor(
                new ClockModel(DialColorMode: ClockDialColorMode.Custom), isDarkVariant: false)
                == Color.Parse(ClockModel.DefaultDialColorLight)
            && AnalogIV.ResolveDialColor(
                new ClockModel(DialColorMode: ClockDialColorMode.Custom), isDarkVariant: true)
                == Color.Parse(ClockModel.DefaultDialColorDark));

        var legacyModel = JsonSerializer.Deserialize<ClockModel>("{\"Use24Hours\":false}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("stored models without dial-color fields default to 跟随背景",
            legacyModel is
            {
                DialColorMode: ClockDialColorMode.BackgroundColor,
                DialColorLight: null,
                DialColorDark: null
            });
    }

    private static void CheckNativeWindowTransparency()
    {
        Console.WriteLine();
        Console.WriteLine("--- native window transparency switching ---");
        var host = new Window { Width = 368, Height = 184, ShowInTaskbar = false };
        var apply = typeof(FramelessDigital).GetMethod(
            "ApplyWindowTransparency",
            BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(Window), typeof(bool)])!;

        // 1. Acrylic
        apply.Invoke(null, [host, true]);
        var hint1 = host.TransparencyLevelHint;
        Check("Acrylic sets TransparencyLevelHint[0] = AcrylicBlur",
            hint1.Count > 0 && hint1[0] == WindowTransparencyLevel.AcrylicBlur);

        // 2. Non-acrylic (LiquidGlass / Solid / Colorful)
        apply.Invoke(null, [host, false]);
        var hint2 = host.TransparencyLevelHint;
        Check("Non-acrylic allocates a fresh TransparencyLevelHint array with Transparent",
            !ReferenceEquals(hint1, hint2) && hint2.Count > 0 && hint2[0] == WindowTransparencyLevel.Transparent);

        // 3. Back to Acrylic
        apply.Invoke(null, [host, true]);
        var hint3 = host.TransparencyLevelHint;
        Check("Switching back to Acrylic allocates a fresh TransparencyLevelHint array with AcrylicBlur",
            !ReferenceEquals(hint2, hint3) && hint3.Count > 0 && hint3[0] == WindowTransparencyLevel.AcrylicBlur);

        host.Close();
    }

    private static byte[]? Render(
        StubLayout layout,
        string dir,
        string name,
        SurfaceStyle globalSurface,
        SurfaceStyle? surfaceOverride,
        out Bitmap? displayedGlassBitmap)
    {
        const double width = 368, height = 184;

        var settings = new StubSettings(BuildSettings(globalSurface));

        var model = new FramelessClockModel(
            Use24Hours: true,
            FontFamily: "Bebas Neue",
            FontWeight: 700,
            StretchFill: true,
            SurfaceOverride: surfaceOverride);

        var view = new FramelessDigital(model, layout, settings) { Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        const double scale = 2.0;
        var pixelSize = new PixelSize((int)(width * scale), (int)(height * scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(view);

        displayedGlassBitmap = ReadField(view, "liquidGlassBitmap") as Bitmap;

        var path = Path.Combine(dir, $"{name}.png");
        bitmap.Save(path);
        Console.WriteLine($"  {name}: saved {path}");

        return LoadPixels(path);
    }

    private static byte[]? LoadPixels(string path)
    {
        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;

        var pixels = new byte[decoded.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
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
            OpacityLevel: surface == SurfaceStyle.Solid || surface == SurfaceStyle.Colorful ? 1.0 : 0.20,
            Monochrome: surface != SurfaceStyle.Colorful,
            UseNativeFrame: false,
            FontFamily: "Segoe UI",
            Surface: surface,
            LiquidGlass: new LiquidGlassSettings(),
            LiquidGlassV2: new LiquidGlassV2Settings()),
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
        private WidgetLayout current = new("Clock", "FramelessDigital", 0, 0, 368, 184, null);
        public event DataChangedEvent<WidgetLayout>? DataChanging;
        public event DataChangedEvent<WidgetLayout>? DataChanged;

        public string ScreenId { get; set; } = "probe";

        public WidgetLayout Get() => current;

        public void Save(WidgetLayout data)
        {
            var old = current;
            DataChanging?.Invoke(this, old, data);
            current = data;
            DataChanged?.Invoke(this, old, data);
        }

        public void Remove() { }
    }
}
