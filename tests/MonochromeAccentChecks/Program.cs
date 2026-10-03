using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace MonochromeAccentChecks;

/// <summary>
/// Pins the 单色模式-黑白 accent contract: the accent IS the inverted background —
/// <b>light mode renders the dark-mode card color, dark mode renders the light-mode
/// card color</b> — through the real ThemeService + the real Accent / MonochromeBlackWhite
/// style dictionaries. Uses non-default background colors on purpose, so any mix-up
/// between the two variants shows up as a swapped value instead of two equal whites.
/// </summary>
internal class Program
{
    private static int failed;

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {message}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {message}");
            failed++;
        }
    }

    private static Color Resource(string key) =>
        (Color)(Application.Current!.Resources[key]
            ?? throw new InvalidOperationException($"Resource {key} is missing"));

    /// <summary>
    /// Variant-scoped brush lookup through the real style chain (same path the widgets use).
    /// The probe control lives inside a real (never shown) Window: a fully detached control's
    /// resource walk does not reach the Application.Styles theme dictionaries.
    /// </summary>
    private static readonly Window ProbeWindow = new();

    private static Color BrushOf(string key, ThemeVariant variant)
    {
        var probe = new Control();
        ProbeWindow.Content = probe;
        if (!probe.TryFindResource(key, variant, out var value) || value is not SolidColorBrush brush)
            throw new InvalidOperationException($"{key} ({variant}) did not resolve to a brush");
        return brush.Color;
    }

    public static int Main(string[] args)
    {
        AppBuilder.Configure<DeskCanvas.App>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        Console.WriteLine("=== Monochrome BlackWhite Accent Checks ===");

        var themeService = (IThemeService)(DeskCanvas.App.Services?.GetService(typeof(IThemeService))
            ?? throw new InvalidOperationException("IThemeService is not registered"));

        // Non-default background colors make any variant mix-up obvious (#112233 vs #FEDCBA).
        const string darkBg = "#112233";
        const string lightBg = "#FEDCBA";
        var darkBgColor = Color.Parse(darkBg);
        var lightBgColor = Color.Parse(lightBg);

        static Theme BlackWhite(bool? darkMode, string darkBackground, string lightBackground, string? accentColor = null) => new(
            DarkMode: darkMode,
            AccentColor: accentColor,
            OpacityLevel: 1.0,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid,
            SolidBackgroundDark: darkBackground,
            SolidBackgroundLight: lightBackground,
            MonochromeVariant: MonochromeStyle.BlackWhite);

        Console.WriteLine("\n--- Dark mode (explicit): accent = LIGHT-mode background ---");
        themeService.Apply(BlackWhite(true, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == lightBgColor,
            $"SystemAccentColor == light-mode bg {lightBg} (got {Resource("SystemAccentColor")})");
        Assert(Resource("SystemAccentColorLight1") == lightBgColor && Resource("SystemAccentColorLight2") == lightBgColor,
            "Light ramp (SystemAccentColorLight1/2) == light-mode bg");
        Assert(Resource("SystemAccentColorDark1") == darkBgColor,
            "Dark ramp (SystemAccentColorDark1) == dark-mode bg");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush == light-mode bg");
        Assert(BrushOf("CalendarHeaderBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant semantic accent brush (CalendarHeaderBrush) == light-mode bg");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Dark) == Colors.Black,
            "Dark-variant AccentContrastForegroundBrush == black (readable on the light accent)");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Light) == Colors.White,
            "Light-variant AccentContrastForegroundBrush == white (readable on the dark accent)");

        Console.WriteLine("\n--- Light mode (explicit): accent = DARK-mode background ---");
        themeService.Apply(BlackWhite(false, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == darkBgColor,
            $"SystemAccentColor == dark-mode bg {darkBg} (got {Resource("SystemAccentColor")})");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant accent brush == dark-mode bg");
        Assert(BrushOf("CalendarHeaderBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant semantic accent brush (CalendarHeaderBrush) == dark-mode bg");

        Console.WriteLine("\n--- A hand-picked accent color must NOT leak through 黑白 monochrome ---");
        themeService.Apply(BlackWhite(true, darkBg, lightBg, accentColor: "#FF0000"));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush still == light-mode bg (the red accent is ignored)");
        Assert(Resource("SystemAccentColor") == lightBgColor,
            "SystemAccentColor still == light-mode bg (the red accent is ignored)");

        Console.WriteLine("\n--- 强调色 monochrome variant stays accent-based (no inversion) ---");
        themeService.Apply(new Theme(
            DarkMode: true,
            AccentColor: "#FF0000",
            OpacityLevel: 1.0,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid,
            MonochromeVariant: MonochromeStyle.Accent));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == Colors.Red,
            "Dark-variant accent brush == the picked accent (red), untouched by 黑白 logic");

        Console.WriteLine("\n--- Follow-system (DarkMode = null): per-variant brushes stay correct ---");
        themeService.Apply(BlackWhite(null, darkBg, lightBg));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush == light-mode bg");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant accent brush == dark-mode bg");
        Console.WriteLine($"  [INFO] Application.ActualThemeVariant under follow-system: {Application.Current!.ActualThemeVariant}");
        Console.WriteLine($"  [INFO] Base SystemAccentColor picked: {Resource("SystemAccentColor")} (dark bg {darkBgColor} / light bg {lightBgColor})");

        Console.WriteLine("\n--- Widget-style lookups (the way imperative widget code MUST resolve) ---");
        // The variant-less TryFindResource overload resolves theme dictionaries against
        // ThemeVariant.Default — i.e. it ALWAYS returns the LIGHT dictionary's brush, even
        // while the widget renders dark. The widgets therefore resolve with the variant
        // explicitly (Month.ResolveAccentBrush, AggregateViewModel.ResolveAccentBrush,
        // AnalogWorldSingle.BuildTicks, MarkdownRenderer.ResolveThemed). These asserts pin
        // that contract in 黑白 monochrome; the variant-less result is reported as INFO.
        themeService.Apply(BlackWhite(true, darkBg, lightBg));

        var hostWindow = new Window { RequestedThemeVariant = ThemeVariant.Dark, Width = 10, Height = 10 };
        var attached = new Control();
        hostWindow.Content = attached;
        Console.WriteLine($"  [INFO] attached probe.ActualThemeVariant = {attached.ActualThemeVariant}");
        Console.WriteLine($"  [INFO] variant-less TryFindResource (the trap) -> " +
            (attached.TryFindResource("CalendarTodayBrush", out var trap) && trap is SolidColorBrush t ? t.Color.ToString() : "(unresolved)"));

        Assert(attached.TryFindResource("CalendarTodayBrush", attached.ActualThemeVariant, out var darkDot)
               && darkDot is SolidColorBrush ddb && ddb.Color == lightBgColor,
            "Attached-in-dark-window variant-aware lookup of CalendarTodayBrush == light-mode bg");
        Assert(((IResourceHost)Application.Current!).TryGetResource("CalendarTodayBrush", Application.Current.ActualThemeVariant, out var appLevel)
               && appLevel is SolidColorBrush alb && alb.Color == lightBgColor,
            "Application-host variant-aware lookup of CalendarTodayBrush == light-mode bg");
        Assert(((IResourceHost)Application.Current!).TryGetResource("SystemControlForegroundBaseHighBrush", Application.Current.ActualThemeVariant, out var baseHigh)
               && baseHigh is SolidColorBrush bhb && bhb.Color == Colors.White,
            "Application-host variant-aware lookup of SystemControlForegroundBaseHighBrush == white (dark Fluent dict)");

        var lightWindow = new Window { RequestedThemeVariant = ThemeVariant.Light, Width = 10, Height = 10 };
        var attachedLight = new Control();
        lightWindow.Content = attachedLight;
        Assert(attachedLight.TryFindResource("CalendarTodayBrush", attachedLight.ActualThemeVariant, out var lightDot)
               && lightDot is SolidColorBrush ldb && ldb.Color == darkBgColor,
            "Attached-in-light-window variant-aware lookup of CalendarTodayBrush == dark-mode bg");

        if (failed == 0)
        {
            Console.WriteLine("\nALL CHECKS PASSED!");
        }
        else
        {
            Console.WriteLine($"\n{failed} CHECK(S) FAILED!");
        }

        return failed;
    }
}
