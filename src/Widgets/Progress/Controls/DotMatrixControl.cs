using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Progress.Models;

namespace Progress.Controls;

public class DotMatrixControl : Control
{
    public static readonly StyledProperty<int> TotalDotsProperty =
        AvaloniaProperty.Register<DotMatrixControl, int>(nameof(TotalDots), 365);

    public static readonly StyledProperty<int> PassedDotsProperty =
        AvaloniaProperty.Register<DotMatrixControl, int>(nameof(PassedDots), 0);

    public static readonly StyledProperty<bool> HasCurrentDotProperty =
        AvaloniaProperty.Register<DotMatrixControl, bool>(nameof(HasCurrentDot), true);

    public static readonly StyledProperty<IBrush?> PassedBrushProperty =
        AvaloniaProperty.Register<DotMatrixControl, IBrush?>(nameof(PassedBrush));

    public static readonly StyledProperty<IBrush?> CurrentBrushProperty =
        AvaloniaProperty.Register<DotMatrixControl, IBrush?>(nameof(CurrentBrush));

    public static readonly StyledProperty<IBrush?> RemainingBrushProperty =
        AvaloniaProperty.Register<DotMatrixControl, IBrush?>(nameof(RemainingBrush));

    public static readonly StyledProperty<DotShape> DotShapeProperty =
        AvaloniaProperty.Register<DotMatrixControl, DotShape>(nameof(DotShape), DotShape.Circle);

    public static readonly StyledProperty<int> PreferredColumnsProperty =
        AvaloniaProperty.Register<DotMatrixControl, int>(nameof(PreferredColumns), 0);

    public static readonly StyledProperty<Func<int, string>?> DotTooltipFuncProperty =
        AvaloniaProperty.Register<DotMatrixControl, Func<int, string>?>(nameof(DotTooltipFunc));

    public int TotalDots
    {
        get => GetValue(TotalDotsProperty);
        set => SetValue(TotalDotsProperty, value);
    }

    public int PassedDots
    {
        get => GetValue(PassedDotsProperty);
        set => SetValue(PassedDotsProperty, value);
    }

    public bool HasCurrentDot
    {
        get => GetValue(HasCurrentDotProperty);
        set => SetValue(HasCurrentDotProperty, value);
    }

    public IBrush? PassedBrush
    {
        get => GetValue(PassedBrushProperty);
        set => SetValue(PassedBrushProperty, value);
    }

    public IBrush? CurrentBrush
    {
        get => GetValue(CurrentBrushProperty);
        set => SetValue(CurrentBrushProperty, value);
    }

    public IBrush? RemainingBrush
    {
        get => GetValue(RemainingBrushProperty);
        set => SetValue(RemainingBrushProperty, value);
    }

    public DotShape DotShape
    {
        get => GetValue(DotShapeProperty);
        set => SetValue(DotShapeProperty, value);
    }

    public int PreferredColumns
    {
        get => GetValue(PreferredColumnsProperty);
        set => SetValue(PreferredColumnsProperty, value);
    }

    public Func<int, string>? DotTooltipFunc
    {
        get => GetValue(DotTooltipFuncProperty);
        set => SetValue(DotTooltipFuncProperty, value);
    }

    static DotMatrixControl()
    {
        AffectsRender<DotMatrixControl>(
            TotalDotsProperty,
            PassedDotsProperty,
            HasCurrentDotProperty,
            PassedBrushProperty,
            CurrentBrushProperty,
            RemainingBrushProperty,
            DotShapeProperty,
            PreferredColumnsProperty);
    }

    public DotMatrixControl()
    {
        ClipToBounds = true;
    }

    private (int Cols, int Rows, double CellSize, double OffsetX, double OffsetY) ComputeLayout(double width, double height, int total)
    {
        if (total <= 0 || width <= 0 || height <= 0)
            return (1, 1, 0, 0, 0);

        int bestCols = 1;
        int bestRows = total;
        double bestSide = 0;

        if (PreferredColumns > 0)
        {
            bestCols = PreferredColumns;
            bestRows = (int)Math.Ceiling((double)total / bestCols);
            double sideW = width / bestCols;
            double sideH = height / bestRows;
            bestSide = Math.Min(sideW, sideH);
        }
        else
        {
            // Maximize dot size across candidate column counts
            for (int c = 1; c <= total; c++)
            {
                int r = (int)Math.Ceiling((double)total / c);
                double sideW = width / c;
                double sideH = height / r;
                double side = Math.Min(sideW, sideH);

                if (side > bestSide)
                {
                    bestSide = side;
                    bestCols = c;
                    bestRows = r;
                }
            }
        }

        double totalGridW = bestCols * bestSide;
        double totalGridH = bestRows * bestSide;
        double offsetX = Math.Max(0, (width - totalGridW) / 2.0);
        double offsetY = Math.Max(0, (height - totalGridH) / 2.0);

        return (bestCols, bestRows, bestSide, offsetX, offsetY);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        int total = TotalDots;
        if (total <= 0) return;

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var (cols, rows, cellSize, offsetX, offsetY) = ComputeLayout(width, height, total);
        if (cellSize <= 0) return;

        var passedBrush = PassedBrush ?? Brushes.MediumPurple;
        var currentBrush = CurrentBrush ?? Brushes.DeepPink;
        bool isDark = Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
        var defaultRemColor = isDark ? Color.FromArgb(50, 255, 255, 255) : Color.FromArgb(38, 0, 0, 0);
        var remainingBrush = RemainingBrush ?? new SolidColorBrush(defaultRemColor);

        // Dot radius: 76% of half-cell, leaving comfortable 24% gap between dots
        double radius = (cellSize / 2.0) * 0.76;
        if (radius < 0.5) radius = 0.5;

        int passed = PassedDots;
        bool hasCurrent = HasCurrentDot;
        var shape = DotShape;

        var currentHighlightPen = new Pen(currentBrush, Math.Max(1.0, radius * 0.28));

        for (int i = 0; i < total; i++)
        {
            int col = i % cols;
            int row = i / cols;

            double cx = offsetX + (col + 0.5) * cellSize;
            double cy = offsetY + (row + 0.5) * cellSize;

            IBrush fill;
            bool isCurrent = false;

            if (i < passed)
            {
                fill = passedBrush;
            }
            else if (i == passed && hasCurrent)
            {
                fill = currentBrush;
                isCurrent = true;
            }
            else
            {
                fill = remainingBrush;
            }

            if (shape == DotShape.Circle)
            {
                context.DrawEllipse(fill, null, new Point(cx, cy), radius, radius);

                if (isCurrent)
                {
                    // Subtle glowing outer accent ring on current dot
                    double ringRadius = radius + Math.Max(1.2, radius * 0.35);
                    context.DrawEllipse(null, currentHighlightPen, new Point(cx, cy), ringRadius, ringRadius);
                }
            }
            else
            {
                var rect = new Rect(cx - radius, cy - radius, radius * 2, radius * 2);
                double corner = radius * 0.42;
                context.DrawRectangle(fill, null, rect, corner, corner);

                if (isCurrent)
                {
                    double pad = Math.Max(1.2, radius * 0.35);
                    var ringRect = new Rect(cx - radius - pad, cy - radius - pad, (radius + pad) * 2, (radius + pad) * 2);
                    context.DrawRectangle(null, currentHighlightPen, ringRect, corner + pad * 0.4, corner + pad * 0.4);
                }
            }
        }
    }

    /// <summary>The dot the tooltip is currently shown for. Pointer moves fire for every pixel of
    /// travel; re-running SetTip/SetIsOpen for the same dot made Avalonia tear down and rebuild the
    /// popup each time, so sweeping the 365-dot grid produced hundreds of open/close cycles.</summary>
    private int? lastHoverIndex;

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (DotTooltipFunc == null) return;

        var pt = e.GetPosition(this);
        int total = TotalDots;
        if (total <= 0) return;

        var (cols, rows, cellSize, offsetX, offsetY) = ComputeLayout(Bounds.Width, Bounds.Height, total);
        if (cellSize <= 0) return;

        double localX = pt.X - offsetX;
        double localY = pt.Y - offsetY;

        if (localX >= 0 && localY >= 0)
        {
            int col = (int)(localX / cellSize);
            int row = (int)(localY / cellSize);

            if (col >= 0 && col < cols && row >= 0 && row < rows)
            {
                int index = row * cols + col;
                if (index >= 0 && index < total)
                {
                    if (index == lastHoverIndex) return;

                    var text = DotTooltipFunc(index);
                    lastHoverIndex = index;
                    if (!string.IsNullOrEmpty(text))
                    {
                        ToolTip.SetTip(this, text);
                        ToolTip.SetIsOpen(this, true);
                        return;
                    }
                }
            }
        }

        lastHoverIndex = null;
        ToolTip.SetIsOpen(this, false);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        lastHoverIndex = null;
        ToolTip.SetIsOpen(this, false);
    }
}
