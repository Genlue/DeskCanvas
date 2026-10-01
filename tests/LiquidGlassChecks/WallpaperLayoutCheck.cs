using System.Diagnostics;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// Ground-truth wallpaper layout check: compare the production renderer's
/// wallpaper placement against a captured image of the actual displayed
/// desktop (Progman PrintWindow). Tries several anchoring hypotheses and
/// reports which one matches the real display.
/// </summary>
public static class WallpaperLayoutCheck
{
    public static void Run(string capturePath, string outDir, string style = "22", bool tile = false)
    {
        Directory.CreateDirectory(outDir);
        using var capture = SKBitmap.Decode(capturePath)!;
        var wallpaperPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        var wallpaperBytes = File.Exists(wallpaperPath) ? File.ReadAllBytes(wallpaperPath) : null;
        var wallpaper = new WallpaperSnapshot(wallpaperBytes, new SKColor(32, 38, 48), style, tile);
        var w = capture.Width;
        var h = capture.Height;

        Console.WriteLine($"Capture {w}x{h}; wallpaper {wallpaperBytes?.Length} bytes ({wallpaperPath}); style='{style}' tile={tile}");

        var neutral = new Theme(null, null, 0, false, false, "Inter",
            SurfaceStyle.LiquidGlass, LiquidGlass: new LiquidGlassSettings(0, 0, 32, 0, 0, 225));

        var hypotheses = new (string Name, LiquidGlassRenderer.Frame Frame)[]
        {
            // H1: wallpaper fills the full monitor, centered on the monitor (current renderer logic).
            ("fill-monitor", new LiquidGlassRenderer.Frame(w, h, 1, 0, 0, 0, w, h, 0, 0, w, h, neutral, false, false, 1)),
            // H2: wallpaper fills the working area (monitor minus the 44px top strip), centered on it.
            ("fill-workingarea", new LiquidGlassRenderer.Frame(w, h, 1, 0, 0, 44, w, 1396, 0, 44, w, 1396, neutral, false, false, 1)),
        };

        foreach (var (name, frame) in hypotheses)
        {
            var bytes = LiquidGlassRenderer.Render(frame, wallpaper);
            using var rendered = SKBitmap.Decode(bytes)!;
            var diff = Diff(capture, rendered);
            Console.WriteLine($"{name}: mean abs diff = {diff:F2}");
            File.WriteAllBytes(Path.Combine(outDir, $"layout-{name}.png"), bytes);
        }

        // Best-shift search around the monitor hypothesis: if the true display is
        // this layout but shifted by (dx, dy), report the shift that minimizes the diff.
        using (var rendered = SKBitmap.Decode(LiquidGlassRenderer.Render(hypotheses[0].Frame, wallpaper))!)
        {
            var (shiftX, shiftY, best) = SearchShift(capture, rendered, 480, 4);
            Console.WriteLine($"fill-monitor coarse alignment shift = ({shiftX}, {shiftY}) with diff {best:F2}");
            var (fineX, fineY, fine) = SearchShift(capture, rendered, 8, 1, shiftX, shiftY);
            Console.WriteLine($"fill-monitor fine alignment shift = ({fineX}, {fineY}) with diff {fine:F2}");
            // Interpret: image top = (h - drawH)/2 + shift; drawH from the image aspect.
            var (iw, ih) = WallpaperSize();
            var ratio = Math.Max((double)w / iw, (double)h / ih);
            var drawH = ih * ratio;
            var top = (h - drawH) / 2 + fineY;
            Console.WriteLine($"displayed image top = {top:F1} (monitor coords), bottom = {top + drawH:F1}, center = {top + drawH / 2:F1}; scale = {ratio:F5}");
        }    }

    private static double Diff(SKBitmap a, SKBitmap b)
    {
        double total = 0;
        long count = 0;
        for (var y = 0; y < a.Height; y += 4)
        for (var x = 0; x < a.Width; x += 4)
        {
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            count += 3;
        }
        return total / Math.Max(1, count);
    }

    /// <summary>
    /// End-to-end verification: re-render the exact production frame (from the
    /// probe JSON) with a live desktop capture as the wallpaper source, then
    /// compare against the actual widget window captured via PrintWindow.
    /// The border band (refraction zone) must match almost exactly.
    /// </summary>
    public static void VerifyWidget(string widgetPng, string probeJson, string desktopPng, bool liveCapture)
    {
        using var widget = SKBitmap.Decode(widgetPng)!;
        using var desktop = SKBitmap.Decode(desktopPng)!;
        var probe = System.Text.Json.JsonDocument.Parse(File.ReadAllText(probeJson));
        var frameRoot = probe.RootElement.GetProperty("Prepared");
        var frame = new LiquidGlassRenderer.Frame(
            frameRoot.GetProperty("Width").GetInt32(),
            frameRoot.GetProperty("Height").GetInt32(),
            frameRoot.GetProperty("Scale").GetSingle(),
            frameRoot.GetProperty("Radius").GetSingle(),
            frameRoot.GetProperty("DesktopX").GetSingle(),
            frameRoot.GetProperty("DesktopY").GetSingle(),
            frameRoot.GetProperty("DesktopWidth").GetSingle(),
            frameRoot.GetProperty("DesktopHeight").GetSingle(),
            frameRoot.GetProperty("ScreenX").GetSingle(),
            frameRoot.GetProperty("ScreenY").GetSingle(),
            frameRoot.GetProperty("ScreenWidth").GetSingle(),
            frameRoot.GetProperty("ScreenHeight").GetSingle(),
            ThemeFrom(probe.RootElement.GetProperty("Prepared").GetProperty("Theme")),
            frameRoot.GetProperty("Dark").GetBoolean(),
            frameRoot.GetProperty("SettingsSurface").GetBoolean(),
            frameRoot.GetProperty("PixelScale").GetSingle());

        using (var stream = System.IO.File.OpenRead(desktopPng))
        using (var source = SKBitmap.Decode(stream)!)
        using (var capture = SKImage.FromBitmap(source))
        using (var data = capture.Encode(SKEncodedImageFormat.Png, 100))
        {
            var wallpaper = new WallpaperSnapshot(data.ToArray(), SKColors.Black, LiveCapture: liveCapture);
            var bytes = LiquidGlassRenderer.Render(frame, wallpaper);
            using var expected = SKBitmap.Decode(bytes)!;

            var margin = (int)Math.Round(((widget.Width - frame.Width) / 2f));
            Console.WriteLine($"widget {widget.Width}x{widget.Height}, frame {frame.Width}x{frame.Height}, margin={margin}");

            // Border band diff (both images should show nearly identical glass).
            var band = 6;
            double total = 0; long count = 0;
            for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
            {
                var border = x < band || y < band || x >= expected.Width - band || y >= expected.Height - band;
                if (!border) continue;
                var p = widget.GetPixel(x + margin, y + margin);
                var q = expected.GetPixel(x, y);
                total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                count += 3;
            }
            var diff = total / Math.Max(1, count);
            Console.WriteLine($"border band mean abs diff = {diff:F2} (0 = widget glass == re-render)");
            Console.WriteLine($"{(liveCapture ? "CAPTURE" : "FILE")} MODE: {(diff < 40 ? "MATCH" : "MISMATCH")}");
        }
    }

    private static Theme ThemeFrom(System.Text.Json.JsonElement theme) => new(
        GetNullableBool(theme, "DarkMode"),
        theme.TryGetProperty("AccentColor", out var accent) ? accent.GetString() : null,
        theme.GetProperty("OpacityLevel").GetDouble(),
        theme.GetProperty("Monochrome").GetBoolean(),
        theme.GetProperty("UseNativeFrame").GetBoolean(),
        theme.GetProperty("FontFamily").GetString() ?? "Inter",
        GetNullable<SurfaceStyle>(theme, "Surface"),
        theme.TryGetProperty("OutlineColor", out var oc) ? oc.GetString() : null,
        theme.TryGetProperty("OutlineWidth", out var ow) ? ow.GetDouble() : 0,
        theme.TryGetProperty("SolidBackgroundDark", out var sbd) ? sbd.GetString() : null,
        theme.TryGetProperty("SolidBackgroundLight", out var sbl) ? sbl.GetString() : null,
        GetNullable<MonochromeStyle>(theme, "MonochromeVariant"),
        theme.TryGetProperty("AutoTheme", out var at) && at.GetBoolean());

    private static bool? GetNullableBool(System.Text.Json.JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind == System.Text.Json.JsonValueKind.True) return true;
        if (value.ValueKind == System.Text.Json.JsonValueKind.False) return false;
        return null;
    }

    private static T? GetNullable<T>(System.Text.Json.JsonElement element, string name)
        where T : struct, Enum =>
        element.TryGetProperty(name, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number
            ? (T)Enum.ToObject(typeof(T), value.GetInt32())
            : null;

    private static (int Dx, int Dy, double Diff) SearchShift(SKBitmap a, SKBitmap b, int range, int step, int centerX = 0, int centerY = 0)    {
        var best = double.MaxValue;
        var bestDx = 0;
        var bestDy = 0;
        for (var dy = centerY - range; dy <= centerY + range; dy += step)
        for (var dx = centerX - range; dx <= centerX + range; dx += step)
        {
            double total = 0;
            long count = 0;
            for (var y = 20; y + 20 < a.Height; y += 16)
            for (var x = 20; x + 20 < a.Width; x += 16)
            {
                var sx = x + dx;
                var sy = y + dy;
                if (sx < 0 || sy < 0 || sx >= b.Width || sy >= b.Height) continue;
                var p = a.GetPixel(x, y);
                var q = b.GetPixel(sx, sy);
                total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
                count += 3;
            }
            if (count == 0) continue;
            var d = total / count;
            if (d < best) { best = d; bestDx = dx; bestDy = dy; }
        }
        return (bestDx, bestDy, best);
    }

    private static (int W, int H) WallpaperSize()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap == null) throw new InvalidOperationException("cannot decode wallpaper");
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>
    /// Classify the real wallpaper layout: try every plausible placement rule
    /// (cover/fit/stretch × top/center/bottom anchor) and report the diff of each.
    /// The rule with the lowest diff is what the display actually uses.
    /// </summary>
    public static void LayoutScan(string capturePath, string imagePath, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var capture = SKBitmap.Decode(capturePath)!;
        using var image = SKBitmap.Decode(imagePath)!;
        var w = capture.Width;
        var h = capture.Height;
        var modes = new[] { "cover", "fit", "stretch", "center" };
        var anchors = new[] { "top", "center", "bottom" };
        var results = new List<(string Mode, string Anchor, double Diff, byte[] Png)>();

        foreach (var mode in modes)
        foreach (var anchor in anchors)
        {
            using var surface = SKSurface.Create(new SKImageInfo(w, h));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            double scaleX, scaleY, drawW, drawH, x, y;
            var iw = image.Width;
            var ih = image.Height;
            switch (mode)
            {
                case "cover": scaleX = scaleY = Math.Max((double)w / iw, (double)h / ih); break;
                case "fit": scaleX = scaleY = Math.Min((double)w / iw, (double)h / ih); break;
                case "stretch": scaleX = (double)w / iw; scaleY = (double)h / ih; break;
                default: scaleX = scaleY = 1; break; // center: native size
            }
            drawW = iw * scaleX;
            drawH = ih * scaleY;
            x = (w - drawW) / 2;                                  // always horizontally centered
            y = anchor switch { "top" => 0, "center" => (h - drawH) / 2, _ => h - drawH };
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            canvas.DrawBitmap(image, new SKRect((float)x, (float)y, (float)(x + drawW), (float)(y + drawH)), paint);
            using var snapshot = surface.Snapshot();
            using var rendered = SKBitmap.FromImage(snapshot);
            var diff = Diff(capture, rendered);
            using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            results.Add((mode, anchor, diff, data.ToArray()));
            Console.WriteLine($"{mode,-8} × {anchor,-6}: {diff:F2}");
        }

        var best = results.OrderBy(r => r.Diff).First();
        Console.WriteLine($"BEST: {best.Mode} × {best.Anchor} (diff {best.Diff:F2})");
        File.WriteAllBytes(Path.Combine(outDir, "best-layout.png"), best.Png);
        foreach (var r in results.OrderBy(r => r.Diff).Take(3))
            File.WriteAllBytes(Path.Combine(outDir, $"candidate-{r.Mode}-{r.Anchor}.png"), r.Png);

        // Refine: localized shift search around the best candidate (the true rule
        // may be cover + a translated anchor that no top/center/bottom candidate covers).
        using (var bestBitmap = SKBitmap.Decode(Path.Combine(outDir, "best-layout.png"))!)
        {
            var (fineX, fineY, fine) = SearchShift(capture, bestBitmap, 200, 4);
            Console.WriteLine($"REFINED best {best.Mode}×{best.Anchor}: shift ({fineX},{fineY}), diff {fine:F2}");
        }
    }

}
