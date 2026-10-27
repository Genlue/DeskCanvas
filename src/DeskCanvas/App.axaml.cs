using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace DeskCanvas;

public class App : Application
{
    public static IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        HorizontalScrollHelper.RegisterGlobal();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            desktopLifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        var services = new ServiceCollection()
            .AddSingleton<IAppSettingsProvider>(_ => new AppSettingsProvider(
                () => WidgetBundle.ReadDefaultAppSettings()
                      ?? throw new InvalidOperationException("The shipped app settings template is missing.")))
            .AddSingleton<ILayoutProvider, LayoutProvider>()
            .AddSingleton<IAssemblyProvider, AssemblyProvider>()
            .AddSingleton<WallpaperThemeService>()
            .AddSingleton<WallpaperWatcherService>()
            .AddSingleton<ThemeService>()
            .AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>())
            .AddSingleton<ILocaleService, LocaleService>()
            .AddSingleton<IGridService<Widget>, GridService>()
            .AddSingleton<DisplayMonitorService>()
            .AddSingleton<FullscreenWatcherService>()
            .AddSingleton<FocusWatchService>()
            .AddSingleton<FocusThemeCoordinator>()
            .AddSingleton<WidgetFactory>()
            .AddSingleton<IWidgetFactory<Window, UserControl>>(sp => sp.GetRequiredService<WidgetFactory>())
            .AddSingleton<ProfileService>()
            .AddSingleton<Settings, Settings>()
            // 聚焦搜索: one catalogue, one icon cache, one hotkey host for the process.
            .AddSingleton<ShellIconService>()
            .AddSingleton<LauncherCatalog>()
            .AddSingleton<FileSearchService>()
            // The unified result pipeline: providers feed the router, the router feeds the VM.
            .AddSingleton<SpotlightUsageStore>()
            .AddSingleton<SpotlightAppCategoryStore>()
            .AddSingleton<SpotlightRecentLaunchStore>()
            .AddSingleton<SpotlightClipboardService>()
            .AddSingleton<SpotlightExternalResults>()
            .AddSingleton<SpotlightAppProvider>()
            .AddSingleton<SpotlightFileProvider>()
            .AddSingleton<SpotlightClipboardProvider>()
            .AddSingleton<SpotlightWebProvider>()
            // The inline web hits: a network source, queried after the local ones, never inside
            // their WhenAll — see SpotlightViewModel.LoadWebResultsAsync.
            .AddSingleton<SpotlightWebResults>()
            .AddSingleton<SpotlightQueryRouter>()
            .AddSingleton<SpotlightService>()
            // One settings window for the whole process: widgets (and a hand-over from a second
            // launch) resolve this same instance instead of each building their own.
            .AddSingleton<Func<Settings>>(sp => () => sp.GetRequiredService<Settings>())
            .AddSingleton<UpdateService, UpdateService>()
            .BuildServiceProvider();

        Services = services;

        var appSettingsProvider = services
            .GetRequiredService<IAppSettingsProvider>();

        var themeService = services
            .GetRequiredService<IThemeService>();

        var localeService = services
            .GetRequiredService<ILocaleService>();
        
        localeService.SetCulture(appSettingsProvider.Get().Region.Language);
        themeService.Apply(appSettingsProvider.Get());
        UpdateCornerRadiusResources(appSettingsProvider.Get());
        appSettingsProvider.DataChanged += (_, _, newSettings) => UpdateCornerRadiusResources(newSettings);

        // The settings window paints its own rounded glass surface on top of Avalonia's
        // WinUIComposition backdrop, so it has to be clipped to the radius that backdrop was
        // built with. Both sides read Program.EffectiveWindowCornerRadius (8 on Win11+, 0 on Win10),
        // so they can never drift apart and reopen the transparent corner notch or leave unclipped
        // square corner artifacts on Win10. This is a window radius — not the widget-card
        // radius in Dimensions.Radius, which is a different (and much larger) setting.
        Resources["SettingsWindowCornerRadius"] = new CornerRadius(Program.EffectiveWindowCornerRadius);


        Control.LoadedEvent.AddClassHandler<ContextMenu>((cm, _) =>
        {
            void ApplyMenuPopup()
            {
                // The shell's own outward roundness must be clipped by the host (the template
                // Border only rounds the fill, not the child menu items' hover highlights — see
                // the same clip in Widget.axaml.cs). Mirrors the widget menu's Clip + region pair.
                if (cm.Bounds.Width > 0 && cm.Bounds.Height > 0)
                {
                    cm.Clip = new RectangleGeometry(
                        new Rect(0, 0, cm.Bounds.Width, cm.Bounds.Height),
                        MenuPopupCornerRadius, MenuPopupCornerRadius);
                }

                if (TopLevel.GetTopLevel(cm) is WindowBase wb)
                    ApplyMenuPopupRounding(wb);
            }

            ApplyMenuPopup();
            Dispatcher.UIThread.Post(ApplyMenuPopup, DispatcherPriority.Render);
            if (MenuPopupBoundsHooked.TryAdd(cm, MenuPopupBoundsHookedMarker))
                cm.GetObservable(Visual.BoundsProperty).Subscribe(_ => ApplyMenuPopup());
        });

        // MenuFlyouts (editor row/overflow/variable menus, search, pomodoro, ...) open in their
        // own acrylic PopupRoots too and need the same rounded native region as context menus.
        Control.LoadedEvent.AddClassHandler<MenuFlyoutPresenter>((presenter, _) =>
        {
            void ApplyMenuPopup()
            {
                if (TopLevel.GetTopLevel(presenter) is WindowBase wb)
                    ApplyMenuPopupRounding(wb);
            }

            ApplyMenuPopup();
            Dispatcher.UIThread.Post(ApplyMenuPopup, DispatcherPriority.Render);
            if (MenuPopupBoundsHooked.TryAdd(presenter, MenuPopupBoundsHookedMarker))
                presenter.GetObservable(Visual.BoundsProperty).Subscribe(_ => ApplyMenuPopup());
        });

        // Strip OS-level AcrylicBlur, window shadow and DWM capsule border from ToolTip PopupRoots
        // so hover tooltips render only the shadowed text without any pill/capsule background.
        static void ConfigureToolTipPopupRoot(ToolTip tt)
        {
            if (TopLevel.GetTopLevel(tt) is WindowBase wb)
            {
                wb.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
                wb.Background = Avalonia.Media.Brushes.Transparent;
                if (wb is Avalonia.Controls.Primitives.PopupRoot pr)
                {
                    pr.WindowManagerAddShadowHint = false;
                }
                var handle = wb.TryGetPlatformHandle()?.Handle;
                if (handle.HasValue && handle.Value != IntPtr.Zero)
                {
                    InteropService.DisableWindowBorder(handle.Value);
                }
            }
        }

        Control.LoadedEvent.AddClassHandler<ToolTip>((tt, _) =>
        {
            ConfigureToolTipPopupRoot(tt);
            Dispatcher.UIThread.Post(() => ConfigureToolTipPopupRoot(tt), DispatcherPriority.Render);
        });
        ToolTip.IsOpenProperty.Changed.AddClassHandler<Control>((ctrl, e) =>
        {
            if (e.NewValue is true)
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (ToolTip.GetTip(ctrl) is ToolTip tt)
                        ConfigureToolTipPopupRoot(tt);
                }, DispatcherPriority.Render);
            }
        });

        // 全局元素阴影: any TextBlock (or elem-shadow-marked template part) that attaches inside a
        // desktop widget window picks up the current shadow (see WidgetTextShadow — settings,
        // dialog and panel text is excluded).
        WidgetTextShadow.Install();

        services.GetRequiredService<WallpaperWatcherService>();

        var profileService = services.GetRequiredService<ProfileService>();
        profileService.EnsureSeeded();

        var displayMonitor = services.GetRequiredService<DisplayMonitorService>();
        var widgetFactory = (WidgetFactory) services.GetRequiredService<IWidgetFactory<Window, UserControl>>();

        // Multi-screen: a permanent invisible anchor window keeps an Avalonia
        // TopLevel alive so the monitor service can enumerate + watch screens even
        // when no widget/settings window exists yet. Order matters: the screen
        // list must be refreshed BEFORE widget creation so widgets are only
        // created for screens that are actually attached.
        var anchor = new WidgetAnchorWindow();
        WindowRoles.Register(anchor, WindowRole.Anchor);
        anchor.ShowAnchored();

        // Wire the display pipeline BEFORE the first Refresh runs inside Attach: the
        // initial refresh publishes ScreensChanged, and a subscriber that is added
        // afterwards would never see it (the startup reconciliation then only ran
        // because the App-level Create() call below happened to cover it).
        anchor.DisplaySettingsChanged += (_, _) => displayMonitor.NotifyDisplaySettingsChanged();
        anchor.DisplayDevicesChanged += (_, _) => displayMonitor.Refresh();

        // Hot-plug: hide widgets of unplugged screens, recreate widgets when a
        // screen comes back (their per-screen configuration is still on disk).
        displayMonitor.ScreensChanging += (_, _) => widgetFactory.SuspendForDisplayChange();
        displayMonitor.ScreensChanged += (_, _) => widgetFactory.OnScreensChanged();

        displayMonitor.Attach(anchor);

        // The anchor is desktop furniture like the widgets: registered in the bottom
        // band so the band predicate never sees a visible non-member window below a
        // widget (Windows relocating the offscreen anchor during a topology change
        // would otherwise re-introduce exactly that violation).
        WidgetZOrder.PinWidgetToBottom(anchor);

        // Fullscreen (games, video, presentations): what happens to the widgets is the
        // 最大化或全屏时 choice in 高级设置 — 隐藏 hides the windows, pauses every shared
        // widget timer and releases the material caches (handing the memory to the covering
        // application), 暂停 freezes them in place, 无处理 leaves them running. Restored on
        // exit; a settings save made while covered re-applies the choice immediately.
        var fullscreenWatcher = services.GetRequiredService<FullscreenWatcherService>();
        fullscreenWatcher.FullscreenChanged += (_, isFullscreen) =>
            widgetFactory.ApplyFullscreen(isFullscreen);
        appSettingsProvider.DataChanged += (_, _, _) =>
            widgetFactory.ApplyFullscreen(fullscreenWatcher.IsFullscreen);
        fullscreenWatcher.Attach(anchor);

        // 失焦自动切换 (设置 → 外观): watch the desktop's activation state and push the
        // per-screen light/dark variant onto the widget windows. The watcher rides the same
        // anchor; the coordinator is constructed eagerly so it already observes settings,
        // wallpaper and fullscreen changes — widgets register themselves when they open.
        var focusWatch = services.GetRequiredService<FocusWatchService>();
        focusWatch.Attach(anchor);
        focusWatch.StateChanged += (_, _) => fullscreenWatcher.Evaluate();
        services.GetRequiredService<FocusThemeCoordinator>();

        // The startup reconciliation (ScreensChanged → OnScreensChanged, now wired before
        // Attach) usually created and showed the widgets already; Create() then only covers
        // the paths where no reconciliation ran. Either way, whether any widget window is
        // alive decides the settings-window auto-show below — not whether Create() itself
        // happened to build one.
        foreach (var widget in widgetFactory.Create())
            widget.Show();

        // Resolved on demand: a widget-only start must not pay for building the settings window,
        // while both the primary launch and a hand-over from a second launch address the same
        // instance (it is a DI singleton).
        Settings SharedSettingsWindow() => services.GetRequiredService<Settings>();

        // A second launch of the exe signals this instance instead of starting a rival process;
        // surface the settings window so the user sees why nothing new appeared. The hand-over
        // runs on the UI thread of the running instance — a failure here would kill the whole
        // app (and leave the user with nothing, since the second launch has already exited), so
        // it must never throw.
        SingleInstance.Current?.Listen(() =>
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    SharedSettingsWindow().ShowAndActivate();
                }
                catch (Exception ex)
                {
                    GlassDiagnostics.Event($"settings hand-over failed: {ex.GetType().Name}: {ex.Message}");
                }
            }));

        // Notification-area icon: the always-reachable way back into a desktop-only app.
        var tray = new TrayIconService(appSettingsProvider, SharedSettingsWindow, widgetFactory);
        tray.Start();

        // 聚焦搜索: start after the anchor exists (it needs a TopLevel for screen enumeration) and
        // before any window is shown, so the global hotkey is live from the first moment. The
        // overlay itself is created lazily on the first summon; only the folder index runs now.
        var spotlight = services.GetRequiredService<SpotlightService>();
        spotlight.Start();

        // The overlay's "open settings" affordance funnels through here: one shared settings
        // window for the process, landing on the 聚焦搜索 page.
        spotlight.OpenSettingsRequested += () => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var settingsWindow = SharedSettingsWindow();
                settingsWindow.ShowAndActivate();
                settingsWindow.SelectPage(typeof(Views.Pages.Spotlight));
            }
            catch (Exception ex)
            {
                GlassDiagnostics.Event($"spotlight open-settings failed: {ex.GetType().Name}: {ex.Message}");
            }
        });

        if ((ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args.Length: > 0 } desktop && desktop.Args[0] == "--settings") || !widgetFactory.HasWidgets)
            SharedSettingsWindow().ShowAndActivate();

        // Development / shortcut entry: open 聚焦搜索 immediately instead of waiting for the
        // global hotkey. Only meaningful on the first launch — a second launch hands over to the
        // running instance and does not forward its arguments.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { Args.Length: > 0 } spotlightArgs
            && spotlightArgs.Args[0] == "--spotlight")
        {
            Dispatcher.UIThread.Post(
                () => services.GetRequiredService<SpotlightService>().Toggle(bypassBlacklist: true),
                DispatcherPriority.Background);
        }
        
        services.GetRequiredService<UpdateService>().CheckForUpdates();

        // One startup maintenance point, not a routine: the first seconds of a session build every
        // widget's surfaces, captures and caches at once, and the leftover segments from that burst
        // are worth handing back once it has settled. The trimmer's single-flight / cooldown /
        // hysteresis guards keep this a one-off.
        System.Threading.Tasks.Task.Delay(6000).ContinueWith(_ => MemoryTrimmerService.RequestTrim());

        // From here on a timer carries the case no event can see — a heap that settled fragmented
        // after some burst, with nothing left to trigger a release. The tick only pays for a
        // compacting collection when the collector reports fragmentation worth compacting, so an
        // idle, healthy process just gets a working-set trim (see MemoryTrimmerService).
        MemoryTrimmerService.StartPeriodicRelease();

        base.OnFrameworkInitializationCompleted();
    }

    private void UpdateCornerRadiusResources(DeskCanvas.Core.Models.Settings.AppSettings settings)
    {
        var r = settings.Theme.UseNativeFrame ? 0 : settings.Dimensions.Radius;
        var cardRadius = new CornerRadius(r);
        var innerRadius = r <= 0 ? new CornerRadius(0) : new CornerRadius(Math.Max(2, Math.Round(r * 0.70)));
        var pillRadius = r <= 0 ? new CornerRadius(0) : new CornerRadius(Math.Max(2, Math.Round(r * 0.40)));

        Resources["WidgetCardCornerRadius"] = cardRadius;
        Resources["WidgetInnerCornerRadius"] = innerRadius;
        Resources["WidgetPillCornerRadius"] = pillRadius;
    }

    /// <summary>
    /// The shared menu surface radius (DesignTokens HostSurfaceCornerRadius), used to keep the
    /// acrylic popup's native window region in sync with the menu card's rounded corners.
    /// </summary>
    private static double MenuPopupCornerRadius =>
        ThemeScopeResolver.TryFindResource<CornerRadius>(null, "HostSurfaceCornerRadius", null, out var radius)
            ? radius.TopLeft
            : 12;

    /// <summary>
    /// Menu controls re-enter the visual tree on every open while the instance itself lives as
    /// long as its owner — the size subscription above must be attached once per instance, the
    /// per-open calls stay on the Loaded handler.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Visual, object> MenuPopupBoundsHooked = new();

    private static readonly object MenuPopupBoundsHookedMarker = new();

    /// <summary>
    /// Rounds a menu popup's native window to the menu card corner radius. DWM paints the
    /// AcrylicBlur backdrop over the whole square HWND, so without the region the frosted glass
    /// bleeds past the menu's rounded corners. Only acrylic popups are clipped: ToolTip / tray
    /// popups are Transparent windows that round their own content with anti-aliasing, and the
    /// 1-bit GDI region would add hard aliased corners and cut their shadows.
    /// </summary>
    private static void ApplyMenuPopupRounding(WindowBase wb)
    {
        if (!wb.TransparencyLevelHint.Contains(WindowTransparencyLevel.AcrylicBlur)) return;

        var handle = wb.TryGetPlatformHandle()?.Handle;
        if (handle is null || handle.Value == IntPtr.Zero) return;

        var scaling = wb.DesktopScaling;
        var width = (int)Math.Round(wb.ClientSize.Width * scaling);
        var height = (int)Math.Round(wb.ClientSize.Height * scaling);
        if (width <= 0 || height <= 0) return;

        InteropService.DisableWindowBorder(handle.Value);
        InteropService.SetWindowRegion(handle.Value, 0, 0, width, height,
            (int)Math.Round(MenuPopupCornerRadius * scaling));
    }
}
