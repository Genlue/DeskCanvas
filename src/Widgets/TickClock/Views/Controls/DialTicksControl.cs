using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TickClock.Views.Controls;

/// <summary>
/// 卡片四周的一圈刻度（复刻 MIUI 桌面方卡）。
/// <para>
/// 位置沿“卡片内缩 4.1%”的圆角矩形轮廓**等弧长**排布，所以直边和四个圆角处在同一条
/// 轮廓线上、间距也一致；方向统一指向卡片中心（参考图逐根实测径向夹角 ≈0°）。
/// 早先按等角度布点时，圆角弧只覆盖 ±6° 的径向角度，角上会突然出现一根孤零零的斜线、
/// 内端还会戳出直边刻度形成的内圈——等弧长布点没有这个问题。
/// </para>
/// </summary>
public class DialTicksControl : Control
{
    public static readonly StyledProperty<IBrush?> TickBrushProperty =
        AvaloniaProperty.Register<DialTicksControl, IBrush?>(nameof(TickBrush));

    /// <summary>刻度外端与卡片边缘的距离，相对短边的比例（参考图 20/483）。</summary>
    public static readonly StyledProperty<double> InsetRatioProperty =
        AvaloniaProperty.Register<DialTicksControl, double>(nameof(InsetRatio), 0.041);

    /// <summary>刻度长度，相对短边的比例（参考图 14/483）。</summary>
    public static readonly StyledProperty<double> TickLengthRatioProperty =
        AvaloniaProperty.Register<DialTicksControl, double>(nameof(TickLengthRatio), 0.030);

    /// <summary>相邻刻度的轮廓间距，相对短边的比例（参考图 20/483）。</summary>
    public static readonly StyledProperty<double> SpacingRatioProperty =
        AvaloniaProperty.Register<DialTicksControl, double>(nameof(SpacingRatio), 0.046);

    /// <summary>刻度环轮廓的圆角半径，相对短边的比例（宿主圆角拿不到时的回退值）。</summary>
    public static readonly StyledProperty<double> CornerRatioProperty =
        AvaloniaProperty.Register<DialTicksControl, double>(nameof(CornerRatio), 0.066);

    /// <summary>
    /// 宿主卡片当前的圆角（DIP）。设置后刻度环圆角取「卡片圆角 − inset」，
    /// 与卡片边缘保持同心；NaN 表示未知，退回 <see cref="CornerRatio"/> 的比例值。
    /// </summary>
    public static readonly StyledProperty<double> CardCornerRadiusProperty =
        AvaloniaProperty.Register<DialTicksControl, double>(nameof(CardCornerRadius), double.NaN);

    static DialTicksControl()
    {
        AffectsRender<DialTicksControl>(
            TickBrushProperty, InsetRatioProperty, TickLengthRatioProperty, SpacingRatioProperty,
            CornerRatioProperty, CardCornerRadiusProperty);
    }

    public IBrush? TickBrush
    {
        get => GetValue(TickBrushProperty);
        set => SetValue(TickBrushProperty, value);
    }

    public double InsetRatio
    {
        get => GetValue(InsetRatioProperty);
        set => SetValue(InsetRatioProperty, value);
    }

    public double TickLengthRatio
    {
        get => GetValue(TickLengthRatioProperty);
        set => SetValue(TickLengthRatioProperty, value);
    }

    public double SpacingRatio
    {
        get => GetValue(SpacingRatioProperty);
        set => SetValue(SpacingRatioProperty, value);
    }

    public double CornerRatio
    {
        get => GetValue(CornerRatioProperty);
        set => SetValue(CornerRatioProperty, value);
    }

    public double CardCornerRadius
    {
        get => GetValue(CardCornerRadiusProperty);
        set => SetValue(CardCornerRadiusProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w < 12 || h < 12) return;

        double shortSide = Math.Min(w, h);
        // 1x1 单格太小，刻度会糊成一条灰边，直接不画（参考图也没有这种尺寸）。
        if (shortSide < 92) return;

        double inset = shortSide * Clamp(InsetRatio, 0.005, 0.24);
        double left = inset;
        double top = inset;
        double right = w - inset;
        double bottom = h - inset;
        if (right <= left || bottom <= top) return;

        // 圆角跟随宿主卡片：同心内缩后半径 = 卡片圆角 − inset（内缩圆角矩形的几何关系）；
        // 拿不到宿主圆角时退回参考图的比例值。上限仍受矩形尺寸约束。
        double cardCorner = double.IsFinite(CardCornerRadius) && CardCornerRadius >= 0
            ? CardCornerRadius
            : shortSide * Clamp(CornerRatio, 0, 0.45);
        double radius = Math.Min(Math.Max(0, cardCorner - inset),
                                 Math.Min((right - left) / 2, (bottom - top) / 2));

        double edgeX = Math.Max(0, (right - left) - 2 * radius);
        double edgeY = Math.Max(0, (bottom - top) - 2 * radius);
        double arcLength = Math.PI * radius / 2;
        double perimeter = 2 * edgeX + 2 * edgeY + 4 * arcLength;
        if (perimeter <= 1) return;

        double spacing = Math.Max(6, shortSide * Clamp(SpacingRatio, 0.012, 0.2));
        int count = Math.Max(8, (int)Math.Round(perimeter / spacing));
        double step = perimeter / count;

        double tickLength = shortSide * Clamp(TickLengthRatio, 0.006, 0.16);
        double thickness = Math.Max(1.0, shortSide * 0.0062);

        var pen = new Pen(TickBrush ?? Brushes.Gray, thickness, lineCap: PenLineCap.Flat);
        var center = new Point(w / 2, h / 2);

        for (int i = 0; i < count; i++)
        {
            SampleOnRoundedRect(i * step, left, top, right, bottom, radius,
                edgeX, edgeY, arcLength, out var point);

            // 方向：从轮廓点指向卡片中心（参考图逐根实测的径向朝向）。
            var toCenter = center - point;
            double length = Math.Sqrt(toCenter.X * toCenter.X + toCenter.Y * toCenter.Y);
            if (length < 0.001) continue;
            var direction = toCenter / length;

            context.DrawLine(pen, point, point + direction * tickLength);
        }
    }

    /// <summary>
    /// 从左上角起算、顺时针走一圈的弧长参数化：
    /// 上直边 → 右上弧 → 右直边 → 右下弧 → 下直边 → 左下弧 → 左直边 → 左上弧。
    /// </summary>
    private static void SampleOnRoundedRect(
        double s,
        double left, double top, double right, double bottom, double radius,
        double edgeX, double edgeY, double arcLength,
        out Point point)
    {
        double cursor = s;

        // 1. 上边（左 → 右）
        if (cursor < edgeX)
        {
            point = new Point(left + radius + cursor, top);
            return;
        }
        cursor -= edgeX;

        // 2. 右上圆角（-90° → 0°）
        if (cursor < arcLength && arcLength > 0)
        {
            double angle = -Math.PI / 2 + cursor / radius;
            point = new Point(right - radius + Math.Cos(angle) * radius,
                              top + radius + Math.Sin(angle) * radius);
            return;
        }
        cursor -= arcLength;

        // 3. 右边（上 → 下）
        if (cursor < edgeY)
        {
            point = new Point(right, top + radius + cursor);
            return;
        }
        cursor -= edgeY;

        // 4. 右下圆角（0° → 90°）
        if (cursor < arcLength && arcLength > 0)
        {
            double angle = cursor / radius;
            point = new Point(right - radius + Math.Cos(angle) * radius,
                              bottom - radius + Math.Sin(angle) * radius);
            return;
        }
        cursor -= arcLength;

        // 5. 下边（右 → 左）
        if (cursor < edgeX)
        {
            point = new Point(right - radius - cursor, bottom);
            return;
        }
        cursor -= edgeX;

        // 6. 左下圆角（90° → 180°）
        if (cursor < arcLength && arcLength > 0)
        {
            double angle = Math.PI / 2 + cursor / radius;
            point = new Point(left + radius + Math.Cos(angle) * radius,
                              bottom - radius + Math.Sin(angle) * radius);
            return;
        }
        cursor -= arcLength;

        // 7. 左边（下 → 上）
        if (cursor < edgeY)
        {
            point = new Point(left, bottom - radius - cursor);
            return;
        }
        cursor -= edgeY;

        // 8. 左上圆角（180° → 270°）
        if (arcLength > 0)
        {
            double angle = Math.PI + Math.Min(cursor / radius, Math.PI / 2);
            point = new Point(left + radius + Math.Cos(angle) * radius,
                              top + radius + Math.Sin(angle) * radius);
            return;
        }

        point = new Point(left + radius, top);
    }

    private static double Clamp(double value, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}
