using Avalonia;
using Avalonia.Controls;

namespace Countdown.Views.Controls;

/// <summary>
/// 2x2 极简样式的数字 + 单位：
/// 数字保持原来的居中位置与大小（整体高度仍按"数字 + 间距 + 单位"计算，与旧的
/// 垂直 StackPanel 完全一致），单位则贴到数字包围盒的右下角。
/// 这样单位不再把内容整体往下撑，矮卡片上也不会压到底部进度条；
/// 同时数字本身的布局位置不因本次调整而移动。
/// </summary>
public sealed class NumberWithCornerUnit : Panel
{
    /// <summary>旧 StackPanel 的数字 / 单位间距，保留它让数字的垂直位置与修改前一致。</summary>
    private const double StackSpacing = 2;

    /// <summary>单位与数字包围盒之间的水平间隙。</summary>
    private const double CornerGap = 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count < 2) return base.MeasureOverride(availableSize);

        var number = Children[0];
        var unit = Children[1];

        number.Measure(availableSize);
        unit.Measure(Size.Infinity);

        var numberSize = number.DesiredSize;
        var unitSize = unit.DesiredSize;

        // 宽度沿用旧 StackPanel 的算法（取数字 / 单位的较大者），父容器据此水平居中；
        // 单位允许绘制到面板右侧（Panel 默认不裁剪），因此数字的左右位置与修改前一致。
        var height = numberSize.Height + StackSpacing + unitSize.Height;
        var width = Math.Max(numberSize.Width, unitSize.Width);

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count < 2) return base.ArrangeOverride(finalSize);

        var number = Children[0];
        var unit = Children[1];

        var numberSize = number.DesiredSize;
        var unitSize = unit.DesiredSize;

        // 数字水平居中、顶部对齐 —— 与旧垂直 StackPanel 里的排布相同。
        var numberX = (finalSize.Width - numberSize.Width) / 2;
        number.Arrange(new Rect(numberX, 0, numberSize.Width, numberSize.Height));

        // 单位贴数字包围盒右下角：左边留 2px 间隙，底边略低于数字底边（约等于基线）。
        // 允许绘制到面板右侧（Panel 默认不裁剪），水平位置因此不受面板宽度约束。
        var unitX = numberX + numberSize.Width + CornerGap;
        var unitY = numberSize.Height - unitSize.Height + StackSpacing;
        unit.Arrange(new Rect(unitX, unitY, unitSize.Width, unitSize.Height));

        return finalSize;
    }
}
