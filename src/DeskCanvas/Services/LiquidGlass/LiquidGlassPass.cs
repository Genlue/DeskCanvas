using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using SkiaSharp;

namespace DeskCanvas.Services.LiquidGlass;

/// <summary>
/// The liquid-glass material as a <b>reusable pass</b>: give it a backdrop, a closed shape and a
/// set of optical parameters, get glass back. It knows nothing about widgets, windows, themes,
/// wallpapers, DPI or Avalonia — a caller with a bitmap and a rectangle can use it.
/// <para>
/// The model is the one this app has always used (SDF → refraction lens → diagonal dispersion →
/// vibrancy → <b>coating</b> → hairline highlight), with two things the core adds:
/// </para>
/// <list type="number">
///   <item><description><b>The shape is data.</b> A rounded rectangle or any closed polygon — see
///   <see cref="LiquidGlassShape"/>. A card is one outline among many, not a special case of the
///   material.</description></item>
///   <item><description><b>The surface colour lives inside the material.</b> <see cref="LiquidGlassCoating"/>
///   mixes the refracted backdrop toward the card's own colour in the same pass, so the result is
///   opaque inside the outline and a host never paints a second rectangle behind it.</description></item>
/// </list>
/// <para>
/// <b>Why the polygon is unrolled instead of looped:</b> this codebase's shader contract forbids
/// loops, switches and <c>out</c>/<c>inout</c> parameters in the optical SkSL (see
/// <c>GpuMaterialCheck</c>), because those are the constructs that behave differently across Skia's
/// backends. The polygon's edge walk is therefore generated as straight-line code for the shape's
/// edge count and cached per count — the same source-generation trick the backdrop fetch already
/// uses, which keeps the guard intact.
/// </para>
/// </summary>
public static class LiquidGlassPass
{
    private static readonly object Gate = new();
    private static readonly Dictionary<ShapeKey, SKRuntimeEffect?> Effects = new();

    private readonly record struct ShapeKey(bool Polygon, int Edges);

    private static string? baseSource;

    /// <summary>True when the runtime shader compiles on this build. Check before selecting the GPU path.</summary>
    public static bool IsSupported
    {
        get
        {
            var effect = EnsureEffect(new ShapeKey(false, 0));
            return effect != null;
        }
    }

    /// <summary>The compilation error for the base (rounded rectangle) source, when unsupported.</summary>
    public static string? CompileError { get; private set; }

    /// <summary>
    /// The base SkSL, for diagnostics and source-level guards. Polygon variants are generated from
    /// the same header on demand and are not exposed here.
    /// </summary>
    public static string ShaderSource => baseSource ??= Expand(BuildSource(0));

    // ------------------------------------------------------------------ public API

    /// <summary>
    /// Build a shader for one surface. <paramref name="backdrop"/> is borrowed, never disposed.
    /// </summary>
    /// <param name="destinationOrigin">
    /// Where this shape's local (0,0) sits in the canvas the shader will be painted on — pass the
    /// destination rect's origin when painting into an offset canvas region, or leave the default
    /// when the canvas is already shape-local.
    /// </param>
    /// <param name="destinationToLocal">Canvas units per shape unit (1 when they are the same).</param>
    /// <returns>Null when the runtime shader cannot be built; the caller must fall back.</returns>
    public static SKShader? CreateShader(LiquidGlassBackdrop backdrop, LiquidGlassShape shape,
        LiquidGlassParams parameters, SKPoint destinationOrigin = default, float destinationToLocal = 1f)
    {
        if (shape == null) return null;

        var key = shape.IsPolygon ? new ShapeKey(true, shape.EdgeCount) : new ShapeKey(false, 0);
        var effect = EnsureEffect(key);
        if (effect == null) return null;

        var optics = parameters.Normalized();
        var coating = optics.Coating;

        try
        {
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { shape.Size.Width, shape.Size.Height },
                ["radius"] = shape.CornerRadius,
                ["gradRadius"] = shape.GradientRadius,
                ["destOrigin"] = new[] { destinationOrigin.X, destinationOrigin.Y },
                ["destToRender"] = destinationToLocal > 0f ? destinationToLocal : 1f,
                ["srcOrigin"] = new[] { backdrop.Origin.X, backdrop.Origin.Y },
                ["srcScale"] = backdrop.Scale,
                ["refrHeight"] = optics.RefractionBand,
                ["refrAmount"] = optics.RefractionAmount,
                ["depthEffect"] = optics.DepthEffect,
                ["chroma"] = optics.Dispersion,
                ["saturation"] = optics.Vibrancy,
                ["tint"] = coating.Strength,
                ["coating"] = new[]
                {
                    (float)coating.Color.Red, (float)coating.Color.Green, (float)coating.Color.Blue
                },
                ["strokeHalf"] = optics.StrokeHalf,
                ["strokeFeather"] = optics.StrokeFeather,
                ["strokeAlpha"] = optics.StrokeAlpha,
                ["falloff"] = optics.HighlightFalloff,
                ["lightDir"] = new[] { optics.LightDirection.X, optics.LightDirection.Y }
            };

            if (shape.IsPolygon) uniforms["verts"] = PackRing(shape);

            var children = new SKRuntimeEffectChildren(effect);
            using var content = backdrop.Bitmap.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            children["content"] = content;

            return effect.ToShader(uniforms, children);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Draw one surface into <paramref name="destination"/>. The result is premultiplied BGRA with
    /// alpha = the shape mask, so it composites over anything (or over nothing) without the caller
    /// having to know the outline.
    /// </summary>
    public static bool Draw(SKCanvas canvas, SKRect destination, LiquidGlassBackdrop backdrop,
        LiquidGlassShape shape, LiquidGlassParams parameters)
    {
        if (canvas == null || shape == null) return false;
        if (destination.Width <= 0f || destination.Height <= 0f) return false;

        using var shader = CreateShader(backdrop, shape, parameters);
        if (shader == null) return false;

        // The shader is shape-local; put the shape's box onto the destination rect. Scaling here
        // (rather than in the uniforms) keeps `srcOrigin`/`srcScale` in one coordinate space.
        var scaleX = destination.Width / shape.Size.Width;
        var scaleY = destination.Height / shape.Size.Height;

        canvas.Save();
        canvas.Translate(destination.Left, destination.Top);
        if (MathF.Abs(scaleX - 1f) > 1e-6f || MathF.Abs(scaleY - 1f) > 1e-6f)
            canvas.Scale(scaleX, scaleY);

        using var paint = new SKPaint { Shader = shader, IsAntialias = true };
        canvas.DrawRect(new SKRect(0f, 0f, shape.Size.Width, shape.Size.Height), paint);
        canvas.Restore();
        return true;
    }

    /// <summary>
    /// The CPU twin: the same pass evaluated per pixel. Used by hosts without a GPU context, and as
    /// the reference the shape geometry is checked against.
    /// </summary>
    /// <param name="backdrop">Borrowed; its coordinate space is the one the shape samples.</param>
    /// <param name="width">Output width in destination pixels.</param>
    /// <param name="height">Output height in destination pixels.</param>
    public static SKBitmap? RenderBitmap(LiquidGlassBackdrop backdrop, LiquidGlassShape shape,
        LiquidGlassParams parameters, int width, int height)
    {
        if (shape == null || width <= 0 || height <= 0) return null;

        var optics = parameters.Normalized();
        var coating = optics.Coating;

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            return null;
        }

        var source = backdrop.Bitmap;
        var lightX = optics.LightDirection.X;
        var lightY = optics.LightDirection.Y;

        // Chroma's diagonal term is normalised by the shape's half-extents, exactly as the shader
        // does with size.x * size.y * 0.25 — both sides therefore live in shape units.
        var areaGuard = Math.Max(shape.Size.Width * shape.Size.Height * 0.25f, 1e-5f);
        var toShape = new SKPoint(shape.Size.Width / width, shape.Size.Height / height);

        var pixels = new SKColor[width * height];
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8)
        };

        Parallel.For(0, height, parallel, y =>
        {
            for (var x = 0; x < width; x++)
            {
                // Destination pixel centre → shape-local, then centred (the SDF's own origin).
                var localX = (x + 0.5f) * toShape.X;
                var localY = (y + 0.5f) * toShape.Y;
                var cx = localX - shape.Size.Width / 2f;
                var cy = localY - shape.Size.Height / 2f;

                var (sdRaw, gradX, gradY) = shape.IsPolygon
                    ? PolygonField(shape, cx, cy)
                    : RoundedRectField(shape, cx, cy);

                var mask = 1f - SmoothStep(-0.75f, 0.75f, sdRaw);
                if (mask <= 0f)
                {
                    pixels[y * width + x] = SKColors.Transparent;
                    continue;
                }

                var sd = Math.Min(sdRaw, 0f);
                var depth01 = optics.RefractionBand > 0f
                    ? Math.Clamp(-sd / optics.RefractionBand, 0f, 1f)
                    : 1f;
                var lensX = 1f - depth01;
                var displacement = (1f - MathF.Sqrt(MathF.Max(0f, 1f - lensX * lensX)))
                                   * optics.RefractionAmount;

                var radialLength = MathF.Sqrt(cx * cx + cy * cy);
                var rx = radialLength > 1e-4f ? cx / radialLength : 0f;
                var ry = radialLength > 1e-4f ? cy / radialLength : 0f;
                var gx = gradX + optics.DepthEffect * rx;
                var gy = gradY + optics.DepthEffect * ry;
                var gradientLength = MathF.Sqrt(gx * gx + gy * gy);
                if (gradientLength > 1e-5f)
                {
                    gx /= gradientLength;
                    gy /= gradientLength;
                }
                else
                {
                    gx = gy = 0f;
                }

                var sampleX = localX - gx * displacement;
                var sampleY = localY - gy * displacement;

                var centre = Sample(source, backdrop, sampleX, sampleY);
                var r = (float)centre.Red;
                var g = (float)centre.Green;
                var b = (float)centre.Blue;

                if (optics.Dispersion > 0.001f && displacement > 0f)
                {
                    var intensity = optics.Dispersion * (cx * cy) / areaGuard;
                    var dx = displacement * gx * intensity;
                    var dy = displacement * gy * intensity;

                    var red = Sample(source, backdrop, sampleX + dx, sampleY + dy);
                    var orange = Sample(source, backdrop, sampleX + dx * (2f / 3f), sampleY + dy * (2f / 3f));
                    var yellow = Sample(source, backdrop, sampleX + dx / 3f, sampleY + dy / 3f);
                    var cyan = Sample(source, backdrop, sampleX - dx / 3f, sampleY - dy / 3f);
                    var blue = Sample(source, backdrop, sampleX - dx * (2f / 3f), sampleY - dy * (2f / 3f));
                    var purple = Sample(source, backdrop, sampleX - dx, sampleY - dy);

                    r = red.Red / 3.5f + orange.Red / 3.5f + yellow.Red / 3.5f + purple.Red / 7f;
                    g = orange.Green / 7f + yellow.Green / 3.5f + centre.Green / 3.5f + cyan.Green / 3.5f;
                    b = cyan.Blue / 3f + blue.Blue / 3f + purple.Blue / 3f;
                }

                var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = luma + (r - luma) * optics.Vibrancy;
                g = luma + (g - luma) * optics.Vibrancy;
                b = luma + (b - luma) * optics.Vibrancy;

                // The surface's own colour, fused in. This is what makes the layer self-sufficient:
                // nothing has to be painted behind it for the card to read as a card.
                r = r * (1f - coating.Strength) + coating.Color.Red * coating.Strength;
                g = g * (1f - coating.Strength) + coating.Color.Green * coating.Strength;
                b = b * (1f - coating.Strength) + coating.Color.Blue * coating.Strength;

                var strokeMask = 1f - SmoothStep(
                    optics.StrokeHalf - optics.StrokeFeather,
                    optics.StrokeHalf + optics.StrokeFeather,
                    MathF.Abs(sdRaw));
                var light = 255f
                            * MathF.Pow(MathF.Abs(gradX * lightX + gradY * lightY), optics.HighlightFalloff)
                            * strokeMask * optics.StrokeAlpha;
                r += light;
                g += light;
                b += light;

                pixels[y * width + x] = new SKColor(
                    ClampByte(r * mask), ClampByte(g * mask), ClampByte(b * mask), ClampByte(255f * mask));
            }
        });

        bitmap.Pixels = pixels;
        bitmap.SetImmutable();
        return bitmap;
    }

    // ------------------------------------------------------------------ shape fields

    /// <summary>Exact rounded-rectangle SDF and its inflated direction field (iq's formulas).</summary>
    private static (float Sd, float GradX, float GradY) RoundedRectField(LiquidGlassShape shape, float cx, float cy)
    {
        var halfWidth = shape.Size.Width / 2f;
        var halfHeight = shape.Size.Height / 2f;
        var radius = shape.CornerRadius;

        var qx = MathF.Abs(cx) - halfWidth + radius;
        var qy = MathF.Abs(cy) - halfHeight + radius;
        var outside = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        var sd = MathF.Min(MathF.Max(qx, qy), 0f) + outside - radius;

        // Inflated field: the same shape with a wider corner radius, so the direction stays smooth
        // along the straight edges instead of snapping to an axis.
        var gradRadius = shape.GradientRadius;
        var cornerX = MathF.Abs(cx) - (halfWidth - gradRadius);
        var cornerY = MathF.Abs(cy) - (halfHeight - gradRadius);
        var signX = cx >= 0f ? 1f : -1f;
        var signY = cy >= 0f ? 1f : -1f;

        float gradX, gradY;
        if (cornerX >= 0f && cornerY >= 0f)
        {
            var mx = MathF.Max(cornerX, 1e-5f);
            var my = MathF.Max(cornerY, 1e-5f);
            var length = MathF.Sqrt(mx * mx + my * my);
            gradX = signX * mx / length;
            gradY = signY * my / length;
        }
        else if (cornerX >= 0f)
        {
            gradX = signX;
            gradY = 0f;
        }
        else if (cornerY >= 0f)
        {
            gradX = 0f;
            gradY = signY;
        }
        else
        {
            var ex = MathF.Max(-cornerX, 1e-5f);
            var ey = MathF.Max(-cornerY, 1e-5f);
            var length = MathF.Sqrt(ex * ex + ey * ey);
            gradX = signX * ey / length;
            gradY = signY * ex / length;
        }

        return (sd, gradX, gradY);
    }

    /// <summary>
    /// Exact polygon SDF plus the nearest-edge normal — the same edge walk the generated SkSL
    /// performs, so the two twins cannot drift apart.
    /// </summary>
    private static (float Sd, float GradX, float GradY) PolygonField(LiquidGlassShape shape, float px, float py)
    {
        var vertices = shape.Vertices;
        if (vertices.Length < 3) return (float.MaxValue, 0f, 0f);

        var nearestX = 0f;
        var nearestY = 0f;
        var minDistanceSquared = float.MaxValue;
        var sign = 1f;

        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];

            var ex = b.X - a.X;
            var ey = b.Y - a.Y;
            var wx = px - a.X;
            var wy = py - a.Y;

            var edgeLengthSquared = ex * ex + ey * ey;
            var t = edgeLengthSquared > 0f
                ? Math.Clamp((wx * ex + wy * ey) / edgeLengthSquared, 0f, 1f)
                : 0f;

            var dx = wx - ex * t;
            var dy = wy - ey * t;
            var distanceSquared = dx * dx + dy * dy;
            if (distanceSquared < minDistanceSquared)
            {
                minDistanceSquared = distanceSquared;
                nearestX = dx;
                nearestY = dy;
            }

            var c1 = py >= a.Y;
            var c2 = py < b.Y;
            var c3 = ex * wy > ey * wx;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3)) sign = -sign;
        }

        var length = MathF.Sqrt(minDistanceSquared);
        if (length <= 1e-5f) return (0f, 0f, 0f);

        return (sign * length, sign * nearestX / length, sign * nearestY / length);
    }

    // ------------------------------------------------------------------ sampling

    private static SKColor Sample(SKBitmap source, LiquidGlassBackdrop backdrop, float localX, float localY)
    {
        var texelX = Math.Clamp((backdrop.Origin.X + localX) * backdrop.Scale, 0f, source.Width - 1f);
        var texelY = Math.Clamp((backdrop.Origin.Y + localY) * backdrop.Scale, 0f, source.Height - 1f);

        var x0 = (int)texelX;
        var y0 = (int)texelY;
        var x1 = Math.Min(x0 + 1, source.Width - 1);
        var y1 = Math.Min(y0 + 1, source.Height - 1);
        var fx = texelX - x0;
        var fy = texelY - y0;

        var top = source.GetPixel(x0, y0);
        var topRight = source.GetPixel(x1, y0);
        var bottom = source.GetPixel(x0, y1);
        var bottomRight = source.GetPixel(x1, y1);

        return new SKColor(
            LerpByte(top.Red, topRight.Red, bottom.Red, bottomRight.Red, fx, fy),
            LerpByte(top.Green, topRight.Green, bottom.Green, bottomRight.Green, fx, fy),
            LerpByte(top.Blue, topRight.Blue, bottom.Blue, bottomRight.Blue, fx, fy));
    }

    private static byte LerpByte(byte p, byte q, byte r, byte s, float fx, float fy) =>
        (byte)Math.Clamp(MathF.Round((p + (q - p) * fx) * (1 - fy) + (r + (s - r) * fx) * fy), 0f, 255f);

    private static byte ClampByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value >= edge1 ? 1f : 0f;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    // ------------------------------------------------------------------ shader source

    /// <summary>
    /// The closing ring the shader consumes: the shape's vertices plus the first one repeated, so
    /// the last edge joins back to the start without any wrap-around arithmetic in the shader.
    /// </summary>
    private static float[] PackRing(LiquidGlassShape shape)
    {
        var vertices = shape.Vertices;
        var packed = new float[(vertices.Length + 1) * 4];
        for (var i = 0; i <= vertices.Length; i++)
        {
            var point = i < vertices.Length ? vertices[i] : vertices[0];
            packed[i * 4 + 0] = point.X;
            packed[i * 4 + 1] = point.Y;
        }

        return packed;
    }

    private static SKRuntimeEffect? EnsureEffect(ShapeKey key)
    {
        lock (Gate)
        {
            if (Effects.TryGetValue(key, out var cached)) return cached;

            SKRuntimeEffect? effect;
            try
            {
                var source = key.Polygon ? Expand(BuildSource(key.Edges)) : ShaderSource;
                effect = SKRuntimeEffect.CreateShader(source, out var errors);
                if (effect == null && !key.Polygon) CompileError = errors;
            }
            catch (Exception ex)
            {
                if (!key.Polygon) CompileError = ex.Message;
                effect = null;
            }

            Effects[key] = effect;
            return effect;
        }
    }

    private static string BuildSource(int polygonEdges)
    {
        var builder = new StringBuilder(SourceUniforms.Length + SourceMainOpen.Length + SourceTail.Length
                                       + polygonEdges * 340 + 1024);
        builder.Append(SourceUniforms);

        // The vertex array is only declared for the polygon form: a rounded rectangle does not carry
        // the uniform at all, so its source stays byte-identical to what the guards were written for.
        if (polygonEdges >= 3)
            builder.Append($"uniform float4 verts[{polygonEdges + 1}];\n\n");

        builder.Append(SourceMainOpen);
        builder.Append(polygonEdges >= 3 ? BuildPolygonField(polygonEdges) : SourceRoundedRectField);
        builder.Append(SourceTail);
        return builder.ToString();
    }

    /// <summary>
    /// The unrolled polygon edge walk. Each edge contributes its distance (tracking the nearest) and
    /// its ray-crossing parity; there is no loop and no out-parameter, so the dialect guard holds.
    /// </summary>
    private static string BuildPolygonField(int edges)
    {
        var builder = new StringBuilder(edges * 340 + 512);
        builder.Append(
            """
              float sdRaw;
              float2 grad;
              {
                float2 polyNearest = float2(0.0);
                float polyMinD2 = 3.0e38;
                float polySign = 1.0;
                float2 a; float2 b; float2 e; float2 w; float ee; float t; float2 d; float d2;

            """);

        for (var i = 0; i < edges; i++)
        {
            // `$$` so the GLSL braces stay literal and only {{…}} interpolates.
            builder.Append(
                $$"""
                      a = verts[{{i}}].xy; b = verts[{{i + 1}}].xy;
                      e = b - a; w = p - a; ee = dot(e, e);
                      t = ee > 0.0 ? clamp(dot(w, e) / ee, 0.0, 1.0) : 0.0;
                      d = w - e * t; d2 = dot(d, d);
                      if (d2 < polyMinD2) { polyMinD2 = d2; polyNearest = d; }
                      if ((p.y >= a.y && p.y < b.y && e.x * w.y > e.y * w.x) ||
                          (p.y < a.y && p.y >= b.y && e.x * w.y <= e.y * w.x)) polySign = -polySign;

                """);
        }

        builder.Append(
            """
                float polyLength = sqrt(polyMinD2);
                sdRaw = polySign * polyLength;
                grad = polyLength > 1e-5 ? polySign * polyNearest / polyLength : float2(0.0);
              }

            """);
        return builder.ToString();
    }

    private const string SourceRoundedRectField =
        """
          float2 halfSize = size * 0.5;
          float sdRaw = sdRoundedRect(c, halfSize, radius);
          float2 grad = gradSdRoundedRect(c, halfSize, gradRadius);

        """;

    private const string SourceUniforms =
        """
        // Liquid-glass pass. Shape-local pixel space, origin top-left; the SDF is evaluated in the
        // centred space `c`, and the backdrop is sampled with `(srcOrigin + p) * srcScale`.
        uniform float2 size;
        uniform float radius;
        uniform float gradRadius;
        uniform float2 destOrigin;
        uniform float destToRender;
        uniform float2 srcOrigin;
        uniform float srcScale;
        uniform float refrHeight;
        uniform float refrAmount;
        uniform float depthEffect;
        uniform float chroma;
        uniform float saturation;
        uniform float tint;
        uniform float3 coating;
        uniform float strokeHalf;
        uniform float strokeFeather;
        uniform float strokeAlpha;
        uniform float falloff;
        uniform float2 lightDir;

        uniform shader content;

        float smoothStep(float edge0, float edge1, float x) {
          float t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
          return t * t * (3.0 - 2.0 * t);
        }

        float sdRoundedRect(float2 p, float2 b, float r) {
          float2 q = abs(p) - b + float2(r);
          return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
        }

        float2 sgnCoord(float2 v) {
          return float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
        }

        float2 gradSdRoundedRect(float2 p, float2 b, float r) {
          float2 corner = abs(p) - (b - float2(r));
          float2 sgn = sgnCoord(p);
          if (corner.x >= 0.0 && corner.y >= 0.0) {
            float mx = max(corner.x, 1e-5);
            float my = max(corner.y, 1e-5);
            return float2(sgn.x * mx, sgn.y * my) / length(float2(mx, my));
          }
          if (corner.x >= 0.0) return float2(sgn.x, 0.0);
          if (corner.y >= 0.0) return float2(0.0, sgn.y);
          float ex = max(-corner.x, 1e-5);
          float ey = max(-corner.y, 1e-5);
          return float2(sgn.x * ey, sgn.y * ex) / length(float2(ex, ey));
        }

        float3 contentTexel(float2 p) {
          // Skia m130+ removed sample(shader, coord); child.eval(coord) is the replacement.
          return content.eval(p).rgb * 255.0;
        }

        """;

    private const string SourceMainOpen =
        """
        half4 main(float2 fragCoord) {
          float2 p = (fragCoord - destOrigin) * destToRender;
          float2 c = p - size * 0.5;

        """;

    private const string SourceTail =
        """
          float mask = 1.0 - smoothStep(-0.75, 0.75, sdRaw);
          if (mask <= 0.0) return half4(0.0);

          float sd = min(sdRaw, 0.0);
          float depth01 = refrHeight > 0.0 ? clamp(-sd / refrHeight, 0.0, 1.0) : 1.0;
          float lensX = 1.0 - depth01;
          float d = (1.0 - sqrt(max(0.0, 1.0 - lensX * lensX))) * refrAmount;

          float radialLen = length(c);
          float2 radial = radialLen > 1e-4 ? c / radialLen : float2(0.0);
          float2 g = grad + depthEffect * radial;
          float glen = length(g);
          g = glen > 1e-5 ? g / glen : float2(0.0);

          float2 baseSamplePos = (srcOrigin + p - g * d) * srcScale;

          float3 col;
          float3 col00; float3 col10; float3 col01; float3 col11; float2 colF;
          @fetch(col, baseSamplePos);

          if (chroma > 0.001 && d > 0.0) {
            float dispersionIntensity = chroma * (c.x * c.y) / max(size.x * size.y * 0.25, 1e-5);
            float2 delta = d * g * dispersionIntensity * srcScale;
            float3 cRed; float3 cRed00; float3 cRed10; float3 cRed01; float3 cRed11; float2 cRedF;
            @fetch(cRed, baseSamplePos + delta);
            float3 cOrange; float3 cOrange00; float3 cOrange10; float3 cOrange01; float3 cOrange11; float2 cOrangeF;
            @fetch(cOrange, baseSamplePos + delta * (2.0 / 3.0));
            float3 cYellow; float3 cYellow00; float3 cYellow10; float3 cYellow01; float3 cYellow11; float2 cYellowF;
            @fetch(cYellow, baseSamplePos + delta / 3.0);
            float3 cGreen = col;
            float3 cCyan; float3 cCyan00; float3 cCyan10; float3 cCyan01; float3 cCyan11; float2 cCyanF;
            @fetch(cCyan, baseSamplePos - delta / 3.0);
            float3 cBlue; float3 cBlue00; float3 cBlue10; float3 cBlue01; float3 cBlue11; float2 cBlueF;
            @fetch(cBlue, baseSamplePos - delta * (2.0 / 3.0));
            float3 cPurple; float3 cPurple00; float3 cPurple10; float3 cPurple01; float3 cPurple11; float2 cPurpleF;
            @fetch(cPurple, baseSamplePos - delta);

            col = float3(0.0);
            col.r += cRed.r / 3.5;
            col.r += cOrange.r / 3.5;
            col.r += cYellow.r / 3.5;
            col.r += cPurple.r / 7.0;
            col.g += cOrange.g / 7.0;
            col.g += cYellow.g / 3.5;
            col.g += cGreen.g / 3.5;
            col.g += cCyan.g / 3.5;
            col.b += cCyan.b / 3.0;
            col.b += cBlue.b / 3.0;
            col.b += cPurple.b / 3.0;
          }

          float luma = 0.2126 * col.r + 0.7152 * col.g + 0.0722 * col.b;
          col = luma + (col - luma) * saturation;

          // The surface's own colour, fused in. Everything below this line already carries it, so
          // the host draws one layer and nothing has to line up behind it.
          col = col * (1.0 - tint) + coating * tint;

          float strokeMask = 1.0 - smoothStep(strokeHalf - strokeFeather, strokeHalf + strokeFeather, abs(sdRaw));
          float dH = dot(grad, lightDir);
          float intensity = pow(abs(dH), falloff);
          col += float3(255.0) * intensity * strokeMask * strokeAlpha;

          float3 rgb = clamp(col / 255.0, 0.0, 1.0) * mask;
          return half4(half3(rgb), half(mask));
        }
        """;

    /// <summary>
    /// One bilinear fetch of the backdrop at <paramref name="coord"/>, written as four single-texel
    /// taps plus a hand-written blend (a bitmap shader samples nearest, so Skia's own filtering is
    /// not available here).
    /// <para>
    /// <b>The tap base and the blend weight must come from the same texel grid.</b> A texel
    /// <c>k</c> covers <c>[k, k+1)</c> and is stored at its centre <c>k+0.5</c>, so the two texels a
    /// point <c>c</c> lies between are <c>floor(c-0.5)+0.5</c> and <c>+1</c>, and the weight is
    /// <c>c-0.5-floor(c-0.5)</c>. Pairing those centres with <c>fract(c)</c> instead only adds a
    /// constant half-texel shift; pairing them with <c>fract(c-0.5)</c> makes the reconstructed
    /// position jump by a whole texel at every half-texel boundary — at 25% 背景清晰度 one texel
    /// spans four screen pixels, so that jump showed up as the reported 网格状像素.
    /// </para>
    /// </summary>
    private static string SampleBackdrop(string target, string coord) =>
        $"float2 {target}B = floor({coord} - float2(0.5, 0.5));\n" +
        $"{target}00 = contentTexel({target}B + float2(0.5, 0.5));\n" +
        $"{target}10 = contentTexel({target}B + float2(1.5, 0.5));\n" +
        $"{target}01 = contentTexel({target}B + float2(0.5, 1.5));\n" +
        $"{target}11 = contentTexel({target}B + float2(1.5, 1.5));\n" +
        $"{target}F = {coord} - float2(0.5, 0.5) - {target}B;\n" +
        $"{target} = mix(mix({target}00, {target}10, {target}F.x), mix({target}01, {target}11, {target}F.x), {target}F.y);\n";

    private static string Expand(string raw)
    {
        var result = raw;
        int idx;
        while ((idx = result.IndexOf("@fetch(", StringComparison.Ordinal)) >= 0)
        {
            var depth = 1;
            var end = -1;
            for (var i = idx + 7; i < result.Length; i++)
            {
                if (result[i] == '(') depth++;
                else if (result[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0) break;

            var args = result.Substring(idx + 7, end - (idx + 7)).Split(',', 2);
            var target = args[0].Trim();
            var coord = args[1].Trim();
            var expansion = SampleBackdrop(target, coord);
            var skip = end + 1;
            if (skip < result.Length && result[skip] == ';') skip++;
            result = result.Substring(0, idx) + expansion + result.Substring(skip);
        }

        return result;
    }
}
