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
/// Drives 失焦自动切换. The desktop's occupancy is observed <b>per screen</b>
/// (<see cref="FocusStateSnapshot.UnfocusedScreens"/>); the theme each window renders with is
/// resolved from the state of the screen <i>that window sits on</i> — a screen still carrying a
/// non-whitelisted application window shows the unfocused theme while a cleared screen next to
/// it shows the focused one. The application-level switch (settings window, dialogs) keeps
/// following the uniform live state, which is what makes a state flip identical to switching
/// the theme by hand. A window that cannot be mapped to a screen — and the whole feature when
/// <see cref="FocusLinkSettings.Enabled"/> is false — falls back to the uniform behaviour, so
/// headless contexts and screens without geometry keep working exactly as before.
/// <para>
/// The coordinator's remaining jobs: listen to the focus watcher and re-run the switch on
/// state changes (and on settings saves, wallpaper brightness flips, exits from fullscreen),
/// and poke the tracked widget windows — applying each window's own per-screen variant and
/// window-scope colour resources first (<see cref="ApplyWindowTheme"/>) — so they re-resolve
/// their material paths when their theme moved without a variant change, including the one
/// refresh that follows a flip of the master switch itself (see <see cref="Reapply"/>). While
/// <see cref="FocusLinkSettings.Enabled"/> is false the app-level theme is simply the shared
/// one — the pre-feature behaviour.
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

    /// <summary>
    /// Does the 最大化或全屏时 choice leave nothing on screen to re-render while the desktop
    /// is covered? Only the <see cref="FullscreenBehavior.Hide"/> behaviour hides the widgets;
    /// <see cref="FullscreenBehavior.Pause"/> keeps them visible frozen and
    /// <see cref="FullscreenBehavior.None"/> keeps them running, so in those modes theme
    /// changes must still apply.
    /// </summary>
    private bool ShouldHoldForFullscreen() =>
        (appSettingsProvider.Get().FullscreenBehavior ?? FullscreenBehavior.Hide) == FullscreenBehavior.Hide;

    /// <summary>
    /// The snapshot the tracked windows were last poked against. A state flip that reshuffles
    /// <i>which</i> screens are unfocused ({"A"} → {"B"}) leaves the uniform focused-bool — and
    /// therefore the global application — untouched, yet windows moved between the two state
    /// themes still need a poke; comparing against this catches exactly those reshuffles.
    /// </summary>
    private FocusStateSnapshot? lastPokedSnapshot;

    /// <summary>
    /// The master switch as the last pass saw it. Flipping it is the one settings change that
    /// cannot be handled by the global application alone — see <see cref="Reapply"/>.
    /// </summary>
    private bool featureEnabled;

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
        fullscreenSuspended = fullscreenWatcher.IsFullscreen && ShouldHoldForFullscreen();
        // Seed the switch from the saved configuration: the settle pass below must not read as a
        // toggle (nothing to refresh — the session just started).
        featureEnabled = appSettingsProvider.Get().FocusLink is { Enabled: true };

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
        // While the desktop is covered, applying would only re-render glass — but that is true
        // only under the 隐藏 behaviour of 最大化或全屏时: 暂停 keeps the widgets visible
        // (frozen) and 无处理 keeps them running, so the state still has to settle. Hold and
        // reapply once when the desktop is back only when nothing is on screen to refresh.
        fullscreenWatcher.FullscreenChanged += (_, covered) =>
        {
            fullscreenSuspended = covered && ShouldHoldForFullscreen();
            if (!fullscreenSuspended) Reapply();
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
    /// actually flipped, when the master switch itself moved (see below), or when the
    /// per-screen occupancy reshuffled which screens are unfocused while the uniform
    /// state stayed put.</summary>
    /// <para>
    /// <b>开关本功能后接一次刷新。</b> Flipping the master switch is the one settings change the
    /// global application alone cannot carry: the switch is not part of the tuple the application
    /// is keyed on, so the pass that follows a save reports "nothing changed" and never pokes the
    /// windows — yet every window resolves its card brush, glass layer, outline and native
    /// transparency hint from its <i>own</i> effective theme, which just changed (into a state
    /// theme on enable, back to the shared one on disable). Left alone, the widgets keep painting
    /// the previous material over a global resource set that already moved on — the garbled
    /// themes. So a toggle forces one application and pokes every tracked window: one refresh,
    /// exactly like switching the theme by hand.
    /// </para>
    public void Reapply()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reapply);
            return;
        }

        windows.RemoveAll(reference => !reference.TryGetTarget(out _));

        var settings = appSettingsProvider.Get();
        var enabled = settings.FocusLink is { Enabled: true };

        // A fullscreen application owns the desktop (and, under the 隐藏 behaviour of
        // 最大化或全屏时, the widgets nobody can see): hold everything — the toggle refresh
        // included — and settle once when the desktop is back. 暂停/无处理 keep the widgets
        // on screen, so there is nothing to hold and theme changes keep applying.
        if ((fullscreenSuspended || fullscreenWatcher.IsFullscreen) && ShouldHoldForFullscreen()) return;

        var toggled = enabled != featureEnabled;
        featureEnabled = enabled;

        if (!enabled)
        {
            // Switched off: the shared theme drives the app again, and every window has to drop
            // the state theme it was painting (its material may differ from the shared one).
            if (!toggled) return;
            themeService.ApplyFocusState(settings, focused: true, force: true);
            lastPokedSnapshot = state;
            PokeAll();
            return;
        }

        // Per-screen: a snapshot that reshuffles which screens are unfocused ({"A"} → {"B"})
        // leaves the uniform focused-bool alone — the global application short-circuits — but
        // the windows on those screens really did move between the two state themes. Poke
        // whenever the snapshot actually changed, not only when the uniform theme flipped.
        var snapshotMoved = lastPokedSnapshot is not { } last || !state.SameAs(last);

        // A variant change reaches the windows through ActualThemeVariantChanged on its own;
        // the poke covers the material-swap cases where the variant stays put — and runs only
        // when the global application really happened (or the switch just moved, or the
        // per-screen occupancy reshuffled), never on a no-op save.
        if (!themeService.ApplyFocusState(settings, ResolveFocused(state), force: toggled)
            && !toggled && !snapshotMoved) return;

        lastPokedSnapshot = state;
        PokeAll();
    }

    /// <summary>Tell every tracked window to re-resolve its material (the refresh half of a
    /// switch — the global application alone cannot reach per-window values). Each window is
    /// first given its own per-screen theme state (variant + window-scope colours) and only
    /// then poked, so the material paths re-resolve against the fresh window-level state.</summary>
    private void PokeAll()
    {
        foreach (var reference in windows)
            if (reference.TryGetTarget(out var window)) PokeWindow(window);
    }

    private void PokeWindow(Window window)
    {
        ApplyWindowTheme(window);
        if (window is IFocusThemedWindow themed) themed.OnFocusThemeChanged();
    }

    /// <summary>
    /// Push the window's own per-screen theme state onto the window: its
    /// <see cref="Window.RequestedThemeVariant"/> and its window-scope colour resources
    /// (accent ramp, or the colorful dictionary when this screen's state theme is 多彩).
    /// The global material style dictionaries stay app-wide — only the variant and the
    /// colour keys can differ per screen without a StyleIncludes rework (ADR-P4b).
    /// </summary>
    /// <remarks>
    /// Only keys this coordinator itself writes are ever cleared on the fallback path:
    /// widgets keep their own window-scope semantic colours (CalendarTodayBrush …), and the
    /// app-level monochrome style unification stays uniform by design — window scope can add
    /// styles but never remove the application's, so per-screen monochrome <i>styles</i> are a
    /// recorded boundary.
    /// </remarks>
    private void ApplyWindowTheme(Window window)
    {
        var settings = appSettingsProvider.Get();
        if (settings.FocusLink is not { Enabled: true })
        {
            // Feature off = the uniform fallback: drop everything this class put on the window
            // so it follows the application resources again, exactly like the pre-feature code.
            ThemeResourceHelper.ClearAccentOverrides(window.Resources);
            ThemeResourceHelper.RemoveColorfulResourcesInclude(window);
            window.Resources.Remove("ColorfulActive");
            window.RequestedThemeVariant = ThemeVariant.Default;
            return;
        }

        var theme = ResolveWindowTheme(settings, window);
        var variant = ResolveVariant(new FocusMode(theme.DarkMode, theme.AutoTheme), wallpaperTheme.IsWallpaperDark());
        window.RequestedThemeVariant = variant;

        if (theme.IsColorful)
        {
            // 多彩: merge the closed colorful dictionary into the window — the Fluent keys
            // resolve colorfully inside THIS window without moving the app-wide policy, and
            // the accent ramp is strictly cleared (ColorfulFixed forbids user accents).
            ThemeResourceHelper.AttachColorfulResourcesInclude(window);
            window.Resources["ColorfulActive"] = true;
            ThemeResourceHelper.ClearAccentOverrides(window.Resources);
        }
        else
        {
            ThemeResourceHelper.RemoveColorfulResourcesInclude(window);
            window.Resources.Remove("ColorfulActive");

            // ThemeVariant members are static properties, not constants — equality, not switch
            // patterns. Follow-the-system (Default) reads the window's live variant: the same
            // reading the global path takes (darkMode ?? actual variant == Dark).
            var isDarkVariant = variant == ThemeVariant.Dark
                ? true
                : variant == ThemeVariant.Light
                    ? false
                    : window.ActualThemeVariant == ThemeVariant.Dark;
            ThemeResourceHelper.ApplyAccentRamp(window.Resources, theme, isDarkVariant);
        }
    }

    /// <summary>The theme a visual's window should render with: the shared theme when the
    /// feature is off, otherwise the state theme of the screen that visual's window sits on —
    /// the entry point for widget-assembly content (which resolves its own colours and has no
    /// direct reference to the coordinator). A visual whose window cannot be determined falls
    /// back to the uniform entry point. Returns <c>null</c> when the app services are
    /// unavailable (design time, headless checks).</summary>
    public static Theme? ResolveVisualTheme(Visual visual)
    {
        var provider = App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
        if (provider is null) return null;

        var settings = provider.Get();
        if (settings.FocusLink is not { Enabled: true }) return settings.Theme;

        var coordinator = App.Services?.GetService(typeof(FocusThemeCoordinator)) as FocusThemeCoordinator;
        if (coordinator is null) return settings.Theme;

        var window = visual as Window ?? visual.GetVisualRoot() as Window;
        return window is { } w
            ? coordinator.ResolveWindowTheme(settings, w)
            : coordinator.ResolveWindowTheme(settings);
    }

    /// <summary>The window's effective theme under the per-screen policy: the state theme of
    /// the screen the window itself sits on. Windows that cannot be mapped to a screen (no
    /// platform screens yet, headless without an injected mapping) fall back to the uniform
    /// entry point below.</summary>
    public Theme ResolveWindowTheme(AppSettings settings, Window window) =>
        ResolveWindowTheme(settings, state, ResolveWindowScreen(window));

    /// <summary>The window's effective theme with the live uniform state: the shared theme
    /// with the live state's light/dark pair and (optional) surface material substituted.
    /// Kept as the uniform compatibility entry — every caller that has no window at hand
    /// (and the headless fallback) resolves through here.</summary>
    public Theme ResolveWindowTheme(AppSettings settings) =>
        settings.FocusLink is { Enabled: true } link
            ? ResolveStateTheme(link, ResolveFocused(state), settings)
            : settings.Theme;

    /// <summary>
    /// Resolves the physical-pixel rect of the screen a window sits on; <c>null</c> = unknown
    /// (the uniform fallback then applies). The default implementation is the platform's own
    /// window→screen query — the same bounds source the focus watcher builds
    /// <see cref="FocusStateSnapshot.UnfocusedScreens"/> from — and the headless checks inject
    /// a fake mapping here.
    /// </summary>
    public Func<Window, ScreenRect?>? WindowScreenResolver { get; set; }

    private ScreenRect? ResolveWindowScreen(Window window)
    {
        if (WindowScreenResolver is { } resolver) return resolver(window);

        try
        {
            return window.Screens.ScreenFromWindow(window) is { } screen
                ? new ScreenRect(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Right, screen.Bounds.Bottom)
                : null;
        }
        catch
        {
            // No platform screens (design time, headless setup): the uniform fallback.
            return null;
        }
    }

    /// <summary>
    /// Test seam for the headless checks: replaces the live occupancy snapshot so per-screen
    /// resolution can be exercised without a real desktop walk. Production code only ever
    /// sets the snapshot through <see cref="FocusWatchService.StateChanged"/>.
    /// </summary>
    public void InjectSnapshotForChecks(FocusStateSnapshot snapshot) => state = snapshot;

    // ---------- Decision (pure, testable) ----------

    /// <summary>
    /// Is the desktop focused? Uniform rule: the desktop is focused exactly when no
    /// application window occupies any screen — minimized, closed or hidden windows free
    /// their screens; clicking the desktop, the taskbar or a widget changes nothing; the
    /// allowlisted processes and shell surfaces never occupy anything.
    /// </summary>
    public static bool ResolveFocused(FocusStateSnapshot state) => !state.AnyApplicationFocused;

    /// <summary>
    /// Is this screen focused? Per-screen rule: a screen is focused exactly when no
    /// application window occupies <b>it</b> — the per-screen reading of
    /// <see cref="FocusStateSnapshot.UnfocusedScreens"/> that the uniform switch used to
    /// collapse into a single bool. A window that cannot be mapped to a screen (null rect)
    /// falls back to the uniform rule, so headless / geometry-less contexts behave exactly
    /// like the pre-per-screen code.
    /// </summary>
    public static bool ResolveFocused(FocusStateSnapshot state, ScreenRect? screen) =>
        screen is { } rect ? !state.UnfocusedScreens.Contains(rect) : ResolveFocused(state);

    /// <summary>
    /// Pure per-screen theme resolution: the occupancy state of the given screen picks
    /// between the two state themes. Feature off, or a screen-less window, resolves exactly
    /// like the uniform entry point — one function so the matrix in
    /// <c>tests/PerScreenThemeChecks</c> needs no windows and no desktop.
    /// </summary>
    public static Theme ResolveWindowTheme(AppSettings settings, FocusStateSnapshot snapshot, ScreenRect? screen)
    {
        if (settings.FocusLink is not { Enabled: true } link) return settings.Theme;
        return ResolveStateTheme(link, ResolveFocused(snapshot, screen), settings);
    }

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
