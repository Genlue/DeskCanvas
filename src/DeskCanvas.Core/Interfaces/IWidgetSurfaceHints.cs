namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// 可选的外观提示协议（W10 §9.1）：组件视图用它向宿主声明自己的表面外观意图。
/// <para>
/// 本接口只返回数据（字符串键等），不引用任何 Avalonia 类型，Core 因此保持无 UI 依赖。
/// 它是<b>可选</b>接口：不在任何现有必需接口上新增成员，未实现的组件（包括旧版 DLL）
/// 自然走宿主的通用回退外观，不会产生 TypeLoad。
/// </para>
/// 宿主（Widget）在多彩（Colorful）模式下消费该键：从组件自己的资源上下文按明暗变体
/// 解析画刷，解析不到再退回全局 WidgetBackground。宿主不再按视图类型名（如
/// Forecast/ProgressView）分支判断外观。
/// </summary>
public interface IWidgetSurfaceHints
{
    /// <summary>
    /// 多彩（Colorful）模式下组件卡片背景的资源键，例如 <c>"WeatherCardBackground"</c>。
    /// 返回 <c>null</c> 或空串表示无专属外观（使用宿主通用回退）。
    /// 资源键必须指向 IBrush；键全局保留（旧插件 DLL 兼容），按变体提供明暗两份定义。
    /// </summary>
    string? ColorfulBackgroundResourceKey => null;

    /// <summary>
    /// 组件是否会在卡片圆角之外作画（例如启动器的图标悬停放大：图标以自身中心放大，
    /// 本来就会越过卡片边缘）。宿主默认把内容裁到卡片圆角内，这是「放大后被组件边缘切掉」
    /// 的唯一来源；声明为 <c>true</c> 的组件不裁，放大的图标可以画到卡片外的透明留白上。
    /// <para>
    /// 硬边界是组件窗口本身，而窗口 = 一个网格单元（<c>span × cell</c>）。所以放大的边界是
    /// <b>网格单元</b>，不是卡片：卡片只是单元按 <c>WidgetMargin</c> 内缩后的矩形，卡片与单元之间
    /// 的透明带、以及视图自己在卡片里留的内边距 / 居中余量，都属于单元内部，放大都可以用。超出
    /// 窗口的部分由操作系统裁掉（一条直边，比小一点的放大更难看），所以声明本提示的组件应当用
    /// <c>HoverZoomMath.ScaleWithin</c> 把倍率收进「图标静止边缘 → 单元边缘」这段<b>实测</b>距离内
    /// （<c>CardOverflowRoom.Around(icon)</c>），而不是假设它等于 <c>WidgetMargin</c>（图标比卡片
    /// 小时会把可用空间算小），更不是靠缩小静止尺寸来「塞进卡片」。
    /// </para>
    /// <para>
    /// 宿主侧还有一处容易漏掉的裁剪：<c>Widget.axaml</c> 的内容模板把视图包在一个
    /// <c>ContentControl</c> 里，它的 bounds 就是卡片矩形且默认 <c>ClipToBounds</c>——即使本提示
    /// 已经让宿主清掉了 <c>ContentPresenter.Clip</c>，它仍会在卡片边缘再切一刀。该包装必须保持
    /// <c>ClipToBounds="False"</c>，圆角裁剪只由 presenter 那一处负责。
    /// </para>
    /// <para>
    /// 毛玻璃（OS 亚克力）下无效：原生区域是 1 位掩码，只认卡片矩形，无法表达「多画一圈」。
    /// </para>
    /// </summary>
    bool AllowsCardOverflow => false;
}
