using System;
using System.Linq;
using DeskCanvas.Services;
using AvApplication = Avalonia.Application;
using AvLifetime = Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 在 WinUI UI 线程初始化 Avalonia 组件宿主。
/// <para>
/// 只调用 <c>SetupWithLifetime</c>，不调用 <c>Start()</c>/<c>StartWithClassicDesktopLifetime()</c>：
/// WinUI 的 <c>Application.Start</c> 已经提供了 Win32 消息循环，Avalonia 的 Dispatcher 工作项和
/// 定时器通过自己的消息窗口投递，由同一个循环泵出（官方 WinUIEmbedSample 也是如此）。
/// </para>
/// <para>
/// 保留 <see cref="AvLifetime"/> 而不是 SingleViewLifetime：旧代码大量通过
/// <c>IClassicDesktopStyleApplicationLifetime.Args/Windows/ShutdownMode</c> 工作，且
/// <c>OnExplicitShutdown</c> 是“隐藏设置后组件继续运行”的前提。
/// </para>
/// </summary>
internal static class AvaloniaHost
{
    private static AvLifetime? lifetime;

    public static AvLifetime? Lifetime => lifetime;

    public static void Initialize(string[] args)
    {
        if (lifetime is not null) return;

        var created = new AvLifetime
        {
            Args = args,
            // 不用 OnLastWindowClose：桌面组件全部隐藏时软件仍需驻留。

            ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown,
        };
        lifetime = created;

        // 这一步会同步触发 DeskCanvas.App.OnFrameworkInitializationCompleted（DI、组件、托盘、
        // 聚焦搜索都在里面启动）。不要在它之前解析 AvaloniaSwapChainPanel 类型。

        DeskCanvas.Program.BuildAvaloniaApp().SetupWithLifetime(created);

        AppHost.Services ??= DeskCanvas.App.Services;

        // 顺序关键：Setup 完成 Win32 平台初始化后立刻保存原拖放服务，再安装路由。

        // 此时还没有任何 AvaloniaSwapChainPanel，集成包尚未替换全局 IPlatformDragSource。

        DragSourceRouter.CaptureOriginal();
        DragSourceRouter.EnsureInstalled();
    }

    /// <summary>
    /// 依次关闭 Avalonia 顶层窗口，让组件的 Closing/数据保存/资源释放逻辑有机会执行。

    /// 不调用 lifetime.Shutdown()（它会连带关闭 Avalonia Dispatcher）。

    /// </summary>
    public static void CloseAvaloniaWindowsForExit()
    {
        if (AvApplication.Current?.ApplicationLifetime is not AvLifetime desktop)
            return;

        foreach (var window in desktop.Windows.ToArray())
        {
            try
            {
                window.Close();
            }
            catch (Exception ex)
            {
                AppStartup.LogDiagnostic(
                    $"closing Avalonia window {window.GetType().Name} failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
