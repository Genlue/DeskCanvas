using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TickClock.Views.Controls;

/// <summary>
/// 时间专用的墨迹排版控件：把 "19:48" 拆成「小时 / 冒号 / 分钟」三段绘制。
/// <para>
/// 普通字体里的冒号坐落在基线上，视觉上贴着数字底部；参考图的冒号在数字墨迹的正中。
/// 所以数字仍用字体本身绘制（按墨迹填满高度），冒号改成两个实心圆点，垂直居中于数字，
/// 大小与间距都按数字高度取比例——这样换任何字体，冒号都待在中间。
/// </para>
/// </summary>
public class TimeInkText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<TimeInkText, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TimeInkText, IBrush?>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<TimeInkText, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<TimeInkText, FontWeight>(nameof(FontWeight), FontWeight.Bold);

    /// <summary>冒号两个圆点的中心距，相对数字墨迹高度的比例。</summary>
    public static readonly StyledProperty<double> ColonGapRatioProperty =
        AvaloniaProperty.Register<TimeInkText, double>(nameof(ColonGapRatio), 0.30);

    /// <summary>冒号圆点半径，相对数字墨迹高度的比例。</summary>
    public static readonly StyledProperty<double> ColonRadiusRatioProperty =
        AvaloniaProperty.Register<TimeInkText, double>(nameof(ColonRadiusRatio), 0.068);

    /// <summary>冒号占用的横向空间（含两侧留白），相对数字墨迹高度的比例。</summary>
    public static readonly StyledProperty<double> ColonWidthRatioProperty =
        AvaloniaProperty.Register<TimeInkText, double>(nameof(ColonWidthRatio), 0.30);

    private const double ReferenceFontSize = 100;

    static TimeInkText()
    {
        AffectsRender<TimeInkText>(TextProperty, ForegroundProperty, FontFamilyProperty, FontWeightProperty,
            ColonGapRatioProperty, ColonRadiusRatioProperty, ColonWidthRatioProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double ColonGapRatio
    {
        get => GetValue(ColonGapRatioProperty);
        set => SetValue(ColonGapRatioProperty, value);
    }

    public double ColonRadiusRatio
    {
        get => GetValue(ColonRadiusRatioProperty);
        set => SetValue(ColonRadiusRatioProperty, value);
    }

    public double ColonWidthRatio
    {
        get => GetValue(ColonWidthRatioProperty);
        set => SetValue(ColonWidthRatioProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (string.IsNullOrEmpty(Text)) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var brush = Foreground ?? Brushes.Black;
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);

        int separator = Text.IndexOfAny([':', '：']);
        if (separator < 0)
        {
            DrawSegment(context, Create(Text, typeface, brush), new Rect(0, 0, Bounds.Width, Bounds.Height));
            return;
        }

        string head = Text[..separator];
        string tail = Text[(separator + 1)..];

        var headText = Create(head, typeface, brush);
        var tailText = Create(tail, typeface, brush);

        double inkHeight = Math.Max(headText.Extent, tailText.Extent);
        if (inkHeight <= 0) return;

        // 数字先按目标高度取自然宽度；放不下时才横向压缩，冒号吃掉剩余空间。
        double verticalScale = Bounds.Height / inkHeight;
        double headWidth = headText.Width * verticalScale;
        double tailWidth = tailText.Width * verticalScale;
        double minColon = Bounds.Height * Math.Clamp(ColonWidthRatio, 0.05, 0.8);

        double squeeze = 1;
        if (headWidth + tailWidth + minColon > Bounds.Width)
            squeeze = Math.Max(0.2, (Bounds.Width - minColon) / Math.Max(1, headWidth + tailWidth));

        headWidth *= squeeze;
        tailWidth *= squeeze;
        double colonWidth = Math.Max(minColon, Bounds.Width - headWidth - tailWidth);

        DrawSegment(context, headText, new Rect(0, 0, headWidth, Bounds.Height));
        DrawSegment(context, tailText, new Rect(headWidth + colonWidth, 0, tailWidth, Bounds.Height));

        // 冒号：两个圆点，中心严格落在数字墨迹的垂直中线上。
        double radius = Bounds.Height * Math.Clamp(ColonRadiusRatio, 0.01, 0.2);
        double gap = Bounds.Height * Math.Clamp(ColonGapRatio, 0.05, 0.8);
        double cx = headWidth + colonWidth / 2;
        double cy = Bounds.Height / 2;

        context.DrawEllipse(brush, null, new Point(cx, cy - gap / 2), radius, radius);
        context.DrawEllipse(brush, null, new Point(cx, cy + gap / 2), radius, radius);
    }

    private static FormattedText Create(string text, Typeface typeface, IBrush brush) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, ReferenceFontSize, brush);

    /// <summary>把一段文字的墨迹左上角对齐目标矩形左上角，并拉伸到它的大小。</summary>
    private static void DrawSegment(DrawingContext context, FormattedText formatted, Rect target)
    {
        if (formatted.Width <= 0 || formatted.Extent <= 0) return;

        double scaleX = target.Width / formatted.Width;
        double scaleY = target.Height / formatted.Extent;

        // 绘制原点是行的左上角，墨迹比它低 inkTop；直接给出行向量矩阵，
        // 避免 PushTransform 里乘法顺序的歧义（x' = sx·x + tx, y' = sy·y + ty）。
        double inkTop = formatted.Baseline - formatted.Extent;
        double ty = target.Y - inkTop * scaleY;

        using (context.PushTransform(new Matrix(scaleX, 0, 0, scaleY, target.X, ty)))
        {
            context.DrawText(formatted, new Point(0, 0));
        }
    }
}
