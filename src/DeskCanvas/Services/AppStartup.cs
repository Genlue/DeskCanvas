using System;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Services;

/// <summary>
/// UI 之前的启动步骤，从 <see cref="Program.Main"/> 抽出，供旧 Avalonia 入口与新 WinUI 入口共用。
/// <para>
/// 顺序与行为必须和原 <c>Program.Main</c> 完全一致：先拿单实例（拿不到就通知已运行实例并退出），
/// 再准备默认数据/组件包，最后同步自启动项。这里不创建任何窗口，也不碰 Avalonia/WinUI 运行时——
/// 两个入口在 UI 框架初始化之前各调用一次。
/// </para>
/// </summary>
public static class AppStartup
{
    /// <summary>
    /// 取得本进程的单实例所有权。
    /// <para>
    /// 返回 <c>null</c> 表示已有实例在运行：本进程已经向它发出“显示设置”的信号，调用方应直接退出。
    /// 成功时把实例放到 <see cref="SingleInstance.Current"/>，调用方负责在退出时 Dispose。
    /// </para>
    /// </summary>
    public static SingleInstance? AcquireSingleInstance()
    {
        var singleInstance = SingleInstance.Acquire();
        if (singleInstance == null)
        {
            SingleInstance.Signal();
            return null;
        }

        SingleInstance.Current = singleInstance;
        return singleInstance;
    }

    /// <summary>
    /// 默认数据/组件包初始化与自启动同步。必须在 UI 初始化之前调用，失败时由调用方写崩溃日志。
    /// </summary>
    public static void InitializeDataAndStartup()
    {
        // Single-file mode: extract the embedded widget bundle & default settings first,
        // so the rest of the app sees a regular data folder. No-op in portable mode.
        WidgetBundle.ExtractIfNeeded();

        // Keep the Windows auto-start entry in sync with the saved preference so the
        // feature reliably takes effect (re-asserts the path each launch; safe when off).
        SyncRunOnStartup();
    }

    /// <summary>
    /// 把新入口的诊断写进宿主的 <c>GlassDiagnostics</c>。该类仍是宿主内部类型，
    /// 新 GUI 不因日志而反向引用 Avalonia 类型，所以在这里留一个公开的窄转发。
    /// </summary>
    public static void LogDiagnostic(string message) => GlassDiagnostics.Event(message);

    /// <summary>
    /// Re-asserts the Windows auto-start registry entry when the user has enabled it.
    /// This is best-effort and never blocks app startup; it fixes a stale or missing
    /// entry (e.g. after the exe was moved) so "Run on startup" reliably takes effect.
    /// </summary>
    private static void SyncRunOnStartup()
    {
        try
        {
            if (new AppSettingsProvider().Get().RunOnStartup)
                new StartupService().SetRunOnStartup(true);
        }
        catch
        {
            // Auto-start is non-critical; ignore any failure here.
        }
    }
}
