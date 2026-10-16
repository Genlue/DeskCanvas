using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Wheel.Models;

namespace Wheel.Views.Controls;

public class WheelControl : Control
{
    public static readonly StyledProperty<double> RotationAngleProperty =
        AvaloniaProperty.Register<WheelControl, double>(nameof(RotationAngle), 0.0);

    public static readonly StyledProperty<IEnumerable<WheelOption>?> OptionsProperty =
        AvaloniaProperty.Register<WheelControl, IEnumerable<WheelOption>?>(nameof(Options));

    public static readonly StyledProperty<string> CenterTextProperty =
        AvaloniaProperty.Register<WheelControl, string>(nameof(CenterText), "GO");

    public double RotationAngle
    {
        get => GetValue(RotationAngleProperty);
        set => SetValue(RotationAngleProperty, value);
    }

    public IEnumerable<WheelOption>? Options
    {
        get => GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string CenterText
    {
        get => GetValue(CenterTextProperty);
        set => SetValue(CenterTextProperty, value);
    }

    static WheelControl()
    {
        AffectsRender<WheelControl>(RotationAngleProperty, OptionsProperty, CenterTextProperty);
    }

    private static readonly IBrush SliceStrokeBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
    private static readonly IBrush RimStrokeBrush = new SolidColorBrush(Color.FromArgb(0x26, 255, 255, 255));
    private static readonly IBrush TextShadowBrush = new SolidColorBrush(Color.FromArgb(0x14, 0, 0, 0));

    /// <summary>取主题资源画刷，取不到时按深浅主题返回兜底色。</summary>
    private IBrush GetThemeBrush(string key, string lightFallback, string darkFallback)
    {
        if (this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            return brush;
        }
        bool isDark = ActualThemeVariant == ThemeVariant.Dark;
        return new SolidColorBrush(Color.Parse(isDark ? darkFallback : lightFallback));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 10 || height <= 10) return;

        Point center = new(width / 2.0, height / 2.0);
        double radius = Math.Min(width, height) / 2.0 - 10.0;
        if (radius <= 10) return;

        var optionList = Options?.Where(o => o.IsEnabled).ToList();
        if (optionList == null || optionList.Count == 0)
        {
            optionList = Options?.ToList();
        }

        if (optionList == null || optionList.Count == 0)
        {
            // Empty placeholder
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#20808080")), new Pen(new SolidColorBrush(Color.Parse("#1E808080")), 1), center, radius, radius);
            return;
        }

        int totalWeight = optionList.Sum(o => Math.Max(1, o.Weight));
        if (totalWeight <= 0) totalWeight = 1;

        // 1. Draw Rotating Slices
        using (context.PushTransform(Matrix.CreateRotation(Math.PI * RotationAngle / 180.0) * Matrix.CreateTranslation(center.X, center.Y)))
        {
            Point localCenter = new(0, 0);
            double currentAngleDeg = -90.0; // Start at 12 o'clock

            foreach (var opt in optionList)
            {
                double sweepDeg = 360.0 * Math.Max(1, opt.Weight) / totalWeight;
                double startRad = Math.PI * currentAngleDeg / 180.0;
                double endRad = Math.PI * (currentAngleDeg + sweepDeg) / 180.0;

                Point pStart = new(radius * Math.Cos(startRad), radius * Math.Sin(startRad));
                Point pEnd = new(radius * Math.Cos(endRad), radius * Math.Sin(endRad));

                var geo = new StreamGeometry();
                using (var ctx = geo.Open())
                {
                    ctx.BeginFigure(localCenter, isFilled: true);
                    ctx.LineTo(pStart);
                    ctx.ArcTo(pEnd, new Size(radius, radius), 0, sweepDeg > 180.0, SweepDirection.Clockwise);
                    ctx.EndFigure(isClosed: true);
                }

                Color col = Color.TryParse(opt.ColorHex, out var parsedCol) ? parsedCol : Color.Parse("#0078D4");
                context.DrawGeometry(new SolidColorBrush(col), new Pen(SliceStrokeBrush, 1), geo);

                // Draw Slice Text
                double midDeg = currentAngleDeg + sweepDeg / 2.0;
                using (context.PushTransform(Matrix.CreateRotation(Math.PI * midDeg / 180.0)))
                {
                    string label = opt.Text;
                    if (label.Length > 6) label = label[..5] + "…";

                    double textDistance = radius * 0.62;
                    var typeface = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
                    double fontSize = Math.Clamp(radius * 0.13, 10.0, 13.0);

                    // 8% 黑色微投影提升白字可读性
                    var shadowFt = new FormattedText(
                        label,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        TextShadowBrush
                    );
                    context.DrawText(shadowFt, new Point(textDistance - shadowFt.Width / 2.0, -shadowFt.Height / 2.0 + 1));

                    var ft = new FormattedText(
                        label,
                        CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        typeface,
                        fontSize,
                        Brushes.White
                    );

                    Point textOrigin = new(textDistance - ft.Width / 2.0, -ft.Height / 2.0);
                    context.DrawText(ft, textOrigin);
                }

                currentAngleDeg += sweepDeg;
            }

            // Outer border
            context.DrawEllipse(null, new Pen(RimStrokeBrush, 2), localCenter, radius, radius);
        }

        // 2. Draw Center GO Circle (Static in Center, theme-adaptive)
        double centerRadius = radius * 0.28;
        var centerBg = GetThemeBrush("WidgetBackground", "#F5F5F5", "#2C2C2E");
        var centerStroke = new SolidColorBrush(Color.Parse("#1E808080"));
        context.DrawEllipse(
            centerBg,
            new Pen(centerStroke, 1.5),
            center, centerRadius, centerRadius
        );

        var centerForeground = GetThemeBrush("WidgetForeground", "#222222", "#F5F5F5");
        var centerFt = new FormattedText(
            CenterText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.ExtraBold),
            Math.Clamp(centerRadius * 0.65, 10.0, 16.0),
            centerForeground
        );
        context.DrawText(centerFt, new Point(center.X - centerFt.Width / 2.0, center.Y - centerFt.Height / 2.0));

        // 3. Draw Top Indicator Pointer (Small triangle at 12 o'clock, pointing down)
        Point pt1 = new(center.X, center.Y - radius + 8);   // Tip embedded 8px into the rim
        Point pt2 = new(center.X - 6, center.Y - radius - 4);
        Point pt3 = new(center.X + 6, center.Y - radius - 4);

        var pointerGeo = new StreamGeometry();
        using (var ctx = pointerGeo.Open())
        {
            ctx.BeginFigure(pt1, isFilled: true);
            ctx.LineTo(pt2);
            ctx.LineTo(pt3);
            ctx.EndFigure(isClosed: true);
        }

        var pointerBrush = GetThemeBrush("MacosRedBrush", "#FF3B30", "#FF453A");
        context.DrawGeometry(
            pointerBrush,
            new Pen(Brushes.White, 1.2),
            pointerGeo
        );
    }
}
