using System;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using Microsoft.UI.Xaml;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// 组件编辑页的最小契约（37 页执行者照这个写；窗口只认这个接口）。/// <para>
/// <b>生命周期</b>：窗口创建页。。。<see cref="View"/> 放进唯一的滚动容。。调一。/// <see cref="Attach"/>；窗口关闭时先调 <see cref="Detach"/> 再丢引用。Detach 必须幂等。/// 页面。Attach 里订阅的事件/计时。异步要在这里对称释放（报。§4.2）。/// </para>
/// <para>
/// <b>保存语义</b>：各页保持原来的提交点——立即保存的继续在设置项变化时保存，
/// 完成编辑才保存的（滑。颜色）只在完成时保存一次。初始化只读模型，不写盘。/// 保存抛异常时调用 <see cref="WidgetEditorContext.ReportSaveFailure"/>，由窗口显示错误。/// 不要吞掉失败或关窗伪装成功。/// </para>
/// </summary>
internal interface IWidgetEditorPage
{
    /// <summary>页面根元素；一个实例只能挂一次。/summary>
    UIElement View { get; }

    /// <summary>进入可见状态：在这里读模型、订阅需要跟随的外部变化。/summary>
    void Attach();

    /// <summary>离开可见状态：退订、取消请求。必须幂等。/summary>
    void Detach();
}

/// <summary>
/// 编辑页上下文：只暴露页面真正需要的东西（DI 容器、组件自己的 Provider、请求信息、窗口回调）。/// 页面不自己去 new 窗口/服务。/// </summary>
internal sealed class WidgetEditorContext
{
    private readonly EditWidgetWindow window;

    public WidgetEditorContext(
        IServiceProvider services,
        IWidgetLayoutProvider layoutProvider,
        WidgetEditorRequest request,
        EditWidgetWindow window)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        LayoutProvider = layoutProvider ?? throw new ArgumentNullException(nameof(layoutProvider));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        this.window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>原宿。DI 容器（组件模。Store/Service 都从这里取）。/summary>
    public IServiceProvider Services { get; }

    /// <summary>
    /// 组件<b>自己</b>的布局 Provider（与桌面组件是同一个实例）：页面按原页面一。
    /// <c>Get()/Save()</c>，保存后组件立即刷新，存档仍是原 layout.json + Settings JSON。
    /// </summary>
    public IWidgetLayoutProvider LayoutProvider { get; }

    /// <summary>本次编辑的目标描述（InstanceId/Generation/类型/原设置页身份）。/summary>
    public WidgetEditorRequest Request { get; }

    /// <summary>按需取服务；未注册返。null（页面应降级显示，而不是抛）。/summary>
    public T? GetService<T>() where T : class => Services.GetService(typeof(T)) as T;

    /// <summary>取必需服务；未注册时抛出带目标信息的明确异常。/summary>
    public T GetRequiredService<T>() where T : class
        => GetService<T>() ?? throw new InvalidOperationException(
            $"Editor page for '{Request.AssemblyName}/{Request.SubType}' requires service " +
            $"'{typeof(T).FullName}', but it is not registered.");

    /// <summary>把这次保存失败显示在窗口顶部。InfoBar 并写诊断日志（不是静默失败）。/summary>
    public void ReportSaveFailure(Exception exception)
        => ReportSaveFailure(exception?.Message ?? string.Empty);

    /// <summary>把失败信息显示在窗口顶部（保存抛异常/数据写回被拒绝时用）。/summary>
    public void ReportSaveFailure(string message)
    {
        window.ShowSaveError(message);
    }
}
