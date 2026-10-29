using System;
using DeskCanvas.Services;
using DeskCanvas.WinUI.WidgetEditors;

namespace DeskCanvas.WinUI.Host;

/// <summary>
/// <see cref="ISoftwareUi"/> 。WinUI 实现。/// <para>
/// 这一层刻意只认识 <see cref="ISettingsSurface"/>，不引用设置窗口的具体类型：设置窗口。/// 并行执行者实现并通过 <see cref="SettingsSurfaceRegistry"/> 注册。未注册、创建失败或调用
/// 抛异常时都只记录诊断并安全返回——自动打开设置是启动路径的一部分，不能因此崩掉进程。/// </para>
/// <para>
/// 所有窗口操作都切到 WinUI UI 线程（Avalonia 。WinUI 在本宿主共用同一线程，因此旧代码
/// 。Avalonia 回调进来时是同步执行的）。/// </para>
/// </summary>
internal sealed class WinUiSoftwareUi : ISoftwareUi
{
    private readonly IServiceProvider services;
    private ISettingsSurface? surface;

    public WinUiSoftwareUi(IServiceProvider services)
    {
        this.services = services ?? throw new ArgumentNullException(nameof(services));

        // 早期就暴露容器：设置窗口。Avalonia 初始化过程中被自动打开时也能解析服务。
        AppHost.Services ??= services;
    }

    public bool IsAvailable => AppHost.IsReady;

    public void OpenSettings(string? pageKey = null)
    {
        if (!AppHost.RunOnUiThread(() => WithSurface(current => current.Show(pageKey))))
            LogUnavailable("OpenSettings");
    }

    public void ActivateSettings()
    {
        if (!AppHost.RunOnUiThread(() => WithSurface(current => current.Activate())))
            LogUnavailable("ActivateSettings");
    }

    public void RequestExit()
    {
        // 退出同样必须发生在 UI 线程；没有队列（UI 还没建好）时直接结束进程。
        // 否则 RequestExit 会“成功返回但什么也没关”。
        if (!AppHost.RunOnUiThread(RequestExitOnUiThread))
            Environment.Exit(0);
    }

    public WidgetEditorOpenResult OpenWidgetEditor(WidgetEditorRequest request)
    {
        if (request is null) return WidgetEditorOpenResult.Failed;

        var queue = AppHost.UiQueue;
        if (queue is null || !AppHost.IsReady) return WidgetEditorOpenResult.Unavailable;

        // Avalonia 。WinUI 在本宿主共用同一。UI 线程：组件菜单点进来时是同步执行的，
        // 调用方拿得到真实结果；只有极端情况下才排队并返回 Deferred。
        if (queue.HasThreadAccess) return OpenEditorNow(request);

        queue.TryEnqueue(() => OpenEditorNow(request));
        return WidgetEditorOpenResult.Deferred;
    }

    private WidgetEditorOpenResult OpenEditorNow(WidgetEditorRequest request)
    {
        try
        {
            return WidgetEditorHost.Open(services, request);
        }
        catch (Exception ex)
        {
            // 页面/窗口构造失败不能把组件菜单整个带崩：结果返。Failed，详情进诊断日志。
            AppStartup.LogDiagnostic(
                $"opening the widget editor for {request.AssemblyName}/{request.SubType} failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return WidgetEditorOpenResult.Failed;
        }
    }

    public WidgetScaleRequestResult OpenWidgetScale(WidgetScaleRequest request)
    {
        // TODO(报告 §3 第四。S4.3)：WinUI 。ScaleDialog（小工具窗口，保。current/onApply。
        // 范围、实时应用与恢复继承值）。在实现之前这里必须是明确的失败结果，由调用方记录，
        // 不能静默什么都不做（Widget.OpenScaleDialog 已按同一语义处理）。
        AppStartup.LogDiagnostic(
            $"WinUI scale dialog is not implemented yet (migration step 4); request for " +
            $"{request?.AssemblyName}/{request?.SubType} ({request?.InstanceId}) returned NotImplemented.");
        return WidgetScaleRequestResult.NotImplemented;
    }

    private void RequestExitOnUiThread()
    {
        // 设置窗口是本进程最可能存在。WinUI 顶层：先让实现方销毁窗口（它的
        // CloseForShutdown 才会解除关闭拦截并真正释放页面），再让协调器关闭
        // 组件窗口并结束消息循环。失败时退。Hide，不阻止退出。
        CloseSettingsForExit();
        HostShutdown.ExitNow();
    }

    private void CloseSettingsForExit()
    {
        var current = surface;
        if (current is null) return;

        try
        {
            // 销毁属于退出流程，已提升到 ISettingsSurface（此前按方法名反射，改名即失效）。
            current.CloseForShutdown();
            return;
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"closing WinUI settings before exit failed: {ex.GetType().Name}: {ex.Message}");
        }

        try
        {
            current.Hide();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"hiding WinUI settings before exit failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void WithSurface(Action<ISettingsSurface> action)
    {
        var current = ResolveSurface();
        if (current is null) return;

        try
        {
            action(current);
        }
        catch (Exception ex)
        {
            // 窗口已经被销毁、页面工厂失败等都不该从这里冒泡到启。托盘路径。
            AppStartup.LogDiagnostic($"WinUI settings surface call failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private ISettingsSurface? ResolveSurface()
    {
        if (surface is not null) return surface;

        var factory = SettingsSurfaceRegistry.Factory;
        if (factory is null)
        {
            AppStartup.LogDiagnostic(
                "WinUI settings surface is not registered; the settings request was ignored (host keeps running).");
            return null;
        }

        try
        {
            surface = factory(services);
        }
        catch (Exception ex)
        {
            // 不缓存失败结果：设置窗口实现方可能稍后注。修复，下一次请求仍可重试。
            surface = null;
            AppStartup.LogDiagnostic(
                $"WinUI settings surface creation failed: {ex.GetType().Name}: {ex.Message}");
        }

        return surface;
    }

    private static void LogUnavailable(string operation)
    {
        AppStartup.LogDiagnostic($"WinUI software UI is not ready; {operation} was ignored.");
    }
}