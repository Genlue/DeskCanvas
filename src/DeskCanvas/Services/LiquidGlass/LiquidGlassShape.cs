using System;
using SkiaSharp;

namespace DeskCanvas.Services.LiquidGlass;

/// <summary>
/// The outline a liquid-glass surface is cut to: a rounded rectangle, or <b>any closed polygon</b>.
/// <para>
/// This is the shape half of the pass contract. A host hands one of these plus a
/// <see cref="LiquidGlassBackdrop"/> to <see cref="LiquidGlassPass"/> and gets the material — it
/// never has to know whether the outline is a card, a capsule, a triangle or a hand-built blob.
/// </para>
/// <para>
/// Coordinates are <b>card-local pixels with the origin at the shape's centre</b>, the same space
/// the shader's SDF works in. A polygon is a closed ring: the last vertex is implicitly joined to
/// the first, so callers must not repeat it.
/// </para>
/// </summary>
public sealed class LiquidGlassShape
{
    /// <summary>
    /// Edge slots the pass can emit. Each edge is unrolled into the shader source (see
    /// <see cref="LiquidGlassPass"/>), so the cap is a compile-size trade: 32 covers a 16-point star
    /// with room to spare.
    /// </summary>
    public const int MaxEdges = 32;

    private readonly SKPoint[]? vertices;

    private LiquidGlassShape(SKSize size, float cornerRadius, float gradientRadius, SKPoint[]? vertices)
    {
        Size = size;
        CornerRadius = cornerRadius;
        GradientRadius = gradientRadius;
        this.vertices = vertices;
    }

    /// <summary>Bounding size of the shape in pixels (the card's box).</summary>
    public SKSize Size { get; }

    /// <summary>Corner radius for the rounded-rectangle form; also the refraction band's inner bound.</summary>
    public float CornerRadius { get; }

    /// <summary>
    /// Radius of the inflated rounded rectangle whose gradient field aims the refraction. Always
    /// wider than <see cref="CornerRadius"/> so the direction field stays smooth along the straight
    /// edges too (see <see cref="LiquidGlassPass"/>).
    /// </summary>
    public float GradientRadius { get; }

    /// <summary>True when this shape is a polygon rather than a rounded rectangle.</summary>
    public bool IsPolygon => vertices != null;

    /// <summary>Number of polygon edges actually used (0 for a rounded rectangle).</summary>
    public int EdgeCount => vertices?.Length ?? 0;

    /// <summary>Polygon vertices in card-local pixels, centre origin. Empty for a rounded rectangle.</summary>
    public ReadOnlySpan<SKPoint> Vertices => vertices ?? Array.Empty<SKPoint>();

    /// <summary>
    /// A rounded rectangle of <paramref name="size"/>. <paramref name="gradientRadius"/> defaults to
    /// the value the optical model wants (1.5× the corner radius, capped at half the short side);
    /// pass an explicit one only when matching a previously computed lens.
    /// </summary>
    public static LiquidGlassShape RoundedRect(SKSize size, float cornerRadius, float gradientRadius = 0f)
    {
        var radius = Math.Clamp(cornerRadius, 0f, Math.Min(size.Width, size.Height) / 2f);
        if (gradientRadius <= 0f)
            gradientRadius = Math.Min(Math.Max(radius * 1.5f, radius), Math.Min(size.Width, size.Height) / 2f);

        return new LiquidGlassShape(size, radius, gradientRadius, null);
    }

    /// <summary>
    /// Any closed polygon. Points are card-local pixels with the origin at the centre of the shape's
    /// box; the ring is closed implicitly.
    /// </summary>
    /// <param name="points">Ring vertices, at most <see cref="MaxEdges"/>.</param>
    /// <param name="size">Bounding box, used for the refraction band and the area guard.</param>
    /// <param name="cornerRadius">Refraction band constraint for the polygon form.</param>
    public static LiquidGlassShape Polygon(ReadOnlySpan<SKPoint> points, SKSize size, float cornerRadius = 0f)
    {
        if (points.Length < 3)
            throw new ArgumentException("A closed polygon needs at least three vertices.", nameof(points));
        if (points.Length > MaxEdges)
            throw new ArgumentException(
                $"At most {MaxEdges} vertices are supported (the shader evaluates a fixed edge count). " +
                "Simplify the outline or split it into several glass items.", nameof(points));

        var copy = new SKPoint[points.Length];
        points.CopyTo(copy);

        var radius = Math.Max(0f, Math.Min(cornerRadius, Math.Min(size.Width, size.Height) / 2f));
        var gradientRadius = Math.Min(Math.Max(radius * 1.5f, radius), Math.Min(size.Width, size.Height) / 2f);
        return new LiquidGlassShape(size, radius, gradientRadius, copy);
    }

    /// <summary>
    /// The same outline rescaled/offset into another local space. Used when a host animates a shape
    /// (a morph, a squash) without rebuilding the vertex list every frame.
    /// </summary>
    public LiquidGlassShape Transformed(SKSize size, float scale, SKPoint offset = default)
    {
        if (vertices == null) return RoundedRect(size, CornerRadius * scale);

        var scaled = new SKPoint[vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
            scaled[i] = new SKPoint(vertices[i].X * scale + offset.X, vertices[i].Y * scale + offset.Y);

        return Polygon(scaled, size, CornerRadius * scale);
    }

}
