using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// CPU renderer for the 液态玻璃 material (<see cref="SurfaceStyle.LiquidGlass"/>): the
/// per-pixel twin of <see cref="LiquidGlassEffect"/>, used as the software-rendering fallback
/// (and for the clock's pre-rendered frames).
/// <para>
/// A faithful port of Kyant0/AndroidLiquidGlass 2.0's optical model — the rounded-rect refraction
/// lens with depth effect, diagonal chromatic aberration, vibrancy, the surface scrim, and the
/// hairline outline highlight with configurable specular highlight angle.
/// </para>
/// </summary>
public static class LiquidGlassRenderer
{
    public record Frame(int Width, int Height, float Scale, float Radius,
        float DesktopX, float DesktopY, float DesktopWidth, float DesktopHeight,
        float ScreenX, float ScreenY, float ScreenWidth, float ScreenHeight,
        Theme Theme, bool Dark, bool SettingsSurface = false, float PixelScale = 1,
        int Columns = 0, int Rows = 0, float GlyphOpacity = 1f, float BackdropScale = 0f)
    {
        public float EffectiveBackdropScale => BackdropScale > 0f ? BackdropScale : Scale;
    }

    public record AdaptiveOptics(
        float EdgeScale,
        float ShiftScale,
        float SpreadMult,
        float InnerMult,
        float RimDips,
        float MaxLensFrac,
        float RimBaseLight,
        float RimDirLight,
        float RimLightScale);

    public static AdaptiveOptics GetAdaptiveOptics(Frame frame, float minSideDip, float maxSideDip)
    {
        bool is1x1 = (frame.Columns == 1 && frame.Rows == 1) || (frame.Columns == 0 && minSideDip <= 110f && maxSideDip <= 115f);
        if (is1x1)
        {
            return new AdaptiveOptics(
                EdgeScale: 0.58f,
                ShiftScale: 0.68f,
                SpreadMult: 1.40f,
                InnerMult: 1.30f,
                RimDips: 1.20f,
                MaxLensFrac: 0.28f,
                RimBaseLight: 0.28f,
                RimDirLight: 0.62f,
                RimLightScale: 1.10f);
        }

        bool is1Strip = (frame.Columns == 1 || frame.Rows == 1) || (frame.Columns == 0 && minSideDip <= 130f);
        if (is1Strip)
        {
            return new AdaptiveOptics(
                EdgeScale: 0.75f,
                ShiftScale: 0.82f,
                SpreadMult: 1.60f,
                InnerMult: 1.45f,
                RimDips: 1.40f,
                MaxLensFrac: 0.35f,
                RimBaseLight: 0.29f,
                RimDirLight: 0.66f,
                RimLightScale: 1.18f);
        }

        return new AdaptiveOptics(
            EdgeScale: 1.0f,
            ShiftScale: 1.0f,
            SpreadMult: 1.80f,
            InnerMult: 1.60f,
            RimDips: 1.60f,
            MaxLensFrac: 0.45f,
            RimBaseLight: 0.30f,
            RimDirLight: 0.70f,
            RimLightScale: 1.25f);
    }

    /// <summary>
    /// The frame with its desktop origin moved to the snapshot's own patch origin. A full-desktop
    /// snapshot has <c>RegionX/Y = 0</c>, so this is the identity there.
    /// </summary>
    public static Frame RegionFrame(Frame frame, WallpaperSnapshot wallpaper) =>
        wallpaper.RegionX == 0 && wallpaper.RegionY == 0
            ? frame
            : frame with
            {
                DesktopX = frame.DesktopX - wallpaper.RegionX,
                DesktopY = frame.DesktopY - wallpaper.RegionY
            };

    /// <summary>Builds a card-local backdrop bitmap for the GPU shader path.</summary>
    public static SKBitmap? CreateBackdrop(Frame frame, WallpaperSnapshot wallpaper)
    {
        try
        {
            var source = wallpaper.CachedBitmap;
            if (source == null && wallpaper.ImageBytes != null) source = SKBitmap.Decode(wallpaper.ImageBytes);
            if (source == null) return null;
            var sigma = (float)frame.Theme.EffectiveLiquidGlass.Blur * frame.Scale / 8f;
            var radius = Math.Clamp(frame.Radius * frame.Scale, 0f, Math.Min(frame.Width, frame.Height) / 2f);
            var pad = (int)Math.Ceiling(Math.Max(sigma * 3f, 16f));
            using var surface = SKSurface.Create(new SKImageInfo(frame.Width + 2 * pad, frame.Height + 2 * pad));
            surface.Canvas.Clear(wallpaper.Background);
            using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma) : null;
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
            surface.Canvas.Save();
            surface.Canvas.Translate(pad, pad);
            // A region-limited capture addresses the desktop from its own origin: shifting the
            // frame by -region puts the patch back where it belongs, and the optics are untouched.
            DrawWallpaper(surface.Canvas, source, paint, RegionFrame(frame, wallpaper), wallpaper);
            surface.Canvas.Restore();
            using var image = surface.Snapshot();
            var result = new SKBitmap(new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            image.ReadPixels(result.Info, result.GetPixels(), result.RowBytes, pad, pad);
            result.SetImmutable();
            if (!ReferenceEquals(source, wallpaper.CachedBitmap)) source.Dispose();
            return result;
        }
        catch { return null; }
    }

    /// <summary>Render one background to PNG. Safe on worker threads.</summary>
    public static byte[] Render(Frame frame, WallpaperSnapshot wallpaper)
    {
        using var bitmap = RenderBitmap(frame, wallpaper);
        if (bitmap == null) return [];
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Render one background. Safe on worker threads; the caller owns the result.
    /// </summary>
    public static SKBitmap? RenderBitmap(Frame frame, WallpaperSnapshot wallpaper)
    {
        var optics = frame.Theme.EffectiveLiquidGlass;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var minSide = Math.Min(width, height);
        var sigma = (float)optics.Blur * scale / 8f;
        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);

        var lens = LiquidGlassV2Lens.Compute(optics.Refraction, minSide, radius);
        var amount = lens.Amount;
        var refrHeight = lens.Band;
        var depthEffect = lens.DepthEffect;

        var chroma = (float)(optics.Dispersion / 100.0);
        var saturation = 1f + (float)LiquidGlassSettings.VibrancySaturationBoost * (float)(optics.Vibrancy / 100.0);

        var colorHex = frame.Dark ? frame.Theme.EffectiveSolidBackgroundDark : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.20);

        var strokeHalf = MathF.Ceiling((float)LiquidGlassSettings.StrokeWidthDips * scale);
        var strokeFeather = (float)LiquidGlassSettings.StrokeFeatherDips * scale;
        var strokeAlpha = (float)(optics.Highlight / LiquidGlassSettings.HighlightReference);
        var falloff = (float)LiquidGlassSettings.HighlightFalloff;
        var angle = (float)(optics.HighlightAngle * Math.PI / 180.0);
        var lightX = MathF.Cos(angle);
        var lightY = MathF.Sin(angle);

        var halfWidth = width / 2f;
        var halfHeight = height / 2f;
        var gradRadius = lens.GradRadius;

        var field = new BevelField(width, height, radius);
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
                using var filter = sigma > 0 ? SKImageFilter.CreateBlur(sigma, sigma) : null;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
                canvas.Save();
                canvas.Translate(pad, pad);
                DrawWallpaper(canvas, image, paint, RegionFrame(frame, wallpaper), wallpaper);
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
                if (gradLen > 1e-5f)
                {
                    gx /= gradLen;
                    gy /= gradLen;
                }
                else
                {
                    gx = 0f;
                    gy = 0f;
                }

                var sx = x + pad + 0.5f - gx * d;
                var sy = y + pad + 0.5f - gy * d;

                var col = Sample(sx, sy);
                var r = (float)col.Red;
                var g = (float)col.Green;
                var b = (float)col.Blue;

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

                var luma = 0.2126f * r + 0.7152f * g + 0.0722f * b;
                r = luma + (r - luma) * saturation;
                g = luma + (g - luma) * saturation;
                b = luma + (b - luma) * saturation;

                r = r * (1f - tint) + coating.Red * tint;
                g = g * (1f - tint) + coating.Green * tint;
                b = b * (1f - tint) + coating.Blue * tint;

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

    public static (float X, float Y) GradSd(float cx, float cy, float halfWidth, float halfHeight, float gradRadius)
    {
        var cornerX = Math.Abs(cx) - (halfWidth - gradRadius);
        var cornerY = Math.Abs(cy) - (halfHeight - gradRadius);
        var sx = cx >= 0 ? 1f : -1f;
        var sy = cy >= 0 ? 1f : -1f;
        if (cornerX >= 0f && cornerY >= 0f)
        {
            var mx = Math.Max(cornerX, 1e-5f);
            var my = Math.Max(cornerY, 1e-5f);
            var len = MathF.Sqrt(mx * mx + my * my);
            return (sx * mx / len, sy * my / len);
        }
        if (cornerX >= 0f) return (sx, 0f);
        if (cornerY >= 0f) return (0f, sy);
        var ex = Math.Max(-cornerX, 1e-5f);
        var ey = Math.Max(-cornerY, 1e-5f);
        var elen = MathF.Sqrt(ex * ex + ey * ey);
        return (sx * ey / elen, sy * ex / elen);
    }

    public static byte ClampByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);

    public static byte LerpByte(byte p, byte q, byte r, byte s, float fx, float fy) =>
        (byte)Math.Clamp(MathF.Round((p + (q - p) * fx) * (1 - fy) + (r + (s - r) * fx) * fy), 0f, 255f);

    public static float SmoothStep(float edge0, float edge1, float value)
    {
        if (edge1 <= edge0) return value >= edge1 ? 1f : 0f;
        var t = Math.Clamp((value - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public sealed class BevelField
    {
        public readonly float HalfWidth, HalfHeight, InnerWidth, InnerHeight, Radius;

        public BevelField(int width, int height, float radius)
        {
            HalfWidth = width / 2f;
            HalfHeight = height / 2f;
            Radius = Math.Clamp(radius, 0f, Math.Min(HalfWidth, HalfHeight));
            InnerWidth = HalfWidth - Radius;
            InnerHeight = HalfHeight - Radius;
        }

        public Bevel Evaluate(float x, float y)
        {
            var vx = x - HalfWidth;
            var vy = y - HalfHeight;
            var sx = vx >= 0 ? 1f : -1f;
            var sy = vy >= 0 ? 1f : -1f;
            var ax = MathF.Abs(vx);
            var ay = MathF.Abs(vy);
            var qx = ax - InnerWidth;
            var qy = ay - InnerHeight;

            if (qx > 0f && qy > 0f)
            {
                var len = MathF.Sqrt(qx * qx + qy * qy);
                if (len > 1e-5f)
                {
                    return new Bevel(qx / len * sx, qy / len * sy, Radius - len);
                }
                return new Bevel(0.7071f * sx, 0.7071f * sy, Radius);
            }

            if (qx <= 0f && qy > 0f)
            {
                return new Bevel(0f, sy, HalfHeight - ay);
            }

            if (qx > 0f && qy <= 0f)
            {
                return new Bevel(sx, 0f, HalfWidth - ax);
            }

            var distV = HalfWidth - ax;
            var distH = HalfHeight - ay;
            var depth = MathF.Min(distV, distH);

            var u = -qx;
            var v = -qy;
            var angle = MathF.Atan2(u + 1e-4f, v + 1e-4f);
            var nx = MathF.Cos(angle) * sx;
            var ny = MathF.Sin(angle) * sy;

            return new Bevel(nx, ny, depth);
        }
    }

    public readonly struct Bevel
    {
        public readonly float Nx, Ny, Depth;

        public Bevel(float nx, float ny, float depth)
        {
            Nx = nx; Ny = ny; Depth = depth;
        }
    }

    public static void DrawWallpaper(SKCanvas canvas, SKBitmap image, SKPaint paint,
        Frame frame, WallpaperSnapshot wallpaper)
    {
        var glass = frame.Theme.EffectiveGlass;
        var offX = (float)(glass.WallpaperOffsetX * frame.Scale);
        var offY = (float)(glass.WallpaperOffsetY * frame.Scale);

        if (wallpaper.LiveCapture)
        {
            var rect = SKRect.Create(-frame.DesktopX - offX, -frame.DesktopY - offY,
                image.Width * frame.PixelScale, image.Height * frame.PixelScale);
            if (frame.PixelScale > 0f && (frame.DesktopX != 0f || frame.DesktopY != 0f)
                && canvas.GetLocalClipBounds(out var clip) && !clip.IsEmpty)
            {
                var margin = Math.Max(96f, 24f * frame.Scale);
                var wantedLeft = Math.Max(rect.Left, clip.Left - margin);
                var wantedTop = Math.Max(rect.Top, clip.Top - margin);
                var wantedRight = Math.Min(rect.Right, clip.Right + margin);
                var wantedBottom = Math.Min(rect.Bottom, clip.Bottom + margin);
                if (wantedRight > wantedLeft && wantedBottom > wantedTop
                    && (wantedRight - wantedLeft < rect.Width * 0.75f || wantedBottom - wantedTop < rect.Height * 0.75f))
                {
                    var invScale = 1f / frame.PixelScale;
                    var srcLeft = Math.Clamp((int)MathF.Floor((wantedLeft - rect.Left) * invScale), 0, image.Width - 1);
                    var srcTop = Math.Clamp((int)MathF.Floor((wantedTop - rect.Top) * invScale), 0, image.Height - 1);
                    var srcRight = Math.Clamp((int)MathF.Ceiling((wantedRight - rect.Left) * invScale), srcLeft + 1, image.Width);
                    var srcBottom = Math.Clamp((int)MathF.Ceiling((wantedBottom - rect.Top) * invScale), srcTop + 1, image.Height);
                    var srcRect = new SKRect(srcLeft, srcTop, srcRight, srcBottom);
                    var dstRect = SKRect.Create(
                        rect.Left + srcLeft * frame.PixelScale,
                        rect.Top + srcTop * frame.PixelScale,
                        (srcRight - srcLeft) * frame.PixelScale,
                        (srcBottom - srcTop) * frame.PixelScale);
                    canvas.DrawBitmap(image, srcRect, dstRect, paint);
                    return;
                }
            }
            canvas.DrawBitmap(image, rect, paint);
            return;
        }

        var style = wallpaper.Style;
        var tile = wallpaper.Tile;
        var x = frame.ScreenX - frame.DesktopX;
        var y = frame.ScreenY - frame.DesktopY;
        var w = frame.ScreenWidth;
        var h = frame.ScreenHeight;
        if (style == "22")
        {
            x = -frame.DesktopX;
            y = -frame.DesktopY;
            w = frame.DesktopWidth;
            h = frame.DesktopHeight;
        }
        if (tile)
        {
            var matrix = SKMatrix.CreateScale(frame.PixelScale, frame.PixelScale);
            matrix.TransX = x - offX;
            matrix.TransY = y - offY;
            using var tiled = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, matrix);
            using var tiledPaint = new SKPaint { Shader = tiled, ImageFilter = paint.ImageFilter };
            canvas.DrawPaint(tiledPaint);
            return;
        }
        float drawWidth, drawHeight;
        if (style == "2") { drawWidth = w; drawHeight = h; }
        else if (style == "0") { drawWidth = image.Width * frame.PixelScale; drawHeight = image.Height * frame.PixelScale; }
        else
        {
            var ratio = style == "6" ? Math.Min(w / image.Width, h / image.Height) : Math.Max(w / image.Width, h / image.Height);
            drawWidth = image.Width * ratio;
            drawHeight = image.Height * ratio;
        }
        canvas.DrawBitmap(image, SKRect.Create(x + (w - drawWidth) / 2 - offX, y + (h - drawHeight) / 2 - offY, drawWidth, drawHeight), paint);
    }
}

public sealed class WallpaperLease : IDisposable
{
    private static int liveLeases;
    private static long liveLeaseBytes;

    private int references = 1;
    private readonly SKBitmap? bitmap;
    private readonly bool pooled;

    public WallpaperLease(SKBitmap? bitmap) : this(bitmap, false)
    {
    }

    /// <param name="pooled">
    /// The bitmap was rented from <see cref="GlassCapture.PatchBitmapPool"/>: releasing the last
    /// reference hands it back for the next capture instead of freeing it. This is what keeps a live
    /// screen sample from allocating a patch per published frame — the pixels are native, so the GC
    /// never sees them, and a per-frame allocation shows up as the process ballooning by a gigabyte.
    /// </param>
    internal WallpaperLease(SKBitmap? bitmap, bool pooled)
    {
        this.bitmap = bitmap;
        this.pooled = pooled;
        Interlocked.Increment(ref liveLeases);
        if (bitmap is { } leased) Interlocked.Add(ref liveLeaseBytes, (long)leased.RowBytes * leased.Height);
        ReportLeases();
    }

    public SKBitmap? Bitmap => bitmap;

    public void AddRef() => Interlocked.Increment(ref references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) > 0) return;
        Interlocked.Decrement(ref liveLeases);
        if (bitmap is { } released)
        {
            Interlocked.Add(ref liveLeaseBytes, -((long)released.RowBytes * released.Height));
            if (pooled) GlassCapture.PatchBitmapPool.Return(released);
            else
            {
                try { released.Dispose(); } catch { }
            }
        }

        ReportLeases();
    }

    private static void ReportLeases() =>
        DeskCanvas.Runtime.ResourceLedger.Instance.Report("glass.wallpaper-lease",
            DeskCanvas.Contracts.ResourceKind.GlassLease,
            Volatile.Read(ref liveLeases), Interlocked.Read(ref liveLeaseBytes),
            Environment.CurrentManagedThreadId.ToString());
}

public record WallpaperSnapshot(
    byte[]? ImageBytes,
    SKColor Background,
    string Style = "10",
    bool Tile = false,
    bool LiveCapture = false) : IDisposable
{
    private static long nextCaptureId;
    private byte[]? _imageBytes = ImageBytes;

    public long CaptureId { get; } = Interlocked.Increment(ref nextCaptureId);

    internal WallpaperLease? Lease { get; init; }

    public SKBitmap? CachedBitmap => Lease?.Bitmap;

    /// <summary>
    /// Where <see cref="CachedBitmap"/>'s pixel (0,0) sits in virtual-desktop physical pixels.
    /// <para>
    /// A live capture is <b>region-limited</b>: it holds the neighbourhood the glass needs, not the
    /// whole desktop (see <c>GlassInterest</c>). One bitmap pixel is still one desktop pixel, so the
    /// renderer only has to know where the patch starts — <c>(0,0)</c> means "the desktop origin",
    /// which is what the static wallpaper path and every pre-region snapshot carry.
    /// </para>
    /// </summary>
    public int RegionX { get; init; }

    /// <summary>See <see cref="RegionX"/>.</summary>
    public int RegionY { get; init; }

    public static WallpaperSnapshot FromBitmap(byte[]? bytes, SKColor background, SKBitmap? bitmap,
        string style = "10", bool tile = false, bool live = false, int regionX = 0, int regionY = 0,
        bool pooled = false) =>
        new(bytes, background, style, tile, live)
        {
            Lease = bitmap == null ? null : new WallpaperLease(bitmap, pooled),
            RegionX = regionX,
            RegionY = regionY
        };

    public byte[]? ImageBytes
    {
        get
        {
            if (_imageBytes != null) return _imageBytes;
            if (CachedBitmap != null)
            {
                try
                {
                    using var img = SKImage.FromBitmap(CachedBitmap);
                    using var data = img.Encode(SKEncodedImageFormat.Png, 90);
                    _imageBytes = data.ToArray();
                }
                catch { }
            }
            return _imageBytes;
        }
        init => _imageBytes = value;
    }

    public WallpaperSnapshot WithPlacement(string style, bool tile)
    {
        Lease?.AddRef();
        return this with { Style = style, Tile = tile };
    }

    public void AddRef() => Lease?.AddRef();

    public void Dispose() => Lease?.Dispose();

    public string Describe() =>
        $"WallpaperSnapshot(bytes={_imageBytes?.Length ?? 0}, hasBitmap={CachedBitmap != null}, bg=#{Background.Red:X2}{Background.Green:X2}{Background.Blue:X2}, style={Style}, tile={Tile}, live={LiveCapture})";
}
