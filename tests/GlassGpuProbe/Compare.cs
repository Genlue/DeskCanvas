using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace GlassGpuProbe;

/// <summary>
/// Renders the same card with the GPU material and with the CPU renderer, compares them and writes
/// both to <c>dist/glass-gpu-compare</c>.
/// <para>
/// The GPU path shipped without ever having drawn a frame on a device, so nothing had ever checked
/// that it looks like the CPU material it mirrors. The two use different backdrops (the GPU one
/// samples the shared downscaled desktop texture, the CPU one rebuilds the wallpaper at card
/// resolution), so they are not expected to be pixel-identical; a large difference means the shader
/// disagrees about geometry, sampling or colour.
/// </para>
/// </summary>
internal static class Compare
{
    public static void Run(GRContext grContext)
    {
        var output = Path.GetFullPath("dist/glass-gpu-compare");
        Directory.CreateDirectory(output);

        // A high-frequency wallpaper: the backdrop's resolution must not move where the material
        // samples from, and only structure — not a smooth wash — makes a positional error visible.
        foreach (var clarity in new[] { 100.0, 50.0, 25.0 })
            CompareAt(clarity, output, grContext);

        CoatingCheck(grContext);
    }

    /// <summary>
    /// The coating (纯色 background colour) has to reach the shader at the same scale the CPU
    /// renderer uses. It is mixed into a colour that is already in 0–255 units, so handing over a
    /// normalised 0–1 value contributes ~255× too little: the tint degenerates into a plain
    /// darkening and the card reads as though it were under a black mask, whatever colour is
    /// configured — and the higher 不透明度 is, the blacker it looks.
    /// </summary>
    private static void CoatingCheck(GRContext grContext)
    {
        const int card = 200;
        using var wallpaperBitmap = MakeWallpaper(800, 600);
        using var wallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), wallpaperBitmap, live: true);

        foreach (var (label, opacity, dark, hex) in new (string, double, bool, string)[]
        {
            ("dark  #2E2E2E @ 18%", 0.18, true, "#2E2E2E"),
            ("dark  #101418 @ 75%", 0.75, true, "#101418"),
            ("light #FFFFFF @ 60%", 0.60, false, "#FFFFFF"),
            ("light #E8F0FF @ 60%", 0.60, false, "#E8F0FF")
        })
        {
            var theme = new Theme(null, null, opacity, true, false, "Inter", SurfaceStyle.LiquidGlass,
                SolidBackgroundDark: hex, SolidBackgroundLight: hex,
                LiquidGlass: new LiquidGlassSettings(BackdropClarity: 100));
            var frame = new LiquidGlassRenderer.Frame(card, card, 1f, 20f, 100f, 80f, 800f, 600f,
                0f, 0f, 800f, 600f, theme, dark, SettingsSurface: false, PixelScale: 1f, Columns: 1, Rows: 1);

            using var cpu = LiquidGlassRenderer.RenderBitmap(frame, wallpaper);
            if (cpu == null) { Probe.Write($"coating [{label}]: CPU produced nothing"); continue; }
            using var shared = LiquidGlassSourceCache.Get(frame, wallpaper);
            if (shared == null) { Probe.Write($"coating [{label}]: no backdrop"); continue; }
            using var gpu = RenderGpu(grContext, shared.Backdrop, null,
                LiquidGlassGpuEffect.BuildParams(frame, shared.Scale, card, card), card);
            if (gpu == null) { Probe.Write($"coating [{label}]: GPU produced nothing"); continue; }

            long total = 0;
            for (var y = 0; y < card; y += 2)
            for (var x = 0; x < card; x += 2)
            {
                var a = cpu.GetPixel(x, y);
                var b = gpu.GetPixel(x, y);
                total += Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
            }
            var n = (card / 2) * (card / 2);
            Probe.Write($"coating [{label}]  GPU={gpu.GetPixel(card / 2, card / 2)}  " +
                        $"CPU={cpu.GetPixel(card / 2, card / 2)}  mean |delta| {total / (double)n:F1}/765");
        }
    }

    private static void CompareAt(double clarity, string output, GRContext grContext)
    {
        const int card = 200;
        using var wallpaperBitmap = MakeWallpaper(800, 600);
        using var wallpaper = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), wallpaperBitmap, live: true);
        var theme = new Theme(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass,
            LiquidGlass: new LiquidGlassSettings(BackdropClarity: clarity));
        var frame = new LiquidGlassRenderer.Frame(card, card, 1f, 20f, 100f, 80f, 800f, 600f,
            0f, 0f, 800f, 600f, theme, true, SettingsSurface: false, PixelScale: 1f, Columns: 1, Rows: 1);

        using var cpu = LiquidGlassRenderer.RenderBitmap(frame, wallpaper);
        if (cpu == null)
        {
            Probe.Write($"compare [{clarity:F0}%]: the CPU renderer produced nothing");
            return;
        }

        using var shared = LiquidGlassSourceCache.Get(frame, wallpaper);
        if (shared == null)
        {
            Probe.Write($"compare [{clarity:F0}%]: the shared backdrop could not be built");
            return;
        }

        var auraField = LiquidGlassRenderer.AuraField.BuildFromSampler((x, y) =>
        {
            var sx = Math.Clamp((int)((x + frame.DesktopX) * shared.Scale), 0, shared.Backdrop.Width - 1);
            var sy = Math.Clamp((int)((y + frame.DesktopY) * shared.Scale), 0, shared.Backdrop.Height - 1);
            return shared.Backdrop.GetPixel(sx, sy);
        }, card, card);
        using var auraBitmap = auraField.ToBitmap();

        var parameters = LiquidGlassGpuEffect.BuildParams(frame, shared.Scale, card, card) with
        {
            AuraEnabled = true,
            AuraColumns = auraField.Columns,
            AuraRows = auraField.Rows,
            AuraStep = auraField.Step,
            AuraMargin = auraField.Margin
        };
        Probe.Write($"compare: lensWidth={parameters.LensWidth:F1} lensShift={parameters.LensShift:F1} " +
                    $"auraWidth={parameters.AuraWidth:F1} innerSpread={parameters.InnerSpread:F1} " +
                    $"auraSize={auraField.Columns}x{auraField.Rows} step={auraField.Step:F1} margin={auraField.Margin:F1}");

        using var gpu = RenderGpu(grContext, shared.Backdrop, auraBitmap, parameters, card);
        if (gpu == null)
        {
            Probe.Write("compare: the GPU material produced nothing");
            return;
        }

        long total = 0;
        var worst = 0;
        var over16 = 0;
        for (var y = 0; y < card; y++)
        for (var x = 0; x < card; x++)
        {
            var a = cpu.GetPixel(x, y);
            var b = gpu.GetPixel(x, y);
            var d = Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue) + Math.Abs(a.Alpha - b.Alpha);
            total += d;
            worst = Math.Max(worst, d);
            if (d > 16) over16++;
        }

        Probe.Write($"compare: GPU vs CPU mean |delta| = {total / (double)(card * card):F2}/1020, " +
                    $"worst = {worst}, pixels differing by >16 = {over16 * 100.0 / (card * card):F1}%");
        Probe.Write($"compare: centre  CPU={cpu.GetPixel(card / 2, card / 2)}  GPU={gpu.GetPixel(card / 2, card / 2)}");
        Probe.Write($"compare: rim(20,100) CPU={cpu.GetPixel(20, card / 2)}  GPU={gpu.GetPixel(20, card / 2)}");
        Probe.Write($"compare: outside(2,2) CPU={cpu.GetPixel(2, 2)}  GPU={gpu.GetPixel(2, 2)}");

        Save(cpu, Path.Combine(output, "cpu.png"));
        Save(gpu, Path.Combine(output, "gpu.png"));
        Save(shared.Backdrop, Path.Combine(output, "backdrop.png"));
        Save(auraBitmap, Path.Combine(output, "aura.png"));
        Probe.Write($"compare: wrote {output}");
    }

    private static SKBitmap? RenderGpu(GRContext grContext, SKBitmap backdrop, SKBitmap aura,
        LiquidGlassGpuEffect.Params parameters, int size)
    {
        using var shader = LiquidGlassGpuEffect.Create(backdrop, aura, parameters);
        if (shader == null) return null;

        var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(grContext, false, info);
        if (surface == null) return null;
        surface.Canvas.Clear(SKColors.Transparent);
        using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
            surface.Canvas.DrawRect(new SKRect(0, 0, size, size), paint);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        var result = new SKBitmap(info);
        if (!snapshot.ReadPixels(info, result.GetPixels(), result.RowBytes, 0, 0))
        {
            result.Dispose();
            return null;
        }
        result.SetImmutable();
        return result;
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
    }

    private static SKBitmap MakeWallpaper(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(24, 46, 32));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(210, 230, 200);
        surface.Canvas.DrawCircle(width * 0.62f, height * 0.4f, 120, paint);
        paint.Color = new SKColor(60, 140, 90);
        surface.Canvas.DrawCircle(width * 0.3f, height * 0.65f, 90, paint);
        paint.Color = new SKColor(240, 240, 245);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height * 0.12f), paint);
        // A 40 px grid. The material's own blur and lens would hide a positional error in a smooth
        // wallpaper — which is exactly how "the backdrop's resolution moved where the card samples
        // from" survived: the original comparison used a soft wash and reported a small delta even
        // while the sampling origin was half a desktop off.
        using (var grid = new SKPaint { Color = new SKColor(250, 250, 250), StrokeWidth = 6, IsAntialias = false })
        {
            for (var x = 0; x < width; x += 40) surface.Canvas.DrawLine(x, 0, x, height, grid);
            for (var y = 0; y < height; y += 40) surface.Canvas.DrawLine(0, y, width, y, grid);
        }
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image).Copy();
    }
}
