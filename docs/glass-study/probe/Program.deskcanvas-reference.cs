using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using uWidgets.Core.Models.Settings;
using uWidgets.Services;
using probe;

// uWidgets' shipping CPU liquid-glass renderer, measured and rendered against the
// shared synthetic backdrop. The Android AGSL port is exercised in the Avalonia
// probe, because SkiaSharp 2.88.8 can only rasterize SkSL on a GPU context.
var mode = args.ElementAtOrDefault(0) ?? "uw";
var outDir = Path.GetFullPath(args.ElementAtOrDefault(1) ?? "out");
Directory.CreateDirectory(outDir);

const int Card = 420, Radius = 32;

var theme = JsonSerializer.Deserialize<Theme>(
    """{"DarkMode":null,"AccentColor":null,"OpacityLevel":0.18,"Monochrome":false,"UseNativeFrame":false,"FontFamily":"Inter"}""")!
    with { Surface = SurfaceStyle.LiquidGlass, LiquidGlass = new LiquidGlassSettings() };

using var backdrop = Backdrop.Build();

if (mode == "compose")
{
    // args: compose <outDir> <uwPng> <gpuPng> [gpuTunedPng]
    var uw = SKBitmap.Decode(args[2]);
    var gpu = SKBitmap.Decode(args[3]);
    var gpuTuned = args.Length > 4 ? SKBitmap.Decode(args[4]) : null;
    var count = gpuTuned is null ? 3 : 4;
    var size = uw.Width;
    using var strip = new SKBitmap(size * count + (count + 1) * 20, size + 80);
    using (var canvas = new SKCanvas(strip))
    {
        canvas.Clear(new SKColor(22, 22, 26));
        canvas.DrawBitmap(Backdrop.Crop(backdrop, Backdrop.CardX, Backdrop.CardY, size), 20, 40);
        canvas.DrawBitmap(uw, 40 + size, 40);
        canvas.DrawBitmap(gpu, 60 + size * 2, 40);
        if (gpuTuned is not null) canvas.DrawBitmap(gpuTuned, 80 + size * 3, 40);
    }
    Backdrop.Save(strip, Path.Combine(outDir, "10-compare.png"));
    return;
}

SKBitmap RenderUWidgets(int cardW, int cardH, int cx, int cy, float radius)
{
    var wallpaper = new WallpaperSnapshot(null, SKColors.Black, "2", false, false, backdrop);
    var frame = new LiquidGlassRenderer.Frame(cardW, cardH, 1f, radius,
        cx, cy, Backdrop.W, Backdrop.H,
        0, 0, Backdrop.W, Backdrop.H,
        theme, true);
    return SKBitmap.Decode(LiquidGlassRenderer.Render(frame, wallpaper));
}

Backdrop.Save(backdrop, Path.Combine(outDir, "00-backdrop.png"));

var card420 = RenderUWidgets(Card, Card, Backdrop.CardX, Backdrop.CardY, Radius);
Backdrop.Save(card420, Path.Combine(outDir, "uw-420.png"));

using (var scene = new SKBitmap(Backdrop.W, Backdrop.H))
{
    using (var canvas = new SKCanvas(scene))
    {
        canvas.DrawBitmap(backdrop, 0, 0);
        canvas.DrawBitmap(RenderUWidgets(Card, Card, 80, 220, Radius), 80, 220);
        canvas.DrawBitmap(RenderUWidgets(Card, Card, 460, 220, Radius), 460, 220);
        canvas.DrawBitmap(RenderUWidgets(Card, Card, 840, 220, Radius), 840, 220);
    }
    Backdrop.Save(scene, Path.Combine(outDir, "uw-scene.png"));
}

static double TimeMs(Action action, int iterations)
{
    action();
    var best = double.MaxValue;
    for (var i = 0; i < iterations; i++)
    {
        var watch = Stopwatch.StartNew();
        action();
        watch.Stop();
        best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
    }
    return best;
}

Console.WriteLine();
Console.WriteLine("uWidgets LiquidGlassRenderer (CPU, incl. PNG encode), best of N:");
var small = TimeMs(() => RenderUWidgets(Card, Card, Backdrop.CardX, Backdrop.CardY, Radius).Dispose(), 5);
Console.WriteLine($"  {Card}x{Card} ({Card * Card / 1000.0:F0}k px): {small:F1} ms");
var big = TimeMs(() => RenderUWidgets(1200, 800, 0, 0, Radius).Dispose(), 3);
Console.WriteLine($"  1200x800 (960k px): {big:F1} ms");
