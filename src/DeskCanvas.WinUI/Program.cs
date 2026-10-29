using System;
using System.Threading;
using DeskCanvas.Services;
using DeskCanvas.WinUI.Host;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskCanvas.WinUI;

/// <summary>
/// WinUI 入口。启动顺序（报告 §3 第一步）：单实例 。默认数据/组件包初始化 。自启动同。。/// WinRT/WinUI 入口 。。WinUI UI 线程初始。Avalonia 。构建。DI/启动组件 。显示设置或只运行组件。/// <para>
/// 这里只做 UI 之前的宿主准备；Avalonia 初始化、DI、组件、托盘和聚焦搜索仍在
/// <c>DeskCanvas.App.OnFrameworkInitializationCompleted</c> 中，避免。300 行启动编排搬两遍。/// </para>
/// </summary>
public static class Program
{
    internal static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    internal static StartupOptions Options { get; private set; } = StartupOptions.Parse(null);

    [STAThread]
    public static void Main(string[] args)
    {
        StartupArgs = args;
        Options = StartupOptions.Parse(args);
        SmokeLog.Configure(Options);

        // --check-editors[=<path>]：只读覆盖率诊断。7 个组件编辑页里已实现多少）。
        // 放在单实。任何 UI 之前：诊断不创建窗口、不抢焦点，也不影响正在运行的实例。
        WidgetEditorCoverageReport.TryReportFromCommandLine();

        try
        {
            // 与旧入口同一套单实例语义：第二个进程只发信号后退出，不初始化任何 UI 框架。
            var singleInstance = AppStartup.AcquireSingleInstance();
            if (singleInstance == null)
                return;

            try
            {
                AppStartup.InitializeDataAndStartup();
                SmokeLog.Stage("appstartup");

                // 阶段观察者在 Avalonia 初始化之前挂上；App 实例创建后会自动转给诊断窗口。
                global::DeskCanvas.App.StartupStageReporter = App.ReportStartupStage;

                global::WinRT.ComWrappersSupport.InitializeComWrappers();
                Application.Start(_ =>
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
            }
            finally
            {
                SingleInstance.Current = null;
                singleInstance.Dispose();
            }
        }
        catch (Exception e)
        {
            SmokeLog.Stage("exiting");
            CrashLog.Write(e);
            throw;
        }
    }
}
