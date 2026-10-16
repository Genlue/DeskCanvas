using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Clock.Views.Controls;

/// <summary>
/// Renders the 60 radial tick marks for 表盘-3 (New Dial 3),
/// featuring a prominent 12 o'clock major tick and dynamic comet tail seconds illumination.
/// </summary>
public class TickRingControl : Control
{
    public static readonly StyledProperty<int> CurrentSecondProperty =
        AvaloniaProperty.Register<TickRingControl, int>(nameof(CurrentSecond));

    public static readonly StyledProperty<bool> ShowSecondsProperty =
        AvaloniaProperty.Register<TickRingControl, bool>(nameof(ShowSeconds), true);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<TickRingControl, IBrush?>(nameof(Foreground));

    static TickRingControl()
    {
        AffectsRender<TickRingControl>(CurrentSecondProperty, ShowSecondsProperty, ForegroundProperty);
    }

    public int CurrentSecond
    {
        get => GetValue(CurrentSecondProperty);
        set => SetValue(CurrentSecondProperty, value);
    }

    public bool ShowSeconds
    {
        get => GetValue(ShowSecondsProperty);
        set => SetValue(ShowSecondsProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        double cx = width / 2.0;
        double cy = height / 2.0;
        double radiusOuter = Math.Min(cx, cy) * 0.92;
        double radiusInnerNormal = radiusOuter * 0.88;
        double radiusInnerMajor12 = radiusOuter * 0.75; // 12点方向长刻度

        Color baseColor = Colors.White;
        if (Foreground is ISolidColorBrush scb)
            baseColor = scb.Color;
        else if (this.TryFindResource("SystemControlForegroundBaseHighBrush", out var res) && res is ISolidColorBrush resScb)
            baseColor = resScb.Color;

        int sec = CurrentSecond;
        bool animate = ShowSeconds;
        const int tailLength = 20;

        for (int i = 0; i < 60; i++)
        {
            double angleRad = (i * 6.0 - 90.0) * Math.PI / 180.0;
            double cos = Math.Cos(angleRad);
            double sin = Math.Sin(angleRad);

            bool is12 = (i == 0);
            double rIn = is12 ? radiusInnerMajor12 : radiusInnerNormal;

            Point p1 = new(cx + rIn * cos, cy + rIn * sin);
            Point p2 = new(cx + radiusOuter * cos, cy + radiusOuter * sin);

            byte alpha;
            double thickness;

            if (animate)
            {
                int diff = (sec - i + 60) % 60;
                if (diff == 0)
                {
                    alpha = 255;
                    thickness = is12 ? 14 : 11;
                }
                else if (diff < tailLength)
                {
                    double factor = 1.0 - (diff / (double)tailLength) * 0.65;
                    alpha = (byte)(255 * factor);
                    thickness = is12 ? 12 : 9;
                }
                else
                {
                    alpha = 85;
                    thickness = is12 ? 11 : 7;
                }
            }
            else
            {
                alpha = 90;
                thickness = is12 ? 11 : 7;
            }

            var brush = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            var pen = new Pen(brush, thickness, lineCap: PenLineCap.Round);
            context.DrawLine(pen, p1, p2);
        }
    }
}
