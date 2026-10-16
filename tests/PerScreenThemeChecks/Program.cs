using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace PerScreenThemeChecks;

/// <summary>
/// Verifies the per-screen theme resolution of 失焦自动切换 (slice 2 / ADR-P4b): every window
/// follows the occupancy state of the screen <i>it sits on</i> instead of the single uniform
/// state, with the uniform behaviour as the fallback.
///
/// Checked deterministically, without a real desktop walk:
/// <list type="bullet">
/// <item><see cref="FocusThemeCoordinator.ResolveFocused(FocusStateSnapshot, ScreenRect?)"/> —
/// the per-screen decision matrix (own screen occupied vs. clear vs. no screen mapping),</item>
/// <item><see cref="FocusThemeCoordinator.ResolveWindowTheme(AppSettings, FocusStateSnapshot, ScreenRect?)"/> —
/// the pure theme matrix (two windows, two screens, two states → two themes; same screen →
/// same theme; feature off → shared theme),</item>
/// <item>the instance path — a fake window→screen mapping (<c>WindowScreenResolver</c>) plus an
/// injected fake snapshot drive <c>ResolveWindowTheme(settings, window)</c>, and
/// <c>Track</c>/pokes push each window's own variant and window-scope accent resources,</item>
/// <item>the poke-on-reshuffle rule — a snapshot that moves the unfocused screen between two
/// screens (uniform bool unchanged) still refreshes the tracked windows, and an unchanged
/// snapshot does not,</item>
/// <item>the fallbacks — feature off drops the per-window overrides and resets the variant to
/// Default; the old uniform entry <c>ResolveWindowTheme(settings)</c> keeps its behaviour.</item>
/// </list>
/// </summary>
class Program
{
    private static int failures;

    private static readonly ScreenRect Primary = new(0, 0, 2048, 1152);
    private static readonly ScreenRect Secondary = new(2048, 0, 4096, 1152);

    /// <summary>Shared appearance parameters (mono BlackWhite), with the unfocused state
    /// carrying no surface override — so the unfocused theme follows this shared surface.</summary>
    private static readonly Theme Shared = new(
        DarkMode: false, AccentColor: null, OpacityLevel: 0.4, Monochrome: true,
        UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Solid,
        MonochromeVariant: MonochromeStyle.BlackWhite);

    /// <summary>The focused state's material snapshot: liquid glass with its own accent.</summary>
    private static readonly Theme LiquidPreset = new(
        DarkMode: false, AccentColor: "#FF5500", OpacityLevel: 0.18, Monochrome: false,
        UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.LiquidGlass);

    /// <summary>Focused = light, unfocused = dark; only the focused state overrides the material.</summary>
    private static FocusLinkSettings Link() => new(true)
    {
        Focused = new FocusMode(false, false),
        Unfocused = new FocusMode(true, false),
        FocusedSurface = SurfaceStyle.LiquidGlass,
        UnfocusedSurface = null
    };

    /// <summary>An occupancy snapshot: the given screens carry a non-whitelisted application.</summary>
    private static FocusStateSnapshot Snapshot(params ScreenRect[] unfocusedScreens) =>
        new(ForegroundKind.Application, "chrome",
            AnyApplicationFocused: unfocusedScreens.Length > 0,
            new HashSet<ScreenRect>(unfocusedScreens));

    private static AppSettings TestSettings(Theme theme, FocusLinkSettings? link = null) =>
        new AppSettings(theme, Array.Empty<Theme>(), new Layout(GridMode.Manual, false, false, true, false),
            new Dimensions(100, 10, 16), new Region("en"), false, null) with
        {
            FocusLink = link,
            SurfaceThemes = new Dictionary<string, Theme>(StringComparer.OrdinalIgnoreCase)
            {
                [nameof(SurfaceStyle.LiquidGlass)] = LiquidPreset
            }
        };

    static int Main()
    {
        if (Avalonia.Application.Current is null)
        {
            AppBuilder.Configure<Avalonia.Application>()
                .UsePlatformDetect()
                .SetupWithoutStarting();
        }

        Console.WriteLine("=== per-screen theme resolution checks ===");

        // --- per-screen focus decision (pure) ---
        Console.WriteLine();
        Console.WriteLine("--- per-screen focus decision ---");

        var oneUnfocused = Snapshot(Secondary);

        Check("per-screen: the window's own occupied screen is unfocused",
            !FocusThemeCoordinator.ResolveFocused(oneUnfocused, Secondary));
        Check("per-screen: a cleared screen next to an occupied one is focused",
            FocusThemeCoordinator.ResolveFocused(oneUnfocused, Primary));
        Check("per-screen: no screen mapping falls back to the uniform rule",
            FocusThemeCoordinator.ResolveFocused(oneUnfocused, null)
                == FocusThemeCoordinator.ResolveFocused(oneUnfocused)
                && !FocusThemeCoordinator.ResolveFocused(oneUnfocused, null));
        Check("per-screen: with the desktop clear every screen is focused",
            FocusThemeCoordinator.ResolveFocused(FocusStateSnapshot.Focused, Secondary)
                && FocusThemeCoordinator.ResolveFocused(Snapshot(), Primary));

        // --- pure theme resolution matrix ---
        Console.WriteLine();
        Console.WriteLine("--- theme resolution matrix (pure) ---");

        var settings = TestSettings(Shared, Link());

        var onUnfocusedScreen = FocusThemeCoordinator.ResolveWindowTheme(settings, oneUnfocused, Secondary);
        var onFocusedScreen = FocusThemeCoordinator.ResolveWindowTheme(settings, oneUnfocused, Primary);
        Check("matrix: two screens in two states resolve two different light/dark pairs",
            onUnfocusedScreen.DarkMode == true && onFocusedScreen.DarkMode == false);
        Check("matrix: the occupied screen shows the unfocused state's surface override",
            onFocusedScreen.EffectiveSurface == SurfaceStyle.LiquidGlass);
        Check("matrix: the clear screen follows the shared surface (no override)",
            onUnfocusedScreen.EffectiveSurface == SurfaceStyle.Solid);
        Check("matrix: the focused state's material snapshot brings its own accent",
            onFocusedScreen.AccentColor == "#FF5500");
        Check("matrix: the unfocused state keeps the shared monochrome",
            onUnfocusedScreen.Monochrome && onUnfocusedScreen.EffectiveMonochromeVariant == MonochromeStyle.BlackWhite);
        Check("matrix: two windows on the same screen resolve the same theme",
            onUnfocusedScreen == FocusThemeCoordinator.ResolveWindowTheme(settings, oneUnfocused, Secondary));
        Check("matrix: a screen-less window resolves the uniform state theme",
            FocusThemeCoordinator.ResolveWindowTheme(settings, oneUnfocused, null)
                == FocusThemeCoordinator.ResolveStateTheme(Link(), FocusThemeCoordinator.ResolveFocused(oneUnfocused), settings));
        Check("matrix: feature off resolves the shared theme on every screen",
            FocusThemeCoordinator.ResolveWindowTheme(TestSettings(Shared), oneUnfocused, Secondary)
                == TestSettings(Shared).Theme
            && FocusThemeCoordinator.ResolveWindowTheme(TestSettings(Shared), FocusStateSnapshot.Focused, Primary)
                == TestSettings(Shared).Theme);

        // --- instance path: fake mapping + injected snapshot ---
        Console.WriteLine();
        Console.WriteLine("--- instance path (fake mapping, injected snapshot) ---");

        var mapping = new Dictionary<Window, ScreenRect?>();
        var provider = new NotifyingSettings(settings);
        var (_, coordinator) = CreateFocusStack(provider);
        coordinator.WindowScreenResolver = window => mapping.TryGetValue(window, out var rect) ? rect : null;
        coordinator.InjectSnapshotForChecks(oneUnfocused);

        var windowA = new Window(); // sits on the focused screen
        var windowB = new Window(); // sits on the unfocused screen
        var windowC = new Window(); // no screen mapping at all
        mapping[windowA] = Primary;
        mapping[windowB] = Secondary;

        Check("instance: the window on the focused screen resolves the focused pair",
            coordinator.ResolveWindowTheme(provider.Get(), windowA).DarkMode == false);
        Check("instance: the window on the unfocused screen resolves the unfocused pair",
            coordinator.ResolveWindowTheme(provider.Get(), windowB).DarkMode == true);
        Check("instance: a window without a mapping falls back to the uniform theme",
            coordinator.ResolveWindowTheme(provider.Get(), windowC)
                == coordinator.ResolveWindowTheme(provider.Get()));
        Check("instance: the two windows really do resolve different themes",
            coordinator.ResolveWindowTheme(provider.Get(), windowA)
                != coordinator.ResolveWindowTheme(provider.Get(), windowB));

        // --- window state application (Track → ApplyWindowTheme → poke) ---
        Console.WriteLine();
        Console.WriteLine("--- window state application ---");

        coordinator.Track(windowA);
        coordinator.Track(windowB);
        Check("window state: the focused screen's window carries the light variant",
            windowA.RequestedThemeVariant == ThemeVariant.Light);
        Check("window state: the focused screen's window carries its state accent",
            windowA.Resources["SystemAccentColor"] is Color accentA && accentA == Color.Parse("#FF5500"));
        Check("window state: the unfocused screen's window carries the dark variant",
            windowB.RequestedThemeVariant == ThemeVariant.Dark);
        Check("window state: the unfocused screen's window resolves the shared mono accent (white in dark)",
            windowB.Resources["SystemAccentColor"] is Color accentB && accentB == Colors.White);
        Check("window state: a normal (non-colorful) window has no colorful include or marker",
            !windowA.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources") == true)
            && !windowA.Resources.ContainsKey("ColorfulActive"));

        // The unfocused state flips to 多彩: only that window switches to the colorful scope.
        provider.Save(provider.Get() with
        {
            FocusLink = Link() with { UnfocusedSurface = SurfaceStyle.Colorful }
        });
        Check("window state: the unfocused window switches to the colorful scope on save",
            windowB.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources") == true)
            && windowB.Resources["ColorfulActive"] is bool active && active);
        Check("window state: the colorful scope clears the window accent ramp",
            !windowB.Resources.ContainsKey("SystemAccentColor"));
        Check("window state: the focused window is untouched by the other screen's colorful state",
            !windowA.Resources.ContainsKey("ColorfulActive")
            && windowA.Resources["SystemAccentColor"] is Color stillA && stillA == Color.Parse("#FF5500"));

        // Feature off = the uniform fallback: the windows drop their per-screen state.
        provider.Save(provider.Get() with { FocusLink = Link() with { Enabled = false } });
        Check("fallback: the windows return to the inherited (Default) variant",
            windowA.RequestedThemeVariant == ThemeVariant.Default
            && windowB.RequestedThemeVariant == ThemeVariant.Default);
        Check("fallback: the window accent ramp and colorful marker are gone",
            !windowA.Resources.ContainsKey("SystemAccentColor")
            && !windowB.Resources.ContainsKey("ColorfulActive")
            && !windowB.Resources.MergedDictionaries.Any(d => d is ResourceInclude inc
                && inc.Source?.OriginalString?.Contains("ColorfulResources") == true));

        // --- poke-on-reshuffle (uniform bool unchanged, per-screen state moved) ---
        Console.WriteLine();
        Console.WriteLine("--- poke on per-screen reshuffle ---");

        var reshuffleProvider = new NotifyingSettings(TestSettings(Shared, Link()));
        var (_, reshuffleCoordinator) = CreateFocusStack(reshuffleProvider);
        reshuffleCoordinator.InjectSnapshotForChecks(Snapshot(Secondary));

        var pokeWindow = new PokeCountingWindow();
        reshuffleCoordinator.Track(pokeWindow);
        var afterTrack = pokeWindow.Pokes;
        Check("reshuffle: tracking a window refreshes it once", afterTrack == 1);

        reshuffleCoordinator.Reapply();
        var afterFirstReapply = pokeWindow.Pokes;
        Check("reshuffle: a re-apply against a moved snapshot refreshes the window", afterFirstReapply == afterTrack + 1);

        // {"Secondary"} → {"Primary"}: the uniform focused-bool stays "unfocused" (the global
        // application short-circuits) but the windows moved between the two state themes.
        reshuffleCoordinator.InjectSnapshotForChecks(Snapshot(Primary));
        reshuffleCoordinator.Reapply();
        Check("reshuffle: moving the unfocused screen between screens refreshes the windows "
              + "even though the uniform state did not flip", pokeWindow.Pokes == afterFirstReapply + 1);

        // Same snapshot again: nothing moved — a no-op save must stay a no-op.
        reshuffleCoordinator.Reapply();
        Check("reshuffle: an unchanged snapshot does not refresh again", pokeWindow.Pokes == afterFirstReapply + 1);

        // --- uniform compatibility (old entries keep their behaviour) ---
        Console.WriteLine();
        Console.WriteLine("--- uniform compatibility ---");

        var compatCoordinator = CreateFocusStack(new NotifyingSettings(settings)).Item2;
        compatCoordinator.InjectSnapshotForChecks(oneUnfocused);
        Check("compat: the parameterless ResolveWindowTheme still resolves the uniform state theme",
            compatCoordinator.ResolveWindowTheme(settings)
                == FocusThemeCoordinator.ResolveStateTheme(Link(), FocusThemeCoordinator.ResolveFocused(oneUnfocused), settings));
        Check("compat: the static ResolveFocused still applies the uniform rule",
            FocusThemeCoordinator.ResolveFocused(oneUnfocused) == false
            && FocusThemeCoordinator.ResolveFocused(FocusStateSnapshot.Focused));
        Check("compat: PanelAppearanceResolver's old signature still resolves (null provider → fallback)",
            PanelAppearanceResolver.ResolveEffectiveTheme(null) == PanelAppearanceResolver.FallbackTheme);
        Check("compat: the new per-screen overload with a null window matches the old signature",
            PanelAppearanceResolver.ResolveEffectiveTheme(null, null) == PanelAppearanceResolver.FallbackTheme);

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool condition)
    {
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}  {name}");
        if (!condition) failures++;
    }

    /// <summary>A coordinator over a real (but never attached) watcher stack — the same shape
    /// FocusSwitchChecks uses; the snapshot is injected, the platform never observed.</summary>
    private static (ThemeService, FocusThemeCoordinator) CreateFocusStack(IAppSettingsProvider provider)
    {
        var wallpaperTheme = new WallpaperThemeService();
        var themeService = new ThemeService(provider, wallpaperTheme);
        var coordinator = new FocusThemeCoordinator(
            provider, new FocusWatchService(provider), wallpaperTheme, new FullscreenWatcherService(), themeService);
        return (themeService, coordinator);
    }

    /// <summary>A settings provider that really raises <c>DataChanged</c> — the master-switch
    /// toggle and the state-surface save reach the coordinator through it.</summary>
    private sealed class NotifyingSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged;

        public AppSettings Get() => value;

        public void Save(AppSettings data)
        {
            var old = value;
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
}
