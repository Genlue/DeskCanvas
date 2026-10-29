using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.WinUI.Host;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 设置窗口的宿主：实现 <see cref="ISettingsSurface"/>，负责「首个请求时才创建唯一窗口」、
/// 「复用与前台激活」、「隐藏/关闭」与「窗口失效后丢弃引用」。
/// <para>
/// 谁调用它：<c>SettingsSurfaceRegistry.Factory</c>。引导代码（S1 的 <c>WinUiSoftwareUi</c>）在
/// <c>OpenSettings/ActivateSettings</c> 里惰性取工厂并创建本类；组件、托盘、二次启动接管、
/// <c>--settings</c>、无组件自动打开，最终都只是往这里发一个页面键，不再直接 new 窗口
/// （报告 §3 第一步的窄接口 + §3 第二步的窗口保留语义）。
/// </para>
/// <para>
/// 所有窗口操作都会被切到 WinUI UI 线程（<see cref="AppHost.RunOnUiThread"/>）：调用方可能来自
/// 组件的 Avalonia 线程或二次启动的命名管道回调。
/// </para>
/// </summary>
public sealed class SettingsWindowHost : ISettingsSurface
{
    private readonly IServiceProvider services;

    private SettingsWindow? window;
    private SettingsPageCatalog? catalog;
    private SettingsPageFactory? pageFactory;

    public SettingsWindowHost(IServiceProvider services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));
    }

    /// <summary>
    /// 把构造方式注册进引导层的注册表。放在 <c>[ModuleInitializer]</c> 里：本程序集一加载就完成注册，
    /// 引导代码不需要引用 <see cref="SettingsWindowHost"/> 这个具体类型，S1/S2 可以并行开发。

    /// </summary>
    [ModuleInitializer]
    internal static void RegisterSettingsSurface()
    {
        SettingsSurfaceRegistry.Factory = static services => new SettingsWindowHost(services);
    }

    /// <inheritdoc />
    public bool IsOpen => window is { IsShown: true };

    /// <inheritdoc />
    public void Show(string? pageKey = null)
        => RunOnUiThread(() => EnsureWindow().ShowPage(pageKey));

    /// <inheritdoc />
    public void Activate() => Show(null);

    /// <inheritdoc />
    public void Hide() => RunOnUiThread(() => window?.HideAndUnload());

    /// <summary>
    /// 真正退出时由生命周期协调器调用：销毁窗口、不再拦截关闭。

    /// 不在 <see cref="ISettingsSurface"/> 里，因为接口只描述"显示/隐藏设置"，销毁属于退出流程。

    /// </summary>
    public void CloseForShutdown() => RunOnUiThread(() => window?.CloseForShutdown());

    private SettingsWindow EnsureWindow()
    {
        if (window is not null) return window;

        // 目录与工厂都惰性构建：第一次真正打开设置时才去枚举 Widgets 目录（有文件 IO 与反射），
        // 与旧实现一致（旧 SettingsViewModel 也在窗口构造时枚举）。之后复用同一份目录。

        catalog ??= SettingsPageCatalog.Build(services);
        pageFactory ??= new SettingsPageFactory(services);

        var created = new SettingsWindow(services, catalog, pageFactory);

        // 标题栏策略的输入来自旧设置（外观 → 标题栏风格）；本轮只有原生标题栏真正实现，
        // 请求红绿灯时窗口会降级为原生并在调试输出留痕（见 SettingsWindow.ApplyTitleBarMode）。

        created.ApplyTitleBarMode(ResolveRequestedTitleBarMode());

        created.ClosedForGood += OnWindowClosedForGood;
        window = created;
        return created;
    }

    /// <summary>
    /// 读取用户配置的标题栏风格。读不到（Provider 未注册/数据异常）时按旧配置的同一默认值
    /// <see cref="TitleBarStyle.Native"/> 处理——标题栏读不到不能连设置窗口一起打不开。

    /// </summary>
    private SettingsTitleBarMode ResolveRequestedTitleBarMode()
    {
        try
        {
            if (services.GetService(typeof(IAppSettingsProvider)) is IAppSettingsProvider provider
                && provider.Get().EffectiveTitleBarStyle == TitleBarStyle.TrafficLights)
            {
                return SettingsTitleBarMode.TrafficLights;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 读取标题栏风格失败，按原生标题栏处理：{ex.Message}");
        }

        return SettingsTitleBarMode.Native;
    }

    private void OnWindowClosedForGood(object? sender, EventArgs e)
    {
        if (sender is SettingsWindow closed)
        {
            closed.ClosedForGood -= OnWindowClosedForGood;
            if (ReferenceEquals(window, closed)) window = null;
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (!AppHost.IsReady)
        {
            Debug.WriteLine("[Settings] AppHost 尚未就绪，忽略设置窗口请求。");
            return;
        }

        if (!AppHost.RunOnUiThread(action))
        {
            Debug.WriteLine("[Settings] 无法切到 WinUI UI 线程，忽略设置窗口请求。");
        }
    }
}
