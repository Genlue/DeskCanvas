using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// The shared backdrop's blur must not depend on 背景清晰度: the cache downscales the desktop by
/// the clarity factor and blurs with a sigma stated in <b>desktop pixels</b>, which the canvas'
/// own scale converts into texture pixels (Skia maps an image filter's sigma through the CTM).
/// <para>
/// A sigma pre-multiplied by the clarity factor was therefore mapped through the CTM a second
/// time — sigma · scale² reached the texture — and the very same 模糊 value read ever softer the
/// lower the clarity was set (a quarter of the blur at the 25% default). This check pins the
/// invariance the way the eye sees it: it measures the 20-80% transition width of a step edge in
/// the built backdrop, expressed in desktop pixels, at both clarity extremes and demands they
/// agree. In the buggy ordering the low-clarity edge was ~4× too sharp, far outside tolerance.
/// </para>
/// </summary>
internal static class BackdropClarityBlurCheck
{
    private static int failures;

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("=== 背景清晰度 / 模糊 invariance ===");

        const int desktopWidth = 1600, desktopHeight = 1200;
        const int lineX = 800, lineHalf = 100; // wide enough that the two edges never contaminate each other

        // A "wallpaper" that is one white vertical line on black: its edges are the steps whose
        // blur width the measurement reads. Live capture keeps the desktop→image mapping 1:1.
        var image = new SKBitmap(new SKImageInfo(desktopWidth, desktopHeight, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(image))
        {
            canvas.Clear(SKColors.Black);
            using var paint = new SKPaint { Color = SKColors.White };
            canvas.DrawRect(lineX - lineHalf, 0, lineHalf * 2f, desktopHeight, paint);
        }
        // The snapshot's lease owns the bitmap from here on.
        using var wallpaper = WallpaperSnapshot.FromBitmap(null, SKColors.Black, image, live: true);

        LiquidGlassSourceCache.Clear();
        try
        {
            var native = Measure(BuildSource(wallpaper, 100), lineX, lineHalf, out var scaleNative);
            var quarter = Measure(BuildSource(wallpaper, 25), lineX, lineHalf, out var scaleQuarter);

            Check(scaleNative > 0.999f && scaleNative < 1.001f, "clarity 100% builds the backdrop at native resolution");
            Check(Math.Abs(scaleQuarter - 0.25f) < 0.001f, "clarity 25% builds the backdrop at a quarter resolution");
            Check(native > 4f && quarter > 4f, "the blur actually spreads (edge transition wider than a hard step)");

            var deviation = Math.Abs(quarter - native) / native;
            Check(deviation < 0.20,
                $"edge blur width in desktop px matches across clarity (100%: {native:F1} px, 25%: {quarter:F1} px, deviation {deviation:P1})");
        }
        finally
        {
            LiquidGlassSourceCache.Clear();
        }

        Console.WriteLine(failures == 0 ? "backdrop clarity blur check passed" : $"{failures} BACKDROP CLARITY CHECK(S) FAILED");
        return failures;
    }

    private static GlassSource BuildSource(WallpaperSnapshot wallpaper, double backdropClarity)
    {
        var theme = new Theme(null, null, 0.18, false, false, "Inter",
            Surface: SurfaceStyle.LiquidGlass,
            LiquidGlass: new LiquidGlassSettings(Blur: 100, BackdropClarity: backdropClarity));
        var frame = new LiquidGlassRenderer.Frame(
            Width: 400, Height: 300, Scale: 1f, Radius: 0f,
            DesktopX: 0f, DesktopY: 0f, DesktopWidth: 1600f, DesktopHeight: 1200f,
            ScreenX: 0f, ScreenY: 0f, ScreenWidth: 1600f, ScreenHeight: 1200f,
            Theme: theme, Dark: false);
        // Get hands out an owned reference; the measurement is done synchronously and the source
        // is released here, so the cache's own reference is what keeps the bitmap alive between
        // the two clarity settings.
        var source = LiquidGlassSourceCache.Get(frame, wallpaper);
        if (source == null) throw new InvalidOperationException("the shared backdrop build failed");
        return source;
    }

    /// <summary>
    /// 20-80% transition width of the line's two edges, averaged, converted from texture pixels
    /// to desktop pixels. The caller disposes the returned source.
    /// </summary>
    private static double Measure(GlassSource source, int lineX, int lineHalf, out float scale)
    {
        using (source)
        {
            scale = source.Scale;
            var backdrop = source.Backdrop;
            var y = backdrop.Height / 2;
            // All scan bounds are texture coordinates: the line's centre sits at lineX · scale.
            var center = (int)MathF.Round(lineX * scale);
            var left = EdgeWidth(backdrop, y, from: 0, to: center, ascending: true);
            var right = EdgeWidth(backdrop, y, from: backdrop.Width - 1, to: center, ascending: false);
            return (left + right) / 2f / scale;
        }
    }

    /// <summary>
    /// Distance between the 20% and 80% crossings of one edge, in texture pixels;
    /// <paramref name="from"/> exclusive, <paramref name="to"/> the line's centre (also exclusive —
    /// the opposite edge must not leak into the scan).
    /// </summary>
    private static double EdgeWidth(SKBitmap backdrop, int y, int from, int to, bool ascending)
    {
        var step = ascending ? 1 : -1;
        double low = double.MaxValue, high = double.MinValue;
        var row = new double[Math.Abs(to - from)];
        var i = 0;
        for (var x = from; x != to; x += step)
        {
            var value = (double)backdrop.GetPixel(x, y).Red;
            row[i++] = value;
            low = Math.Min(low, value);
            high = Math.Max(high, value);
        }
        var range = high - low;
        if (range < 1) throw new InvalidOperationException("the scanned backdrop row is flat — no edge under it");

        double at20 = -1, at80 = -1;
        for (var k = 0; k < row.Length - 1; k++)
        {
            var a = (row[k] - low) / range;
            var b = (row[k + 1] - low) / range;
            if (at20 < 0 && Math.Min(a, b) < 0.2 && Math.Max(a, b) >= 0.2) at20 = k + (0.2 - a) / (b - a);
            if (at80 < 0 && Math.Min(a, b) < 0.8 && Math.Max(a, b) >= 0.8) at80 = k + (0.8 - a) / (b - a);
        }
        if (at20 < 0 || at80 < 0) throw new InvalidOperationException("the expected 20%/80% crossings were not found");
        return Math.Abs(at80 - at20);
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"FAIL: {label}");
            return;
        }
        Console.WriteLine($"PASS: {label}");
    }
}
