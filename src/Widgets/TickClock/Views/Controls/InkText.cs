using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace TickClock.Views.Controls;

/// <summary>
/// 把文字的**墨迹**精确填满控件矩形。
/// <para>
/// 普通 TextBlock 的行盒比字形高一大截（HarmonyOS 100 号字：行盒 134，墨迹只有 72），
/// 放进 Viewbox 后无论怎么缩放，墨迹最多占到盒子的一半，时间永远显得“扁”。
/// 这里按 <see cref="FormattedText.Extent"/>（实际墨迹高）和基线做仿射变换，
/// 让墨迹的左上角对齐控件左上角、右下角对齐控件右下角——需要的话允许非等比，
/// 配合天生窄的字体就能得到参考图那种“高瘦且粗”的数字。
/// </para>
/// </summary>
public class InkText : Control
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<InkText, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<InkText, IBrush?>(nameof(Foreground), Brushes.Black);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<InkText, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<InkText, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    /// <summary>基准字号；实际大小由控件矩形决定，改这个值不影响观感。</summary>
    private const double ReferenceFontSize = 100;

    static InkText()
    {
        AffectsRender<InkText>(TextProperty, ForegroundProperty, FontFamilyProperty, FontWeightProperty);
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

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (string.IsNullOrEmpty(Text)) return;
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight);
        var formatted = new FormattedText(
            Text,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            ReferenceFontSize,
            Foreground ?? Brushes.Black);

        double inkWidth = formatted.Width;
        double inkHeight = formatted.Extent;
        if (inkWidth <= 0 || inkHeight <= 0) return;

        // 墨迹相对绘制原点的顶端：基线往上 Extent。
        double inkTop = formatted.Baseline - inkHeight;

        double scaleX = Bounds.Width / inkWidth;
        double scaleY = Bounds.Height / inkHeight;

        using (context.PushTransform(
                   Matrix.CreateTranslation(0, -inkTop) *
                   Matrix.CreateScale(scaleX, scaleY)))
        {
            context.DrawText(formatted, new Point(0, 0));
        }
    }
}
