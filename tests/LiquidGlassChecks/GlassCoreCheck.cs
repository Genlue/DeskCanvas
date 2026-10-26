using DeskCanvas.Services.LiquidGlass;
using SkiaSharp;

/// <summary>
/// The reusable glass core: a backdrop plus a shape plus parameters, and nothing else.
/// <para>
/// Three properties are pinned here, each one a thing that silently ruins the material when it
/// breaks:
/// </para>
/// <list type="number">
///   <item><description><b>Any closed shape.</b> The polygon form must be the same material as the
///   rounded rectangle — a square polygon with no corner radius has to produce the same mask as a
///   zero-radius rectangle, and a triangle has to be <i>triangular</i> (opaque inside, transparent
///   in the corners of its bounding box) rather than a clipped box.</description></item>
///   <item><description><b>The coating is inside the material.</b> At strength 0 the surface is pure
///   glass; at strength 1 the centre is the coating colour. That is what lets a host drop the
///   rectangle it used to paint behind the glass.</description></item>
///   <item><description><b>The polygon shader compiles.</b> Its edge walk is generated as unrolled
///   straight-line code (the dialect guard forbids loops), so a mistake there is a shader that
///   fails to build — only a real compile catches it.</description></item>
/// </list>
/// </summary>
internal static class GlassCoreCheck
{
    private static int failures;

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("=== 液态玻璃核心 (backdrop + shape + params) ===");

        using var backdropBitmap = MakeBackdrop(256, 256);
        var backdrop = new LiquidGlassBackdrop(backdropBitmap, 0f, 0f, 1f);

        ShapeGuards();
        RoundedRectMatchesSquarePolygon(backdrop);
        TriangleIsTriangular(backdrop);
        CoatingIsInsideTheMaterial(backdrop);
        PolygonShaderCompiles(backdrop);

        Console.WriteLine(failures == 0 ? "glass core check passed" : $"{failures} CORE CHECK(S) FAILED");
        return failures;
    }

    private static void ShapeGuards()
    {
        Check(Throws(() => LiquidGlassShape.Polygon(new[] { new SKPoint(0, 0), new SKPoint(10, 0) }, new SKSize(10, 10))),
            "a polygon with fewer than three vertices is rejected");

        var tooMany = new SKPoint[LiquidGlassShape.MaxEdges + 1];
        for (var i = 0; i < tooMany.Length; i++)
        {
            var angle = i * MathF.Tau / tooMany.Length;
            tooMany[i] = new SKPoint(MathF.Cos(angle) * 50f, MathF.Sin(angle) * 50f);
        }
        Check(Throws(() => LiquidGlassShape.Polygon(tooMany, new SKSize(100, 100))),
            $"a polygon beyond {LiquidGlassShape.MaxEdges} edges is rejected rather than silently truncated");

        var rect = LiquidGlassShape.RoundedRect(new SKSize(100, 60), 12f);
        Check(!rect.IsPolygon && Math.Abs(rect.CornerRadius - 12f) < 1e-4f,
            "a rounded rectangle keeps its radius");
        Check(rect.GradientRadius >= rect.CornerRadius,
            $"the gradient field is at least as round as the corner ({rect.GradientRadius:F1} >= {rect.CornerRadius:F1})");
    }

    /// <summary>A square polygon and a zero-radius rectangle describe the same outline.</summary>
    private static void RoundedRectMatchesSquarePolygon(LiquidGlassBackdrop backdrop)
    {
        var size = new SKSize(120f, 120f);
        var half = size.Width / 2f;
        var square = LiquidGlassShape.Polygon(new[]
        {
            new SKPoint(-half, -half), new SKPoint(half, -half),
            new SKPoint(half, half), new SKPoint(-half, half)
        }, size);
        var rectangle = LiquidGlassShape.RoundedRect(size, 0f);

        var parameters = LiquidGlassParams.Default with
        {
            RefractionAmount = 0f,      // isolate the shape mask from the lens displacement
            StrokeAlpha = 0f,
            Coating = LiquidGlassCoating.None
        };

        using var polygon = LiquidGlassPass.RenderBitmap(backdrop, square, parameters, 120, 120);
        using var rect = LiquidGlassPass.RenderBitmap(backdrop, rectangle, parameters, 120, 120);
        if (polygon == null || rect == null)
        {
            Check(false, "both shapes render");
            return;
        }

        var worst = 0;
        for (var y = 0; y < 120; y++)
        for (var x = 0; x < 120; x++)
        {
            var a = polygon.GetPixel(x, y);
            var b = rect.GetPixel(x, y);
            worst = Math.Max(worst, Math.Abs(a.Alpha - b.Alpha));
        }

        Check(worst <= 2, $"a square polygon masks exactly like a zero-radius rectangle (worst alpha delta {worst})");

        // ...both fill their box, and a *rounded* outline pulls the corner in.
        Check(polygon.GetPixel(60, 60).Alpha == 255, "the square polygon is opaque at its centre");
        // The outermost pixel is deliberately feathered (the mask's ±0.75 px anti-alias band), so
        // "fills the box" is asserted one pixel in.
        Check(polygon.GetPixel(2, 2).Alpha == 255, "a square polygon fills its box to the edge");

        using var rounded = LiquidGlassPass.RenderBitmap(backdrop,
            LiquidGlassShape.RoundedRect(size, 30f), parameters, 120, 120);
        if (rounded == null)
        {
            Check(false, "the rounded rectangle renders");
            return;
        }

        Check(rounded.GetPixel(0, 0).Alpha == 0,
            "a rounded rectangle is transparent in the corner its radius cuts away");
        Check(rounded.GetPixel(60, 60).Alpha == 255, "the rounded rectangle is opaque at its centre");
    }

    /// <summary>A triangle must be a triangle: its bounding-box corners are not part of it.</summary>
    private static void TriangleIsTriangular(LiquidGlassBackdrop backdrop)
    {
        var size = new SKSize(160f, 140f);
        var triangle = LiquidGlassShape.Polygon(new[]
        {
            new SKPoint(0f, -70f), new SKPoint(80f, 70f), new SKPoint(-80f, 70f)
        }, size);

        var parameters = LiquidGlassParams.Default with
        {
            RefractionAmount = 0f,
            StrokeAlpha = 0f,
            Coating = LiquidGlassCoating.None
        };

        using var bitmap = LiquidGlassPass.RenderBitmap(backdrop, triangle, parameters, 160, 140);
        if (bitmap == null)
        {
            Check(false, "the triangle renders");
            return;
        }

        Check(bitmap.GetPixel(80, 96).Alpha == 255, "the triangle is opaque near its base");
        Check(bitmap.GetPixel(80, 8).Alpha == 255, "the triangle is opaque near its apex");
        Check(bitmap.GetPixel(4, 4).Alpha == 0, "the top-left corner of the bounding box is outside the triangle");
        Check(bitmap.GetPixel(155, 4).Alpha == 0, "the top-right corner of the bounding box is outside the triangle");

        // A box mask would have filled that corner; a triangle mask cannot.
        Check(bitmap.GetPixel(4, 4).Alpha < bitmap.GetPixel(80, 60).Alpha,
            "the mask follows the outline, not the bounding box");
    }

    /// <summary>The surface colour is the material's coating, not a rectangle painted behind it.</summary>
    private static void CoatingIsInsideTheMaterial(LiquidGlassBackdrop backdrop)
    {
        var shape = LiquidGlassShape.RoundedRect(new SKSize(120f, 120f), 24f);
        var coating = new SKColor(255, 0, 0);

        var bare = LiquidGlassParams.Default with
        {
            Coating = LiquidGlassCoating.None,
            StrokeAlpha = 0f,
            RefractionAmount = 0f,
            Dispersion = 0f,
            Vibrancy = 1f
        };

        using var glassOnly = LiquidGlassPass.RenderBitmap(backdrop, shape, bare, 120, 120);
        using var opaque = LiquidGlassPass.RenderBitmap(backdrop, shape,
            bare.WithCoating(coating, 1f), 120, 120);
        using var half = LiquidGlassPass.RenderBitmap(backdrop, shape,
            bare.WithCoating(coating, 0.5f), 120, 120);

        if (glassOnly == null || opaque == null || half == null)
        {
            Check(false, "coating variants render");
            return;
        }

        var glassCentre = glassOnly.GetPixel(60, 60);
        var opaqueCentre = opaque.GetPixel(60, 60);
        var halfCentre = half.GetPixel(60, 60);

        Check(opaqueCentre.Red > 245 && opaqueCentre.Green < 12 && opaqueCentre.Blue < 12,
            $"coating 1.0 makes the centre the coating colour (#{opaqueCentre.Red:X2}{opaqueCentre.Green:X2}{opaqueCentre.Blue:X2})");
        Check(opaqueCentre.Alpha == 255,
            "the coated surface is opaque — nothing has to be painted behind it");
        Check(halfCentre.Red > glassCentre.Red && halfCentre.Red < opaqueCentre.Red,
            $"coating 0.5 sits between bare glass and the full coating " +
            $"({glassCentre.Red} < {halfCentre.Red} < {opaqueCentre.Red})");
        Check(glassCentre.Red != opaqueCentre.Red,
            "coating 0 leaves the refracted backdrop visible");
    }

    /// <summary>The generated polygon SkSL has to actually build on this Skia.</summary>
    private static void PolygonShaderCompiles(LiquidGlassBackdrop backdrop)
    {
        var square = LiquidGlassShape.Polygon(new[]
        {
            new SKPoint(-40f, -40f), new SKPoint(40f, -40f),
            new SKPoint(40f, 40f), new SKPoint(-40f, 40f)
        }, new SKSize(80f, 80f));

        using var shader = LiquidGlassPass.CreateShader(backdrop, square, LiquidGlassParams.Default);
        Check(shader != null,
            "the generated polygon shader compiles (unrolled edge walk, no loops)");

        // A many-sided shape exercises the longest unrolled variant the API accepts.
        var ring = new SKPoint[LiquidGlassShape.MaxEdges];
        for (var i = 0; i < ring.Length; i++)
        {
            var angle = i * MathF.Tau / ring.Length;
            ring[i] = new SKPoint(MathF.Cos(angle) * 40f, MathF.Sin(angle) * 40f);
        }
        using var dense = LiquidGlassPass.CreateShader(backdrop,
            LiquidGlassShape.Polygon(ring, new SKSize(80f, 80f)), LiquidGlassParams.Default);
        Check(dense != null, $"a {LiquidGlassShape.MaxEdges}-edge polygon compiles");

        Check(!LiquidGlassPass.ShaderSource.Contains("for (", StringComparison.Ordinal),
            "the base shader source still declares no loops (dialect guard)");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static SKBitmap MakeBackdrop(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(30, 34, 44));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(210, 70, 60);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height / 3f), paint);
        paint.Color = new SKColor(60, 140, 210);
        surface.Canvas.DrawRect(new SKRect(0, height / 3f, width, height * 2f / 3f), paint);
        paint.Color = new SKColor(80, 180, 110);
        surface.Canvas.DrawRect(new SKRect(0, height * 2f / 3f, width, height), paint);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image).Copy();
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}");
            return;
        }

        Console.WriteLine($"  PASS: {label}");
    }
}
