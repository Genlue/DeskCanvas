namespace AudioVisualizer.Models;

/// <summary>
/// 归一化路径节点坐标 (取值范围 0.0 ~ 1.0，根据组件任意尺寸自适应缩放映射)
/// </summary>
public record PathPoint(double X, double Y)
{
    public static PathPoint Lerp(PathPoint a, PathPoint b, double t) =>
        new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    public double DistanceTo(PathPoint other)
    {
        double dx = X - other.X;
        double dy = Y - other.Y;
        return System.Math.Sqrt(dx * dx + dy * dy);
    }
}
