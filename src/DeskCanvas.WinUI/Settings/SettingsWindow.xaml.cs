using System;
using System.Collections.Generic;
using System.Diagnostics;
using DeskCanvas.WinUI.Settings.Localization;
using DeskCanvas.WinUI.Settings.Pages;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 共享设置窗口：整个软件只有这一个实例，关闭按钮 = 隐藏，再次唤出恢复最后页面。
/// <para>
/// 生命周期（报告 §3 第二步 / §4.2）：
/// <list type="number">
/// <item><description>关闭：拦 <see cref="AppWindow.Closing"/>，取消关闭并隐藏；隐藏前卸载当前页
/// （Content 置空 + <see cref="ISettingsPage.Detach"/> + Dispose），页面订阅与异步请求随之停止。</description></item>
/// <item><description>再次唤出：先按记住的页面键**重建**页面再显示，首帧就是完整内容；
/// 不复用 WinUI 导航缓存，因为缓存不会替我们跑释放逻辑。</description></item>
/// <item><description>真正退出：由生命周期协调器调用 <see cref="CloseForShutdown"/>，
/// 只有它会放行 <see cref="AppWindow.Closing"/> 并调用 <c>Window.Close()</c>。</description></item>
/// </list>
/// </para>
/// </summary>
public sealed partial class SettingsWindow : Window
{
    /// <summary>初始客户区尺寸，沿用旧 Settings 窗口的 1000×650（DIP）。</summary>
    private const double InitialWidthDip = 1000;
    private const double InitialHeightDip = 650;

    private readonly IServiceProvider services;
    private readonly SettingsPageCatalog catalog;
    private readonly ISettingsPageFactory pageFactory;

    /// <summary>页面键 → 左栏导航项；同时用于「导航项 → 页面」的反查和选中态同步。</summary>
    private readonly Dictionary<string, NavigationViewItem> navItems = new(StringComparer.Ordinal);

    private ISettingsPage? currentPage;
    private string? currentPageKey;
    private string? lastPageKey;
    private bool allowClose;
    private bool suppressSelectionChanged;
    private bool navigationLoaded;
    private bool hasShown;
    private bool isShown;
    private bool initialSizeCorrected;

    /// <summary>上次校正时使用的缩放；缩放真正稳定前允许再校正一次。</summary>
    private double lastCorrectionScale = -1;

    /// <summary>首次显示后监听的 XamlRoot（1 秒兜底后解除）。</summary>
    private XamlRoot? watchedRoot;

    /// <summary>创建窗口时算初始尺寸所用的 DPI；首次显示后据此判断要不要按真实 DPI 校正一次。</summary>
    private uint sizingDpi = 96;

    /// <summary>窗口被真正关闭（不是隐藏）时触发，宿主据此丢掉失效引用。</summary>
    public event EventHandler? ClosedForGood;

    public SettingsWindow(IServiceProvider services, SettingsPageCatalog catalog, ISettingsPageFactory pageFactory)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.pageFactory = pageFactory ?? throw new ArgumentNullException(nameof(pageFactory));

        InitializeComponent();

        Title = SettingsShellStrings.SettingsWindowTitle;
        PaneHeaderText.Text = SettingsShellStrings.SettingsWindowTitle;
        lastPageKey = catalog.DefaultPageKey;

        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnWindowClosed;
        NavRoot.Loaded += OnNavigationLoaded;

        BuildNavigation();
        ApplyInitialSize();
        ApplyTitleBarMode(Chrome.TitleBarMode);
    }

    /// <summary>标题栏/材质策略槽位（本轮只有原生 + 不透明真正生效，见 <see cref="SettingsWindowChrome"/>）。</summary>
    public SettingsWindowChrome Chrome { get; } = new();

    /// <summary>窗口当前是否处于显示状态（隐藏后为 false；关闭后也保持 false）。</summary>
    public bool IsShown => isShown;

    /// <summary>当前页面键；隐藏/未加载时为 null。</summary>
    public string? CurrentPageKey => currentPageKey;

    /// <summary>
    /// 显示并按需切换页面。必须在 WinUI UI 线程调用（<see cref="Host.AppHost.RunOnUiThread"/>）。

    /// </summary>
    /// <param name="pageKey">目标页面键；null/未知键时恢复上次页面（首次为默认页）。</param>
    public void ShowPage(string? pageKey = null)
    {
        var target = (!string.IsNullOrEmpty(pageKey) ? catalog.Find(pageKey) : null)
                     ?? catalog.Find(lastPageKey)
                     ?? catalog.DefaultItem;

        // 先建内容再显示：页面在显示前完成 Attach，首帧不会先是空白再跳出内容。

        NavigateTo(target);

        // 最小化状态下 AppWindow.Show 不会把窗口挪回屏幕：先走 Win32 还原。

        SettingsNativeWindow.RestoreIfMinimized(this);

        var firstShow = !hasShown;
        if (firstShow)
        {
            hasShown = true;
            Activate();
        }
        else
        {
            AppWindow.Show();
            Activate();
        }

        SettingsNativeWindow.BringToFront(this);

        // 首次显示后再校正一次：窗口创建时还没落到显示器上，GetDpiForWindow 只会给出 96。

        if (firstShow) ScheduleInitialSizeCorrection();

        isShown = true;
    }

    /// <summary>
    /// 隐藏窗口并卸下当前页（关闭按钮、外部请求隐藏都走这里）。

    /// <para>卸载放在隐藏之前：页面不再可见的同一刻就断订阅，不会出现「窗口已隐藏但页面还在跑」的窗口期。</para>
    /// </summary>
    public void HideAndUnload()
    {
        if (!hasShown || !isShown) return;

        UnloadCurrentPage();
        AppWindow.Hide();
        isShown = false;
    }

    /// <summary>
    /// 只在真正退出时调用：放行关闭并销毁窗口。调用方必须先确保没有页面还在跑
    /// （本方法会自己再卸一次页面，幂等）。

    /// </summary>
    public void CloseForShutdown()
    {
        allowClose = true;
        UnloadCurrentPage();
        isShown = false;
        Close();
    }

    /// <summary>
    /// 应用标题栏策略。本轮只实现系统原生标题栏；红绿灯自绘标题栏与三种材质背景是下一步
    /// （报告 §4.1），未实现前保持原生 + 不透明，并在调试输出里留痕，不静默"看起来像实现了"。

    /// </summary>
    public void ApplyTitleBarMode(SettingsTitleBarMode mode)
    {
        Chrome.TitleBarMode = mode;

        switch (mode)
        {
            case SettingsTitleBarMode.Native:
                // 不扩展到标题栏：系统标题栏/系统按钮/系统菜单与键盘操作全部保持原生行为。

                ExtendsContentIntoTitleBar = false;
                break;

            case SettingsTitleBarMode.TrafficLights:
                // TODO(报告 §4.1)：接自绘红绿灯需要 (1) ExtendsContentIntoTitleBar + 可拖动区，
                // (2) 自绘最小化/最大化/关闭三个原生 Button 并保留系统菜单与命中区域，
                // (3) 与 SettingsWindowMaterial 的三种背景一起做透明/材质适配。

                // 在完成之前保持原生标题栏，避免出现"没有关闭按钮也拖不动"的窗口。

                Debug.WriteLine("[Settings] 红绿灯标题栏尚未实现，保持原生标题栏（报告 §4.1）。");
                Chrome.TitleBarMode = SettingsTitleBarMode.Native;
                ExtendsContentIntoTitleBar = false;
                break;
        }
    }

    private void BuildNavigation()
    {
        foreach (var group in catalog.Groups)
        {
            NavRoot.MenuItems.Add(new NavigationViewItemHeader { Content = group.Title });

            foreach (var item in group.Items)
            {
                var navItem = new NavigationViewItem
                {
                    Content = item.Title,
                    Tag = item.PageKey,
                };

                if (SettingsPageIconFactory.Create(item.IconPath) is { } icon)
                {
                    navItem.Icon = icon;
                }

                navItems[item.PageKey] = navItem;
                NavRoot.MenuItems.Add(navItem);
            }
        }
    }

    private void NavigateTo(SettingsNavItem item)
    {
        if (currentPage is not null && string.Equals(currentPageKey, item.PageKey, StringComparison.Ordinal))
        {
            // 同一页面重复唤出：只同步选中态，不重建（避免隐藏前的一次点击把页面来回重建）。

            SyncNavigationSelection(item.PageKey);
            return;
        }

        UnloadCurrentPage();

        var context = new SettingsPageContext(services, item.PageKey, item.Title, item.Assemblies);

        ISettingsPage page;
        try
        {
            page = pageFactory.Create(context);
        }
        catch (Exception ex)
        {
            // 页面工厂/注册项炸了不能把整个设置窗口带下水：退化成占位页并留痕。

            Debug.WriteLine($"[Settings] 页面 '{item.PageKey}' 创建失败，回退占位页：{ex}");
            page = new PagePlaceholder();
        }

        PageHost.Content = page.View;
        try
        {
            page.Attach(context);
        }
        catch (Exception ex)
        {
            // Attach 失败时仍保留页面实例：隐藏/下次导航会走 Detach，保证对称。

            Debug.WriteLine($"[Settings] 页面 '{item.PageKey}' Attach 失败：{ex}");
        }

        currentPage = page;
        currentPageKey = item.PageKey;
        lastPageKey = item.PageKey;
        PageTitleText.Text = item.Title;
        SyncNavigationSelection(item.PageKey);
    }

    /// <summary>
    /// 释放当前页：先脱离视觉树，再 Detach（退订/取消异步），最后 Dispose（如果页面实现了）。

    /// <para>
    /// 这里是「关闭=隐藏 + 卸下内容」的唯一实现点。WinUI 的 Frame 导航缓存、ContentControl 的
    /// 旧 Content 都不会自动替我们做这些，因此外壳丢掉页面实例，重开时由工厂重建。

    /// </para>
    /// </summary>
    private void UnloadCurrentPage()
    {
        var page = currentPage;
        currentPage = null;
        currentPageKey = null;

        PageHost.Content = null;

        if (page is null) return;

        try
        {
            page.Detach();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 页面 Detach 失败：{ex}");
        }

        if (page is IDisposable disposable)
        {
            try
            {
                disposable.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Settings] 页面 Dispose 失败：{ex}");
            }
        }
    }

    private void SyncNavigationSelection(string pageKey)
    {
        // 加载前不设置 SelectedItem：项数一多，NavigationView 在首次布局时会把预设置的选中项
        // 重新解析成别的项（实测会跳到某个分类页并触发一次导航）。改为 Loaded 之后再钉一次。

        if (!navigationLoaded) return;

        if (!navItems.TryGetValue(pageKey, out var navItem)) return;
        if (ReferenceEquals(NavRoot.SelectedItem, navItem)) return;

        suppressSelectionChanged = true;
        try
        {
            NavRoot.SelectedItem = navItem;
        }
        finally
        {
            suppressSelectionChanged = false;
        }
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (suppressSelectionChanged) return;
        if (!navigationLoaded) return;
        if (args.SelectedItemContainer?.Tag is not string pageKey) return;
        if (catalog.Find(pageKey) is not { } item) return;

        NavigateTo(item);
    }

    /// <summary>
    /// 首次显示后按 XamlRoot 的真实缩放，把窗口物理尺寸纠正到 1000×650 DIP。

    /// <para>
    /// 窗口在构造时还没落到显示器上，GetDpiForWindow 返回 96；而 WinUI 的 XAML 岛会按真实缩放
    /// （例如 125%）排版。只按 96 设一次尺寸，窗口就比内容小一圈，右边和底部被裁掉。

    /// 这里以 XamlRoot.RasterizationScale 为准（那才是 WinUI 排版用的缩放）补一次；
    /// 成功一次后就不再干预用户自己摆好的窗口。

    /// </para>
    /// </summary>
    private void CorrectInitialSizeOnce()
    {
        if (WindowSurface is null) return;

        try
        {
            var scale = WindowSurface.XamlRoot?.RasterizationScale ?? 0;
            if (scale <= 0) return; // XamlRoot 还没就绪，等 Changed/兜底定时器再来

            // XamlRoot 在窗口还没真正落到显示器上时会短暂报 1.0（96 DPI），此时按 1000×650
            // 算出的「目标」恰好等于窗口当前的物理客户区，一旦据此认定「已经校正过」，
            // 125% 缩放下窗口就永远小一圈（实测首帧 1014×687 而不是 1268×859）。

            // 取原生 DPI 与 XamlRoot 缩放的较大值，并在缩放真正稳定下来之前允许再次校正。

            var nativeScale = SettingsNativeWindow.GetDpi(this) / 96.0;
            if (nativeScale > scale) scale = nativeScale;

            if (initialSizeCorrected && Math.Abs(scale - lastCorrectionScale) < 0.01) return;

            var (width, height) = SettingsNativeWindow.GetClientSize(this);
            var targetWidth = (int)Math.Round(InitialWidthDip * scale);
            var targetHeight = (int)Math.Round(InitialHeightDip * scale);
            if (width == targetWidth && height == targetHeight)
            {
                initialSizeCorrected = true;
                lastCorrectionScale = scale;
                return;
            }

            sizingDpi = SettingsNativeWindow.ResizeClient(this, InitialWidthDip, InitialHeightDip, scale);
            SettingsNativeWindow.CenterOnWorkArea(this);
            initialSizeCorrected = true;
            lastCorrectionScale = scale;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 首次显示后的尺寸校正失败：{ex.Message}");
        }
    }

    /// <summary>首次显示后的一次性尺寸校正：立即一次 + XamlRoot 变化时再兜 + 1 秒兜底。</summary>
    private void ScheduleInitialSizeCorrection()
    {
        CorrectInitialSizeOnce();

        // XamlRoot 的缩放在窗口刚显示时会从 1.0 跳到真实值（125% 就是 1.25）。

        // 监听期间允许按新缩放再校正一次，兜底定时器到点后解除监听。

        if (WindowSurface?.XamlRoot is { } root)
        {
            watchedRoot = root;
            root.Changed += OnWatchedRootChanged;
        }

        var queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (queue is null) return;

        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CorrectInitialSizeOnce();
            // 1 秒兜底之后再改尺寸就是用户在摆窗口了：解除监听。

            if (watchedRoot is { } settled)
            {
                settled.Changed -= OnWatchedRootChanged;
                watchedRoot = null;
            }
        };
        timer.Start();
    }

    /// <summary>XamlRoot 缩放/尺寸变化：缩放真正稳定前允许再校正一次（见 CorrectInitialSizeOnce）。</summary>
    private void OnWatchedRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => CorrectInitialSizeOnce();

    /// <summary>
    /// 导航第一次加载完成：这时才把选中态钉到当前页面（加载前的设置会被重新解析成别的项）。

    /// 布局期的自动选择可能在本轮之后才落地，所以再排一个低优先级回调补一次。

    /// </summary>
    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        if (navigationLoaded) return;
        navigationLoaded = true;

        if (currentPageKey is { } key)
        {
            SyncNavigationSelection(key);
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    if (currentPageKey is { } latest) SyncNavigationSelection(latest);
                });
        }
    }

    private void ApplyInitialSize()
    {
        try
        {
            sizingDpi = SettingsNativeWindow.ResizeClient(this, InitialWidthDip, InitialHeightDip);
            // 只在创建时定位一次；之后窗口停在哪里由用户决定（与旧窗口 Manual 语义一致）。

            SettingsNativeWindow.CenterOnWorkArea(this);
        }
        catch (Exception ex)
        {
            // 尺寸调整失败不阻断打开：窗口用系统默认尺寸，仍然可用。

            Debug.WriteLine($"[Settings] 设置初始窗口尺寸失败：{ex.Message}");
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 真退出时放行：旧 Settings.OnClosing 也是以 AppShutdown.IsShuttingDown 为唯一放行条件。

        if (allowClose || DeskCanvas.Services.AppShutdown.IsShuttingDown) return;

        // 关闭按钮 = 隐藏：窗口是全软件共享的单例，真关掉就再也 show 不回来（旧 Settings.OnClosing 同一语义）。

        args.Cancel = true;
        HideAndUnload();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        // 走到这里说明是 CloseForShutdown 放行的真关闭（或平台强制关闭）。

        allowClose = true;
        hasShown = false;
        isShown = false;
        UnloadCurrentPage();

        try
        {
            AppWindow.Closing -= OnAppWindowClosing;
        }
        catch (Exception)
        {
            // 窗口销毁后 AppWindow 可能已不可用；此处失败无关紧要。

        }

        ClosedForGood?.Invoke(this, EventArgs.Empty);
    }
}