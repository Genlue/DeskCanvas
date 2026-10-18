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

    /// <summary>
    /// Distance from the card edge to the stroke centreline, in device pixels.
    /// <para>
    /// 0.5 would centre the 1 px band on the card's outermost row. That row is also the
    /// window's outermost row, and on 毛玻璃 the HWND carries a native 1-bit region whose
    /// last row/column the OS can lose (GDI's <c>CreateRoundRectRgn</c> drops it — see
    /// <c>InteropService.SetWindowRegion</c>). Even with that compensated, keeping the
    /// hairline one pixel off the clip boundary is cheap insurance: the bottom/right half of
    /// the outline can never share a row with the mask edge, whatever the compositor does
    /// with it. The stroke therefore walks the rounded rect 1.5 px inside the card edge
    /// (outer edge at 1 px), which at every DPI still reads as a hairline along the outline.
    /// </para>
    /// </summary>
    private const double CenterlineInsetDevicePixels = 1.5;

    private readonly Dictionary<int, StreamGeometry> runs = [];
    private readonly Dictionary<int, IPen> pens = [];
    private int builtWidth = -1, builtHeight = -1, builtRadius = -1;
    private double builtAngle = double.NaN, builtScaling = -1.0, penThickness = -1;

    static SpecularHighlight()
    {
        AffectsRender<SpecularHighlight>(CornerRadiusProperty, StrengthProperty, AngleProperty);
    }

    public SpecularHighlight()
    {
        // The rim is decoration: it must never intercept presses meant for the card beneath.
        IsHitTestVisible = false;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // Bounds is not an affecting property, and a size change alone (fractional grid
        // resizes, a margin change, a DPI change) has to repaint: the old raster is laid out
        // for the previous device-pixel grid. IsVisible false → true happens on every
        // material switch that hides/shows the rim, and the control can be toggled back on
        // without any geometry or angle change — repaint it unconditionally.
        if (change.Property == BoundsProperty)
        {
            var bounds = change.GetNewValue<Rect>();
            if (bounds != change.GetOldValue<Rect>())
                InvalidateVisual();
        }
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The surface the rim belongs to is (re)created with the visual tree; the cached
        // segment table stays valid, but the pixels must be produced again.
        InvalidateVisual();
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
        if (!(scaling > 0.0)) scaling = 1.0;

        // The rim is a one-device-pixel hairline, so its band has to land on whole pixel
        // rows/columns. The card is laid out from fractional DIP bounds (grid percentages
        // times scaling), so anchoring the stroke at "0" and "height" put the bottom and
        // right strokes across two pixel rows at ~50 % each. Snap the outline to the
        // device-pixel grid: strip the card's fractional pixel and place the stroke centres
        // on whole-pixel offsets inside the card (see CenterlineInsetDevicePixels — 1.5 px,
        // i.e. one pixel clear of the card edge), so every edge gets one full pixel
        // row/column inside the card and none of them shares a row with the window/mask
        // boundary. (Floor, not round: the control's own AA edge pixel must stay outside the
        // stroke.)
        var pxWidth = (int)Math.Floor(width * scaling);
        var pxHeight = (int)Math.Floor(height * scaling);
        if (pxWidth < 1 || pxHeight < 1) return;

        var pxRadius = (int)Math.Round(CornerRadius.TopLeft * scaling, MidpointRounding.AwayFromZero);
        pxRadius = Math.Clamp(pxRadius, 0, Math.Min(pxWidth, pxHeight) / 2);
        var angle = Math.Clamp(Angle, 0.0, 360.0);

        if (pxWidth != builtWidth || pxHeight != builtHeight || pxRadius != builtRadius
            || angle != builtAngle || scaling != builtScaling)
        {
            Build(pxWidth, pxHeight, pxRadius, angle, scaling);
        }

        // Exactly one device pixel, whatever the DPI scaling.
        var thickness = SpecularRim.ThicknessDevicePixels / scaling;
        if (Math.Abs(thickness - penThickness) > 1e-9)
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
    /// the intensity of its own normal. Everything is laid out in whole device pixels (the stroke
    /// centreline is half a pixel inside the card edge, so the 1 px band lands on complete pixel
    /// rows/columns) and converted back to DIP only when the geometry is written.
    /// </summary>
    private void Build(int pxWidth, int pxHeight, int pxRadius, double angle, double scaling)
    {
        builtWidth = pxWidth;
        builtHeight = pxHeight;
        builtRadius = pxRadius;
        builtAngle = angle;
        builtScaling = scaling;
        runs.Clear();

        // Stroke centreline: one device pixel inside each edge (see
        // CenterlineInsetDevicePixels). For cards thinner than two pixels the two parallel
        // lines collapse onto the same line (degenerate, but still drawn instead of
        // throwing/vanishing).
        var left = CenterlineInsetDevicePixels;
        var top = CenterlineInsetDevicePixels;
        var right = Math.Max(left, pxWidth - CenterlineInsetDevicePixels);
        var bottom = Math.Max(top, pxHeight - CenterlineInsetDevicePixels);

        // Outer stroke edge radius = pxRadius; the centreline runs `inset` px inside it.
        var corner = Math.Max(0.0, Math.Min(pxRadius - CenterlineInsetDevicePixels, Math.Min(right - left, bottom - top) / 2.0));
        var segmentLength = SpecularRim.SegmentLengthDips * scaling;
        var points = new List<RimPoint>();

        void Edge(Point from, Point to, double nx, double ny)
        {
            var length = Math.Sqrt((to.X - from.X) * (to.X - from.X) + (to.Y - from.Y) * (to.Y - from.Y));
            var count = Math.Max(1, (int)Math.Ceiling(length / segmentLength));
            for (var i = 0; i <= count; i++)
            {
                var t = (double)i / count;
                points.Add(new RimPoint(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t, nx, ny));
            }
        }

        void Arc(double cx, double cy, double startDegrees, double endDegrees)
        {
            var length = Math.Abs(endDegrees - startDegrees) * Math.PI / 180.0 * corner;
            var count = Math.Max(1, (int)Math.Ceiling(length / segmentLength));
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
            (buckets[level] ??= []).Add((
                new Point(from.X / scaling, from.Y / scaling),
                new Point(to.X / scaling, to.Y / scaling)));
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
