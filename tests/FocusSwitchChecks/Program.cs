using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using FixedWidgets.Models;
using FixedWidgets.ViewModels;

namespace FocusSwitchChecks;

/// <summary>
/// Verifies the 失焦自动切换 (auto light/dark switching on desktop focus loss) decision rules.
///
/// The pure pieces are checked deterministically:
/// <list type="bullet">
/// <item><see cref="FocusWatchService.IsWhitelisted"/> — the allowlist match (case, .exe suffix, blanks),</item>
/// <item><see cref="FocusWatchService.IsShellWindow"/> — shell classification (the generic
/// CoreWindow class only counts for real shell hosts),</item>
/// <item><see cref="FocusWatchService.ResolveUnfocusedScreens"/> — the z-order occupancy walk:
/// which screens application windows keep unfocused, and what never counts (minimized, cloaked,
/// tool and titleless windows, the allowlist, DeskCanvas itself, shell surfaces),</item>
/// <item><see cref="FocusThemeCoordinator.ResolveFocused"/> — the uniform focus decision,</item>
/// <item><see cref="FocusThemeCoordinator.ResolveVariant"/> — the four mode choices,</item>
/// <item><see cref="FocusThemeCoordinator.ResolveStateTheme"/> — the per-state surface override
/// and its material-owned coating level,</item>
/// <item><see cref="FocusLinkSettings"/> — seeding and null-tolerant accessors.</item>
/// </list>
///
/// The live Win32 layer is exercised as well: the windowed-process enumeration the picker
/// shows is printed (a few entries), so a broken P/Invoke is visible in the output.
/// </summary>
class Program
{
    private static int failures;

    private static readonly ScreenRect Primary = new(0, 0, 2048, 1152);
    private static readonly ScreenRect Secondary = new(2048, 0, 4096, 1152);
    private static readonly ScreenRect AppBounds = new(100, 100, 800, 600);

    /// <summary>A small flyout-sized rect inside the primary screen (the shape of the taskbar's
    /// thumbnail popup: 66×50 physical pixels over the dock).</summary>
    private static readonly ScreenRect FlyoutBounds = new(400, 500, 466, 550);

    static int Main()
    {
        Console.WriteLine("=== 失焦自动切换 decision checks ===");
        Console.WriteLine();

        // --- allowlist matching ---
        Check("whitelist: exact name matches", FocusWatchService.IsWhitelisted("chrome", ["chrome"]));
        Check("whitelist: case-insensitive both ways",
            FocusWatchService.IsWhitelisted("Chrome", ["chrome"]) && FocusWatchService.IsWhitelisted("chrome", ["CHROME"]));
        Check("whitelist: .exe suffix tolerated on either side",
            FocusWatchService.IsWhitelisted("chrome", ["chrome.exe"]) && FocusWatchService.IsWhitelisted("chrome.exe", ["chrome"]));
        Check("whitelist: surrounding blanks trimmed", FocusWatchService.IsWhitelisted("chrome", ["  chrome  "]));
        Check("whitelist: blank entries ignored", FocusWatchService.IsWhitelisted("chrome", ["", "   ", "chrome"]));
        Check("whitelist: unlisted process does not match", !FocusWatchService.IsWhitelisted("explorer", ["chrome"]));
        Check("whitelist: null process does not match", !FocusWatchService.IsWhitelisted(null, ["chrome"]));
        Check("whitelist: null / empty list never matches",
            !FocusWatchService.IsWhitelisted("chrome", null) && !FocusWatchService.IsWhitelisted("chrome", []));

        // --- shell classification (an app using the generic CoreWindow class is NOT the desktop) ---
        Console.WriteLine();
        Console.WriteLine("--- shell classification ---");

        Check("shell: desktop / taskbar classes are always shell",
            FocusWatchService.IsShellWindow("Progman", null)
            && FocusWatchService.IsShellWindow("WorkerW", null)
            && FocusWatchService.IsShellWindow("Shell_TrayWnd", null));
        Check("shell: task view / alt-tab surfaces are shell too",
            FocusWatchService.IsShellWindow("XamlExplorerHostIslandWindow", null)
            && FocusWatchService.IsShellWindow("TaskSwitcherWnd", null));
        Check("shell: a CoreWindow owned by a shell host is shell",
            FocusWatchService.IsShellWindow(FocusWatchService.CoreWindowClassName, "StartMenuExperienceHost"));
        Check("shell: a CoreWindow owned by a regular app is NOT shell",
            !FocusWatchService.IsShellWindow(FocusWatchService.CoreWindowClassName, "SomeUwpApp"));
        Check("shell: a CoreWindow without a resolvable process is NOT shell",
            !FocusWatchService.IsShellWindow(FocusWatchService.CoreWindowClassName, null));
        Check("shell: an ordinary application class is not shell",
            !FocusWatchService.IsShellWindow("ApplicationFrameWindow", "SomeApp"));
        Check("shell: the taskbar's XAML popup host (the thumbnail flyout 「主机弹出窗口」) is shell",
            FocusWatchService.IsShellWindow("Xaml_WindowedPopupClass", "explorer"));

        // --- the z-order occupancy walk: what keeps a screen unfocused ---
        Console.WriteLine();
        Console.WriteLine("--- occupancy walk ---");

        Walk("an application window keeps its screen unfocused",
            [App("chrome", AppBounds)], [Primary, Secondary], [0]);
        Walk("an application on the secondary screen keeps only the secondary unfocused",
            [App("code", new ScreenRect(2100, 100, 2800, 600))], [Primary, Secondary], [1]);
        Walk("two applications cover two screens",
            [App("chrome", AppBounds), App("code", new ScreenRect(2100, 100, 2800, 600))], [Primary, Secondary], [0, 1]);
        Walk("a window straddling both screens keeps both unfocused",
            [App("code", new ScreenRect(1400, 100, 2700, 700))], [Primary, Secondary], [0, 1]);
        Walk("a window barely over the boundary keeps only the screen holding its center",
            [App("code", new ScreenRect(1950, 100, 2750, 700))], [Primary, Secondary], [1]);
        Walk("the 25% overlap boundary counts (inclusive)",
            [App("code", new ScreenRect(1948, 100, 2348, 500))], [Primary, Secondary], [0, 1]);
        Walk("a window outside every screen keeps nothing unfocused",
            [App("app", new ScreenRect(5000, 5000, 5600, 5400))], [Primary, Secondary], []);
        Walk("a single-screen desktop: the only screen",
            [App("app", new ScreenRect(0, 0, 200, 200))], [Primary], [0]);

        Walk("a minimized application no longer keeps the screen unfocused (snaps back the instant it goes away)",
            [App("chrome", AppBounds, minimized: true)], [Primary, Secondary], []);
        Walk("a cloaked application no longer keeps the screen unfocused",
            [App("chrome", AppBounds, cloaked: true)], [Primary, Secondary], []);
        Walk("a tool window (WS_EX_TOOLWINDOW) no longer keeps the screen unfocused",
            [App("utility", AppBounds, tool: true)], [Primary, Secondary], []);
        Walk("a titleless window no longer keeps the screen unfocused",
            [App("helper", AppBounds, title: "")], [Primary, Secondary], []);
        Walk("an invisible window no longer keeps the screen unfocused",
            [App("ghost", AppBounds, visible: false)], [Primary, Secondary], []);
        Walk("a minimized application behind a visible one: the visible one counts",
            [App("chrome", AppBounds, minimized: true), App("code", AppBounds)], [Primary, Secondary], [0]);

        Walk("a popup that refuses activation (WS_EX_NOACTIVATE — the taskbar thumbnail flyout) keeps nothing unfocused",
            [Popup(FlyoutBounds)], [Primary, Secondary], []);
        Walk("a non-activatable popup above an application: the application still counts",
            [Popup(FlyoutBounds), App("chrome", AppBounds)], [Primary, Secondary], [0]);
        Walk("an activatable window of the very same shape does keep its screen unfocused",
            [App("chrome", FlyoutBounds)], [Primary, Secondary], [0]);

        Walk("an allowlisted window (desktop furniture) keeps nothing unfocused",
            [App("vlc", AppBounds)], [Primary, Secondary], [], whitelist: ["vlc"]);
        Walk("an allowlisted window above an application: the application still counts",
            [App("vlc", AppBounds), App("chrome", AppBounds)], [Primary, Secondary], [0], whitelist: ["vlc"]);

        Walk("shell surfaces (desktop / taskbar / start menu) keep nothing unfocused",
            [Shell("WorkerW"), Shell("Shell_TrayWnd"), Shell(FocusWatchService.CoreWindowClassName, "StartMenuExperienceHost")],
            [Primary, Secondary], []);
        Walk("DeskCanvas' own windows (widgets, panels, settings) keep nothing unfocused",
            [Own("Widget"), Own("Settings")], [Primary, Secondary], []);
        Walk("DeskCanvas' windows above an application: the application still counts",
            [Own("Widget"), App("chrome", AppBounds)], [Primary, Secondary], [0]);

        Check("the leading application is the topmost application window",
            FocusWatchService.ResolveUnfocusedScreens([App("chrome", AppBounds), App("code", AppBounds)], [Primary], null)
                .LeadingApplication == "chrome");
        Check("with no applications left there is no leading application",
            FocusWatchService.ResolveUnfocusedScreens([Shell("WorkerW"), Own("Widget")], [Primary], null)
                .LeadingApplication is null);
        Check("an application with no screen of its own is not the leading one",
            FocusWatchService.ResolveUnfocusedScreens([App("off-screen", new ScreenRect(5000, 5000, 5600, 5400))], [Primary], null)
                is { LeadingApplication: null });
        Check("a non-activatable popup is not the leading application",
            FocusWatchService.ResolveUnfocusedScreens([Popup(AppBounds)], [Primary], null)
                is { LeadingApplication: null });

        // --- uniform focus decision ---
        Console.WriteLine();
        Console.WriteLine("--- uniform focus decision ---");

        var unfocusedOnSecondary = new FocusStateSnapshot(
            ForegroundKind.Application, "chrome", true, new HashSet<ScreenRect> { Secondary });

        Focused("uniform: any application window on any screen unfocuses the desktop",
            unfocusedOnSecondary, expected: false);
        Focused("uniform: an application on the secondary screen alone still unfocuses",
            unfocusedOnSecondary, expected: false);
        Focused("uniform: no unfocused screens means focused",
            FocusStateSnapshot.Focused, expected: true);
        Focused("uniform: an empty occupancy walk means focused",
            new FocusStateSnapshot(ForegroundKind.Shell, null, false, new HashSet<ScreenRect>()), expected: true);

        // --- the four mode choices ---
        Console.WriteLine();
        Console.WriteLine("--- variant resolution ---");

        Check("auto mode follows a dark wallpaper", FocusThemeCoordinator.ResolveVariant(new FocusMode(null, true), true) == Avalonia.Styling.ThemeVariant.Dark);
        Check("auto mode follows a light wallpaper", FocusThemeCoordinator.ResolveVariant(new FocusMode(null, true), false) == Avalonia.Styling.ThemeVariant.Light);
        Check("explicit dark wins over the wallpaper", FocusThemeCoordinator.ResolveVariant(new FocusMode(true, false), false) == Avalonia.Styling.ThemeVariant.Dark);
        Check("explicit light wins over the wallpaper", FocusThemeCoordinator.ResolveVariant(new FocusMode(false, false), true) == Avalonia.Styling.ThemeVariant.Light);
        Check("follow-the-system maps to ThemeVariant.Default", FocusThemeCoordinator.ResolveVariant(new FocusMode(null, false), true) == Avalonia.Styling.ThemeVariant.Default);

        // --- settings seeding / accessors ---
        Console.WriteLine();
        Console.WriteLine("--- settings model ---");

        var seed = FocusLinkSettings.CreateDefault(true, false);
        Check("seeding copies the theme mode into both states",
            seed is { Enabled: false } && seed.Focused == new FocusMode(true, false) && seed.Unfocused == new FocusMode(true, false));
        Check("a null allowlist reads as empty", seed.EffectiveWhitelist.Length == 0);
        Check("null modes read as follow-the-system",
            new FocusLinkSettings(true).EffectiveFocused == new FocusMode(null, false)
            && new FocusLinkSettings(true).EffectiveUnfocused == new FocusMode(null, false));
        Check("seeding leaves both surface overrides unset (follow the shared theme)",
            seed.FocusedSurface is null && seed.UnfocusedSurface is null);

        // --- per-state theme (surface material) resolution ---
        Console.WriteLine();
        Console.WriteLine("--- per-state theme resolution ---");

        var shared = new Theme(
            DarkMode: false, AccentColor: null, OpacityLevel: 0.4, Monochrome: true,
            UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Acrylic);
        var settings = TestSettings(shared);
        var stateLink = FocusLinkSettings.CreateDefault(null, false) with
        {
            Enabled = true,
            Focused = new FocusMode(true, false),
            Unfocused = new FocusMode(false, false),
            FocusedSurface = SurfaceStyle.LiquidGlass,
            UnfocusedSurface = null
        };

        var focusedTheme = FocusThemeCoordinator.ResolveStateTheme(stateLink, focused: true, settings);
        Check("state theme: the state's surface override applies",
            focusedTheme.EffectiveSurface == SurfaceStyle.LiquidGlass);
        Check("state theme: the state's light/dark pair applies",
            focusedTheme.DarkMode == true && !focusedTheme.AutoTheme);
        Check("state theme: a material override brings the material's own coating level (液态玻璃 0.18)",
            Math.Abs(focusedTheme.OpacityLevel - 0.18) < 0.001);

        var unfocusedTheme = FocusThemeCoordinator.ResolveStateTheme(stateLink, focused: false, settings);
        Check("state theme: a null override follows the shared theme's surface",
            unfocusedTheme.EffectiveSurface == SurfaceStyle.Acrylic);
        Check("state theme: following keeps the shared opacity",
            Math.Abs(unfocusedTheme.OpacityLevel - 0.4) < 0.001);
        Check("state theme: the unfocused pair applies", unfocusedTheme.DarkMode == false);

        var sameSurfaceLink = stateLink with { FocusedSurface = SurfaceStyle.Acrylic };
        Check("state theme: re-selecting the shared surface keeps the shared opacity (no stale snapshot)",
            Math.Abs(FocusThemeCoordinator.ResolveStateTheme(sameSurfaceLink, focused: true, settings).OpacityLevel - 0.4) < 0.001);

        var fullColorShared = shared with { Surface = SurfaceStyle.FullColor };
        Check("state theme: 全彩 override is opaque",
            FocusThemeCoordinator.ResolveStateTheme(stateLink with { FocusedSurface = SurfaceStyle.FullColor }, focused: true, TestSettings(fullColorShared)).OpacityLevel >= 0.999);

        var liquidGlassLink = stateLink with { FocusedSurface = SurfaceStyle.LiquidGlass };
        Check("state theme: 液态玻璃 resolves to LiquidGlass",
            FocusThemeCoordinator.ResolveStateTheme(liquidGlassLink, focused: true, settings).EffectiveSurface == SurfaceStyle.LiquidGlass);

        var colorfulLink = stateLink with { FocusedSurface = SurfaceStyle.Colorful };
        var colorfulTheme = FocusThemeCoordinator.ResolveStateTheme(colorfulLink, focused: true, settings);
        Check("state theme: 多彩 is a legal per-state material",
            colorfulTheme.EffectiveSurface == SurfaceStyle.Colorful);
        Check("state theme: 多彩 override is opaque", colorfulTheme.OpacityLevel >= 0.999);

        var colorfulShared = shared with { Surface = SurfaceStyle.Colorful };
        Check("state theme: an override still wins while the shared theme is 多彩",
            FocusThemeCoordinator.ResolveStateTheme(stateLink, focused: true, TestSettings(colorfulShared)).EffectiveSurface == SurfaceStyle.LiquidGlass);

        // Verify user scenario: Shared is Colorful, Unfocused is LiquidGlass (Monochrome BlackWhite)
        var userLink = FocusLinkSettings.CreateDefault(null, false) with
        {
            Enabled = true,
            FocusedSurface = SurfaceStyle.Colorful,
            UnfocusedSurface = SurfaceStyle.LiquidGlass
        };
        var userSettings = TestSettings(new Theme(false, null, 1.0, false, false, "Inter", SurfaceStyle.Colorful));
        var userUnfocusedTheme = FocusThemeCoordinator.ResolveStateTheme(userLink, focused: false, userSettings);
        Check("state theme: unfocused LiquidGlass preserves monochrome from surface preset",
            userUnfocusedTheme.EffectiveSurface == SurfaceStyle.LiquidGlass
            && userUnfocusedTheme.Monochrome
            && userUnfocusedTheme.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite
            && !userUnfocusedTheme.IsColorful);

        var userFocusedTheme = FocusThemeCoordinator.ResolveStateTheme(userLink, focused: true, userSettings);
        Check("state theme: focused Colorful preserves colorful and disables monochrome",
            userFocusedTheme.EffectiveSurface == SurfaceStyle.Colorful
            && userFocusedTheme.IsColorful
            && !userFocusedTheme.Monochrome);

        // Verify ApplyAccentRamp applies distinct accents per dictionary
        var unfocusedResources = new Avalonia.Controls.ResourceDictionary();
        ThemeService.ApplyAccentRamp(unfocusedResources, userUnfocusedTheme, isDarkVariant: false);
        Check("accent ramp: unfocused monochrome BlackWhite sets black accent in light mode",
            unfocusedResources["SystemAccentColor"] is Avalonia.Media.Color uColor && uColor == Avalonia.Media.Colors.Black);

        ThemeService.ApplyAccentRamp(unfocusedResources, userUnfocusedTheme, isDarkVariant: true);
        Check("accent ramp: unfocused monochrome BlackWhite sets white accent in dark mode",
            unfocusedResources["SystemAccentColor"] is Avalonia.Media.Color uDarkColor && uDarkColor == Avalonia.Media.Colors.White);

        var focusedResources = new Avalonia.Controls.ResourceDictionary();
        ThemeService.ApplyAccentRamp(focusedResources, userFocusedTheme, isDarkVariant: false);
        Check("accent ramp: colorful theme strictly writes no accent overrides",
            !focusedResources.ContainsKey("SystemAccentColor") && !focusedResources.ContainsKey("SystemAccentColorDark1"));

        // --- the live surface (uniform switch: one state at a time) ---
        Console.WriteLine();
        Console.WriteLine("--- live surface (uniform switch) ---");

        Check("uniform: the focused state's theme is the shared surface when no override applies",
            FocusThemeCoordinator.ResolveStateTheme(stateLink, focused: true, settings).EffectiveSurface
                == SurfaceStyle.LiquidGlass);
        Check("uniform: the unfocused state follows the shared surface when its override is unset",
            FocusThemeCoordinator.ResolveStateTheme(stateLink, focused: false, settings).EffectiveSurface
                == shared.EffectiveSurface);
        Check("uniform: a 多彩 state override resolves to the 多彩 material",
            FocusThemeCoordinator.ResolveStateTheme(colorfulLink, focused: true, settings).EffectiveSurface
                == SurfaceStyle.Colorful);

        // --- theme profile resolver & surface normalization ---
        Console.WriteLine();
        Console.WriteLine("--- theme profile resolver & surface normalization ---");

        // 1. Colorful + manual accent + monochrome enabled -> normalized ColorfulFixed
        var rawColorful = new Theme(
            DarkMode: true,
            AccentColor: "#FF5500",
            OpacityLevel: 0.5,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Segoe UI",
            Surface: SurfaceStyle.Colorful,
            MonochromeVariant: MonochromeStyle.BlackWhite,
            TextShadowEnabled: true,
            TextShadowStrength: 80);

        var normColorful = rawColorful.NormalizeForSurface();
        Check("normalization: colorful clears accent, monochrome and restores opacity 1.0",
            normColorful.AccentColor is null
            && !normColorful.Monochrome
            && normColorful.MonochromeVariant is null
            && Math.Abs(normColorful.OpacityLevel - 1.0) < 0.001
            && normColorful.EffectiveSurface == SurfaceStyle.Colorful
            && normColorful.TextShadowEnabled
            && normColorful.FontFamily == "Segoe UI");

        var liquidGlassTheme = new Theme(false, "#FF0000", 0.14, false, false, "Inter", SurfaceStyle.LiquidGlass);
        var normLiquidGlass = liquidGlassTheme.NormalizeForSurface();
        Check("normalization: non-colorful (liquid glass) runs NormalizeMaterial and preserves accent",
            normLiquidGlass.EffectiveSurface == SurfaceStyle.LiquidGlass
            && normLiquidGlass.Surface == SurfaceStyle.LiquidGlass
            && normLiquidGlass.AccentColor == "#FF0000");

        var colorfulProfile = ThemeProfileResolver.Resolve(rawColorful, ThemeVariant.Dark);
        Check("resolver: colorful resolves to ColorfulFixed policy",
            colorfulProfile.ColorPolicy == ThemeColorPolicy.ColorfulFixed);
        Check("resolver: colorful profile accent and mono are strictly null",
            colorfulProfile.AccentColor is null && colorfulProfile.MonochromeVariant is null);
        Check("resolver: colorful scope signature contains no accent or mono tokens",
            !colorfulProfile.ScopeSignature.Contains("accent=")
            && !colorfulProfile.ScopeSignature.Contains("mono=")
            && colorfulProfile.ScopeSignature == $"ColorfulFixed:Dark:{SurfaceStyle.FullColor}:rev1");

        // 2. Modifying accent in Colorful leaves ScopeSignature constant; normal theme modifies signature
        var colorfulMod1 = rawColorful with { AccentColor = "#112233" };
        var colorfulMod2 = rawColorful with { AccentColor = "#AABBCC" };
        var colorfulSig1 = ThemeProfileResolver.Resolve(colorfulMod1, ThemeVariant.Dark).ScopeSignature;
        var colorfulSig2 = ThemeProfileResolver.Resolve(colorfulMod2, ThemeVariant.Dark).ScopeSignature;
        Check("resolver: colorful signature remains constant when accent changes",
            colorfulSig1 == colorfulSig2 && colorfulSig1 == colorfulProfile.ScopeSignature);

        var normalTheme1 = new Theme(
            DarkMode: false,
            AccentColor: "#112233",
            OpacityLevel: 0.4,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic);
        var normalTheme2 = normalTheme1 with { AccentColor = "#AABBCC" };
        var normalSig1 = ThemeProfileResolver.Resolve(normalTheme1, ThemeVariant.Light).ScopeSignature;
        var normalSig2 = ThemeProfileResolver.Resolve(normalTheme2, ThemeVariant.Light).ScopeSignature;
        Check("resolver: normal signature changes when accent changes",
            normalSig1 != normalSig2);

        // 3. Normal theme: hand-picked accent / follow system / monochrome styles
        var normalManual = new Theme(
            DarkMode: true,
            AccentColor: "#007AFF",
            OpacityLevel: 0.4,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic);
        var resManual = ThemeProfileResolver.Resolve(normalManual, ThemeVariant.Dark);
        Check("resolver: normal with picked accent resolves to Normal policy with color parsed",
            resManual.ColorPolicy == ThemeColorPolicy.Normal
            && resManual.AccentColor == Color.Parse("#007AFF")
            && resManual.MonochromeVariant is null
            && resManual.ScopeSignature == "Normal:Dark:Acrylic:mono=:accent=#007AFF:rev1");

        var normalSystem = normalManual with { AccentColor = null };
        var resSystem = ThemeProfileResolver.Resolve(normalSystem, ThemeVariant.Dark);
        Check("resolver: normal with system accent resolves to null accent and accent=system in signature",
            resSystem.ColorPolicy == ThemeColorPolicy.Normal
            && resSystem.AccentColor is null
            && resSystem.MonochromeVariant is null
            && resSystem.ScopeSignature == "Normal:Dark:Acrylic:mono=:accent=system:rev1");

        var normalMonoBW = normalManual with
        {
            Monochrome = true,
            MonochromeVariant = MonochromeStyle.BlackWhite
        };
        var resMonoBW = ThemeProfileResolver.Resolve(normalMonoBW, ThemeVariant.Dark);
        Check("resolver: normal with monochrome BlackWhite resolves monochrome variant",
            resMonoBW.ColorPolicy == ThemeColorPolicy.Normal
            && resMonoBW.MonochromeVariant == MonochromeStyle.BlackWhite
            && resMonoBW.ScopeSignature == "Normal:Dark:Acrylic:mono=BlackWhite:accent=#007AFF:rev1");

        var normalMonoDefault = normalManual with
        {
            Monochrome = true,
            MonochromeVariant = null
        };
        var resMonoDefault = ThemeProfileResolver.Resolve(normalMonoDefault, ThemeVariant.Light);
        Check("resolver: normal with monochrome default falls back to EffectiveMonochromeVariant (Accent)",
            resMonoDefault.MonochromeVariant == MonochromeStyle.Accent
            && resMonoDefault.ScopeSignature == "Normal:Light:Acrylic:mono=Accent:accent=#007AFF:rev1");

        // 4. Round-trip and AppSettingsProvider normalization
        var originalLiquid = new Theme(
            DarkMode: false,
            AccentColor: "#FF3B30",
            OpacityLevel: 0.25,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Consolas",
            Surface: SurfaceStyle.LiquidGlass,
            MonochromeVariant: MonochromeStyle.BlackWhite);

        var testAppSettings = new AppSettings(
            Theme: originalLiquid,
            Templates: [
                originalLiquid,
                new Theme(true, "#00FF00", 0.5, true, false, "Arial", SurfaceStyle.Colorful, MonochromeVariant: MonochromeStyle.BackgroundColor)
            ],
            Layout: new Layout(GridMode.Manual, false, false, true, false),
            Dimensions: new Dimensions(100, 10, 16),
            Region: new Region("en"),
            RunOnStartup: false,
            IgnoreUpdate: null,
            SurfaceThemes: new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(SurfaceStyle.LiquidGlass)] = originalLiquid,
                [nameof(SurfaceStyle.Colorful)] = new Theme(
                    false, "#990000", 0.7, true, false, "Inter", SurfaceStyle.Colorful, MonochromeVariant: MonochromeStyle.BackgroundColor)
            });

        var provider = new TestAppSettingsProvider();
        var normalizedSettings = provider.NormalizeTest(testAppSettings);

        var restoredLiquid = normalizedSettings.GetThemeForSurface(SurfaceStyle.LiquidGlass);
        Check("round-trip: normal theme in SurfaceThemes preserves custom accent and monochrome",
            restoredLiquid.EffectiveSurface == SurfaceStyle.LiquidGlass
            && restoredLiquid.AccentColor == "#FF3B30"
            && restoredLiquid.Monochrome
            && restoredLiquid.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite
            && Math.Abs(restoredLiquid.OpacityLevel - 0.25) < 0.001);

        var normalizedColorfulSnapshot = normalizedSettings.GetThemeForSurface(SurfaceStyle.Colorful);
        Check("round-trip: colorful theme in SurfaceThemes is normalized (null accent, no mono, opacity 1.0)",
            normalizedColorfulSnapshot.EffectiveSurface == SurfaceStyle.Colorful
            && normalizedColorfulSnapshot.AccentColor is null
            && !normalizedColorfulSnapshot.Monochrome
            && normalizedColorfulSnapshot.MonochromeVariant is null
            && Math.Abs(normalizedColorfulSnapshot.OpacityLevel - 1.0) < 0.001);

        Check("round-trip: templates array was normalized for surface",
            normalizedSettings.Templates.Length == 2
            && normalizedSettings.Templates.Any(t => t.EffectiveSurface == SurfaceStyle.LiquidGlass && t.AccentColor == "#FF3B30")
            && normalizedSettings.Templates.Any(t => t.EffectiveSurface == SurfaceStyle.Colorful && t.AccentColor is null && !t.Monochrome));

        var switchedToColorful = normalizedSettings.WithThemeForSurface(SurfaceStyle.Colorful, normalizedColorfulSnapshot);
        var backToLiquid = switchedToColorful.GetThemeForSurface(SurfaceStyle.LiquidGlass);
        Check("round-trip: switching to colorful and back preserves original liquid glass snapshot",
            backToLiquid.AccentColor == "#FF3B30"
            && backToLiquid.Monochrome
            && backToLiquid.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite);

        // --- Phase B: ColorfulResources closure completeness ---
        Console.WriteLine();
        Console.WriteLine("--- Phase B: ColorfulResources closure completeness ---");
        VerifyColorfulResourcesClosure();

        // --- Phase B: ThemeResourceHelper scope switching ---
        Console.WriteLine();
        Console.WriteLine("--- Phase B: ThemeResourceHelper scope switching ---");
        VerifyThemeResourceHelperScopeSwitching();

        // --- Phase C: Dual-window isolation ---
        Console.WriteLine();
        Console.WriteLine("--- Phase C: Dual-window isolation ---");
        VerifyDualWindowIsolation();

        // --- Phase C: Leak prevention 100 switches ---
        Console.WriteLine();
        Console.WriteLine("--- Phase C: Leak prevention 100 switches ---");
        VerifyLeakPrevention100Switches();

        // --- Phase C: Application theme focus link ---
        Console.WriteLine();
        Console.WriteLine("--- Phase C: Application theme focus link ---");
        VerifyApplicationThemeFocusLink();

        // --- Phase D: Widget resource scope resolution ---
        Console.WriteLine();
        Console.WriteLine("--- Phase D: Widget resource scope resolution ---");
        VerifyWidgetResourceScoping();

        // --- Phase D: 开关本功能后接一次刷新 ---
        Console.WriteLine();
        Console.WriteLine("--- master switch toggle refresh ---");
        VerifyFeatureToggleRefresh();

        // --- live Win32 smoke test ---
        Console.WriteLine();
        Console.WriteLine("--- live enumeration (smoke) ---");
        try
        {
            var processes = FocusWatchService.EnumerateWindowedProcesses();
            Console.WriteLine($"windowed processes: {processes.Count}");
            foreach (var process in processes.Take(5))
                Console.WriteLine($"  {process.Name} — {process.Title}");
        }
        catch (Exception ex)
        {
            Check($"live enumeration threw: {ex.Message}", false);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition)
    {
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}");
        if (!condition) failures++;
    }

    /// <summary>A minimal settings snapshot for the pure resolvers (catalog and grid values are
    /// irrelevant there).</summary>
    private static AppSettings TestSettings(Theme theme) =>
        new(theme, [], new Layout(GridMode.Manual, false, false, true, false),
            new Dimensions(100, 10, 16), new Region("en"), false, null);

    /// <summary>A visible, titled application window at the given bounds.</summary>
    private static FocusWindow App(
        string process, ScreenRect bounds,
        bool minimized = false, bool cloaked = false, bool tool = false, bool visible = true, string title = "Window",
        bool noActivate = false)
        => new("ApplicationFrameWindow", 4242, IsOwnProcess: false, process, title, bounds,
            IsVisible: visible, IsMinimized: minimized, IsCloaked: cloaked, IsToolWindow: tool,
            IsNoActivate: noActivate);

    /// <summary>
    /// A popup / overlay / HUD: visible, titled, <b>not</b> a tool window, but refusing
    /// activation (<c>WS_EX_NOACTIVATE</c>) — the shape of the shell's thumbnail flyout
    /// （「主机弹出窗口」, <c>Xaml_WindowedPopupClass</c>, explorer） that made the theme flicker
    /// when the desktop was cleared and the pointer reached the dock / taskbar.
    /// </summary>
    private static FocusWindow Popup(ScreenRect bounds, string process = "explorer") =>
        new("Xaml_WindowedPopupClass", 100, IsOwnProcess: false, process, "主机弹出窗口", bounds,
            IsNoActivate: true);

    /// <summary>One of DeskCanvas' own windows (widget / panel / settings).</summary>
    private static FocusWindow Own(string title)
        => new("DeskCanvas.Widget", 99, IsOwnProcess: true, null, title, new ScreenRect(0, 0, 10, 10));

    /// <summary>A shell surface (desktop / taskbar / start menu / …).</summary>
    private static FocusWindow Shell(string className, string process = "explorer")
        => new(className, 100, IsOwnProcess: false, process, "Shell Surface", new ScreenRect(0, 0, 10, 10));

    private static void Walk(
        string name,
        IReadOnlyList<FocusWindow> windows,
        IReadOnlyList<ScreenRect> screens,
        int[] expected,
        IReadOnlyList<string>? whitelist = null)
    {
        var outcome = FocusWatchService.ResolveUnfocusedScreens(windows, screens, whitelist);
        var ok = outcome.UnfocusedScreenIndices.Count == expected.Length
            && outcome.UnfocusedScreenIndices.SequenceEqual(expected);
        var shown = outcome.UnfocusedScreenIndices.Count == 0
            ? string.Empty
            : string.Join(", ", outcome.UnfocusedScreenIndices);
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name} → [{shown}]");
        if (!ok) failures++;
    }

    private static void Focused(string name, FocusStateSnapshot state, bool expected)
    {
        var actual = FocusThemeCoordinator.ResolveFocused(state);
        Check($"{name} (expected {(expected ? "focused" : "unfocused")})", actual == expected);
    }

    private static void VerifyColorfulResourcesClosure()
    {
        var axamlPath = FindRepoFile(Path.Combine("src", "DeskCanvas", "Styles", "ColorfulResources.axaml"));
        var content = File.ReadAllText(axamlPath);

        var defaultKeys = ExtractDictionaryKeys(content, "Default");
        var darkKeys = ExtractDictionaryKeys(content, "Dark");

        var requiredFluentBase = new[]
        {
            "SystemAccentColor",
            "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
            "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
            "AccentContrastColor", "AccentContrastForegroundBrush",
            "SystemControlForegroundAccentBrush", "SystemControlHighlightAccentBrush"
        };

        var requiredFluentControls = new[]
        {
            "ToggleSwitchKnobFillOn", "ToggleSwitchKnobFillOnPointerOver", "ToggleSwitchKnobFillOnPressed",
            "CheckBoxCheckGlyphForegroundChecked", "CheckBoxCheckGlyphForegroundCheckedPointerOver", "CheckBoxCheckGlyphForegroundCheckedPressed",
            "CheckBoxCheckGlyphForegroundIndeterminate", "CheckBoxCheckGlyphForegroundIndeterminatePointerOver", "CheckBoxCheckGlyphForegroundIndeterminatePressed",
            "RadioButtonCheckGlyphFill", "RadioButtonCheckGlyphFillPointerOver", "RadioButtonCheckGlyphFillPressed",
            "ToggleSwitchFillOnPointerOver", "ToggleSwitchFillOnPressed",
            "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundFillIndeterminatePressed",
            "RadioButtonOuterEllipseCheckedFillPointerOver", "RadioButtonOuterEllipseCheckedFillPressed",
            "RadioButtonOuterEllipseCheckedStrokePointerOver", "RadioButtonOuterEllipseCheckedStrokePressed"
        };

        var requiredWidgetSemantics = new[]
        {
            "WidgetBackground", "WidgetForeground", "WidgetCardBorderBrush",
            "CalendarHeaderBrush", "CalendarTodayBrush", "CalendarCardBackground",
            "ClockHandBrush", "ClockSecondHandBrush", "ClockCenterPinBrush",
            "ClockCardBackground", "ClockAnalogIIICardBackground", "FlipClockCardBackground", "ClockDigitalCardBackground",
            "ClockDigitalTimeBrush", "ClockDigitalDateBrush", "ClockDialBackgroundBrush",
            "ClockOpaqueDialBackgroundBrush", "ClockStrokesBrush",
            "FlipClockTopFlapBrush", "FlipClockBottomFlapBrush", "FlipClockFlapBorderBrush",
            "FlipClockCreaseBrush", "FlipClockHingeBrush",
            "RemindersCardBackground", "RemindersBadgeBrush", "RemindersCheckBrush",
            "RemindersDotOrange", "RemindersDotRed", "RemindersDotBlue", "RemindersDotPurple",
            "NotesCardBackground", "NotesAccentBrush", "NotesPaperBackground",
            "NotesTextBrush", "NotesTitleForegroundBrush", "NotesDividerBrush", "NotesUpdatedBrush",
            "MusicCardBackground", "MusicAccentBrush", "MusicScrubberTrackBrush",
            "MusicPlayButtonBackgroundBrush", "MusicPlayButtonHoverBrush",
            "MusicCoverPlaceholderBrush", "MusicCoverBorderBrush",
            "PomodoroCardBackground", "PomodoroPillBackgroundBrush", "PomodoroPillBorderBrush",
            "PomodoroButtonBackgroundBrush", "PomodoroButtonHoverBrush",
            "HitokotoCardBackground", "HitokotoWatermarkBrush", "HitokotoQuoteBrush",
            "HitokotoAuthorBrush", "HitokotoPillBackgroundBrush", "HitokotoPillBorderBrush",
            "HitokotoPillForegroundBrush", "HitokotoButtonHoverBrush", "HitokotoButtonPressedBrush",
            "BatteriesCardBackground", "BatteriesTrackBrush", "BatteriesIconBrush",
            "BatteriesTextBrush", "BatteriesNormalBrush", "BatteriesChargingBrush",
            "BatteriesWarningBrush", "BatteriesCriticalBrush",
            "MonitorCardBackground", "MonitorTrackBrush", "MonitorCpuBrush",
            "MonitorRamBrush", "MonitorDiskBrush", "MonitorNetBrush", "MonitorBatteryBrush",
            "StorageCardBackground", "StorageSubCardBackground", "StorageSubCardBorderBrush",
            "StorageTrackBackground", "StorageBadgeBackground", "StorageSystemBrush",
            "StorageAppsBrush", "StorageDocsBrush", "StorageMediaBrush",
            "StorageRemovableBrush", "StorageWarningBrush", "StorageCriticalBrush",
            "FoldersCardBackground", "FolderAccentBrush",
            "TranslatorCardBackground", "TranslateCardBackground", "TranslateAccentBrush",
            "TranslatorInputBackground", "TranslatorOutputBackground", "TranslatorOutputBorderBrush",
            "CalculatorCardBackground", "CalculatorDisplayBackground", "CalculatorAccentBrush",
            "CalculatorNumButtonBackground", "CalculatorFuncButtonBackground", "CalculatorOpButtonBackground",
            "ClipboardCardBackground", "ClipboardItemCardBackground", "ClipboardItemCardHoverBackground",
            "ClipboardBadgeTextBgBrush", "ClipboardBadgeTextBrush",
            "ClipboardBadgeCodeBgBrush", "ClipboardBadgeCodeBrush",
            "ClipboardBadgeLinkBgBrush", "ClipboardBadgeLinkBrush",
            "ClipboardBadgeFileBgBrush", "ClipboardBadgeFileBrush",
            "ControlCenterCardBackground", "ControlCenterCapsuleBackground",
            "ControlCenterBadgeInactiveBackground",
            "MapCardBackground", "MapFloatingButtonBackground",
            "ProgressCardBackground", "ProgressMoveBrush", "ProgressExerciseBrush", "ProgressStandBrush",
            "SearchCardBackground", "SearchMagnifierBrush", "SearchPillBackground",
            "SearchPillBorderBrush", "SearchEngineButtonBackground", "SearchDockButtonBackground",
            "SearchButtonBrush",
            "WeatherCardBackground", "WeatherForegroundBrush", "WeatherIconBrush",
            "WeatherGaugeBrush", "WeatherTempBarBrush",
            "TimetableCardBackground"
        };

        var allRequired = requiredFluentBase
            .Concat(requiredFluentControls)
            .Concat(requiredWidgetSemantics)
            .Distinct()
            .ToArray();

        var missingDefault = allRequired.Where(k => !defaultKeys.Contains(k)).ToArray();
        Check($"colorful closure: Default dictionary contains all required keys (missing {missingDefault.Length})",
            missingDefault.Length == 0);
        if (missingDefault.Length > 0)
        {
            Console.WriteLine($"    Default missing: {string.Join(", ", missingDefault)}");
        }

        var missingDark = allRequired.Where(k => !darkKeys.Contains(k)).ToArray();
        Check($"colorful closure: Dark dictionary contains all required keys (missing {missingDark.Length})",
            missingDark.Length == 0);
        if (missingDark.Length > 0)
        {
            Console.WriteLine($"    Dark missing: {string.Join(", ", missingDark)}");
        }

        Check("colorful closure: Default contains SystemAccentColor #007AFF",
            content.Contains("<Color x:Key=\"SystemAccentColor\">#007AFF</Color>"));
        Check("colorful closure: Dark contains SystemAccentColor #0A84FF",
            content.Contains("<Color x:Key=\"SystemAccentColor\">#0A84FF</Color>"));
        Check("colorful closure: AccentContrastColor is White",
            content.Contains("<Color x:Key=\"AccentContrastColor\">White</Color>"));
    }

    private static void VerifyThemeResourceHelperScopeSwitching()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        var window = new Window();

        // 1. Apply Normal scope with manual accent
        var normalManual = new Theme(
            DarkMode: true,
            AccentColor: "#FF5500",
            OpacityLevel: 0.5,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic);
        var profileManual = ThemeProfileResolver.Resolve(normalManual, ThemeVariant.Dark);

        ThemeResourceHelper.ApplyNormalColorScope(window, profileManual, isDarkVariant: true);

        Check("scope normal manual: policy is Normal",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("scope normal manual: ColorfulActive is not present",
            !window.Resources.ContainsKey("ColorfulActive"));
        Check("scope normal manual: ColorfulResources include not in MergedDictionaries",
            !window.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));
        Check("scope normal manual: SystemAccentColor ramp applied with #FF5500",
            window.Resources["SystemAccentColor"] is Color c && c == Color.Parse("#FF5500"));
        Check("scope normal manual: no monochrome styles attached",
            !window.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Monochrome") == true));

        // 2. Apply Normal scope with monochrome BlackWhite
        var normalMonoBW = normalManual with
        {
            Monochrome = true,
            MonochromeVariant = MonochromeStyle.BlackWhite
        };
        var profileMonoBW = ThemeProfileResolver.Resolve(normalMonoBW, ThemeVariant.Light);

        ThemeResourceHelper.ApplyNormalColorScope(window, profileMonoBW, isDarkVariant: false);

        Check("scope normal mono: policy is Normal",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("scope normal mono: BlackWhite in light mode sets black accent",
            window.Resources["SystemAccentColor"] is Color cBw && cBw == Colors.Black);
        Check("scope normal mono: monochrome BlackWhite style attached",
            window.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("MonochromeBlackWhite") == true));

        // 3. Switch to Colorful scope
        var rawColorful = new Theme(
            DarkMode: false,
            AccentColor: "#00FF00",
            OpacityLevel: 0.8,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Colorful,
            MonochromeVariant: MonochromeStyle.BackgroundColor);
        var profileColorful = ThemeProfileResolver.Resolve(rawColorful, ThemeVariant.Light);

        ThemeResourceHelper.ApplyColorfulColorScope(window, profileColorful);

        Check("scope colorful: policy is ColorfulFixed",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.ColorfulFixed);
        Check("scope colorful: ColorfulActive is true",
            window.Resources["ColorfulActive"] is bool active && active);
        Check("scope colorful: ColorfulResources include attached to window",
            window.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));
        Check("scope colorful: Monochrome style unmounted",
            !window.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Monochrome") == true));
        Check("scope colorful: window accent ramp completely cleared",
            !window.Resources.ContainsKey("SystemAccentColor")
            && !window.Resources.ContainsKey("SystemAccentColorDark1")
            && !window.Resources.ContainsKey("SystemAccentColorLight1"));

        // 4. Switch back to Normal (verifying round-trip and zero leftover)
        ThemeResourceHelper.ApplyNormalColorScope(window, profileManual, isDarkVariant: false);

        Check("scope switch back normal: policy is Normal",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("scope switch back normal: ColorfulActive cleared",
            !window.Resources.ContainsKey("ColorfulActive"));
        Check("scope switch back normal: ColorfulResources include removed",
            !window.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));
        Check("scope switch back normal: manual accent restored",
            window.Resources["SystemAccentColor"] is Color cRestored && cRestored == Color.Parse("#FF5500"));
        Check("scope switch back normal: monochrome styles completely removed",
            !window.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Monochrome") == true));

        // 5. ClearWindowColorScope
        ThemeResourceHelper.ClearWindowColorScope(window);

        Check("scope clear: ThemeScopePolicy removed",
            !window.Resources.ContainsKey("ThemeScopePolicy"));
        Check("scope clear: ColorfulActive removed",
            !window.Resources.ContainsKey("ColorfulActive"));
        Check("scope clear: Accent overrides removed",
            !window.Resources.ContainsKey("SystemAccentColor"));
        Check("scope clear: MergedDictionaries empty of Colorful",
            !window.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));
        Check("scope clear: Styles empty of Monochrome",
            !window.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Monochrome") == true));
    }

    private static void VerifyDualWindowIsolation()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        // Snapshot application resources before test
        var appHadAccent = Avalonia.Application.Current!.Resources.ContainsKey("SystemAccentColor");
        var appHadColorfulActive = Avalonia.Application.Current.Resources.ContainsKey("ColorfulActive");

        var windowA = new Window();
        var windowB = new Window();

        var themeA = new Theme(
            DarkMode: false,
            AccentColor: null,
            OpacityLevel: 1.0,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Colorful);
        var profileA = ThemeProfileResolver.Resolve(themeA, ThemeVariant.Light);

        var redAccent = Color.Parse("#FF0000");
        var themeB = new Theme(
            DarkMode: false,
            AccentColor: "#FF0000",
            OpacityLevel: 0.5,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic);
        var profileB = ThemeProfileResolver.Resolve(themeB, ThemeVariant.Light);

        // Apply scopes concurrently in memory
        ThemeResourceHelper.ApplyColorfulColorScope(windowA, profileA);
        ThemeResourceHelper.ApplyNormalColorScope(windowB, profileB, isDarkVariant: false);

        // 1. Assert Window A keeps colorful fixed color and ColorfulActive
        Check("dual window: Window A policy is ColorfulFixed",
            (ThemeColorPolicy)windowA.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.ColorfulFixed);
        Check("dual window: Window A ColorfulActive is true",
            windowA.Resources["ColorfulActive"] is bool actA && actA);
        Check("dual window: Window A has ColorfulResources in merged dictionaries",
            windowA.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));

        bool aResolvedAccent = windowA.TryFindResource("SystemAccentColor", ThemeVariant.Default, out var aColorVal);
        Check("dual window: Window A resolves SystemAccentColor #007AFF from colorful dictionary",
            aResolvedAccent && aColorVal is Color aColor && aColor == Color.Parse("#007AFF"));

        // 2. Assert Window B has red ramp and ColorfulActive is false/not defined
        Check("dual window: Window B policy is Normal",
            (ThemeColorPolicy)windowB.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("dual window: Window B ColorfulActive is not present",
            !windowB.Resources.ContainsKey("ColorfulActive"));
        Check("dual window: Window B has no ColorfulResources include",
            !windowB.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));
        Check("dual window: Window B direct SystemAccentColor is red",
            windowB.Resources["SystemAccentColor"] is Color bColor && bColor == redAccent);

        bool bResolvedAccent = windowB.TryFindResource("SystemAccentColor", ThemeVariant.Default, out var bColorVal);
        Check("dual window: Window B resolves SystemAccentColor as red",
            bResolvedAccent && bColorVal is Color bResolved && bResolved == redAccent);

        // 3. Assert mutual isolation (no cross-contamination)
        Check("dual window: Window A does not have direct red accent key",
            !windowA.Resources.ContainsKey("SystemAccentColor"));
        Check("dual window: Window B does not resolve to #007AFF",
            bColorVal is Color bCheck && bCheck != Color.Parse("#007AFF"));

        // 4. Assert no pollution to Application.Current.Resources
        Check("dual window: Application.Current.Resources not polluted with Window B red accent",
            !Avalonia.Application.Current.Resources.ContainsKey("SystemAccentColor")
            || (Color)Avalonia.Application.Current.Resources["SystemAccentColor"]! != redAccent);
        Check("dual window: Application.Current.Resources ColorfulActive state unchanged",
            Avalonia.Application.Current.Resources.ContainsKey("ColorfulActive") == appHadColorfulActive);
    }

    private static void VerifyLeakPrevention100Switches()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        var window = new Window();

        var colorfulTheme = new Theme(
            DarkMode: false,
            AccentColor: null,
            OpacityLevel: 1.0,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Colorful);
        var colorfulProfile = ThemeProfileResolver.Resolve(colorfulTheme, ThemeVariant.Light);

        var normalTheme = new Theme(
            DarkMode: true,
            AccentColor: "#007AFF",
            OpacityLevel: 0.3,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.LiquidGlass,
            MonochromeVariant: MonochromeStyle.BlackWhite);
        var normalProfile = ThemeProfileResolver.Resolve(normalTheme, ThemeVariant.Dark);

        bool boundsOk = true;
        for (int i = 0; i < 100; i++)
        {
            if (i % 2 == 0)
            {
                ThemeResourceHelper.ApplyColorfulColorScope(window, colorfulProfile);
                if (window.Resources.MergedDictionaries.Count != 1 || window.Styles.Count != 0)
                {
                    boundsOk = false;
                }
            }
            else
            {
                ThemeResourceHelper.ApplyNormalColorScope(window, normalProfile, isDarkVariant: true);
                if (window.Resources.MergedDictionaries.Count != 0 || window.Styles.Count != 1)
                {
                    boundsOk = false;
                }
            }
        }

        Check("100 switches: MergedDictionaries.Count is always 0 or 1 and Styles.Count is always 0 or 1 (no leak)", boundsOk);

        // Final state was Normal (since i=99 is odd)
        Check("100 switches: final policy is Normal",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("100 switches: final MergedDictionaries.Count == 0",
            window.Resources.MergedDictionaries.Count == 0);
        Check("100 switches: final Styles.Count == 1 (MonochromeBlackWhite only)",
            window.Styles.Count == 1
            && window.Styles[0] is StyleInclude inc && inc.Source?.OriginalString?.Contains("MonochromeBlackWhite") == true);
        Check("100 switches: final SystemAccentColor is White (BlackWhite in dark variant)",
            window.Resources["SystemAccentColor"] is Color c && c == Colors.White);
        Check("100 switches: ColorfulActive is cleared",
            !window.Resources.ContainsKey("ColorfulActive"));

        // One more switch to Colorful to verify clean transition from 100th switch
        ThemeResourceHelper.ApplyColorfulColorScope(window, colorfulProfile);
        Check("100 switches + 1: switch back to Colorful policy is ColorfulFixed",
            (ThemeColorPolicy)window.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.ColorfulFixed);
        Check("100 switches + 1: MergedDictionaries.Count is strictly 1",
            window.Resources.MergedDictionaries.Count == 1);
        Check("100 switches + 1: Styles.Count is strictly 0",
            window.Styles.Count == 0);
        Check("100 switches + 1: ColorfulActive is true",
            window.Resources["ColorfulActive"] is bool act && act);
        Check("100 switches + 1: Accent overrides cleared",
            !window.Resources.ContainsKey("SystemAccentColor"));
    }

    private static void VerifyApplicationThemeFocusLink()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        var sharedLiquid = new Theme(
            DarkMode: false,
            AccentColor: "#FF5500",
            OpacityLevel: 0.25,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.LiquidGlass);

        var settings = TestSettings(sharedLiquid) with
        {
            FocusLink = FocusLinkSettings.CreateDefault(null, false) with
            {
                Enabled = true,
                FocusedSurface = SurfaceStyle.Colorful,
                UnfocusedSurface = SurfaceStyle.LiquidGlass,
                Focused = new FocusMode(false, false),
                Unfocused = new FocusMode(true, false)
            }
        };

        var resolvedAppTheme = ThemeService.ResolveApplicationTheme(settings);
        Check("app focus link: ResolveApplicationTheme returns Colorful when focused state is Colorful",
            resolvedAppTheme.EffectiveSurface == SurfaceStyle.Colorful && resolvedAppTheme.IsColorful);
        Check("app focus link: normalized colorful has opacity 1.0 and null accent",
            Math.Abs(resolvedAppTheme.OpacityLevel - 1.0) < 0.001 && resolvedAppTheme.AccentColor is null && !resolvedAppTheme.Monochrome);

        var stub = new StubSettings(settings);
        var wallpaperTheme = new WallpaperThemeService();
        var themeService = new ThemeService(stub, wallpaperTheme);

        themeService.Apply(settings);

        Check("app focus link: Application policy is ColorfulFixed",
            (ThemeColorPolicy)Avalonia.Application.Current!.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.ColorfulFixed);
        Check("app focus link: Application ColorfulActive is true",
            Avalonia.Application.Current.Resources["ColorfulActive"] is bool act && act);
        Check("app focus link: Application.Styles mounted colorfulStyle",
            Avalonia.Application.Current.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Colorful.axaml") == true));
        Check("app focus link: Application.Resources.MergedDictionaries mounted ColorfulResources.axaml",
            Avalonia.Application.Current.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml") == true));
        Check("app focus link: Application accent overrides cleared",
            !Avalonia.Application.Current.Resources.ContainsKey("SystemAccentColor"));

        // Reverse: Focused is LiquidGlass (Normal), Unfocused is Colorful
        var settingsNormalFocused = settings with
        {
            FocusLink = settings.FocusLink with
            {
                FocusedSurface = SurfaceStyle.LiquidGlass,
                UnfocusedSurface = SurfaceStyle.Colorful
            }
        };

        var resolvedAppThemeNormal = ThemeService.ResolveApplicationTheme(settingsNormalFocused);
        Check("app focus link: ResolveApplicationTheme returns LiquidGlass when focused state is LiquidGlass",
            resolvedAppThemeNormal.EffectiveSurface == SurfaceStyle.LiquidGlass && !resolvedAppThemeNormal.IsColorful);

        themeService.Apply(settingsNormalFocused);

        Check("app focus link reverse: Application policy is Normal",
            (ThemeColorPolicy)Avalonia.Application.Current.Resources["ThemeScopePolicy"]! == ThemeColorPolicy.Normal);
        Check("app focus link reverse: Application ColorfulActive is removed",
            !Avalonia.Application.Current.Resources.ContainsKey("ColorfulActive"));
        Check("app focus link reverse: Application.Styles unmounted colorfulStyle",
            !Avalonia.Application.Current.Styles.Any(s => s is StyleInclude inc && inc.Source?.OriginalString?.Contains("Colorful.axaml") == true));
        Check("app focus link reverse: Application.Resources unmounted ColorfulResources.axaml",
            !Avalonia.Application.Current.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc && inc.Source?.OriginalString?.Contains("ColorfulResources.axaml") == true));
        Check("app focus link reverse: Application accent restored with ramp",
            Avalonia.Application.Current.Resources["SystemAccentColor"] is Color c && c == Color.Parse("#FF5500"));
    }

    private sealed class StubSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged { add { } remove { } }
        public AppSettings Get() => value;
        public void Save(AppSettings data) => value = data;
    }

    /// <summary>A settings provider that really raises <c>DataChanging</c> / <c>DataChanged</c>
    /// — the master-switch toggle reaches the coordinator through them.</summary>
    private sealed class NotifyingSettings(AppSettings value) : IAppSettingsProvider
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

    /// <summary>A tracked window that only counts how often the coordinator refreshed it.</summary>
    private sealed class PokeCountingWindow : Window, IFocusThemedWindow
    {
        public int Pokes { get; private set; }

        public void OnFocusThemeChanged() => Pokes++;
    }

    /// <summary>
    /// 开关本功能后接一次刷新: flipping the master switch is the one settings change the global
    /// application alone cannot carry. The switch is not part of the tuple the application is
    /// keyed on, so the pass that follows the save reports "nothing changed" and never pokes the
    /// windows — yet every window resolves its card brush, glass layer, outline and native
    /// transparency hint from its own effective theme, which just changed. Without the forced
    /// refresh the widgets keep painting the previous material over a global resource set that
    /// already moved on.
    /// </summary>
    private static void VerifyFeatureToggleRefresh()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        var shared = new Theme(
            DarkMode: false,
            AccentColor: null,
            OpacityLevel: 0.4,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic);

        var link = FocusLinkSettings.CreateDefault(false, false) with
        {
            Enabled = false,
            Focused = new FocusMode(false, false),
            Unfocused = new FocusMode(true, false),
            UnfocusedSurface = SurfaceStyle.LiquidGlass
        };

        var provider = new NotifyingSettings(TestSettings(shared) with { FocusLink = link });
        var wallpaperTheme = new WallpaperThemeService();
        var themeService = new ThemeService(provider, wallpaperTheme);
        var coordinator = new FocusThemeCoordinator(
            provider, new FocusWatchService(provider), wallpaperTheme, new FullscreenWatcherService(), themeService);

        var window = new PokeCountingWindow();
        coordinator.Track(window);
        // Registration pokes once so the window resolves against the live theme right away.
        var afterTrack = window.Pokes;
        Check("toggle: tracking a window refreshes it once", afterTrack == 1);

        provider.Save(provider.Get() with { FocusLink = link with { Enabled = true } });
        Check("toggle on: the switch change refreshes every tracked window once",
            window.Pokes == afterTrack + 1);

        var afterEnable = window.Pokes;
        // An unrelated save (the same snapshot again): the theme did not move, so nothing is
        // refreshed — a colour drag must still cost exactly one application, not one per save.
        provider.Save(provider.Get());
        Check("toggle: an unrelated save does not refresh the windows again",
            window.Pokes == afterEnable);

        provider.Save(provider.Get() with { FocusLink = link with { Enabled = false } });
        Check("toggle off: the switch change refreshes every tracked window once",
            window.Pokes == afterEnable + 1);

        var afterDisable = window.Pokes;
        coordinator.Reapply();
        Check("toggle: a plain re-apply after the switch settled refreshes nothing",
            window.Pokes == afterDisable);
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Cannot find {relativePath} in parent directories of {AppContext.BaseDirectory}");
    }

    private static HashSet<string> ExtractDictionaryKeys(string xamlContent, string dictKey)
    {
        var startTag = $"x:Key=\"{dictKey}\"";
        var startIdx = xamlContent.IndexOf(startTag, StringComparison.OrdinalIgnoreCase);
        if (startIdx < 0) return [];

        var openTagIdx = xamlContent.LastIndexOf("<ResourceDictionary", startIdx, StringComparison.OrdinalIgnoreCase);
        if (openTagIdx < 0) openTagIdx = startIdx;

        int depth = 0;
        int endIdx = -1;
        var matches = System.Text.RegularExpressions.Regex.Matches(xamlContent[openTagIdx..], @"(</?ResourceDictionary\b)");
        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            if (match.Value.StartsWith("</"))
            {
                depth--;
                if (depth == 0)
                {
                    endIdx = openTagIdx + match.Index + match.Length;
                    break;
                }
            }
            else
            {
                depth++;
            }
        }

        var dictText = endIdx > openTagIdx
            ? xamlContent[openTagIdx..endIdx]
            : xamlContent[openTagIdx..];

        var keyMatches = System.Text.RegularExpressions.Regex.Matches(dictText, @"x:Key=""([^""]+)""");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (System.Text.RegularExpressions.Match km in keyMatches)
        {
            var val = km.Groups[1].Value;
            if (val != dictKey)
                keys.Add(val);
        }
        return keys;
    }

    private static void VerifyWidgetResourceScoping()
    {
        // 1. ThemeScopeResolver Visual tree vs Window vs Application scope lookup
        var winA = new Window();
        var panel = new StackPanel();
        var button = new Button();
        panel.Children.Add(button);
        winA.Content = panel;

        winA.Resources["ScopedTestBrush"] = new SolidColorBrush(Color.Parse("#112233"));
        Avalonia.Application.Current!.Resources["ScopedTestBrush"] = new SolidColorBrush(Color.Parse("#998877"));

        Check("scope resolver: child finds Window resource rather than Application resource",
            ThemeScopeResolver.TryFindResource<IBrush>(button, "ScopedTestBrush", ThemeVariant.Default, out var resBrush)
            && resBrush is ISolidColorBrush scb && scb.Color == Color.Parse("#112233"));

        // 2. ResolveBrush and ResolveColor conversions
        winA.Resources["ScopedAccent"] = Color.Parse("#FF3366");
        var resolvedBrush = ThemeScopeResolver.ResolveBrush(button, "ScopedAccent", ThemeVariant.Default, Brushes.Black);
        Check("scope resolver: ResolveBrush wraps Color in SolidColorBrush",
            resolvedBrush is ISolidColorBrush scbAccent && scbAccent.Color == Color.Parse("#FF3366"));

        var resolvedColor = ThemeScopeResolver.ResolveColor(button, "ScopedTestBrush", ThemeVariant.Default, Colors.Black);
        Check("scope resolver: ResolveColor extracts Color from ISolidColorBrush",
            resolvedColor == Color.Parse("#112233"));

        var fallbackColor = ThemeScopeResolver.ResolveColor(button, "NonExistentKey123", ThemeVariant.Default, Colors.Goldenrod);
        Check("scope resolver: ResolveColor returns fallback on missing key",
            fallbackColor == Colors.Goldenrod);

        // 3. AggregateViewModel scoped resolution
        var winB = new Window();
        winA.Resources["CalendarTodayBrush"] = new SolidColorBrush(Color.Parse("#00AAFF"));
        winB.Resources["CalendarTodayBrush"] = new SolidColorBrush(Color.Parse("#FF5500"));

        var model = new AggregateModel { TodayColorMode = "Accent" };
        using var vmA = new AggregateViewModel(model, winA);
        using var vmB = new AggregateViewModel(model, winB);

        Check("aggregate vm: vmA TodayDotBrush resolves from Window A scope (#00AAFF)",
            vmA.TodayDotBrush is ISolidColorBrush dotA && dotA.Color == Color.Parse("#00AAFF"));
        Check("aggregate vm: vmB TodayDotBrush resolves from Window B scope (#FF5500)",
            vmB.TodayDotBrush is ISolidColorBrush dotB && dotB.Color == Color.Parse("#FF5500"));

        // Dynamic update on host
        winA.Resources["CalendarTodayBrush"] = new SolidColorBrush(Color.Parse("#123456"));
        vmA.UpdateTodayBrush();
        Check("aggregate vm: vmA updates when host resource changes (#123456)",
            vmA.TodayDotBrush is ISolidColorBrush updatedA && updatedA.Color == Color.Parse("#123456"));
        Check("aggregate vm: vmB remains unchanged (#FF5500)",
            vmB.TodayDotBrush is ISolidColorBrush dotB2 && dotB2.Color == Color.Parse("#FF5500"));

        // Cleanup test resources
        Avalonia.Application.Current.Resources.Remove("ScopedTestBrush");
    }

    private class TestAppSettingsProvider : AppSettingsProvider
    {
        public AppSettings NormalizeTest(AppSettings settings) => Normalize(settings);
    }
}
