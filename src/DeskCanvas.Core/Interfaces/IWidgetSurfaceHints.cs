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
}
