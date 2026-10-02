using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
        var services = new ServiceCollection()
            .AddSingleton<IAppSettingsProvider, AppSettingsProvider>()
            .AddSingleton<ILayoutProvider, LayoutProvider>()
            .AddSingleton<IAssemblyProvider, AssemblyProvider>()
            .AddSingleton<WallpaperThemeService>()
            .AddSingleton<WallpaperWatcherService>()
            .AddSingleton<IThemeService, ThemeService>()
            .AddSingleton<ILocaleService, LocaleService>()
            .AddSingleton<IGridService<Widget>, GridService>()
            .AddSingleton<DisplayMonitorService>()
            .AddSingleton<FullscreenWatcherService>()
            .AddSingleton<WidgetFactory>()
            .AddSingleton<IWidgetFactory<Window, UserControl>>(sp => sp.GetRequiredService<WidgetFactory>())
            .AddSingleton<SidebarService>()
            .AddSingleton<ISidebarService>(sp => sp.GetRequiredService<SidebarService>())
            .AddSingleton<GlobalHotKeyService>()
            .AddSingleton<IGlobalHotKeyService>(sp => sp.GetRequiredService<GlobalHotKeyService>())
            .AddSingleton<ProfileService>()
            // Lazy: the sidebar's widget menu switches profiles, and ProfileService itself
            // closes the sidebars — resolving it lazily keeps the two out of a DI cycle.
            .AddSingleton<Func<ProfileService>>(sp => () => sp.GetRequiredService<ProfileService>())
            .AddSingleton<MemoryTrimmerService>()
            .AddSingleton<Settings, Settings>()
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
        themeService.Apply(appSettingsProvider.Get().Theme);
        UpdateCornerRadiusResources(appSettingsProvider.Get());
        appSettingsProvider.DataChanged += (_, _, newSettings) => UpdateCornerRadiusResources(newSettings);

        // The settings window paints its own rounded glass surface on top of Avalonia's
        // WinUIComposition backdrop, so it has to be clipped to the radius that backdrop was
        // built with. Both sides read Program.WindowCornerRadius, so they can never drift apart
        // and reopen the transparent corner notch. This is a window radius — not the widget-card
        // radius in Dimensions.Radius, which is a different (and much larger) setting.
        Resources["SettingsWindowCornerRadius"] = new CornerRadius(Program.WindowCornerRadius);


        Control.LoadedEvent.AddClassHandler<ContextMenu>((cm, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (cm.GetVisualRoot() is WindowBase wb)
                {
                    var handle = wb.TryGetPlatformHandle()?.Handle;
                    if (handle.HasValue && handle.Value != IntPtr.Zero)
                    {
                        InteropService.DisableWindowBorder(handle.Value);
                    }
                }
            }, DispatcherPriority.Render);
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
        var sidebarService = services.GetRequiredService<SidebarService>();
        var hotKeyService = services.GetRequiredService<IGlobalHotKeyService>();

        // Multi-screen: a permanent invisible anchor window keeps an Avalonia
        // TopLevel alive so the monitor service can enumerate + watch screens even
        // when no widget/settings window exists yet. Order matters: the screen
        // list must be refreshed BEFORE widget creation so widgets are only
        // created for screens that are actually attached.
        var anchor = new WidgetAnchorWindow();
        anchor.ShowAnchored();
        displayMonitor.Attach(anchor);

        // Hot-plug: hide widgets of unplugged screens, recreate widgets when a
        // screen comes back (their per-screen configuration is still on disk).
        displayMonitor.ScreensChanged += (_, _) =>
        {
            widgetFactory.OnScreensChanged();
            sidebarService.ValidateScreens();
        };

        // 右侧小组件侧栏: a global hotkey toggles it on the screen the mouse is on. A failed or
        // taken combination is reported to the settings page and never stops the app from running.
        hotKeyService.HotKeyPressed += (_, _) =>
        {
            try
            {
                sidebarService.ToggleForCursorScreen();
            }
            catch (Exception ex)
            {
                // A hotkey that appears to do nothing must leave a trace behind.
                GlassDiagnostics.Failure(ex);
            }
        };
        // Registered on the service's own message-only window (never by subclassing an Avalonia
        // window — see GlobalHotKeyService), with a key-state poll as a safety net.
        hotKeyService.Start();

        // Follow hotkey edits made elsewhere (a different profile, an imported backup).
        appSettingsProvider.DataChanged += (sender, oldSettings, newSettings) =>
        {
            var desired = newSettings.EffectiveSidebar.HotKey;
            if (oldSettings?.EffectiveSidebar.HotKey == desired) return;
            if (!string.Equals(hotKeyService.HotKey, desired, StringComparison.OrdinalIgnoreCase))
                hotKeyService.TrySetHotKey(desired, out _);
        };

        // Fullscreen (games, video, presentations): the widgets are invisible anyway, so
        // hide them, pause every shared widget timer and release the material caches —
        // that hands the memory back to the fullscreen application. Restored on exit.
        var fullscreenWatcher = services.GetRequiredService<FullscreenWatcherService>();
        fullscreenWatcher.FullscreenChanged += (_, isFullscreen) =>
        {
            if (isFullscreen)
            {
                widgetFactory.SuspendAll();
            }
            else
            {
                widgetFactory.ResumeAll();
            }
        };
        fullscreenWatcher.Attach(anchor);

        var widgetsCount = widgetFactory
            .Create()
            .Select(widget =>
            {
                widget.Show();
                return widget;
            })
            .Count();

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
        var tray = new TrayIconService(appSettingsProvider, SharedSettingsWindow, widgetFactory, sidebarService);
        tray.Start();

        var desktopLifetime = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        if ((desktopLifetime is { Args.Length: > 0 } && desktopLifetime.Args[0] == "--settings") || widgetsCount == 0)
            SharedSettingsWindow().ShowAndActivate();

        // --sidebar: open the sidebar immediately (a hand-check path that works even before a
        // hotkey has been recorded, and the quickest way to verify the feature on a new machine).
        if (desktopLifetime?.Args?.Contains("--sidebar") == true)
            sidebarService.ShowForCursorScreen();
        
        services.GetRequiredService<UpdateService>().CheckForUpdates();

        // Long sessions creep upwards on the native side (Skia surfaces, shell icons, DWM
        // buffers) because a mostly-idle process rarely runs the finalizers that would release
        // them; this sweeps back down whenever the process is holding more than its budget.
        services.GetRequiredService<MemoryTrimmerService>().Start();

        System.Threading.Tasks.Task.Delay(6000).ContinueWith(_ => InteropService.TrimProcessMemory());

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
}