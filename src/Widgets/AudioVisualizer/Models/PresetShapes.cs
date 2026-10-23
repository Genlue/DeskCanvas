using System;
using System.Collections.Generic;

namespace AudioVisualizer.Models;

/// <summary>
/// 预设经典几何与艺术图形发生器
/// </summary>
public static class PresetShapes
{
    /// <summary>
    /// 经典水平直线基线
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateHorizontalLine()
    {
        return (
            [
                new(0.06, 0.75),
                new(0.30, 0.75),
                new(0.50, 0.75),
                new(0.70, 0.75),
                new(0.94, 0.75)
            ],
            false,
            DrawToolMode.Line
        );
    }

    /// <summary>
    /// 居中水平对称线 (双向律动绝配)
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateCenterLine()
    {
        return (
            [
                new(0.06, 0.50),
                new(0.30, 0.50),
                new(0.50, 0.50),
                new(0.70, 0.50),
                new(0.94, 0.50)
            ],
            false,
            DrawToolMode.Line
        );
    }

    /// <summary>
    /// 正弦波浪曲线
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateSineWave()
    {
        return (
            [
                new(0.06, 0.65),
                new(0.24, 0.40),
                new(0.50, 0.65),
                new(0.76, 0.40),
                new(0.94, 0.60)
            ],
            false,
            DrawToolMode.Curve
        );
    }

    /// <summary>
    /// 动感闭合圆环
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateCircle(int segments = 24)
    {
        var points = new List<PathPoint>(segments);
        double cx = 0.5;
        double cy = 0.5;
        double rx = 0.32;
        double ry = 0.36; // 稍微考虑长宽比

        for (int i = 0; i < segments; i++)
        {
            double angle = i * 2.0 * Math.PI / segments;
            double x = cx + rx * Math.Cos(angle);
            double y = cy + ry * Math.Sin(angle);
            points.Add(new PathPoint(Math.Round(x, 4), Math.Round(y, 4)));
        }

        return (points, true, DrawToolMode.Curve);
    }

    /// <summary>
    /// 浪漫律动爱心 (闭合曲线)
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateHeart(int segments = 28)
    {
        var points = new List<PathPoint>(segments);
        for (int i = 0; i < segments; i++)
        {
            double t = i * 2.0 * Math.PI / segments;
            // 经典心形参数方程: x = 16 sin^3(t), y = -(13 cos(t) - 5 cos(2t) - 2 cos(3t) - cos(4t))
            double rawX = 16.0 * Math.Pow(Math.Sin(t), 3);
            double rawY = -(13.0 * Math.Cos(t) - 5.0 * Math.Cos(2 * t) - 2.0 * Math.Cos(3 * t) - Math.Cos(4 * t));

            // 映射到 [0.15 ~ 0.85] 范围
            double x = 0.50 + (rawX / 17.0) * 0.36;
            double y = 0.52 + (rawY / 17.0) * 0.36;
            points.Add(new PathPoint(Math.Round(x, 4), Math.Round(y, 4)));
        }

        return (points, true, DrawToolMode.Curve);
    }

    /// <summary>
    /// 科技八边形 (闭合折线)
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateOctagon()
    {
        var points = new List<PathPoint>(8);
        double cx = 0.5;
        double cy = 0.5;
        double r = 0.36;

        for (int i = 0; i < 8; i++)
        {
            double angle = (i * 2.0 * Math.PI / 8) + (Math.PI / 8);
            double x = cx + r * Math.Cos(angle);
            double y = cy + r * Math.Sin(angle);
            points.Add(new PathPoint(Math.Round(x, 4), Math.Round(y, 4)));
        }

        return (points, true, DrawToolMode.Line);
    }

    /// <summary>
    /// 拱桥曲线 (优雅弧形)
    /// </summary>
    public static (List<PathPoint> Points, bool IsClosed, DrawToolMode Mode) CreateArch()
    {
        return (
            [
                new(0.08, 0.85),
                new(0.25, 0.45),
                new(0.50, 0.30),
                new(0.75, 0.45),
                new(0.92, 0.85)
            ],
            false,
            DrawToolMode.Curve
        );
    }
}
