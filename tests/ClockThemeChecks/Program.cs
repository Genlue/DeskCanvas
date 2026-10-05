using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Clock.Models;
using Clock.Views;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace ClockThemeChecks;

/// <summary>
/// Regression checks for the frameless clock's <b>locked 毛玻璃 material</b>.
///
/// The clock is locked to 毛玻璃: the numerals always render over the OS's native acrylic
/// backdrop, whatever the global surface theme is. The old theme-following design (the material
/// resolver, the glyph glass renderer, the pre-render frame cache) was removed together with the
/// lock — this widget's history was a chain of "stopped following the global theme" regressions,
/// and locking the material removes the entire class of bug along with the code.
///
/// Part 1 (activation): the desktop widget is activated by
/// <c>WidgetFactory.CreateWidgetControl</c> as <c>Activate(typeof(FramelessDigital), layoutProvider, model)</c>,
/// which goes through <c>ActivatorUtilities.CreateInstance</c>. That type has five public
/// constructors, two of which accept those two arguments, so which one wins decides whether
/// the widget ever receives <see cref="IAppSettingsProvider"/> — the light/dark wash, the accent
/// behind 跟随强调色 and the global font family all come from it. Part 1b pins the two shapes
/// that arrive WITHOUT a stored model — a freshly added widget (layout.json entry with
/// <c>Settings: null</c>) and the 组件库 preview — where the factory has to build the default
/// model itself or the widget falls back to a constructor without the settings provider.
///
/// Part 2 (the lock): renders the control under global 毛玻璃 / 液态玻璃 / 纯色 themes; the
/// snapshots must be pixel-identical — the global surface may not reach the numerals.
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

        Console.WriteLine("=== Frameless clock theme checks (locked to 毛玻璃) ===");
        Console.WriteLine();

        // ---- Part 1: activation / settings injection ----
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
        // A freshly added widget has no stored Settings (layout.json entry: null) and the
        // gallery preview passes no model at all. CreateWidgetControl used to drop the model
        // argument in both cases, and because the clock's constructors take the model FIRST,
        // activation fell back to (IWidgetLayoutProvider) — the widget ran without a settings
        // provider and lost the wash variant, the accent and the global font family.
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

        // ---- Part 2: the lock — the global surface never reaches the numerals ----
        Console.WriteLine();
        Console.WriteLine($"Rendering into {Path.GetFullPath(outputDir)} …");

        byte[]? acrylic = null, liquidGlass = null, solid = null, acrylicAgain = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            acrylic = Render(layout, outputDir, "global-acrylic", SurfaceStyle.Acrylic);
            liquidGlass = Render(layout, outputDir, "global-liquidglass", SurfaceStyle.LiquidGlass);
            solid = Render(layout, outputDir, "global-solid", SurfaceStyle.Solid);
            acrylicAgain = Render(layout, outputDir, "global-acrylic-again", SurfaceStyle.Acrylic);
            if (acrylic != null && acrylicAgain != null && MeanAbsoluteDifference(acrylic, acrylicAgain) == 0) break;

            // A minute boundary rolled between the first and last render: the time text — and
            // with it the snapshot — legitimately changed. Redo the whole sequence in one minute.
            Console.WriteLine("  minute boundary rolled mid-sequence — re-rendering");
        }

        if (acrylic != null && liquidGlass != null && solid != null && acrylicAgain != null
            && MeanAbsoluteDifference(acrylic, acrylicAgain) == 0)
        {
            Check("a global 液态玻璃 theme renders the same numerals as 毛玻璃 (the lock holds)",
                MeanAbsoluteDifference(acrylic, liquidGlass) == 0);
            Check("a global 纯色 theme renders the same numerals as 毛玻璃 (the lock holds)",
                MeanAbsoluteDifference(acrylic, solid) == 0);
        }
        else
        {
            Check("all global-surface renders completed inside one minute", false);
        }

        // ---- Part 3: the model cannot carry a material any more ----
        // The clock used to carry its own 视觉主题 override (ThemeMode) and, later, a
        // LiquidGlassOpacity slider; both died with the theme-following design. A stale value
        // left in a user's layout.json must not break deserialization.
        Console.WriteLine();
        Console.WriteLine("--- model contract ---");

        Check("FramelessClockModel no longer declares a ThemeMode override",
            typeof(FramelessClockModel).GetProperty("ThemeMode") == null);
        Check("FramelessClockModel no longer declares a LiquidGlassOpacity override",
            typeof(FramelessClockModel).GetProperty("LiquidGlassOpacity") == null);
        var stale = JsonSerializer.Deserialize<FramelessClockModel>(
            "{\"Use24Hours\":false,\"ThemeMode\":4,\"LiquidGlassOpacity\":150}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Check("stale ThemeMode / LiquidGlassOpacity values in stored settings are ignored, not fatal",
            stale is { Use24Hours: false });

        // ---- Part 4: 跟随强调色 must resolve the accent actually in effect ----
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
            var overlayTheme = BuildSettings(SurfaceStyle.Acrylic).Theme with { AccentColor = themeAccent };
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

        CheckNativeAcrylicBackdrop();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// The lock's native side: the widget asserts the OS acrylic backdrop (and nothing else) on
    /// load. The WinUI composition brush must actually come on and stay on across re-asserts.
    /// </summary>
    private static void CheckNativeAcrylicBackdrop()
    {
        Console.WriteLine();
        Console.WriteLine("--- native acrylic backdrop ---");
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

        for (var round = 1; round <= 2; round++)
        {
            apply.Invoke(null, [host]);
            Check($"native round {round}: acrylic activates the composition brush",
                host.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur
                && effect.GetValue(surface)?.ToString() == "Acrylic");
        }
        host.Close();
    }

    /// <summary>
    /// Render the widget off-screen at the real 4×2 grid size under a global
    /// <paramref name="surface"/> theme, and return the decoded BGRA pixels of the snapshot.
    /// </summary>
    private static byte[]? Render(StubLayout layout, string dir, string name, SurfaceStyle surface)
    {
        const double width = 368, height = 184;

        var settings = new StubSettings(BuildSettings(surface));

        var model = new FramelessClockModel(
            Use24Hours: true, FontFamily: "Impact", FontWeight: 800, StretchFill: true);

        var view = new FramelessDigital(model, layout, settings) { Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        const double scale = 2.0;
        var pixelSize = new PixelSize((int)(width * scale), (int)(height * scale));
        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96 * scale, 96 * scale));
        bitmap.Render(view);

        var path = Path.Combine(dir, $"{name}.png");
        bitmap.Save(path);
        Console.WriteLine($"  {name}: saved {path}");

        return LoadPixels(path);
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
