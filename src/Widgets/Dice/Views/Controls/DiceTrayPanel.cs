using System;
using Avalonia;
using Avalonia.Controls;

namespace Dice.Views.Controls;

/// <summary>
/// 骰盘：根据骰子数量与自身可用区域，算出最合适的列数、行数和骰面边长，
/// 把子项排成水平/垂直都居中的网格。
/// <para>
/// 与旧的固定 <c>ItemWidth/ItemHeight</c> WrapPanel 不同，这里先保证“装得下”，
/// 再尽量让骰面大：遍历 1..n 列，取 <c>min(可用宽/列, 可用高/行)</c> 最大者，
/// 因此任意数量都不会换行溢出到 Header/Roll 按钮或卡片边缘。
/// </para>
/// </summary>
public sealed class DiceTrayPanel : Panel
{
    /// <summary>骰面最大边长（DIP）。2x2 用 48，4x2 可放宽到 64。</summary>
    public static readonly StyledProperty<double> MaxFaceSizeProperty =
        AvaloniaProperty.Register<DiceTrayPanel, double>(nameof(MaxFaceSize), 48d);

    // 骰面控件外框 = FaceSize + 两侧各 FaceSize/12 的投影呼吸边距 = FaceSize * 7/6。
    private const double FaceBoxRatio = 7d / 6d;

    // 相邻骰面之间保留的间距：吸收子项布局取整（可多出 1px）造成的贴边/重叠。
    private const double Gap = 2d;

    // 骰盘四周的安全余量：满行时布局取整可能把最后一项挤出 1px，预留后不会压到按钮。
    private const double EdgeInset = 2d;

    private int columns = 1;
    private int rows;
    private double faceSize = 48d;

    static DiceTrayPanel()
    {
        AffectsMeasure<DiceTrayPanel>(MaxFaceSizeProperty);
    }

    public double MaxFaceSize
    {
        get => GetValue(MaxFaceSizeProperty);
        set => SetValue(MaxFaceSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            columns = 1;
            rows = 0;
            faceSize = 0;
            return default;
        }

        Resolve(availableSize, count, out columns, out rows, out faceSize);

        double box = faceSize * FaceBoxRatio;
        double cellWidth = double.IsFinite(availableSize.Width) ? availableSize.Width / columns : box;
        double cellHeight = double.IsFinite(availableSize.Height) ? availableSize.Height / rows : box;
        var constraint = new Size(Math.Max(box, cellWidth), Math.Max(box, cellHeight));

        foreach (var child in Children)
        {
            ApplyFaceSize(child, faceSize);
            child.Measure(constraint);
        }

        return new Size(
            double.IsFinite(availableSize.Width) ? availableSize.Width : columns * box,
            double.IsFinite(availableSize.Height) ? availableSize.Height : rows * box);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int count = Children.Count;
        if (count == 0)
        {
            return finalSize;
        }

        Resolve(finalSize, count, out columns, out rows, out faceSize);

        double box = faceSize * FaceBoxRatio;
        double originY = (finalSize.Height - (rows * box + (rows - 1) * Gap)) / 2;

        for (int row = 0; row < rows; row++)
        {
            int first = row * columns;
            int rowCount = Math.Min(columns, count - first);
            double originX = (finalSize.Width - (rowCount * box + (rowCount - 1) * Gap)) / 2;

            for (int column = 0; column < rowCount; column++)
            {
                var child = Children[first + column];
                ApplyFaceSize(child, faceSize);
                child.Arrange(new Rect(
                    originX + column * (box + Gap),
                    originY + row * (box + Gap),
                    box,
                    box));
            }
        }

        return finalSize;
    }

    /// <summary>
    /// 选出“骰面最大”的网格。同尺寸时优先少留空位，其次接近正方形，再优先横向铺开。
    /// </summary>
    private void Resolve(Size available, int count, out int bestColumns, out int bestRows, out double bestFace)
    {
        double maxFace = MaxFaceSize;
        if (!double.IsFinite(maxFace) || maxFace <= 0)
        {
            maxFace = 48d;
        }

        if (!double.IsFinite(available.Width) || !double.IsFinite(available.Height) ||
            available.Width <= 0 || available.Height <= 0)
        {
            // 无约束场景（滚动的父级等）：退回接近正方形的默认网格与最大骰面。
            bestColumns = Math.Min(count, (int)Math.Ceiling(Math.Sqrt(count)));
            bestRows = (count + bestColumns - 1) / bestColumns;
            bestFace = maxFace;
            return;
        }

        bestColumns = 1;
        bestRows = count;
        bestFace = -1;

        double usableWidth = Math.Max(0, available.Width - EdgeInset);
        double usableHeight = Math.Max(0, available.Height - EdgeInset);

        for (int candidateColumns = 1; candidateColumns <= count; candidateColumns++)
        {
            int candidateRows = (count + candidateColumns - 1) / candidateColumns;
            double cellWidth = (usableWidth - (candidateColumns - 1) * Gap) / candidateColumns;
            double cellHeight = (usableHeight - (candidateRows - 1) * Gap) / candidateRows;
            double cell = Math.Min(cellWidth, cellHeight);
            if (cell <= 0)
            {
                continue;
            }

            double candidateFace = Math.Min(cell * 6d / 7d, maxFace);
            if (candidateFace <= 0)
            {
                continue;
            }

            if (!IsBetter(candidateColumns, candidateRows, candidateFace,
                          bestColumns, bestRows, bestFace, count))
            {
                continue;
            }

            bestColumns = candidateColumns;
            bestRows = candidateRows;
            bestFace = candidateFace;
        }

        if (bestFace < 0)
        {
            bestColumns = 1;
            bestRows = count;
            bestFace = maxFace;
        }
    }

    private static bool IsBetter(
        int columns, int rows, double face,
        int bestColumns, int bestRows, double bestFace, int count)
    {
        if (face > bestFace + 0.01)
        {
            return true;
        }

        if (face < bestFace - 0.01)
        {
            return false;
        }

        int empty = columns * rows - count;
        int bestEmpty = bestColumns * bestRows - count;
        if (empty != bestEmpty)
        {
            return empty < bestEmpty;
        }

        int skew = Math.Abs(columns - rows);
        int bestSkew = Math.Abs(bestColumns - bestRows);
        if (skew != bestSkew)
        {
            return skew < bestSkew;
        }

        return columns > bestColumns;
    }

    private static void ApplyFaceSize(Control child, double size)
    {
        if (child is DiceFaceControl face && Math.Abs(face.FaceSize - size) > 0.01)
        {
            face.FaceSize = size;
        }
    }
}
