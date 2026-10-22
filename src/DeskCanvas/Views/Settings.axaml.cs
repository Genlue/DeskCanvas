using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Views;

public partial class Settings : Window
{
    // One shared array: assigning the hint makes Avalonia re-run the Win32 transparency setup on
    // the live window (a fresh array literal never compares equal to the previous one), so every
    // settings save used to tear down and rebuild the composited acrylic backdrop — the exact
    // moment a DWM hiccup can leave the window with a dead backdrop and no content behind it.
    private static readonly WindowTransparencyLevel[] TransparencyHint =
        [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None];

    private readonly SettingsViewModel viewModel;
    private readonly IAppSettingsProvider appSettingsProvider;

    /// <summary>Periodic check that the acrylic backdrop is still alive while the window is shown.</summary>
    private readonly DispatcherTimer transparencyWatch;

    /// <summary>The transparency level at the previous check, to detect a fresh drop (see VerifyTransparency).</summary>
    private WindowTransparencyLevel lastLevel = WindowTransparencyLevel.None;

    /// <summary>
    /// Set once this instance has really closed. An Avalonia window can never be re-shown after
    /// <see cref="Window.Close"/>, and every way back into the app (tray, widget settings, a
    /// second launch's hand-over) resolves this same singleton — showing it then would throw
    /// <c>InvalidOperationException</c> on the UI thread and take the whole process down.
    /// </summary>
    private bool closedForReal;

    public Settings(IAppSettingsProvider appSettingsProvider, IAssemblyProvider assemblyProvider,
        ILayoutProvider layoutProvider, DisplayMonitorService displayMonitor, IWidgetFactory<Window, UserControl> widgetFactory,
        ProfileService profileService, UpdateService updateService, SpotlightService spotlightService,
        FileSearchService fileSearchService)
    {
        viewModel = new SettingsViewModel(appSettingsProvider, assemblyProvider, layoutProvider, displayMonitor,
            widgetFactory, profileService, updateService, spotlightService, fileSearchService);
        this.appSettingsProvider = appSettingsProvider;
        DataContext = viewModel;
        Resized += OnResized;
        Closed += (_, _) => closedForReal = true;
        // Self-heal is timer-driven only: an Activated-time check fires while the window is
        // still transitioning (level momentarily reads non-acrylic on every re-show) and would
        // tear down and rebuild the composited backdrop on every open.
        transparencyWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        transparencyWatch.Tick += (_, _) => VerifyTransparency();
        appSettingsProvider.DataChanged += (_, _, _) =>
        {
            ApplyTransparencyHint();
            ApplyTitleBarStyle();
        };
        InitializeComponent();
        WindowRoles.Register(this, WindowRole.Settings);
        ListBox.SelectedItem = viewModel.DefaultItem;
        ApplyTransparencyHint();
        ApplyTitleBarStyle();
    }

    /// <summary>
    /// Show the shared settings window and bring it to the front.
    /// <para>
    /// All widgets open this one instance, and a hand-over from a second launch lands here, so
    /// this is the single entry point for surfacing the app.
    /// </para>
    /// </summary>
    public void ShowAndActivate()
    {
        // A closed window cannot come back; bowing out here keeps a hand-over or tray click
        // from crashing the process on a stale singleton (should not happen — OnClosing hides
        // the window instead of closing it — but the guard is what contains such a regression).
        if (closedForReal) return;

        if (!IsVisible)
        {
            // A minimized Win32 window sits at the classic (-32000, -32000) rect, so it has to be
            // brought back to Normal *before* it is shown or it is "shown" off-screen.
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            // Re-attach the page the close path detached, before the window is shown: the page's
            // Loaded contract runs as it re-attaches (the gallery rebuilds its previews, pages
            // re-subscribe), so the first visible frame is already complete. The swap runs
            // transition-less (see OnClosing): the attach must be synchronous too, and a
            // transition started on a hidden window would only replay as a slide on show.
            SwapPageWithoutTransition(() => viewModel.RestoreCurrentPage());

            Show();

            // 原生标题栏模式下，DWM caption 的隐藏（见 ApplyTitleBarStyle）需要在平台句柄
            // 存在后才能真正生效；构造阶段句柄尚不存在，这里补一次（幂等）。
            InteropService.HideNativeCaption(this);

            // WindowStartupLocation="CenterScreen" re-applies on every Show, and it derives the
            // position from the window it is re-showing — for a hidden/minimized window that is a
            // stale rect, so the window jumped (onto another monitor, or off-screen entirely) each
            // time it was opened from the tray. Let it place the window exactly once, then hand
            // placement over to the position the user left it at.
            WindowStartupLocation = WindowStartupLocation.Manual;

            // The Hide/Show reuse path is one of the moments the composited backdrop can be lost;
            // watch for a drop for as long as the window is on screen.
            transparencyWatch.Start();
        }

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();

        // Avalonia's Activate is not enough to raise a background window on Windows; the Win32
        // call is what actually pulls it in front of the user's current window.
        InteropService.BringToFront(this);
    }

    /// <summary>
    /// Fixed stable window transparency for the settings window across all themes. Uses OS-level AcrylicBlur
    /// with fallback to None.
    /// </summary>
    private void ApplyTransparencyHint()
    {
        GlassSurface.IsVisible = false;
        // Assign only when the hint actually differs — see the comment on TransparencyHint for
        // why every assignment is a live backdrop rebuild.
        if (!TransparencyHint.SequenceEqual(TransparencyLevelHint))
            TransparencyLevelHint = TransparencyHint;
    }

    /// <summary>
    /// Force the platform to rebuild the acrylic backdrop. Toggling through <c>None</c> is a real
    /// level change, which the Win32 impl always re-applies — an equal-content re-assert alone
    /// might be short-circuited.
    /// </summary>
    private void ForceTransparencyReapply()
    {
        TransparencyLevelHint = [WindowTransparencyLevel.None];
        TransparencyLevelHint = TransparencyHint;
    }

    /// <summary>
    /// Assert the acrylic backdrop is still alive. Windows tears the composited backdrop down on
    /// DWM resets (screen sharing, lock screen, display changes, driver resets), and with
    /// <c>Background="Transparent"</c> the window then shows the bare desktop through everything
    /// but the traffic lights — it reads as "the whole window disappeared". A fresh drop is
    /// healed by re-asserting the hint (which rebuilds the backdrop); the opaque surface fallback
    /// below keeps the window legible in the meantime and in states the platform genuinely cannot
    /// do acrylic (transparency effects off).
    /// </summary>
    private void VerifyTransparency()
    {
        ApplySurfaceFallback();

        var level = ActualTransparencyLevel;
        var wasAcrylic = lastLevel == WindowTransparencyLevel.AcrylicBlur;
        lastLevel = level;
        // Heal only a fresh dropout: a persistent non-acrylic level is the platform's answer
        // (transparency effects off, unsupported GPU), and re-asserting that every tick would
        // just churn the backdrop.
        if (!IsVisible || !wasAcrylic || level == WindowTransparencyLevel.AcrylicBlur) return;
        GlassDiagnostics.Event($"settings window transparency dropped to {level} — re-asserting acrylic");
        ForceTransparencyReapply();
    }

    /// <summary>
    /// The window has no opaque background of its own — everything but the traffic lights is
    /// semi-transparent by design — so when the compositor is not showing the acrylic backdrop,
    /// the 0.80-alpha tint over the raw wallpaper reads as "the window is gone". Whenever the
    /// achieved transparency level is anything but acrylic, swap the tint for the fully opaque
    /// <c>SettingsBackground</c> (the XAML "opaque" class on the surface Border).
    /// </summary>
    private void ApplySurfaceFallback()
    {
        // Called from OnPropertyChanged while the XAML is still populating: setting the
        // TransparencyLevelHint attribute raises an actual-level change before the surface
        // Border exists. There is nothing to swap yet.
        if (WindowSurface == null) return;

        var wantOpaque = ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur;
        var hasClass = WindowSurface.Classes.Contains("opaque");
        if (wantOpaque == hasClass) return;
        if (wantOpaque) WindowSurface.Classes.Add("opaque");
        else WindowSurface.Classes.Remove("opaque");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TopLevel.ActualTransparencyLevelProperty)
            ApplySurfaceFallback();
    }

    /// <summary>
    /// Native window chrome configuration for the selected title bar style.
    /// "Native" keeps the system window buttons; "TrafficLights" hides them
    /// (NoChrome) and lets the custom 36px macOS-style bar own the top row.
    /// These properties are updated live by the Win32 platform impl, so the
    /// switch takes effect immediately.
    /// </summary>
    private void ApplyTitleBarStyle()
    {
        var settings = appSettingsProvider.Get();
        var isTraffic = settings.EffectiveTitleBarStyle == TitleBarStyle.TrafficLights;
        // Avalonia 12 removed ExtendClientAreaChromeHints: "no chrome" is now expressed as
        // BorderOnly (resizable border, no system caption buttons) so the custom traffic-light
        // bar can own the top row; Full keeps the native caption.
        WindowDecorations = isTraffic
            ? Avalonia.Controls.WindowDecorations.BorderOnly
            : Avalonia.Controls.WindowDecorations.Full;
        ExtendClientAreaTitleBarHeightHint = isTraffic ? 36 : 50;
        TrafficBar.IsVisible = isTraffic;
        TrafficBar.Size = settings.EffectiveTitleBarSize;
        NativeTitleBar.IsVisible = !isTraffic;
        // 原生模式下 DWM 会在扩展客户区之上再画一层系统标题栏：不透明的底色横带加
        // "DeskCanvas" 标题文字，压在亚克力窗口上像一条硬边。让 DWM 不绘制标题栏的
        // 底色与文字（原生最小化/最大化/关闭按钮保留），窗口拖动由 DWM caption 命中
        // 区和上面那层透明的 NativeTitleBar 拖拽条共同承担。
        if (!isTraffic) InteropService.HideNativeCaption(this);
    }

    private void OnNativeTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        if (e.ClickCount >= 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        BeginMoveDrag(e);
    }
    
    /// <summary>
    /// Swap the page host's content without the page transition.
    /// <para>
    /// The close/reopen path cannot ride the transition: <c>TransitioningContentControl</c> hides
    /// the outgoing presenter — the call that actually detaches the old page and runs its
    /// Unloaded contract — only from the transition's completion callback. On a hidden window
    /// nothing is guaranteed to drive that animation, so the release (and the page detach) would
    /// sit behind it indefinitely. Dropping the transition for this one swap takes the control's
    /// immediate path, which clears the outgoing presenter synchronously; navigation between
    /// pages keeps its animation.
    /// </para>
    /// </summary>
    private void SwapPageWithoutTransition(Action swap)
    {
        var transition = PageHost.PageTransition;
        PageHost.PageTransition = null;
        try
        {
            swap();
        }
        finally
        {
            PageHost.PageTransition = transition;
        }
    }

    private void Restart(object? sender, RoutedEventArgs e) => AppRestart.Restart();

    private void Exit(object? sender, RoutedEventArgs e) => AppShutdown.Request();

    /// <summary>
    /// Closing the shared settings window normally only hides it.
    /// <para>
    /// Avalonia cannot re-show a closed window, and every widget (plus a hand-over from a second
    /// launch) resolves this same instance, so it must never really close while the app lives.
    /// Letting the close through when the last widget window disappeared would not exit anything
    /// either — the invisible anchor window keeps the process (tray, timers) alive — it would
    /// only leave this singleton closed and unshowable, and the next tray click or second-launch
    /// hand-over would then crash the whole process ("Cannot re-show a closed window"). A genuine
    /// exit announces itself through <see cref="AppShutdown.Request"/>, which shuts the
    /// application down explicitly.
    /// </para>
    /// <para>
    /// Hiding keeps the window, but not the page: the window outlives every close, and its pages
    /// hold live resources (the gallery's widget previews) while they are attached, so the hide
    /// path detaches the current page — through an immediate, transition-less swap, so the
    /// release cannot sit behind the page transition's completion callback — and asks for a
    /// prompt trim; see <see cref="SettingsViewModel.ReleaseCurrentPage"/>.
    /// <see cref="ShowAndActivate"/> restores it from the page cache.
    /// </para>
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!AppShutdown.IsShuttingDown)
        {
            e.Cancel = true;
            transparencyWatch.Stop();
            Hide();

            // Hiding a window does NOT unload its content (Avalonia keeps a hidden window's
            // visual tree attached), so without this the current page would keep its live
            // resources — the gallery's real widget previews and their timers, page event
            // subscriptions — alive for the whole time the window stays closed, and the trim
            // below would find nothing to release. Detach the page AFTER hiding, through an
            // immediate (transition-less) swap: with the page transition in place the
            // TransitioningContentControl only releases the outgoing page when the transition
            // completes, so the release would sit behind an animation clock that nothing drives
            // on a hidden window — and the trim below would race it.
            SwapPageWithoutTransition(() => viewModel.ReleaseCurrentPage());

            // The one release the user is directly watching does not wait out the trimmer's
            // coalescing cooldown (single-flight and the floor still apply — see TrimPriority).
            MemoryTrimmerService.RequestTrim(TrimPriority.Immediate);
            return;
        }

        base.OnClosing(e);
        System.Threading.Tasks.Task.Delay(500).ContinueWith(_ => MemoryTrimmerService.RequestTrim());
    }

    private void OnResized(object? sender, WindowResizedEventArgs e)
    {
        SplitView.IsPaneOpen = Width >= 800;
    }

    private void OnMenuItemChanged(object? _, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count > 0 && e.AddedItems[0] is PageViewModel item)
        {
            if (item.IsItem)
            {
                viewModel.SetCurrentPage(item);
            }
            else
            {
                // Revert selection if user clicked a non-item (header or separator)
                if (viewModel.CurrentPageTitle != null)
                {
                    var prev = viewModel.AllItems.FirstOrDefault(x => x.Text == viewModel.CurrentPageTitle);
                    if (prev != null)
                    {
                        ListBox.SelectedItem = prev;
                        return;
                    }
                }
                ListBox.SelectedItem = viewModel.DefaultItem;
            }
        }
    }

    public void SelectPage(Type pageType)
    {
        var target = viewModel.AllItems.FirstOrDefault(x => x.Type == pageType);
        if (target != null)
        {
            ListBox.SelectedItem = target;
        }
    }
}
