using Avalonia;
using Avalonia.Controls;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// 通用装饰的放置选择（W10 §9.2）。
/// </summary>
public enum WidgetOverlayPlacement
{
    /// <summary>卡片外侧右缘：落在组件网格 cell 的右边距里（历史上 Stack 分页点的常态位置）。</summary>
    OutsideCardRight,

    /// <summary>卡片内部右缘：盖在卡片右边缘内侧（原生 Acrylic 内移后的落点）。</summary>
    InsideCardRight,
}

/// <summary>
/// 装饰 overlay 的通用布局提示。只描述意图，摆放、命中与窗口 Region 的工作全部留在宿主。
/// </summary>
public sealed record WidgetOverlayHints
{
    /// <summary>inside-card / outside-card 的放置选择。</summary>
    public WidgetOverlayPlacement Placement { get; init; } = WidgetOverlayPlacement.OutsideCardRight;

    /// <summary>装饰在卡片右缘需要的横向空间（DIP，含与卡片内容的安全间距）。</summary>
    public double RightReservedWidth { get; init; } = 13;

    /// <summary>装饰可命中区域的最小高度（DIP）；<see cref="double.NaN"/> 表示随装饰内容自然高度。</summary>
    public double HitAreaHeight { get; init; } = double.NaN;

    /// <summary>
    /// 原生 Acrylic（UsesNativeBlur 且无原生窗框）模式下要求内移到卡片内部：
    /// DWM 对整个被裁剪的 HWND 施加 OS 模糊，卡片外的透明条会被糊成毛玻璃，
    /// 不透明条则像一块异质板——历史上圆点条在这类表面一律内移。
    /// </summary>
    public bool InsetOnNativeBlur { get; init; } = true;

    /// <summary>
    /// overlay 的 Bounds 需要与窗口 Region 合并（否则会被 HWND 裁掉，报告 §13
    /// “Stack 装饰被 HWND Region 裁掉”）。宿主当前用单一 GDI 圆角矩形裁剪
    /// （InteropService.SetWidgetRegion），无法把第二个矩形 OR 进区域：声明了本提示
    /// 但仍停在卡片外的装饰，宿主会回退为内移放置以保证可见。声明
    /// <see cref="InsetOnNativeBlur"/>（且接受内移）的装饰无需本提示。
    /// </summary>
    public bool MergeBoundsIntoWindowRegion { get; init; }

    /// <summary>组件移动/尺寸/边距变化时需要宿主重新计算放置（重新求边距与对齐）。</summary>
    public bool RelayoutOnBoundsChange { get; init; } = true;

    /// <summary>
    /// 装饰隐藏（Opacity→0）时仍保持可命中：常态隐藏的圆点条靠 Opacity 隐身，
    /// 命中一旦关闭，圆点原位的滚轮手势和点击都会失灵。
    /// </summary>
    public bool HitTestableWhenHidden { get; init; } = true;
}

/// <summary>
/// 宿主回传给装饰提供者的一次布局上下文：卡片几何、原生模糊状态与宿主最终施加的放置。
/// </summary>
/// <param name="CardWidth">卡片宽度（DIP，不含窗口边距）。</param>
/// <param name="CardHeight">卡片高度（DIP）。</param>
/// <param name="CardMargin">卡片相对窗口的边距（网格线边距）。</param>
/// <param name="Scaling">所在屏幕的缩放。</param>
/// <param name="NativeBlurActive">原生 Acrylic 模糊当前是否生效（决定 Region 是否裁剪）。</param>
/// <param name="Frameless">无边框（Frameless）内容：宿主不施加卡片几何。</param>
/// <param name="InsetApplied">宿主本次是否实际把装饰内移到了卡片内部。</param>
public readonly record struct WidgetOverlayLayoutContext(
    double CardWidth,
    double CardHeight,
    Avalonia.Thickness CardMargin,
    double Scaling,
    bool NativeBlurActive,
    bool Frameless,
    bool InsetApplied);

/// <summary>
/// 可选的装饰 overlay 提供协议（W10 §9.2，UI 层）：组件插件用它向宿主提供一个装饰
/// Control 和通用布局提示。宿主只持有 Overlay ContentPresenter，按提示处理放置、
/// 命中与窗口区域；Windows 的 HWND Region 与 DWM 模糊工作全部留在宿主，
/// 不转嫁给插件。
/// <para>
/// 本接口在 UI 层（可用 Avalonia Control）而不是 Core，因为 Stack 等插件本就依赖宿主；
/// Core 保持无 UI 依赖。接口同样可选：旧版 DLL 只实现 IStackWidget 时，宿主经
/// LegacyStackOverlayAdapter 走既有路径。
/// </para>
/// </summary>
public interface IWidgetOverlayProvider
{
    /// <summary>
    /// 装饰控件。每个宿主实例只允许对应一个实例：实现方必须在构造时创建（或惰性
    /// 创建一次并缓存），<b>不得在 getter 里每次 new</b>；同一个 visual child 也不能
    /// 被多个窗口同时共享。返回 null 表示本组件不提供装饰。
    /// </summary>
    Control? Overlay => null;

    /// <summary>通用布局提示（放置、右侧空间、内移、Region 合并、重新布局）。</summary>
    WidgetOverlayHints OverlayHints => new();

    /// <summary>
    /// 宿主完成一次放置/布局后回调：组件可据此做自身重排（如按卡片高度重定位）。
    /// 默认空实现，多数装饰无需理会。
    /// </summary>
    void OnOverlayLayoutChanged(in WidgetOverlayLayoutContext context) { }
}
