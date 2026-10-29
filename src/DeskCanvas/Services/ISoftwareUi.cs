using System;

namespace DeskCanvas.Services;

/// <summary>
/// 软件 GUI 的窄接口：由 WinUI 入口实现并交给原服务容器，组件与旧代码继续调用原入口，
/// 旧入口立即转发到这里。接口里不出现 WinUI/Avalonia 控件类型（报告 §3 第一步）。
/// <para>
/// 迁移期间的本意是“只有一处替换”：保留的 Avalonia 组件、托盘、聚焦搜索服务都不反向
/// 依赖 <c>DeskCanvas.WinUI</c>；新 GUI 也不把 WinUI 类型漏进组件程序集。
/// </para>
/// <para>
/// 后续阶段会把能力逐项加上（<c>OpenPanel</c>、<c>OpenWidgetMenu</c>、<c>Confirm</c>/<c>Input</c>、
/// <c>ShowUpdate</c>；组件编辑窗口与缩放请求的接缝已在第三步加入）。接口只做加法：新增成员要同时给出
/// 纯数据请求类型，并由实现方按阶段补齐，不允许为了编译通过塞占位实现。
/// </para>
/// </summary>
public interface ISoftwareUi
{
    /// <summary>实现的 GUI 是否已经可以接收请求（Avalonia 与 WinUI 都初始化完成之后才为 true）。</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// 显示并激活共享设置窗口，落到 <paramref name="pageKey"/> 指定的页面
    /// （见 <see cref="SettingsPageKeys"/>）。<c>null</c> 表示恢复上次页面。
    /// </summary>
    void OpenSettings(string? pageKey = null);

    /// <summary>二次启动接管、托盘点击、组件菜单“软件设置”共用的无参唤出入口。</summary>
    void ActivateSettings();

    /// <summary>
    /// 统一退出请求。迁移期间旧路径是 <c>AppShutdown.Request()</c>。
    /// 实现方在关闭原生面板/搜索/编辑器、释放订阅与租约之后再关闭组件与 WinUI 消息循环，
    /// 不能依次调用两个框架各自的 Dispatcher Shutdown（报告 §6.3）。
    /// </summary>
    void RequestExit();

    /// <summary>
    /// 打开组件的编辑窗口（报告 §3 第三步）。`request` 只含标识信息，没有任何控件/窗口类型。
    /// <para><b>实现方契约</b></para>
    /// <list type="number">
    /// <item><description>打开前必须<b>重新确认目标仍存在</b>：按 `InstanceId` 找到存活组件，并核对
    /// `Generation`、程序集名与 SubType；不一致返回 `TargetMissing` 并记录，绝不对失效对象打开编辑页。</description></item>
    /// <item><description>编辑上下文必须复用该组件自己的 `IWidgetLayoutProvider`（同一实例），
    /// 这样页面保存后桌面组件立即刷新，存档仍是原模型与 JSON（报告 §4.2）。</description></item>
    /// <item><description>内置组件页面尚未迁移时显示明确的“未迁移”页并返回 `NotMigrated`；外部插件返回
    /// `PluginNeedsAdapter`。<b>任何情况都不得回退旧的 Avalonia 编辑窗口。</b></description></item>
    /// <item><description>页面初始化只读取模型，不写盘；保存语义按各页原有提交点
    /// （立即保存 / 完成编辑保存）执行。</description></item>
    /// <item><description>实现方不在 UI 线程时可以把请求排队并返回 `Deferred`。</description></item>
    /// </list>
    /// </summary>
    WidgetEditorOpenResult OpenWidgetEditor(WidgetEditorRequest request);

    /// <summary>
    /// 打开组件缩放弹窗（报告 §3 第四步的 `ScaleDialog`）。
    /// 第四步实现之前，实现方必须返回 `WidgetScaleRequestResult.NotImplemented` 并记录，
    /// 调用方据此把这次操作当作失败——不允许“静默什么都不做”。
    /// </summary>
    WidgetScaleRequestResult OpenWidgetScale(WidgetScaleRequest request);
}

/// <summary>
/// 设置页面的稳定键。字符串而不是 <c>Type</c>：WinUI 侧的页面类型与旧 Avalonia 页面类型不同，
/// 旧代码里按 <c>typeof(General)</c> 定位的地方改用这里的键（报告 §3 第二步）。
/// </summary>
public static class SettingsPageKeys
{
    public const string General = "General";
    public const string Appearance = "Appearance";
    public const string Spotlight = "Spotlight";
    public const string MultiScreen = "MultiScreen";
    public const string Advanced = "Advanced";
    public const string Profiles = "Profiles";
    public const string About = "About";
    public const string Gallery = "Gallery";

    /// <summary>组件库的分类/“其他扩展”页：<c>Gallery:&lt;assemblyName&gt;</c>（空表示全部分类）。</summary>
    public const string GalleryPrefix = "Gallery:";
}
