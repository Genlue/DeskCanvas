using System;
using DeskCanvas.Services;
using DeskCanvas.WinUI.Host;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskCanvas.WinUI;

/// <summary>
/// WinUI 应用对象：只负责 UI 线程上的宿主初始化与退出，不搬 Avalonia 的启动编排。/// <para>
/// 正常启动不创建任。WinUI 窗口（设置窗口由设置实现方经 <see cref="SettingsSurfaceRegistry"/>
/// 提供）；<c>--winui-diagnostic</c> 才显示诊断窗口。/// </para>
/// </summary>
public partial class App : Application
{
    private MainWindow? diagnosticWindow;
    private DispatcherQueueTimer? smokeTimer;
    private bool diagnosticMode;

    internal static App? Instance { get; private set; }

    public App()
    {
        InitializeComponent();
        Instance = this;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        diagnosticMode = Program.Options.Diagnostic;
        AppHost.UiQueue = DispatcherQueue.GetForCurrentThread();

        // 。Avalonia 初始化之前注。UI 接缝：DI 建好。App 会调用工厂创。WinUiSoftwareUi。
        // 于是二启。--settings/无组件自动打开设置都能转发。WinUI。
        global::DeskCanvas.App.SoftwareUiFactory = services => new WinUiSoftwareUi(services);

        try
        {
            if (diagnosticMode)
            {
                diagnosticWindow = new MainWindow();
                diagnosticWindow.SetStage("appstartup");
                diagnosticWindow.Activate();
            }

            // 内置组件程序集必须在。DI 容器（AssemblyProvider 构造时缓存组件清单）之。
            // 进入 Default 加载上下文，否则反射加载会再开一。PluginLoadContext。
            // 同一。Notes/Clock 出现两个程序集实例（报告 §3 第一。5）。
            DeskCanvas.WinUI.WidgetEditors.WidgetAssemblyRegistry.Preload();
            ReportStartupStage("widgets-preloaded");

            AvaloniaHost.Initialize(Program.StartupArgs);

            AppHost.Services ??= global::DeskCanvas.App.Services;
            ReportStartupStage("ui-ready");

            // --settings=<pageKey>：直达某一页（页面开。冒烟用）。旧代码只认。--settings。
            // 那条路径仍会打开默认页；这里补一次定向导航。
            if (Program.Options.SettingsPageKey is { Length: > 0 } pageKey)
            {
                global::DeskCanvas.App.SoftwareUi?.OpenSettings(pageKey);
            }

            // 开。冒烟专用。-edit-widget[=Type/SubType] 直接打开一个组件的 WinUI 编辑窗口。
            // 正常启动不经过这里（命令行没有该开关时整段是空操作）。
            TryOpenDeveloperEditor();

            ScheduleSmokeExit();
        }
        catch (Exception ex)
        {
            SmokeLog.Stage("exiting");
            CrashLog.Write(ex);
            try
            {
                diagnosticWindow?.Close();
            }
            catch
            {
                // 已经在失败路径上，关闭诊断窗口的二次异常没有意义。
            }

            // 启动失败不能停在“无窗口但消息循环还在”的状态：用非零退出码让脚。用户看见。
            Environment.Exit(1);
        }
    }

    /// <summary>
    /// 启动阶段观察者：冒烟阶段日志 + 诊断窗口文本。由 <c>DeskCanvas.App</c> 。DI/组件阶段回调。
    /// </summary>
    internal static void ReportStartupStage(string stage)
    {
        SmokeLog.Stage(stage);
        Instance?.UpdateDiagnosticStage(stage);
    }

    /// <summary>ExitNow 的第一步：先关 WinUI 自己的窗口（设置窗口由实现方在后续步统一登记）。/summary>
    internal static void CloseHostWindows()
    {
        Instance?.CloseDiagnosticWindow();

        // 编辑窗口在关闭时保存几何。Detach 页面；退出路径也要经过同一个关闭流程，
        // 否则最后一次打开的窗口位置不会被记住。
        try
        {
            WidgetEditorHost.CloseAllForShutdown();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"closing widget editor windows failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 开。冒烟开关：。<c>--edit-widget=Type/SubType</c> 找到存活组件并打开它的编辑窗口。
    /// 只用于验证编辑窗口（截图、存档写入、几何记忆），产品入口仍是组件右键菜单。
    /// </summary>
    private static void TryOpenDeveloperEditor()
    {
        if (!Program.Options.EditWidgetRequested) return;

        try
        {
            string? type = null;
            string? subType = null;
            var target = Program.Options.EditWidgetTarget;
            if (!string.IsNullOrWhiteSpace(target))
            {
                var parts = target.Split('/', 2, StringSplitOptions.TrimEntries);
                type = parts[0];
                if (parts.Length > 1) subType = parts[1];
            }

            var factory = AppHost.Services?.GetService(typeof(WidgetFactory)) as WidgetFactory;
            var widget = factory?.FindLiveWidget(type, subType);
            if (widget is null)
            {
                AppStartup.LogDiagnostic(
                    $"--edit-widget: no live editable widget matched '{Program.Options.EditWidgetTarget ?? "(first)"}'.");
                return;
            }

            widget.EditWidget();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"--edit-widget: opening the editor failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void UpdateDiagnosticStage(string stage)
    {
        if (!diagnosticMode) return;

        try
        {
            diagnosticWindow?.SetStage(stage);
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"diagnostic window update failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void CloseDiagnosticWindow()
    {
        var window = diagnosticWindow;
        diagnosticWindow = null;
        if (window is null) return;

        try
        {
            window.Close();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"diagnostic window close failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// <c>--smoke=N</c>：ui-ready 之后 N 毫秒走统一退出。计时器挂在 WinUI 队列上，
    /// 不依。Avalonia Dispatcher；即使组件全部隐藏也一定会触发。
    /// </summary>
    private void ScheduleSmokeExit()
    {
        var milliseconds = Program.Options.SmokeMilliseconds;
        if (milliseconds <= 0) return;

        var queue = AppHost.UiQueue;
        if (queue is null) return;

        smokeTimer = queue.CreateTimer();
        smokeTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        smokeTimer.IsRepeating = false;
        smokeTimer.Tick += (_, _) =>
        {
            smokeTimer?.Stop();
            HostShutdown.RequestExit();
        };
        smokeTimer.Start();
    }
}