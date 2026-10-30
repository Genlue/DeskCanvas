using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using AudioVisualizer.Models;

namespace AudioVisualizer.Services;

/// <summary>
/// 采样点信息：包含空间坐标、切线与法线单位向量
/// </summary>
public record SampledPoint(Point Position, Vector Tangent, Vector Normal);

/// <summary>
/// 路径样条插值与法向律动几何发生器
/// </summary>
public static class PathGeometryHelper
{
    /// <summary>
    /// 对归一化控制点按组件实际像素尺寸采样并生成切线与法线
    /// </summary>
    public static List<SampledPoint> SamplePath(
        IReadOnlyList<PathPoint> points,
        bool isClosed,
        DrawToolMode mode,
        Size targetSize,
        int sampleCount)
    {
        if (points == null || points.Count < 2 || sampleCount < 2)
            return [];

        double w = Math.Max(10.0, targetSize.Width);
        double h = Math.Max(10.0, targetSize.Height);

        // 1. 将归一化控制点转为目标实际像素坐标
        var pixelPoints = new List<Point>(points.Count);
        double sumX = 0, sumY = 0;
        foreach (var p in points)
        {
            double px = Math.Clamp(p.X * w, 0.0, w);
            double py = Math.Clamp(p.Y * h, 0.0, h);
            pixelPoints.Add(new Point(px, py));
            sumX += px;
            sumY += py;
        }

        Point centroid = new(sumX / pixelPoints.Count, sumY / pixelPoints.Count);

        // 2. 密集插值曲线或折线
        var densePoints = InterpolateDensePoints(pixelPoints, isClosed, mode);
        if (densePoints.Count < 2)
            return [];

        // 3. 计算弧长累积表
        var distances = new double[densePoints.Count];
        distances[0] = 0;
        for (int i = 1; i < densePoints.Count; i++)
        {
            double dx = densePoints[i].X - densePoints[i - 1].X;
            double dy = densePoints[i].Y - densePoints[i - 1].Y;
            distances[i] = distances[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        double totalLength = distances[^1];
        if (totalLength <= 0.001)
            return [];

        // 4. 等距采样生成 SampledPoint
        var result = new List<SampledPoint>(sampleCount);
        for (int i = 0; i < sampleCount; i++)
        {
            double targetDist = isClosed
                ? (double)i / sampleCount * totalLength
                : (double)i / (sampleCount - 1) * totalLength;

            // 二分查找对应段
            int idx = Array.BinarySearch(distances, targetDist);
            if (idx < 0) idx = ~idx - 1;
            idx = Math.Clamp(idx, 0, densePoints.Count - 2);

            double segLen = distances[idx + 1] - distances[idx];
            double t = segLen > 0.0001 ? (targetDist - distances[idx]) / segLen : 0.0;

            Point p0 = densePoints[idx];
            Point p1 = densePoints[idx + 1];
            Point pos = new(p0.X + (p1.X - p0.X) * t, p0.Y + (p1.Y - p0.Y) * t);

            double tx = p1.X - p0.X;
            double ty = p1.Y - p0.Y;
            double tlen = Math.Sqrt(tx * tx + ty * ty);
            if (tlen > 0.0001)
            {
                tx /= tlen;
                ty /= tlen;
            }
            else
            {
                tx = 1.0;
                ty = 0.0;
            }

            Vector tangent = new(tx, ty);
            // 垂直切线的单位法向量 (-ty, tx)
            Vector normal = new(-ty, tx);

            // 若闭合图形，确保法向量向外辐射 (离开中心重心)
            if (isClosed)
            {
                double dOutX = pos.X + normal.X - centroid.X;
                double dOutY = pos.Y + normal.Y - centroid.Y;
                double dInX = pos.X - normal.X - centroid.X;
                double dInY = pos.Y - normal.Y - centroid.Y;
                if ((dOutX * dOutX + dOutY * dOutY) < (dInX * dInX + dInY * dInY))
                {
                    normal = new Vector(-normal.X, -normal.Y);
                }
            }
            else
            {
                // 开放线条默认法线向上 (Y轴朝上为负)
                if (normal.Y > 0 && Math.Abs(normal.Y) > Math.Abs(normal.X) * 0.4)
                {
                    normal = new Vector(-normal.X, -normal.Y);
                }
            }

            result.Add(new SampledPoint(pos, tangent, normal));
        }

        return result;
    }

    private static List<Point> InterpolateDensePoints(List<Point> pts, bool isClosed, DrawToolMode mode)
    {
        if (pts.Count < 2) return pts;

        if (mode == DrawToolMode.Line)
        {
            var res = new List<Point>(pts);
            if (isClosed) res.Add(pts[0]);
            return res;
        }

        // Catmull-Rom 样条插值生成平滑连续曲线
        var result = new List<Point>();
        int count = pts.Count;
        int segments = isClosed ? count : count - 1;
        const int stepsPerSegment = 12;

        for (int i = 0; i < segments; i++)
        {
            Point p0 = GetPoint(pts, i - 1, isClosed);
            Point p1 = GetPoint(pts, i, isClosed);
            Point p2 = GetPoint(pts, i + 1, isClosed);
            Point p3 = GetPoint(pts, i + 2, isClosed);

            for (int s = 0; s < stepsPerSegment; s++)
            {
                double t = (double)s / stepsPerSegment;
                double t2 = t * t;
                double t3 = t2 * t;

                double x = 0.5 * ((2.0 * p1.X) +
                                  (-p0.X + p2.X) * t +
                                  (2.0 * p0.X - 5.0 * p1.X + 4.0 * p2.X - p3.X) * t2 +
                                  (-p0.X + 3.0 * p1.X - 3.0 * p2.X + p3.X) * t3);

                double y = 0.5 * ((2.0 * p1.Y) +
                                  (-p0.Y + p2.Y) * t +
                                  (2.0 * p0.Y - 5.0 * p1.Y + 4.0 * p2.Y - p3.Y) * t2 +
                                  (-p0.Y + 3.0 * p1.Y - 3.0 * p2.Y + p3.Y) * t3);

                result.Add(new Point(x, y));
            }
        }

        if (!isClosed)
        {
            result.Add(pts[^1]);
        }
        else
        {
            result.Add(pts[0]);
        }

        return result;
    }

    private static Point GetPoint(List<Point> pts, int index, bool isClosed)
    {
        int n = pts.Count;
        if (isClosed)
        {
            int mod = (index % n + n) % n;
            return pts[mod];
        }
        return pts[Math.Clamp(index, 0, n - 1)];
    }

    /// <summary>
    /// 生成柱状图律动的所有柱体线段 (起点与终点)
    /// </summary>
    public static List<(Point Start, Point End)> BuildBars(
        IReadOnlyList<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        RhythmDirection direction,
        double maxAmplitude,
        double minBarHeight = 3.0)
    {
        var bars = new List<(Point, Point)>(samples.Count);
        int magLen = magnitudes.Length;

        for (int i = 0; i < samples.Count; i++)
        {
            var sp = samples[i];
            float mag = i < magLen ? magnitudes[i] : 0f;
            double h = Math.Max(minBarHeight, mag * maxAmplitude);

            if (direction == RhythmDirection.Unidirectional)
            {
                // 单向：基线向外/向上
                Point start = sp.Position;
                Point end = new(start.X + sp.Normal.X * h, start.Y + sp.Normal.Y * h);
                bars.Add((start, end));
            }
            else
            {
                // 双向：基线两侧对称起伏
                double half = h * 0.5;
                Point start = new(sp.Position.X - sp.Normal.X * half, sp.Position.Y - sp.Normal.Y * half);
                Point end = new(sp.Position.X + sp.Normal.X * half, sp.Position.Y + sp.Normal.Y * half);
                bars.Add((start, end));
            }
        }

        return bars;
    }

    /// <summary>
    /// 生成波浪线条律动几何 (StreamGeometry)
    /// </summary>
    public static StreamGeometry BuildLineWaveGeometry(
        IReadOnlyList<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        RhythmDirection direction,
        double maxAmplitude,
        bool isClosed)
    {
        var geometry = new StreamGeometry();
        if (samples.Count < 2) return geometry;

        int magLen = magnitudes.Length;

        if (direction == RhythmDirection.Unidirectional)
        {
            // 单向波浪
            var wavePoints = new List<Point>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                var sp = samples[i];
                float mag = i < magLen ? magnitudes[i] : 0f;
                double h = mag * maxAmplitude;
                wavePoints.Add(new Point(sp.Position.X + sp.Normal.X * h, sp.Position.Y + sp.Normal.Y * h));
            }

            using var ctx = geometry.Open();
            DrawSmoothCurve(ctx, wavePoints, isClosed);
        }
        else
        {
            // 双向：双层对称波浪
            var upperPoints = new List<Point>(samples.Count);
            var lowerPoints = new List<Point>(samples.Count);

            for (int i = 0; i < samples.Count; i++)
            {
                var sp = samples[i];
                float mag = i < magLen ? magnitudes[i] : 0f;
                double half = mag * maxAmplitude * 0.5;
                upperPoints.Add(new Point(sp.Position.X + sp.Normal.X * half, sp.Position.Y + sp.Normal.Y * half));
                lowerPoints.Add(new Point(sp.Position.X - sp.Normal.X * half, sp.Position.Y - sp.Normal.Y * half));
            }

            using var ctx = geometry.Open();
            DrawSmoothCurve(ctx, upperPoints, isClosed);
            DrawSmoothCurve(ctx, lowerPoints, isClosed);
        }

        return geometry;
    }

    /// <summary>
    /// 生成波浪区域闭合填充几何 (可填充半透明渐变)
    /// </summary>
    public static StreamGeometry BuildAreaWaveGeometry(
        IReadOnlyList<SampledPoint> samples,
        ReadOnlySpan<float> magnitudes,
        RhythmDirection direction,
        double maxAmplitude,
        bool isClosed)
    {
        var geometry = new StreamGeometry();
        if (samples.Count < 2) return geometry;

        int magLen = magnitudes.Length;
        using var ctx = geometry.Open();

        if (direction == RhythmDirection.Unidirectional)
        {
            // 单向：从基线到起伏波浪闭合面
            var wavePoints = new List<Point>(samples.Count);
            for (int i = 0; i < samples.Count; i++)
            {
                var sp = samples[i];
                float mag = i < magLen ? magnitudes[i] : 0f;
                double h = mag * maxAmplitude;
                wavePoints.Add(new Point(sp.Position.X + sp.Normal.X * h, sp.Position.Y + sp.Normal.Y * h));
            }

            ctx.BeginFigure(samples[0].Position, true);
            for (int i = 0; i < wavePoints.Count; i++)
            {
                ctx.LineTo(wavePoints[i]);
            }
            if (!isClosed)
            {
                for (int i = samples.Count - 1; i >= 0; i--)
                {
                    ctx.LineTo(samples[i].Position);
                }
            }
            ctx.EndFigure(true);
        }
        else
        {
            // 双向：上层波浪到下层波浪包络面
            var upper = new List<Point>(samples.Count);
            var lower = new List<Point>(samples.Count);

            for (int i = 0; i < samples.Count; i++)
            {
                var sp = samples[i];
                float mag = i < magLen ? magnitudes[i] : 0f;
                double half = mag * maxAmplitude * 0.5;
                upper.Add(new Point(sp.Position.X + sp.Normal.X * half, sp.Position.Y + sp.Normal.Y * half));
                lower.Add(new Point(sp.Position.X - sp.Normal.X * half, sp.Position.Y - sp.Normal.Y * half));
            }

            ctx.BeginFigure(upper[0], true);
            for (int i = 0; i < upper.Count; i++)
            {
                ctx.LineTo(upper[i]);
            }
            for (int i = lower.Count - 1; i >= 0; i--)
            {
                ctx.LineTo(lower[i]);
            }
            ctx.EndFigure(true);
        }

        return geometry;
    }

    /// <summary>
    /// 构建原始绘制基线几何
    /// </summary>
    public static StreamGeometry BuildBaseGeometry(IReadOnlyList<SampledPoint> samples, bool isClosed)
    {
        var geom = new StreamGeometry();
        if (samples.Count < 2) return geom;

        using var ctx = geom.Open();
        ctx.BeginFigure(samples[0].Position, isClosed);
        for (int i = 1; i < samples.Count; i++)
        {
            ctx.LineTo(samples[i].Position);
        }
        ctx.EndFigure(isClosed);
        return geom;
    }

    private static void DrawSmoothCurve(StreamGeometryContext ctx, List<Point> pts, bool isClosed)
    {
        if (pts.Count < 2) return;
        ctx.BeginFigure(pts[0], isClosed);

        for (int i = 0; i < pts.Count - 1; i++)
        {
            Point p0 = pts[i];
            Point p1 = pts[i + 1];
            Point mid = new((p0.X + p1.X) * 0.5, (p0.Y + p1.Y) * 0.5);
            ctx.QuadraticBezierTo(p0, mid);
        }

        ctx.LineTo(pts[^1]);
        ctx.EndFigure(isClosed);
    }
}
