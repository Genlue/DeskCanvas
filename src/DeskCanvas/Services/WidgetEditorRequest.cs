using System;

namespace DeskCanvas.Services;

/// <summary>
/// 一次“打开组件编辑器”请求的结果。纯数据、不依赖任何 UI 框架，调用方（组件右键菜单、
/// 快捷指令、诊断开关）可以记录这个结果，新入口也可以据此提示失败原因（报告 §3 第三步）。
/// </summary>
public enum WidgetEditorOpenResult
{
    /// <summary>编辑窗口已经打开（或把同一组件的已开窗口提到前台）。</summary>
    Started = 0,

    /// <summary>
    /// 目标已不存在：<c>InstanceId</c>/<c>Generation</c> 与存活组件不一致（组件被删除、方案切换、
    /// 组件被重建）。调用方应丢弃这次操作，不得再写回旧对象（报告 §4.2）。
    /// </summary>
    TargetMissing = 1,

    /// <summary>
    /// 内置组件的设置页还没有迁移到 WinUI：窗口会显示明确的“未迁移”页，
    /// <b>不会</b>回退去打开旧 Avalonia 设置页（报告 §3 第三步）。
    /// </summary>
    NotMigrated = 2,

    /// <summary>
    /// 外部插件只声明了 Avalonia 设置页，WinUI 无法自动转换：窗口显示“编辑器需适配”说明，
    /// 插件仍可继续加载与运行（已知限制 R5）。
    /// </summary>
    PluginNeedsAdapter = 3,

    /// <summary>新 GUI 还没有就绪（WinUI 队列/DI 尚未建立），请求被忽略。</summary>
    Unavailable = 4,

    /// <summary>请求已投递到 WinUI UI 线程（调用方不在该线程），结果不在此返回。</summary>
    Deferred = 5,

    /// <summary>打开过程中抛异常；详情已写入诊断日志。</summary>
    Failed = 6,
}

/// <summary>
/// 组件缩放弹窗请求的结果。缩放弹窗属于迁移第四步（报告 §3 第四步的 ScaleDialog）；
/// 在那之前实现方必须返回 <see cref="NotImplemented"/>，而不是静默什么都不做。
/// </summary>
public enum WidgetScaleRequestResult
{
    /// <summary>缩放弹窗已显示并接管后续应用（第四步实现之后）。</summary>
    Shown = 0,

    /// <summary>新 GUI 尚未实现缩放弹窗——调用方必须把这次请求当作失败（当前状态）。</summary>
    NotImplemented = 1,

    /// <summary>目标已不存在（InstanceId/Generation 失效）。</summary>
    TargetMissing = 2,

    /// <summary>新 GUI 还没有就绪，请求被忽略。</summary>
    Unavailable = 3,

    /// <summary>打开过程中抛异常；详情已写入诊断日志。</summary>
    Failed = 4,
}

/// <summary>
/// 打开组件编辑器的纯数据请求（报告 §3 第三步）：<b>只</b>携带标识与身份信息，
/// 不包含 <c>UserControl</c>/<c>Window</c>/Provider 之类对象。
/// <para>
/// 为什么不是把 <c>Func&lt;EditWidget&gt;</c> 交给 <c>Widget</c>：新 GUI 的编辑窗口在另一个框架里，
/// 旧窗口类型不该出现在生产调用链上。这里给出“目标是谁”，由 <see cref="ISoftwareUi"/> 的实现
/// 按 <see cref="InstanceId"/>/<see cref="Generation"/> 重新确认目标仍然存在，再决定打开哪个页面。
/// </para>
/// </summary>
/// <param name="InstanceId">组件的稳定身份（<see cref="DeskCanvas.Core.Models.WidgetLayout.InstanceId"/>）。</param>
/// <param name="Generation">创建请求时的会话代数；与存活组件不一致时拒绝打开。</param>
/// <param name="AssemblyName">组件程序集名（<c>layout.json</c> 的 <c>Type</c>，例如 <c>Clock</c>）。</param>
/// <param name="SubType">组件类型名（<c>layout.json</c> 的 <c>SubType</c>，例如 <c>Digital</c>）。</param>
/// <param name="ScreenId">请求发出时组件所属的屏幕配置 id（仅用于诊断；打开前以存活组件为准）。</param>
/// <param name="SettingsViewAssemblyName"><c>WidgetInfoAttribute.EditModelViewType</c> 的程序集名。</param>
/// <param name="SettingsViewTypeName"><c>WidgetInfoAttribute.EditModelViewType</c> 的完整类型名。</param>
/// <param name="SettingsViewKey">
/// 旧 <see cref="WindowGeometryStore"/> 的槽位键 <c>"assembly:settingsViewFullName"</c>。
/// 由创建请求的一方按原始类型算好，新窗口直接使用，因此不会因为新程序集/类名变化丢记忆。
/// </param>
public sealed record WidgetEditorRequest(
    Guid InstanceId,
    long Generation,
    string AssemblyName,
    string SubType,
    string ScreenId,
    string? SettingsViewAssemblyName,
    string? SettingsViewTypeName,
    string SettingsViewKey);

/// <summary>组件缩放弹窗的纯数据请求（报告 §3 第四步 ScaleDialog 的接缝）。</summary>
/// <param name="InstanceId">组件稳定身份。</param>
/// <param name="Generation">请求发出时的会话代数。</param>
/// <param name="AssemblyName">组件程序集名。</param>
/// <param name="SubType">组件类型名。</param>
/// <param name="CurrentScale">当前生效的内容缩放（null = 跟随屏幕默认/1.0）。</param>
public sealed record WidgetScaleRequest(
    Guid InstanceId,
    long Generation,
    string AssemblyName,
    string SubType,
    double? CurrentScale);

/// <summary>
/// <c>Widget</c> 持有的“可编辑能力 + 打开编辑器委托”（报告 §3 第三步的宿主调用链）。
/// <para>
/// <b>契约</b>：
/// </para>
/// <list type="number">
/// <item><description>存在 <see cref="ISoftwareUi"/>（新入口）时，请求一律转发给它；即使它返回
/// <see cref="WidgetEditorOpenResult.Unavailable"/>/<see cref="WidgetEditorOpenResult.Failed"/>，
/// 也<b>不回退</b>旧 Avalonia 编辑窗口——生产路径不得再创建旧窗口。</description></item>
/// <item><description>没有 <see cref="ISoftwareUi"/>（旧入口、检查工程）时才走
/// <paramref name="legacyOpen"/>：保持原来的 <c>EditWidget</c> 行为。</description></item>
/// <item><description><see cref="Request"/> 只含标识信息；目标的存活校验由实现方在打开前重做
/// （报告 §4.2：失效对象不允许再接收写入）。</description></item>
/// </list>
/// </summary>
public sealed class WidgetEditorBinding
{
    private readonly Func<WidgetEditorOpenResult>? legacyOpen;

    public WidgetEditorBinding(WidgetEditorRequest request, Func<WidgetEditorOpenResult>? legacyOpen = null)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        this.legacyOpen = legacyOpen;
    }

    /// <summary>可编辑能力的请求描述（只含标识，不含控件）。</summary>
    public WidgetEditorRequest Request { get; }

    /// <summary>
    /// 打开编辑器。新入口存在时转发 <see cref="ISoftwareUi.OpenWidgetEditor"/>，
    /// 否则调用旧 Avalonia 编辑窗口的构造委托；失败只记录，不抛给菜单命令。
    /// </summary>
    public WidgetEditorOpenResult Open()
    {
        if (App.SoftwareUi is { } softwareUi)
        {
            try
            {
                return softwareUi.OpenWidgetEditor(Request);
            }
            catch (Exception ex)
            {
                AppStartup.LogDiagnostic(
                    $"opening the WinUI editor for {Request.AssemblyName}/{Request.SubType} failed: " +
                    $"{ex.GetType().Name}: {ex.Message}");
                return WidgetEditorOpenResult.Failed;
            }
        }

        if (legacyOpen is null) return WidgetEditorOpenResult.Unavailable;

        try
        {
            return legacyOpen();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic(
                $"opening the legacy editor for {Request.AssemblyName}/{Request.SubType} failed: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return WidgetEditorOpenResult.Failed;
        }
    }
}
