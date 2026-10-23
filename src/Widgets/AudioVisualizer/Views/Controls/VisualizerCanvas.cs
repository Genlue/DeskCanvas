using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AudioVisualizer.Models;
using AudioVisualizer.Services;

namespace AudioVisualizer.Views.Controls;

/// <summary>
/// 高性能自适应音频律动画布控件 (重写 Render，支持任意尺寸与形状)
/// </summary>
public class VisualizerCanvas : Control
{
    private AudioVisualizerModel model = new();
    private readonly float[] spectrumBands = new float[64];
    private List<SampledPoint>? cachedSampledPoints;
    private Size lastSampledSize = default;

    public AudioVisualizerModel Model
    {
        get => model;
        set
        {
            model = value;
            InvalidateGeometryCache();
            InvalidateVisual();
        }
    }

    public void UpdateSpectrum(ReadOnlySpan<float> newBands)
    {
        int copyLen = Math.Min(newBands.Length, spectrumBands.Length);
        newBands[..copyLen].CopyTo(spectrumBands);
        InvalidateVisual();
    }

    public void InvalidateGeometryCache()
    {
        cachedSampledPoints = null;
        lastSampledSize = default;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateGeometryCache();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        if (bounds.Width < 10 || bounds.Height < 10)
            return;

        // 重新采样点集
        if (cachedSampledPoints == null || lastSampledSize != bounds.Size)
        {
            cachedSampledPoints = PathGeometryHelper.SamplePath(
                model.Points,
                model.IsClosedShape,
                model.DrawMode,
                bounds.Size,
                model.BarCount);
            lastSampledSize = bounds.Size;
        }

        if (cachedSampledPoints == null || cachedSampledPoints.Count < 2)
            return;

        // 解析颜色与画笔
        ResolveThemeColors(out Color startColor, out Color endColor);

        // 1. 若开启，绘制原基准线
        if (model.ShowBaseline)
        {
            var baseGeom = PathGeometryHelper.BuildBaseGeometry(cachedSampledPoints, model.IsClosedShape);
            var basePen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 1.5, dashStyle: DashStyle.Dash);
            context.DrawGeometry(null, basePen, baseGeom);
        }

        int count = cachedSampledPoints.Count;
        var activeMagnitudes = spectrumBands.AsSpan(0, Math.Min(count, spectrumBands.Length));

        // 2. 根据表现形式进行渲染
        switch (model.VisualizerStyle)
        {
            case VisualizerStyle.Bars:
                RenderBars(context, cachedSampledPoints, activeMagnitudes, startColor, endColor);
                break;

            case VisualizerStyle.LineWave:
                RenderLineWave(context, cachedSampledPoints, activeMagnitudes, startColor, endColor);
                break;

            case VisualizerStyle.AreaWave:
                RenderAreaWave(context, cachedSampledPoints, activeMagnitudes, startColor, endColor);
                break;

            case VisualizerStyle.Dots:
                RenderDots(context, cachedSampledPoints, activeMagnitudes, startColor, endColor);
                break;
        }
    }

    private void RenderBars(
        DrawingContext context,
        List<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        Color startColor,
        Color endColor)
    {
        var bars = PathGeometryHelper.BuildBars(
            samples,
            magnitudes,
            model.RhythmDirection,
            model.MaxAmplitude,
            minBarHeight: 4.0);

        double strokeWidth = Math.Max(1.5, model.StrokeWidth);
        int total = bars.Count;

        for (int i = 0; i < total; i++)
        {
            double t = total > 1 ? (double)i / (total - 1) : 0.0;
            Color barColor = InterpolateColor(startColor, endColor, t);

            var (start, end) = bars[i];

            // 发光晕影
            if (model.ShowGlow)
            {
                var glowPen = new Pen(
                    new SolidColorBrush(Color.FromArgb(40, barColor.R, barColor.G, barColor.B)),
                    strokeWidth + 5.0,
                    lineCap: PenLineCap.Round);
                context.DrawLine(glowPen, start, end);
            }

            // 实体律动柱
            var barPen = new Pen(
                new SolidColorBrush(barColor),
                strokeWidth,
                lineCap: PenLineCap.Round);
            context.DrawLine(barPen, start, end);
        }
    }

    private void RenderLineWave(
        DrawingContext context,
        List<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        Color startColor,
        Color endColor)
    {
        var waveGeom = PathGeometryHelper.BuildLineWaveGeometry(
            samples,
            magnitudes,
            model.RhythmDirection,
            model.MaxAmplitude,
            model.IsClosedShape);

        var gradBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(startColor, 0.0),
                new GradientStop(endColor, 1.0)
            ]
        };

        double strokeWidth = Math.Max(1.5, model.StrokeWidth);

        // 外发光层
        if (model.ShowGlow)
        {
            var glowBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                [
                    new GradientStop(Color.FromArgb(55, startColor.R, startColor.G, startColor.B), 0.0),
                    new GradientStop(Color.FromArgb(55, endColor.R, endColor.G, endColor.B), 1.0)
                ]
            };
            var glowPen = new Pen(glowBrush, strokeWidth + 6.0, lineCap: PenLineCap.Round);
            context.DrawGeometry(null, glowPen, waveGeom);
        }

        // 主线条
        var mainPen = new Pen(gradBrush, strokeWidth, lineCap: PenLineCap.Round);
        context.DrawGeometry(null, mainPen, waveGeom);
    }

    private void RenderAreaWave(
        DrawingContext context,
        List<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        Color startColor,
        Color endColor)
    {
        var areaGeom = PathGeometryHelper.BuildAreaWaveGeometry(
            samples,
            magnitudes,
            model.RhythmDirection,
            model.MaxAmplitude,
            model.IsClosedShape);

        var areaBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(Color.FromArgb(120, startColor.R, startColor.G, startColor.B), 0.0),
                new GradientStop(Color.FromArgb(30, endColor.R, endColor.G, endColor.B), 1.0)
            ]
        };

        context.DrawGeometry(areaBrush, null, areaGeom);

        // 边缘描边
        RenderLineWave(context, samples, magnitudes, startColor, endColor);
    }

    private void RenderDots(
        DrawingContext context,
        List<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        Color startColor,
        Color endColor)
    {
        var bars = PathGeometryHelper.BuildBars(
            samples,
            magnitudes,
            model.RhythmDirection,
            model.MaxAmplitude,
            minBarHeight: 4.0);

        double radius = Math.Max(2.0, model.StrokeWidth * 0.7);
        int total = bars.Count;

        for (int i = 0; i < total; i++)
        {
            double t = total > 1 ? (double)i / (total - 1) : 0.0;
            Color dotColor = InterpolateColor(startColor, endColor, t);
            Point tip = bars[i].End;

            if (model.ShowGlow)
            {
                var glowBrush = new SolidColorBrush(Color.FromArgb(50, dotColor.R, dotColor.G, dotColor.B));
                context.DrawEllipse(glowBrush, null, tip, radius + 4.0, radius + 4.0);
            }

            var brush = new SolidColorBrush(dotColor);
            context.DrawEllipse(brush, null, tip, radius, radius);

            // 如果是双向，另一侧也绘制光点
            if (model.RhythmDirection == RhythmDirection.Bidirectional)
            {
                Point startTip = bars[i].Start;
                if (model.ShowGlow)
                {
                    var glowBrush = new SolidColorBrush(Color.FromArgb(50, dotColor.R, dotColor.G, dotColor.B));
                    context.DrawEllipse(glowBrush, null, startTip, radius + 4.0, radius + 4.0);
                }
                context.DrawEllipse(brush, null, startTip, radius, radius);
            }
        }
    }

    private void ResolveThemeColors(out Color startColor, out Color endColor)
    {
        switch (model.ColorTheme)
        {
            case ColorThemePreset.Cyberpunk:
                startColor = Color.Parse("#00F0FF"); // 赛博霓虹青
                endColor = Color.Parse("#FF007F");   // 霓虹荧光粉
                break;
            case ColorThemePreset.Aurora:
                startColor = Color.Parse("#00FF87"); // 极光荧光绿
                endColor = Color.Parse("#60EFFF");   // 极光淡天蓝
                break;
            case ColorThemePreset.Sunset:
                startColor = Color.Parse("#FF416C"); // 暖阳绯红
                endColor = Color.Parse("#FF4B2B");   // 熔岩日落金
                break;
            case ColorThemePreset.Glacier:
                startColor = Color.Parse("#00D2FF"); // 科技冰蓝
                endColor = Color.Parse("#FFFFFF");   // 冰川雪白
                break;
            case ColorThemePreset.Violet:
                startColor = Color.Parse("#8A2387"); // 暮夜深紫
                endColor = Color.Parse("#F27121");   // 绚丽电紫橙
                break;
            case ColorThemePreset.MinimalWhite:
                startColor = Color.Parse("#FFFFFF");
                endColor = Color.Parse("#C0C0C0");
                break;
            default:
                if (!Color.TryParse(model.StartColorHex, out startColor))
                    startColor = Color.Parse("#00E5FF");
                if (!Color.TryParse(model.EndColorHex, out endColor))
                    endColor = Color.Parse("#FF007F");
                break;
        }
    }

    private static Color InterpolateColor(Color c1, Color c2, double t)
    {
        byte a = (byte)(c1.A + (c2.A - c1.A) * t);
        byte r = (byte)(c1.R + (c2.R - c1.R) * t);
        byte g = (byte)(c1.G + (c2.G - c1.G) * t);
        byte b = (byte)(c1.B + (c2.B - c1.B) * t);
        return Color.FromArgb(a, r, g, b);
    }
}
