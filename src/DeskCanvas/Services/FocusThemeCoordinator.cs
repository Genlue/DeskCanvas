using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Drives 失焦自动切换 as a <b>uniform</b> switch: the whole desktop is in exactly one
/// state at a time — "unfocused" while any non-whitelisted application window occupies
/// any screen, "focused" the moment the last one is gone — and a state flip applies the
/// state's theme through <see cref="ThemeService.ApplyFocusState"/>, the very same
/// application-level switch the settings page's theme buttons run. There is no
/// per-window/per-screen override layer any more: every window (widgets, panels, the
/// settings window itself) simply follows the one live global theme, which is what makes
/// the result identical to switching the theme by hand.
/// <para>
/// The coordinator's remaining jobs: listen to the focus watcher and re-run the global
/// switch on state changes (and on settings saves, wallpaper brightness flips, exits from
/// fullscreen), and poke the tracked widget windows so they re-resolve their material
/// paths when the theme flipped without a variant change. While
/// <see cref="FocusLinkSettings.Enabled"/> is false the app-level theme is simply the
/// shared one — the pre-feature behaviour.
/// </para>
/// </summary>
public sealed class FocusThemeCoordinator : IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly FocusWatchService focusWatch;
    private readonly WallpaperThemeService wallpaperTheme;
    private readonly FullscreenWatcherService fullscreenWatcher;
    private readonly ThemeService themeService;
    private readonly List<WeakReference<Window>> windows = [];

    private FocusStateSnapshot state = FocusStateSnapshot.Focused;
    private bool fullscreenSuspended;

    public FocusThemeCoordinator(
        IAppSettingsProvider appSettingsProvider,
        FocusWatchService focusWatch,
        WallpaperThemeService wallpaperTheme,
        FullscreenWatcherService fullscreenWatcher,
        ThemeService themeService)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.focusWatch = focusWatch;
        this.wallpaperTheme = wallpaperTheme;
        this.fullscreenWatcher = fullscreenWatcher;
        this.themeService = themeService;

        state = focusWatch.Current;
        fullscreenSuspended = fullscreenWatcher.IsFullscreen;

        focusWatch.StateChanged += (_, snapshot) =>
        {
            state = snapshot;
            Reapply();
        };
        // Any settings save can change the switch, the two mode pairs, the allowlist or the
        // shared theme — one reapply covers all of them (an unchanged state short-circuits
        // inside the theme service at zero cost).
        appSettingsProvider.DataChanged += (_, _, _) => Reapply();
        // "自动（跟壁纸）" in either state: the wallpaper's brightness is the mode source.
        wallpaperTheme.DarkFlagChanged += _ => Reapply();
        // While a fullscreen application suspends the widgets, applying would only re-render
        // hidden glass; hold the state and settle once when the desktop is back.
        fullscreenWatcher.FullscreenChanged += (_, covered) =>
        {
            fullscreenSuspended = covered;
            if (!covered) Reapply();
        };
        // The lens every global application resolves through: each save, variant flip and
        // wallpaper flip lands on the LIVE state's theme in a single pass — the settings
        // page's own saves included, so a colour drag costs one global application, exactly
        // like the pre-feature path.
        themeService.FocusLensResolver = settings =>
            settings.FocusLink is { Enabled: true } ? ResolveFocused(state) : null;

        // One settle pass once the UI is up: a session that starts with an application
        // already open must land on the unfocused state's theme, not the shared one.
        Dispatcher.UIThread.Post(Reapply, DispatcherPriority.Background);
    }

    /// <summary>Start tracking a widget / panel window (weakly) and poke it so it resolves
    /// against the live global theme right away.</summary>
    public void Track(Window window)
    {
        foreach (var reference in windows)
            if (reference.TryGetTarget(out var tracked) && ReferenceEquals(tracked, window)) return;

        windows.Add(new WeakReference<Window>(window));
        PokeWindow(window);
    }

    /// <summary>Re-evaluate the uniform state and apply it globally (state change, settings
    /// change, wallpaper flip, fullscreen exit). A state whose theme already applied
    /// short-circuits inside the theme service — the windows are only poked when the theme
    /// actually flipped.</summary>
    public void Reapply()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reapply);
            return;
        }

        windows.RemoveAll(reference => !reference.TryGetTarget(out _));

        var settings = appSettingsProvider.Get();
        if (settings.FocusLink is not { Enabled: true }) return;
        if (fullscreenSuspended) return;

        // A variant change reaches the windows through ActualThemeVariantChanged on its own;
        // the poke covers the material-swap cases where the variant stays put — and runs only
        // when the global application really happened, never on a no-op save.
        if (!themeService.ApplyFocusState(settings, ResolveFocused(state))) return;

        foreach (var reference in windows)
            if (reference.TryGetTarget(out var window)) PokeWindow(window);
    }

    private static void PokeWindow(Window window)
    {
        if (window is IFocusThemedWindow themed) themed.OnFocusThemeChanged();
    }

    /// <summary>The theme a visual's window should render with: the shared theme when the
    /// feature is off, otherwise the live uniform state's theme — the entry point for
    /// widget-assembly content (which resolves its own colours and has no direct reference
    /// to the coordinator). Returns <c>null</c> when the app services are unavailable
    /// (design time, headless checks).</summary>
    public static Theme? ResolveVisualTheme(Visual visual)
    {
        var provider = App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
        if (provider is null) return null;

        var settings = provider.Get();
        if (settings.FocusLink is not { Enabled: true }) return settings.Theme;

        var coordinator = App.Services?.GetService(typeof(FocusThemeCoordinator)) as FocusThemeCoordinator;
        if (coordinator is null) return settings.Theme;

        return coordinator.ResolveWindowTheme(settings);
    }

    /// <summary>The window's effective theme: the shared theme with the live state's
    /// light/dark pair and (optional) surface material substituted. With the uniform switch
    /// every window resolves the same theme — the one the settings window follows, too.</summary>
    public Theme ResolveWindowTheme(AppSettings settings) =>
        settings.FocusLink is { Enabled: true } link
            ? ResolveStateTheme(link, ResolveFocused(state), settings)
            : settings.Theme;

    // ---------- Decision (pure, testable) ----------

    /// <summary>
    /// Is the desktop focused? Uniform rule: the desktop is focused exactly when no
    /// application window occupies any screen — minimized, closed or hidden windows free
    /// their screens; clicking the desktop, the taskbar or a widget changes nothing; the
    /// allowlisted processes and shell surfaces never occupy anything.
    /// </summary>
    public static bool ResolveFocused(FocusStateSnapshot state) => !state.AnyApplicationFocused;

    /// <summary>Resolve a mode pair into the variant a window should carry.</summary>
    public static ThemeVariant ResolveVariant(FocusMode mode, bool wallpaperDark) =>
        mode.AutoTheme
            ? (wallpaperDark ? ThemeVariant.Dark : ThemeVariant.Light)
            : mode.DarkMode switch
            {
                true => ThemeVariant.Dark,
                false => ThemeVariant.Light,
                _ => ThemeVariant.Default,
            };

    /// <summary>
    /// Pure resolution of one state's theme: shared parameters + the state's mode pair + the
    /// state's surface override. An override brings that material's own coating level (its
    /// preset snapshot) — 纯色 must look opaque, 液态玻璃 airy — so "选纯色" really looks like
    /// 纯色 instead of reusing (and blurring away) the shared opacity; everything else stays
    /// shared. A null override — or one equal to the shared surface — keeps the shared theme.
    /// </summary>
    public static Theme ResolveStateTheme(FocusLinkSettings link, bool focused, AppSettings settings)
    {
        var shared = settings.Theme;
        var mode = focused ? link.EffectiveFocused : link.EffectiveUnfocused;
        var surface = FocusLinkSettings.NormalizeSurface(focused ? link.FocusedSurface : link.UnfocusedSurface);

        var baseTheme = surface is { } material && material != shared.EffectiveSurface
            ? settings.GetThemeForSurface(material)
            : shared;

        return (baseTheme with
        {
            Surface = surface ?? shared.EffectiveSurface,
            FontFamily = shared.FontFamily,
            UseNativeFrame = shared.UseNativeFrame,
            DarkMode = mode.DarkMode,
            AutoTheme = mode.AutoTheme,
            TextShadowEnabled = shared.TextShadowEnabled,
            TextShadowStrength = shared.TextShadowStrength
        }).NormalizeForSurface();
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}
