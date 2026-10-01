using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace GlassGpuProbe;

/// <summary>
/// Where a sampling round's ~47 ms actually goes, and what each lever is worth.
/// <para>
/// A round costs one desktop capture plus one shared backdrop build, and that pair is the hard
/// ceiling on the live-sampling rate: a 3 ms interval cannot be honoured by a pipeline whose floor
/// is 47 ms. This measures the two halves and the obvious ways to shrink them, so the optimisation
/// is aimed at whichever term actually dominates.
/// </para>
/// </summary>
internal static class Perf
{
    public static void Run()
    {
        Probe.Write("=== sampling round cost breakdown (2560x1440 desktop) ===");

        using var wallpaperBitmap = MakeWallpaper(2560, 1440);
        using var wallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), wallpaperBitmap, live: true);
        var theme = new Theme(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass);
        var frame = new LiquidGlassRenderer.Frame(420, 190, 1.25f, 24f, 100f, 100f, 2560f, 1440f,
            0f, 0f, 2560f, 1440f, theme, true, SettingsSurface: false, PixelScale: 1f, Columns: 4, Rows: 2);

        // --- the shared backdrop build, as it ships ---
        var sigma = (float)theme.EffectiveLiquidGlass.Blur * frame.Scale / 8f;
        Probe.Write($"  sigma={sigma:F2} scale=1.00 (MaxBackdropSide 2560 == desktop width)");
        Probe.Write($"  A. full Build (SKSurface + blur + High-quality draw + Snapshot/ReadPixels): " +
                    $"{Best(() => { LiquidGlassSourceCache.Get(frame, wallpaper)?.Dispose(); return null; }, 8):F1} ms");

        // --- step by step, so the dominant term is not a guess ---
        Probe.Write($"  B. SKSurface.Create(2560x1440) + Clear only            : {Best(() => SurfaceOnly(), 8):F1} ms");
        Probe.Write($"  C. ... + DrawWallpaper FilterQuality.High + blur       : {Best(() => DrawOnly(wallpaperBitmap, frame, sigma, SKFilterQuality.High), 8):F1} ms");
        Probe.Write($"  D. ... + DrawWallpaper FilterQuality.None + blur       : {Best(() => DrawOnly(wallpaperBitmap, frame, sigma, SKFilterQuality.None), 8):F1} ms");
        Probe.Write($"  E. ... + Snapshot + ReadPixels into a second SKBitmap  : {Best(() => DrawAndRead(wallpaperBitmap, frame, sigma, SKFilterQuality.None), 8):F1} ms");

        // --- what a smaller backdrop would cost: pixels drop with the square of the scale ---
        foreach (var side in new[] { 1600f, 1280f })
        {
            var scale = side / 2560f;
            var info = new SKImageInfo((int)(2560 * scale), (int)(1440 * scale), SKColorType.Bgra8888, SKAlphaType.Opaque);
            Probe.Write($"  F. same steps at backdrop {info.Width}x{info.Height} (scale {scale:F2})    : " +
                        $"{Best(() => DrawAndReadScaled(wallpaperBitmap, frame, sigma * scale, scale, info), 8):F1} ms");
        }

        // --- the other half: grabbing the desktop ---
        Probe.Write("  --- desktop capture ---");
        foreach (var divisor in new[] { 1, 2, 3 })
        {
            var w = 2560 / divisor;
            var h = 1440 / divisor;
            var ms = Best(() => CaptureProbe(w, h), 6);
            Probe.Write($"  G. PrintWindow into a {w}x{h} DC (desktop/{divisor})            : {ms:F1} ms");
        }

        VerifyScaledCapture();
    }

    /// <summary>
    /// Does <c>PrintWindow</c> actually <i>scale</i> the window into a smaller DC, or does it render
    /// at native size and hand back the top-left crop? The timings say the smaller targets are
    /// cheaper, but a crop would also be cheaper — and would silently show the wrong part of the
    /// wallpaper, so the pixels have to be checked, not the clock.
    /// </summary>
    private static void VerifyScaledCapture()
    {
        using var full = CaptureToBitmap(2560, 1440);
        using var half = CaptureToBitmap(1280, 720);
        if (full == null || half == null)
        {
            Probe.Write("  H. scaled-capture check skipped (no Progman window)");
            return;
        }

        var info = new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var downscaled = new SKBitmap(info);
        using (var fullImage = SKImage.FromBitmap(full))
        using (var surface = SKSurface.Create(info))
        {
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.Medium };
            surface.Canvas.DrawImage(fullImage, new SKRect(0, 0, 1280, 720), paint);
            using var snap = surface.Snapshot();
            snap.ReadPixels(info, downscaled.GetPixels(), downscaled.RowBytes, 0, 0);
        }

        double fromScaled = 0, fromCrop = 0;
        var samples = 0;
        for (var y = 0; y < 720; y += 8)
        for (var x = 0; x < 1280; x += 8)
        {
            var a = half.GetPixel(x, y);
            var scaled = downscaled.GetPixel(x, y);
            var crop = full.GetPixel(x, y); // what a top-left crop would have produced
            fromScaled += Math.Abs(a.Red - scaled.Red) + Math.Abs(a.Green - scaled.Green) + Math.Abs(a.Blue - scaled.Blue);
            fromCrop += Math.Abs(a.Red - crop.Red) + Math.Abs(a.Green - crop.Green) + Math.Abs(a.Blue - crop.Blue);
            samples++;
        }

        fromScaled /= samples;
        fromCrop /= samples;
        Probe.Write($"  H. half-size capture vs downscaled full: |delta| {fromScaled:F1}/765; " +
                    $"vs top-left crop: {fromCrop:F1}/765 => {(fromScaled < fromCrop / 2 ? "SCALES" : "LIKELY A CROP")}");
    }

    private static SKBitmap? CaptureToBitmap(int width, int height)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return null;
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(progman, memDc, 0x00000002)) return null;
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var result = new SKBitmap(info);
            var header = new BitmapInfoHeader
            {
                Size = Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0
            };
            var dib = new BitmapInfo { Header = header };
            if (GetDIBits(memDc, bitmap, 0, (uint)height, result.GetPixels(), ref dib, 0) != height)
            {
                result.Dispose();
                return null;
            }
            return result;
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);

    private static SKBitmap? SurfaceOnly()
    {
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440, SKColorType.Bgra8888, SKAlphaType.Opaque));
        surface.Canvas.Clear(new SKColor(32, 38, 48));
        return null;
    }

    private static SKBitmap? DrawOnly(SKBitmap image, LiquidGlassRenderer.Frame frame, float sigma, SKFilterQuality quality)
    {
        using var surface = SKSurface.Create(new SKImageInfo(2560, 1440, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(32, 38, 48));
        using var filter = SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = quality, ImageFilter = filter };
        canvas.Save();
        canvas.Scale(1f);
        LiquidGlassRenderer.DrawWallpaper(canvas, image, paint, frame with { DesktopX = 0f, DesktopY = 0f }, Textured(image));
        canvas.Restore();
        return null;
    }

    private static SKBitmap? DrawAndRead(SKBitmap image, LiquidGlassRenderer.Frame frame, float sigma, SKFilterQuality quality)
        => DrawAndReadScaled(image, frame, sigma, 1f, new SKImageInfo(2560, 1440, SKColorType.Bgra8888, SKAlphaType.Opaque), quality);

    private static SKBitmap? DrawAndReadScaled(SKBitmap image, LiquidGlassRenderer.Frame frame, float sigma, float scale, SKImageInfo info)
        => DrawAndReadScaled(image, frame, sigma, scale, info, SKFilterQuality.None);

    private static SKBitmap? DrawAndReadScaled(SKBitmap image, LiquidGlassRenderer.Frame frame, float sigma, float scale,
        SKImageInfo info, SKFilterQuality quality)
    {
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(32, 38, 48));
        using var filter = sigma > 0.05f ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
        using var paint = new SKPaint { IsAntialias = true, FilterQuality = quality, ImageFilter = filter };
        canvas.Save();
        canvas.Scale(scale);
        LiquidGlassRenderer.DrawWallpaper(canvas, image, paint, frame with { DesktopX = 0f, DesktopY = 0f }, Textured(image));
        canvas.Restore();
        using var snapshot = surface.Snapshot();
        var bitmap = new SKBitmap(info);
        snapshot.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0);
        return bitmap;
    }

    // -----------------------------------------------------------------------------------------
    // capture: does asking GDI for a smaller target actually make PrintWindow cheaper?
    // -----------------------------------------------------------------------------------------

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string? cls, string? name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    private static SKBitmap? CaptureProbe(int width, int height)
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return null;
        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);
        try
        {
            PrintWindow(progman, memDc, 0x00000002);
            return null;
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static double Best(Func<SKBitmap?> action, int iterations)
    {
        action()?.Dispose();
        var best = double.MaxValue;
        for (var i = 0; i < iterations; i++)
        {
            var watch = Stopwatch.StartNew();
            action()?.Dispose();
            watch.Stop();
            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }
        return best;
    }

    /// <summary>Cheap non-owning snapshot over an image the caller keeps alive.</summary>
    private static WallpaperSnapshot Textured(SKBitmap image) =>
        WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), image, live: true);

    private static SKBitmap MakeWallpaper(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(24, 46, 32));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(210, 230, 200);
        surface.Canvas.DrawCircle(width * 0.62f, height * 0.4f, 320, paint);
        paint.Color = new SKColor(60, 140, 90);
        surface.Canvas.DrawCircle(width * 0.3f, height * 0.65f, 240, paint);
        paint.Color = new SKColor(240, 240, 245);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height * 0.12f), paint);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image).Copy();
    }
}

