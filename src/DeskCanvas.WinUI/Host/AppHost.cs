using System;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// 引导完成后的进程级入口：拿到原宿主构建的 DI 容器与 WinUI 的 UI 队列。
/// <para>
/// 这是新 GUI 各子系统（设置窗口、编辑窗口、面板、菜单宿主、聚焦搜索）与旧服务之间的
/// 唯一接缝：子系统只依赖 <see cref="IServiceProvider"/>，不互相直接 new 对方的窗口。
/// </para>
/// </summary>
public static class AppHost
{
    /// <summary>原宿主在 WinUI UI 线程构建的 DI 容器；引导完成前为 null。</summary>
    public static IServiceProvider? Services { get; internal set; }

    /// <summary>WinUI 的 UI 线程调度队列（创建窗口、更新视觉树都必须在这里）。</summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue? UiQueue { get; internal set; }

    public static bool IsReady => Services is not null;

    /// <summary>把动作切到 UI 线程；已在 UI 线程时同步执行（调用方常来自组件线程）。</summary>
    public static bool RunOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var queue = UiQueue;
        if (queue is null)
        {
            return false;
        }

        if (queue.HasThreadAccess)
        {
            action();
            return true;
        }

        return queue.TryEnqueue(() => action());
    }
}
