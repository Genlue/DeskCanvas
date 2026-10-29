using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace DeskCanvas.Services;

/// <summary>
/// The one place that asks the application to exit.
/// <para>
/// The settings window is a reusable singleton that hides instead of closing (a closed Avalonia
/// window can never be shown again), so every real exit has to be announced here first —
/// <see cref="Views.Settings"/> lets its close through only while <see cref="IsShuttingDown"/> is
/// set, and otherwise merely hides itself.
/// </para>
/// <para>
/// 迁移期间这里是旧世界与新入口的唯一出口：WinUI 宿主注册了 <see cref="ISoftwareUi"/> 时，
/// 退出交给它做协调关闭（关 WinUI 窗口、关 Avalonia 组件窗口、结束 WinUI 消息循环），
/// 不再由旧代码直接调用 Avalonia lifetime 的 Shutdown。旧入口/旧测试没有注册接缝时保持原路径。
/// </para>
/// </summary>
public static class AppShutdown
{
    /// <summary>True once an exit has been requested; windows may really close from now on.</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>Close every window and terminate the application.</summary>
    public static void Request()
    {
        IsShuttingDown = true;

        // The maintenance timer must not fire while the windows are being torn down: it would
        // schedule a compacting collection onto the thread pool in the middle of teardown, for a
        // process that is about to exit.
        MemoryTrimmerService.StopPeriodicRelease();

        // WinUI 入口：由新宿主按“先关原生窗口、再结束消息循环”的顺序退出。这里不再碰
        // ClassicDesktopStyleApplicationLifetime，避免两个框架各自关一次 Dispatcher。
        if (App.SoftwareUi is { } softwareUi)
        {
            try
            {
                softwareUi.RequestExit();
            }
            catch (Exception ex)
            {
                GlassDiagnostics.Event($"software UI exit failed: {ex.GetType().Name}: {ex.Message}");
            }

            return;
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
