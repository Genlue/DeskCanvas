using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// The static optics of 液态玻璃's outline highlight, shared by every material that is not
/// drawn by the wallpaper-sampled renderer.
/// <para>
/// The source of truth is the highlight block of <c>LiquidGlassEffect</c>'s SkSL (and its CPU
/// twin): a hairline along the rounded outline whose brightness is
/// <c>|dot(edgeNormal, lightDirection)| ^ falloff</c>. On the sampled material that is evaluated
/// per pixel inside the shader; this control evaluates the same expression per rim segment so
/// 毛玻璃 (OS acrylic) and 全彩 (solid fill) get the identical double-rim specular instead of the
/// removed 描边 ring. Distance and strength conventions are kept in one place here so the three
/// materials cannot drift apart.
/// </para>
/// </summary>
public static class SpecularRim
{
    /// <summary>Light-direction falloff exponent (matches <c>LiquidGlassSettings.HighlightFalloff</c>).</summary>
    public const double Falloff = 1.0;

    /// <summary>Rim thickness in device pixels (the shader's clipped hairline is ~1 px).</summary>
    public const double ThicknessDevicePixels = 1.0;

    /// <summary>Length of one approximation segment, in DIPs. Chord error at a 24 DIP corner is ~0.05 DIP.</summary>
    public const double SegmentLengthDips = 1.6;

    /// <summary>
    /// Brightness of the outline at a point whose outward unit normal is
    /// (<paramref name="normalX"/>, <paramref name="normalY"/>), for a light direction of
    /// <paramref name="angleDegrees"/> in screen coordinates (0° = +X/right, 90° = +Y/down, the
    /// same convention as <c>Theme.EffectiveLiquidGlass.HighlightAngle</c>).
    /// </summary>
    public static double Intensity(double normalX, double normalY, double angleDegrees, double falloff = Falloff)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var dot = Math.Abs(normalX * Math.Cos(radians) + normalY * Math.Sin(radians));
        return falloff == 1.0 ? Math.Min(1.0, dot) : Math.Pow(dot, falloff);
    }
}

/// <summary>
/// A hairline specular highlight along a rounded rectangle's outline, lit by an adjustable
/// direction — the non-sampled-surface twin of the 液态玻璃 shader's outline stroke.
/// <para>
/// Rendered as short flat-capped segments offset inside the card so the whole stroke stays within
/// the rounded bounds (the shader's stroke is clipped by the card mask the same way). The segment
/// table is rebuilt only when the geometry or the angle changes; rotation of the light therefore
/// stays a cheap repaint.
/// </para>
/// </summary>
public sealed class SpecularHighlight : Control
{
    /// <summary>Corner radius of the card the rim is drawn on.</summary>
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<SpecularHighlight, CornerRadius>(nameof(CornerRadius));

    /// <summary>Highlight strength 0-100; 100 = the shader's full white stroke.</summary>
    public static readonly StyledProperty<double> StrengthProperty =
        AvaloniaProperty.Register<SpecularHighlight, double>(nameof(Strength), 50.0);

    /// <summary>Light direction in degrees (0-360, screen coordinates; 90 = straight down).</summary>
    public static readonly StyledProperty<double> AngleProperty =
        AvaloniaProperty.Register<SpecularHighlight, double>(nameof(Angle), 90.0);

    /// <summary>Intensity buckets: one polyline geometry per 1/64 of the highlight's brightness.</summary>
    private const int Buckets = 64;

    private readonly Dictionary<int, StreamGeometry> runs = [];
    private readonly Dictionary<int, IPen> pens = [];
    private double builtWidth = -1, builtHeight = -1, builtRadius = -1, builtAngle = double.NaN, builtThickness = -1;
    private double penThickness = -1;

    static SpecularHighlight()
    {
        AffectsRender<SpecularHighlight>(CornerRadiusProperty, StrengthProperty, AngleProperty);
    }

    public SpecularHighlight()
    {
        // The rim is decoration: it must never intercept presses meant for the card beneath.
        IsHitTestVisible = false;
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double Strength
    {
        get => GetValue(StrengthProperty);
        set => SetValue(StrengthProperty, value);
    }

    public double Angle
    {
        get => GetValue(AngleProperty);
        set => SetValue(AngleProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width < 3 || height < 3) return;

        var strength = Math.Clamp(Strength, 0.0, 100.0) / 100.0;
        if (strength <= 0.001) return;

        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
        var thickness = Math.Max(0.5, SpecularRim.ThicknessDevicePixels / Math.Max(scaling, 0.1));
        var maxRadius = Math.Max(0.0, Math.Min(width, height) / 2.0 - thickness);
        var radius = Math.Clamp(CornerRadius.TopLeft, 0.0, maxRadius);
        var angle = Math.Clamp(Angle, 0.0, 360.0);

        if (width != builtWidth || height != builtHeight || radius != builtRadius
            || angle != builtAngle || thickness != builtThickness)
        {
            Build(width, height, radius, angle, thickness);
        }

        if (Math.Abs(thickness - penThickness) > 1e-6)
        {
            pens.Clear();
            penThickness = thickness;
        }

        // One stroked geometry per brightness bucket rather than one draw call per segment: a
        // 4×4 card's rim is ~850 segments, which is fine to build once but not to hand the
        // compositor 850 times on every theme switch.
        foreach (var (level, geometry) in runs)
        {
            var alphaLevel = (int)Math.Round(Math.Clamp(level / (double)Buckets * strength, 0.0, 1.0) * 255.0);
            if (alphaLevel <= 0) continue;
            if (!pens.TryGetValue(alphaLevel, out var pen))
            {
                pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)alphaLevel, 255, 255, 255)), thickness);
                pens[alphaLevel] = pen;
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>
    /// Walks the inset rounded rectangle once and stores the rim as flat segments, each carrying
    /// the intensity of its own normal. The path is inset by half the pen so the outer edge of
    /// the stroke lands exactly on the card boundary — the shader's inner-only visible band.
    /// </summary>
    private void Build(double width, double height, double radius, double angle, double thickness)
    {
        builtWidth = width;
        builtHeight = height;
        builtRadius = radius;
        builtAngle = angle;
        builtThickness = thickness;
        runs.Clear();

        var inset = thickness / 2.0;
        var left = inset;
        var top = inset;
        var right = width - inset;
        var bottom = height - inset;
        var corner = Math.Max(0.0, radius - inset);
        var points = new List<RimPoint>();

        void Edge(Point from, Point to, double nx, double ny)
        {
            var length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
            var count = Math.Max(1, (int)Math.Ceiling(length / SpecularRim.SegmentLengthDips));
            for (var i = 0; i <= count; i++)
            {
                var t = (double)i / count;
                points.Add(new RimPoint(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, nx, ny));
            }
        }

        void Arc(double cx, double cy, double startDegrees, double endDegrees)
        {
            var length = Math.Abs(endDegrees - startDegrees) * Math.PI / 180.0 * corner;
            var count = Math.Max(1, (int)Math.Ceiling(length / SpecularRim.SegmentLengthDips));
            for (var i = 0; i <= count; i++)
            {
                var degrees = startDegrees + (endDegrees - startDegrees) * i / count;
                var radians = degrees * Math.PI / 180.0;
                var nx = Math.Cos(radians);
                var ny = Math.Sin(radians);
                points.Add(new RimPoint(cx + corner * nx, cy + corner * ny, nx, ny));
            }
        }

        Edge(new Point(left + corner, top), new Point(right - corner, top), 0, -1);
        Arc(right - corner, top + corner, -90, 0);
        Edge(new Point(right, top + corner), new Point(right, bottom - corner), 1, 0);
        Arc(right - corner, bottom - corner, 0, 90);
        Edge(new Point(right - corner, bottom), new Point(left + corner, bottom), 0, 1);
        Arc(left + corner, bottom - corner, 90, 180);
        Edge(new Point(left, bottom - corner), new Point(left, top + corner), -1, 0);
        Arc(left + corner, top + corner, 180, 270);

        var buckets = new List<(Point Start, Point End)>[Buckets + 1];
        for (var i = 1; i < points.Count; i++)
        {
            var from = points[i - 1];
            var to = points[i];
            if (Math.Abs(from.X - to.X) < 1e-9 && Math.Abs(from.Y - to.Y) < 1e-9) continue;

            // Intensity is evaluated at the segment's midpoint normal so a corner arc turns
            // smoothly instead of stepping once per segment.
            var nx = (from.NX + to.NX) / 2.0;
            var ny = (from.NY + to.NY) / 2.0;
            var length = Math.Sqrt(nx * nx + ny * ny);
            if (length > 1e-9)
            {
                nx /= length;
                ny /= length;
            }

            var level = (int)Math.Round(SpecularRim.Intensity(nx, ny, angle) * Buckets);
            (buckets[level] ??= []).Add((new Point(from.X, from.Y), new Point(to.X, to.Y)));
        }

        for (var level = 0; level <= Buckets; level++)
        {
            if (buckets[level] is not { Count: > 0 } bucket) continue;
            var geometry = new StreamGeometry();
            using (var writer = geometry.Open())
            {
                foreach (var (start, end) in bucket)
                {
                    writer.BeginFigure(start, isFilled: false);
                    writer.LineTo(end);
                    writer.EndFigure(isClosed: false);
                }
            }
            runs[level] = geometry;
        }
    }

    private readonly record struct RimPoint(double X, double Y, double NX, double NY);
}
