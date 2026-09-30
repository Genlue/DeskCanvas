using System;
using System.Threading.Tasks;
using SkiaSharp;
using uWidgets.Core.Models.Settings;

namespace uWidgets.Services;

/// <summary>
/// CPU renderer of the 新液态玻璃 material (<see cref="SurfaceStyle.LiquidGlassV2"/>): the
/// per-pixel twin of <see cref="LiquidGlassV2Effect"/>, used as the software-rendering fallback
/// (and for the clock's pre-rendered frames).
/// <para>
/// A faithful port of Kyant0/AndroidLiquidGlass 2.0's optical model — the rounded-rect refraction
/// lens with depth effect and diagonal chromatic aberration, vibrancy, the surface scrim and the
/// hairline outline highlight. The coefficient block mirrors
/// <see cref="LiquidGlassV2Effect.BuildParams"/> so the two paths stay in step.
/// </para>
/// </summary>
public static class LiquidGlassV2Renderer
{
    /// <summary>Render one background to PNG. Safe on worker threads.</summary>
    public static byte[] Render(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper)
    {
        using var bitmap = RenderBitmap(frame, wallpaper);
        if (bitmap == null) return [];
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Render one background. Safe on worker threads; the <b>caller owns</b> the result.
    /// </summary>
    public static SKBitmap? RenderBitmap(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper)
    {
        var optics = frame.Theme.EffectiveLiquidGlassV2;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var minSide = Math.Min(width, height);
        var sigma = (float)optics.Blur * scale / 8f;
        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);

        // Lens: band = amount / 2, both measured against the short side (the library's ratio).
        var amount = (float)(optics.Refraction / 100.0) * (float)LiquidGlassV2Settings.RefractionAmountMaxFrac * minSide;
        var refrHeight = amount * (float)LiquidGlassV2Settings.BandToAmountRatio;
        var depthEffect = 1f;

        var chroma = (float)(optics.Dispersion / 100.0);
        var saturation = 1f + (float)LiquidGlassV2Settings.VibrancySaturationBoost * (float)(optics.Vibrancy / 100.0);

        var colorHex = frame.Dark ? frame.Theme.EffectiveSolidBackgroundDark : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.20);

        // Outline highlight — the library's paint layer: stroke = ceil(0.5dp in px) × 2, blurred
        // by 0.25dp, clipped to the outline → visible inner band = ceil(0.5dp in px), full white
        // added (Plus) scaled by the slider.
        var strokeHalf = MathF.Ceiling((float)LiquidGlassV2Settings.StrokeWidthDips * scale);
        var strokeFeather = (float)LiquidGlassV2Settings.StrokeFeatherDips * scale;
        var strokeAlpha = (float)(optics.Highlight / LiquidGlassV2Settings.HighlightReference);
        var falloff = (float)LiquidGlassV2Settings.HighlightFalloff;
        var angle = (float)(LiquidGlassV2Settings.HighlightAngleDegrees * Math.PI / 180.0);
        var lightX = MathF.Cos(angle);
        var lightY = MathF.Sin(angle);

        var halfWidth = width / 2f;
        var halfHeight = height / 2f;
        var gradRadius = Math.Min(radius * (float)LiquidGlassV2Settings.GradRadiusFactor, Math.Min(halfWidth, halfHeight));

        var field = new LiquidGlassRenderer.BevelField(width, height, radius);
        var pad = (int)Math.Ceiling(Math.Max(sigma * 3f, 16f));
        var info = new SKImageInfo(width + 2 * pad, height + 2 * pad);
        using var backdrop = SKSurface.Create(info);
        var canvas = backdrop.Canvas;
        canvas.Clear(wallpaper.Background);
        SKBitmap? image = wallpaper.CachedBitmap;
        bool ownsBitmap = false;
        if (image == null && wallpaper.ImageBytes != null)
        {
            try
            {
                image = SKBitmap.Decode(wallpaper.ImageBytes);
                ownsBitmap = true;
            }
            catch { }
        }

        try
        {
            if (image != null)
            {
                using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
                canvas.Save();
                canvas.Translate(pad, pad);
                LiquidGlassRenderer.DrawWallpaper(canvas, image, paint, frame, wallpaper);
                canvas.Restore();
            }
        }
        finally
        {
            if (ownsBitmap) image?.Dispose();
        }

        using var background = backdrop.Snapshot();
        using var source = SKBitmap.FromImage(background);
        var sourcePixels = source.Pixels;
        var sourceWidth = info.Width;

        var pixels = new SKColor[width * height];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 2, 8) };

        Parallel.For(0, height, parallel, y =>
        {
            var cy = y + 0.5f - halfHeight;
            for (var x = 0; x < width; x++)
            {
                var cx = x + 0.5f - halfWidth;

                var bevel = field.Evaluate(x + 0.5f, y + 0.5f);
                var sdRaw = -bevel.Depth;
                var sd = Math.Min(sdRaw, 0f);

                // Refraction lens: quarter-circle displacement peaking at the outline, zero
                // across the interior — the library's circleMap.
                var depth01 = refrHeight > 0f ? Math.Clamp(-sd / refrHeight, 0f, 1f) : 1f;
                var lensX = 1f - depth01;
                var d = (1f - MathF.Sqrt(MathF.Max(0f, 1f - lensX * lensX))) * amount;

                var grad = GradSd(cx, cy, halfWidth, halfHeight, gradRadius);
                var radialLen = MathF.Sqrt(cx * cx + cy * cy);
                var rx = cx / MathF.Max(radialLen, 1e-4f);
                var ry = cy / MathF.Max(radialLen, 1e-4f);
                var gx = grad.X + depthEffect * rx;
                var gy = grad.Y + depthEffect * ry;
                var gradLen = MathF.Sqrt(gx * gx + gy * gy);
                gx /= gradLen;
                gy /= gradLen;

                var sx = x + pad + 0.5f - gx * d;
                var sy = y + pad + 0.5f - gy * d;

                var col = Sample(sx, sy);
                var r = (float)col.Red;
                var g = (float)col.Green;
                var b = (float)col.Blue;

                // Chromatic aberration: the library's seven-tap spectral split scaled by the
                // diagonal quadrant factor.
                if (chroma > 0.001f && d > 0f)
                {
                    var dispersionIntensity = chroma * (cx * cy) / MathF.Max(halfWidth * halfHeight, 1e-5f);
                    var dx = d * gx * dispersionIntensity;
                    var dy = d * gy * dispersionIntensity;
                    var cRed = Sample(sx + dx, sy + dy);
                    var cOrange = Sample(sx + dx * (2f / 3f), sy + dy * (2f / 3f));
                    var cYellow = Sample(sx + dx / 3f, sy + dy / 3f);
                    var cGreen = Sample(sx, sy);
                    var cCyan = Sample(sx - dx / 3f, sy - dy / 3f);
                    var cBlue = Sample(sx - dx * (2f / 3f), sy - dy * (2f / 3f));
                    var cPurple = Sample(sx - dx, sy - dy);
                    r = cRed.Red / 3.5f + cOrange.Red / 3.5f + cYellow.Red / 3.5f + cPurple.Red / 7f;
                    g = cOrange.Green / 7f + cYellow.Green / 3.5f + cGreen.Green / 3.5f + cCyan.Green / 3.5f;
                    b = cCyan.Blue / 3f + cBlue.Blue / 3f + cPurple.Blue / 3f;
                }

                // Vibrancy: the library's saturation boost around Rec.601 luma.
                var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = luma + (r - luma) * saturation;
                g = luma + (g - luma) * saturation;
                b = luma + (b - luma) * saturation;

                // Surface scrim: the solid background colour at tint.
                r = r * (1f - tint) + coating.Red * tint;
                g = g * (1f - tint) + coating.Green * tint;
                b = b * (1f - tint) + coating.Blue * tint;

                // Outline highlight: hairline stroke, |dot(edge normal, light)|^falloff, additive.
                var strokeMask = 1f - SmoothStep(strokeHalf - strokeFeather, strokeHalf + strokeFeather, Math.Abs(sdRaw));
                var hgrad = GradSd(cx, cy, halfWidth, halfHeight, gradRadius);
                var intensity = MathF.Pow(MathF.Abs(hgrad.X * lightX + hgrad.Y * lightY), falloff);
                var light = 255f * intensity * strokeMask * strokeAlpha;
                r += light;
                g += light;
                b += light;

                pixels[y * width + x] = new SKColor(ClampByte(r), ClampByte(g), ClampByte(b));
            }
        });

        using var refracted = new SKBitmap(width, height);
        refracted.Pixels = pixels;
        using var shader = refracted.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var output = SKSurface.Create(new SKImageInfo(width, height));
        output.Canvas.Clear(SKColors.Transparent);
        using var glassPaint = new SKPaint { Shader = shader, IsAntialias = true };
        output.Canvas.DrawRoundRect(new SKRect(0, 0, width, height), radius, radius, glassPaint);
        using var result = output.Snapshot();
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!result.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            bitmap.Dispose();
            return null;
        }
        bitmap.SetImmutable();
        return bitmap;

        SKColor Sample(float px, float py)
        {
            px = Math.Clamp(px, 0, info.Width - 1);
            py = Math.Clamp(py, 0, info.Height - 1);
            var ix = (int)px; var iy = (int)py;
            var x1 = Math.Min(ix + 1, info.Width - 1); var y1 = Math.Min(iy + 1, info.Height - 1);
            var a = sourcePixels[iy * sourceWidth + ix]; var b2 = sourcePixels[iy * sourceWidth + x1];
            var c = sourcePixels[y1 * sourceWidth + ix]; var d2 = sourcePixels[y1 * sourceWidth + x1];
            var fx = px - ix; var fy = py - iy;
            return new SKColor(
                LerpByte(a.Red, b2.Red, c.Red, d2.Red, fx, fy),
                LerpByte(a.Green, b2.Green, c.Green, d2.Green, fx, fy),
                LerpByte(a.Blue, b2.Blue, c.Blue, d2.Blue, fx, fy));
        }
    }

    /// <summary>
    /// CPU twin of the shader's <c>gradSdRoundedRect</c> (<see cref="LiquidGlassV2Effect"/>):
    /// the SDF gradient at an inflated radius, restructured to be continuous everywhere. The
    /// library's field hard-switches the axis along the card diagonals and collapses to a 45°
    /// spike exactly on the arc-centre lines, and wherever the band grew past ~1.2× the corner
    /// radius both sliced hard seams into the rim at all four corners — here the corner sector
    /// keeps the radial fan, the edge strips keep exact edge normals, and the deep interior
    /// reflects the radial across the diagonal so it continues the fan smoothly.
    /// </summary>
    internal static (float X, float Y) GradSd(float cx, float cy, float halfWidth, float halfHeight, float gradRadius)
    {
        var cornerX = Math.Abs(cx) - (halfWidth - gradRadius);
        var cornerY = Math.Abs(cy) - (halfHeight - gradRadius);
        var sx = cx >= 0 ? 1f : -1f;
        var sy = cy >= 0 ? 1f : -1f;
        if (cornerX > 0f && cornerY > 0f)
        {
            var len = MathF.Sqrt(cornerX * cornerX + cornerY * cornerY);
            return (sx * cornerX / len, sy * cornerY / len);
        }
        if (cornerX >= 0f) return (sx, 0f);
        if (cornerY >= 0f) return (0f, sy);
        var ex = Math.Max(-cornerX, 1e-5f);
        var ey = Math.Max(-cornerY, 1e-5f);
        var elen = MathF.Sqrt(ex * ex + ey * ey);
        return (sx * ey / elen, sy * ex / elen);
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    private static byte LerpByte(byte p, byte q, byte r, byte s, float fx, float fy) =>
        (byte)Math.Clamp(MathF.Round((p + (q - p) * fx) * (1 - fy) + (r + (s - r) * fx) * fy), 0f, 255f);

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value >= edge1 ? 1f : 0f;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
