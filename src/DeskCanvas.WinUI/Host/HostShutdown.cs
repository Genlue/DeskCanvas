using System;
using System.Threading;
using DeskCanvas.Services;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 退出路径的协调者（本步的窄版本，第六步会扩展为完整生命周期协调器）。
/// <para>
/// 顺序：先让 <see cref="DeskCanvas.Services.AppShutdown"/> 置位“正在退出”（设置窗口的关闭
/// 处理器据此允许真正关闭），再关闭 WinUI 自己的窗口、关闭 Avalonia 组件窗口，最后结束
/// WinUI 消息循环。刻意不调用 Avalonia lifetime 的 <c>Shutdown()</c>，也不调用 Avalonia 的
/// <c>Dispatcher.Shutdown()</c>——两个 Dispatcher 各关一次会让组件线程停在半拆除状态。
/// </para>
/// </summary>
internal static class HostShutdown
{
    private static int requested;
    private static int exiting;

    /// <summary>外部（冒烟定时器、托盘/菜单转发）发起退出。</summary>
    public static void RequestExit()
    {
        if (!AppHost.RunOnUiThread(RequestExitOnUiThread))
            Environment.Exit(0);
    }

    private static void RequestExitOnUiThread()
    {
        if (Interlocked.Exchange(ref requested, 1) != 0) return;

        try
        {
            // 走唯一的退出公告点：置 IsShuttingDown、停维护定时器，并经软件 UI 接缝回到 ExitNow。

            DeskCanvas.Services.AppShutdown.Request();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"coordinated exit failed: {ex.GetType().Name}: {ex.Message}");
        }

        // 接缝未注册/退出路由失败时的兜底：保证 WinUI 消息循环一定结束，不留下无窗口进程。

        ExitNow();
    }

    /// <summary>
    /// 真正的拆除：WinUI 窗口 → Avalonia 组件/settings 窗口 → WinUI 消息循环。

    /// 可重入（<c>AppShutdown.Request</c> 会同步回调到这里）。

    /// </summary>
    public static void ExitNow()
    {
        if (Interlocked.Exchange(ref exiting, 1) != 0) return;

        SmokeLog.Stage("exiting");

        try
        {
            App.CloseHostWindows();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"closing WinUI windows failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            AvaloniaHost.CloseAvaloniaWindowsForExit();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"closing Avalonia windows failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            var application = Microsoft.UI.Xaml.Application.Current;
            if (application is not null)
            {
                application.Exit();
                return;
            }
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"WinUI Application.Exit failed: {ex.GetType().Name}: {ex.Message}");
        }

        Environment.Exit(0);
    }
}
