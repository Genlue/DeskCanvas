using System.Diagnostics;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// Optical quality checks for the iOS-style liquid-glass material. The material
/// is a lens, not a blur panel, and these checks pin down the properties that
/// make it read that way:
///
/// 1. the lens profile (calm edge → shoulder → magnification, smooth, no seam),
/// 2. the rendered pixels against the production displacement formula,
/// 3. the crisp rim line (bright, narrow, lighting-independent),
/// 4. vibrancy + adaptive frost (saturated, dark backdrops lifted),
/// 5. near-invisible prismatic split, concentrated at the rim,
/// 6. the corner normal field stays continuous, and the render stays fast.
/// </summary>
public static class OpticsProfile
{
    private const int CardWidth = 400;
    private const int CardHeight = 300;
    private const int Radius = 28;
    private const int EdgeWidth = 24;
    private const int WallWidth = 800;
    private const int WallHeight = 400;

    /// <summary>
    /// The crisp, macOS-faithful optics recipe — the historic factory defaults. The shipped
    /// defaults are now the heavy-blur end of the range, so probes that assert centre legibility
    /// or pin the iOS-reference uniforms use this recipe explicitly instead of following
    /// <c>new LiquidGlassSettings()</c>.
    /// </summary>
    internal static LiquidGlassSettings CrispReference { get; } = new(12, 28, 24, 65, 18, 225, 50);

    private static Theme Material(LiquidGlassSettings glass, double opacity = 0) =>
        new(null, null, opacity, false, false, "Inter", SurfaceStyle.LiquidGlass, LiquidGlass: glass);

    private static LiquidGlassRenderer.Frame Frame(Theme material) =>
        new(CardWidth, CardHeight, 1, Radius, 0, 0, WallWidth, WallHeight,
            0, 0, WallWidth, WallHeight, material, false);

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        var timer = Stopwatch.StartNew();
        CheckLensProfile();
        CheckCornerContinuity();
        var ramp = RampWallpaper();
        CheckRenderedSampling(ramp, output);
        CheckRefraction(ramp);
        CheckRimLine();
        CheckVibrancyAndFrost();
        CheckClarity();
        CheckDispersion(ramp);
        CheckRealWallpaper(output);
        CheckPerformance(ramp);
        DrawPlot(output, ramp);
        Console.WriteLine($"Optical profile checks passed in {timer.ElapsedMilliseconds} ms.");
    }

    /// <summary>The lens profile: dead calm at the very edge, a shoulder just
    /// inside it, magnification through the ring, a gentle warp in the middle.</summary>
    private static void CheckLensProfile()
    {
        const float lensWidth = EdgeWidth;
        const float lensShift = 9f;
        const float step = 0.25f;

        var profile = new List<(float Depth, float Shift)>();
        for (var depth = 0f; depth <= CardHeight / 2f; depth += step)
            profile.Add((depth, LiquidGlassRenderer.Displacement(depth, lensWidth, lensShift)));

        var atEdge = profile[0].Shift;
        var peak = profile.Max(p => p.Shift);
        var peakAt = profile.First(p => p.Shift >= peak - 1e-3f).Depth;
        var centre = profile[^1].Shift;
        Console.WriteLine($"Lens: shift(edge) {atEdge:F3} px, peak {peak:F2} px at depth {peakAt:F1}, centre {centre:F2} px");

        Check(atEdge < 0.01f, "lens: the very edge is calm (guard tapers displacement to zero)");
        Check(peak > lensShift * 0.8f && peak < lensShift * 2f, "lens: rim displacement is lens-sized (0.8–2×)");
        Check(peakAt > 0.1f * lensWidth && peakAt < 0.6f * lensWidth, "lens: peak sits in the outer meniscus");
        Check(centre < 0.001f, "lens: the center is pristine with zero displacement (no X-crease)");

        // Magnification: the sampled position must move faster than the pixel, so
        // the backdrop visibly stretches around the rim.
        var maxStretch = 0f;
        var maxSecond = 0f;
        for (var i = 1; i < profile.Count; i++)
        {
            var slope = (profile[i].Shift - profile[i - 1].Shift) / step;
            maxStretch = MathF.Max(maxStretch, 1f + slope);
            if (i < 2) continue;
            maxSecond = MathF.Max(maxSecond, MathF.Abs(profile[i].Shift - 2 * profile[i - 1].Shift + profile[i - 2].Shift));
        }
        Console.WriteLine($"Lens: max stretch ×{maxStretch:F2}, max |second difference| {maxSecond:F4} px");
        Check(maxStretch > 1.2f, "lens: the ring magnifies the backdrop (stretch > 1.2×)");
        Check(maxSecond < 0.05f, "lens: the displacement profile is smooth (no seam)");
    }

    /// <summary>The distance-field normal must stay continuous through the corner
    /// arc centres and the medial axis, or the lens shows a notch per corner.</summary>
    private static void CheckCornerContinuity()
    {
        var field = new LiquidGlassRenderer.BevelField(CardWidth, CardHeight, Radius);
        var maxTurn = 0f;
        var maxDepthJump = 0f;
        LiquidGlassRenderer.Bevel? previous = null;
        for (var a = 0f; a <= 1.5f; a += 0.01f)
        {
            var bevel = field.Evaluate(Radius - 1 + a * EdgeWidth, Radius - 1 + a * EdgeWidth);
            if (previous is { } p)
            {
                var dot = Math.Clamp(bevel.Nx * p.Nx + bevel.Ny * p.Ny, -1f, 1f);
                maxTurn = MathF.Max(maxTurn, MathF.Acos(dot) * 180 / MathF.PI);
                maxDepthJump = MathF.Max(maxDepthJump, MathF.Abs(bevel.Depth - p.Depth));
            }
            previous = bevel;
        }
        Console.WriteLine($"Corner: max normal turn {maxTurn:F2}° per 0.4 px, max depth step {maxDepthJump:F2} px");
        Check(maxTurn < 3f, "corner: normal direction is continuous across the corner transition");

        var centre = field.Evaluate(CardWidth / 2f, CardHeight / 2f);
        var border = field.Evaluate(0.5f, CardHeight / 2f);
        Console.WriteLine($"Depth: border {border.Depth:F2} px, centre {centre.Depth:F2} px");
        Check(border.Depth is > 0f and < 1.5f, "field: the border pixel is one px from the edge");
        Check(centre.Depth > CardHeight / 2f - 1f, "field: depth grows to half the short side at the centre");
    }

    /// <summary>Rendered pixels must sample the backdrop exactly where the
    /// production formula says, after the adaptive frost is accounted for.</summary>
    private static void CheckRenderedSampling(byte[] ramp, string output)
    {
        var glass = new LiquidGlassSettings(Blur: 0, Refraction: 100, EdgeWidth: EdgeWidth, Highlight: 0, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        using var rendered = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(ramp, SKColors.Black)));
        using var source = SKBitmap.Decode(ramp);
        var field = new LiquidGlassRenderer.BevelField(CardWidth, CardHeight, Radius);
        var lensShift = 32f;

        var worst = 0f;
        double total = 0;
        var count = 0;
        var rows = new List<string> { "x,depth,shift,rendered,predicted" };
        for (var x = 1; x < 2.2f * EdgeWidth; x++)
        {
            var bevel = field.Evaluate(x + 0.5f, CardHeight / 2f);
            var shift = LiquidGlassRenderer.Displacement(bevel.Depth, EdgeWidth, lensShift);
            var pixel = rendered.GetPixel(x, CardHeight / 2);
            var sampled = SampleRow(source, x + shift, CardHeight / 2);
            var predicted = sampled + (255f - sampled) * LiquidGlassRenderer.FrostMix(sampled);
            rows.Add($"{x},{bevel.Depth:F2},{shift:F2},{pixel.Red},{predicted:F1}");
            if (pixel.Alpha < 255) continue;
            var error = MathF.Abs(pixel.Red - predicted);
            worst = MathF.Max(worst, error);
            total += error;
            count++;
        }
        File.WriteAllLines(Path.Combine(output, "optics-displacement.csv"), rows);
        var mean = (float)(total / Math.Max(1, count));
        Console.WriteLine($"Render: mean |rendered − predicted| {mean:F3} levels, worst {worst:F3} (n={count})");
        Check(worst < 1.5f && mean < 0.6f, "render: sampled backdrop matches the lens formula");
    }

    /// <summary>Refraction must visibly bend the rim and leave the centre alone.</summary>
    private static void CheckRefraction(byte[] ramp)
    {
        var glass = new LiquidGlassSettings(Blur: 0, Refraction: 100, EdgeWidth: EdgeWidth, Highlight: 0, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        using var lens = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(ramp, SKColors.Black)));
        using var flat = Decode(LiquidGlassRenderer.Render(Frame(Material(glass with { Refraction = 0 })), new WallpaperSnapshot(ramp, SKColors.Black)));

        double rim = 0, centre = 0;
        var rimCount = 0;
        var centreCount = 0;
        for (var y = Radius; y < CardHeight - Radius; y += 2)
        for (var x = 0; x < EdgeWidth; x++)
        {
            var a = lens.GetPixel(x, y);
            var b = flat.GetPixel(x, y);
            if (a.Alpha < 255 || b.Alpha < 255) continue;
            rim += Math.Abs(a.Red - b.Red);
            rimCount++;
            var ca = lens.GetPixel(CardWidth / 2, y);
            var cb = flat.GetPixel(CardWidth / 2, y);
            centre += Math.Abs(ca.Red - cb.Red);
            centreCount++;
        }
        var rimDelta = (float)(rim / Math.Max(1, rimCount));
        var centreDelta = (float)(centre / Math.Max(1, centreCount));
        Console.WriteLine($"Refraction: mean |100% − 0%| rim {rimDelta:F2}, centre {centreDelta:F2} levels");
        Check(rimDelta > 8f, "refraction: the rim bends visibly");
        Check(centreDelta < rimDelta * 0.3f, "refraction: the centre stays clear");
    }

    /// <summary>The rim line is the iOS signature: a bright, narrow, even edge.</summary>
    private static void CheckRimLine()
    {
        var grey = SolidWallpaper(new SKColor(96, 96, 96));
        var baseGlass = new LiquidGlassSettings(Blur: 0, Refraction: 0, EdgeWidth: EdgeWidth, Highlight: 0, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        using var none = Decode(LiquidGlassRenderer.Render(Frame(Material(baseGlass)), new WallpaperSnapshot(grey, SKColors.Black)));

        var peaks = new List<float>();
        var totals = new List<float>();
        foreach (var strength in new double[] { 25, 50, 75, 100 })
        {
            var glass = baseGlass with { Highlight = strength };
            using var lit = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(grey, SKColors.Black)));
            var profile = new float[40];
            for (var x = 0; x < profile.Length; x++)
                profile[x] = Luminance(lit.GetPixel(x, CardHeight / 2)) - Luminance(none.GetPixel(x, CardHeight / 2));

            var peak = profile.Max();
            var peakAt = Array.IndexOf(profile, peak);
            peaks.Add(peak);
            totals.Add(profile.Sum());
            Console.WriteLine($"Highlight {strength,3}%: peak {peak:F1} at x={peakAt}, glow integral {totals[^1]:F0}");
            if (strength >= 75)
                Console.WriteLine("  profile: " + string.Join(" ", Enumerable.Range(0, 10).Select(i => $"{i}:{profile[i]:F0}")));

            if (strength < 100) continue;
            var half = profile.Count(v => v > peak * 0.5f);
            var clipped = 0;
            var total = 0;
            for (var y = Radius; y < CardHeight - Radius; y += 2)
            for (var x = 0; x < CardWidth; x += 2)
            {
                var pixel = lit.GetPixel(x, y);
                if (pixel.Alpha < 255) continue;
                total++;
                if (pixel.Red >= 250) clipped++;
            }
            var clippedShare = (float)clipped / Math.Max(1, total);
            Console.WriteLine($"Rim line: FWHM {half} px, clipped share {clippedShare:P2}");
            Check(peak > 80f, "rim line: the edge is bright");
            Check(peakAt <= 1, "rim line: the brightest band is the outermost pixel row");
            Check(half <= 5, "rim line: the bright band is narrow (≤5 px)");
            Check(clippedShare < 0.04f, "rim line: only the line clips, not the surface");
            var corner = WindowDelta(lit, none, Radius, Radius, 10);
            var opposite = WindowDelta(lit, none, CardWidth - Radius - 1, CardHeight - Radius - 1, 10);
            Console.WriteLine($"Rim corners: light-facing {corner:F1}, opposite {opposite:F1}");
            Check(corner > opposite * 1.3f, "rim line: the light-facing corner is brighter");
            var centre = Luminance(lit.GetPixel(CardWidth / 2, CardHeight / 2)) - Luminance(none.GetPixel(CardWidth / 2, CardHeight / 2));
            Check(centre < 20f, $"rim line: the flat centre only carries the soft sheen ({centre:F1} levels)");
        }
        for (var i = 1; i < totals.Count; i++)
            Check(totals[i] > totals[i - 1] + 20f, $"rim line: slider response is monotone at step {i}");
    }

    /// <summary>Vibrancy keeps the transmitted colour saturated; the adaptive
    /// frost lifts dark backdrops so content stays legible.</summary>
    private static void CheckVibrancyAndFrost()
    {
        var glass = new LiquidGlassSettings(Blur: 0, Refraction: 0, EdgeWidth: EdgeWidth, Highlight: 0, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        var saturated = new SKColor(200, 60, 60);
        using var colour = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(SolidWallpaper(saturated), SKColors.Black)));
        var centre = colour.GetPixel(CardWidth / 2, CardHeight / 2);
        var chromaIn = Chroma(saturated);
        var chromaOut = Chroma(centre);
        Console.WriteLine($"Vibrancy: chroma {chromaIn} → {chromaOut} ({chromaOut / chromaIn:P0}), pixel {centre.Red},{centre.Green},{centre.Blue}");
        Check(chromaOut > chromaIn * 0.9f, "vibrancy: the transmitted colour stays saturated");

        var dark = new SKColor(24, 26, 30);
        using var lifted = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(SolidWallpaper(dark), SKColors.Black)));
        var pixel = lifted.GetPixel(CardWidth / 2, CardHeight / 2);
        var lumaIn = Luminance(dark);
        var lumaOut = Luminance(pixel);
        Console.WriteLine($"Frost: dark backdrop {lumaIn:F1} → {lumaOut:F1} levels");
        Check(lumaOut > lumaIn + 8f, "frost: a dark backdrop is lifted toward the neutral frost");
        Check(lumaOut < 140f, "frost: the lift stays translucent, never a milky fill");
    }

    /// <summary>iOS glass stays legible through the middle: measure how much of the
    /// backdrop's detail (local contrast) survives the material in the flat centre.</summary>
    private static void CheckClarity()
    {
        var grid = GridWallpaper(WallWidth, WallHeight, 20);
        using var source = SKBitmap.Decode(grid)!;
        var outside = Contrast(source, 120, 90, 160, 120, 4);
        foreach (var blur in new double[] { 0, 4, 8, 12, 20, 40 })
        {
            using var probe = Decode(LiquidGlassRenderer.Render(Frame(Material(new LiquidGlassSettings(blur), 0.18)),
                new WallpaperSnapshot(grid, SKColors.Black)));
            Console.WriteLine($"  clarity @ blur {blur,2}: {Contrast(probe, 120, 90, 160, 120, 4) / outside:P0} of backdrop structure");
        }
        // Centre legibility is a property of a *sane* recipe; the shipped default is the
        // heavy-blur end of the range (a milky centre is its intent), so probe the crisp one.
        using var card = Decode(LiquidGlassRenderer.Render(Frame(Material(CrispReference, 0.18)),
            new WallpaperSnapshot(grid, SKColors.Black)));
        var inside = Contrast(card, 120, 90, 160, 120, 4);
        var transmission = inside / outside;
        Console.WriteLine($"Clarity: 4-px block contrast {inside:F1} vs backdrop {outside:F1} → {transmission:P0} transmitted");
        Check(transmission > 0.60f, "clarity: the centre transmits the backdrop's structure (not a milky fill)");
        Check(transmission < 1.1f, "clarity: the centre does not amplify the backdrop");
    }

    /// <summary>Standard deviation of block-averaged luminance over a region — a
    /// structural detail proxy (block = 1 measures per-pixel, 4 measures ≥4 px).</summary>
    private static float Contrast(SKBitmap bitmap, int x0, int y0, int width, int height, int block)
    {
        var values = new List<float>();
        for (var y = y0; y < y0 + height && y + block <= bitmap.Height; y += block)
        for (var x = x0; x < x0 + width && x + block <= bitmap.Width; x += block)
        {
            double sum = 0;
            for (var by = 0; by < block; by++)
            for (var bx = 0; bx < block; bx++)
                sum += Luminance(bitmap.GetPixel(x + bx, y + by));
            values.Add((float)(sum / (block * block)));
        }
        if (values.Count == 0) return 0;
        var mean = values.Average();
        return (float)Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / values.Count);
    }

    /// <summary>iOS glass shows almost no prismatic rainbow: the split must be
    /// tiny and live only on the rim.</summary>
    private static void CheckDispersion(byte[] ramp)
    {
        var baseGlass = new LiquidGlassSettings(Blur: 0, Refraction: 100, EdgeWidth: EdgeWidth, Highlight: 0, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        using var none = Decode(LiquidGlassRenderer.Render(Frame(Material(baseGlass)), new WallpaperSnapshot(ramp, SKColors.Black)));
        using var full = Decode(LiquidGlassRenderer.Render(Frame(Material(baseGlass with { Dispersion = 100 })), new WallpaperSnapshot(ramp, SKColors.Black)));

        var rimNone = RimFringe(none, true);
        var rimFull = RimFringe(full, true);
        var centreFull = RimFringe(full, false);
        Console.WriteLine($"Dispersion: |R−B| rim {rimNone:F2} → {rimFull:F2} (100%), centre {centreFull:F2}");
        Check(rimNone < 0.8f, "dispersion: 0% leaves the image achromatic");
        Check(rimFull > 1.5f, "dispersion: 100% produces a visible fringe");
        Check(rimFull < 24f, "dispersion: the fringe stays subtle, as on iOS");
        Check(rimFull > centreFull * 3f, "dispersion: the fringe is concentrated on the rim");
    }

    /// <summary>Over the real desktop wallpaper: the glass must not ADD colour
    /// fringing (a light blur can only reduce |R−B|), and the rim must visibly
    /// move content versus the untouched wallpaper.</summary>
    private static void CheckRealWallpaper(string output)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (!File.Exists(path))
        {
            Console.WriteLine("Real wallpaper checks skipped: no TranscodedWallpaper file.");
            return;
        }
        var bytes = File.ReadAllBytes(path);
        using var wallpaper = SKBitmap.Decode(bytes);
        if (wallpaper == null)
        {
            Console.WriteLine("Real wallpaper checks skipped: undecodable wallpaper.");
            return;
        }

        const int cardWidth = 300;
        const int cardHeight = 220;
        var x0 = Math.Max(0, wallpaper.Width / 2 - cardWidth / 2);
        var y0 = Math.Max(0, wallpaper.Height / 2 - cardHeight / 2);
        var variants = new (string Name, LiquidGlassSettings Glass)[]
        {
            // "the rim lens visibly moves content" is a lens-recipe property: the shipped
            // default sits at the heavy-blur end (its washed-out rim is by design), so pin
            // recipes instead of following <c>new LiquidGlassSettings()</c>.
            ("crisp   12/28/24/65/18", CrispReference),
            ("strong  12/100/32/90/60", new LiquidGlassSettings(12, 100, 32, 90, 60, 225, 70))
        };

        foreach (var (name, glass) in variants)
        {
            var frame = new LiquidGlassRenderer.Frame(cardWidth, cardHeight, 1, 28, x0, y0,
                wallpaper.Width, wallpaper.Height, 0, 0, wallpaper.Width, wallpaper.Height, Material(glass, 0.18), true);
            var wallpaperSnapshot = new WallpaperSnapshot(bytes, SKColors.Black);
            using var card = Decode(LiquidGlassRenderer.Render(frame, wallpaperSnapshot));
            // Compare against the same material with the lens switched off: the
            // blur/tint/frost are identical, so any difference IS the lensing.
            using var flat = Decode(LiquidGlassRenderer.Render(frame with
            {
                Theme = Material(glass with { Refraction = 0 }, 0.18)
            }, wallpaperSnapshot));

            double cardFringe = 0, rawFringe = 0, rimMove = 0, centreMove = 0;
            var count = 0;
            var rimCount = 0;
            var centreCount = 0;
            for (var y = 20; y < cardHeight - 20; y++)
            for (var x = 2; x < cardWidth - 2; x++)
            {
                var a = card.GetPixel(x, y);
                var b = wallpaper.GetPixel(x0 + x, y0 + y);
                if (a.Alpha < 255) continue;
                cardFringe += Math.Abs(a.Red - a.Blue);
                rawFringe += Math.Abs(b.Red - b.Blue);
                count++;
                var f = flat.GetPixel(x, y);
                var move = Math.Abs(a.Red - f.Red) + Math.Abs(a.Green - f.Green) + Math.Abs(a.Blue - f.Blue);
                if (x < 24 || x > cardWidth - 24) { rimMove += move; rimCount++; }
                else if (x > 100 && x < cardWidth - 100) { centreMove += move; centreCount++; }
            }
            var cardMean = cardFringe / Math.Max(1, count);
            var rawMean = rawFringe / Math.Max(1, count);
            var rimDelta = rimMove / Math.Max(1, rimCount);
            var centreDelta = centreMove / Math.Max(1, centreCount);
            Console.WriteLine($"{name}: mean |R−B| card {cardMean:F2} vs raw {rawMean:F2}; " +
                $"lens effect rim {rimDelta:F1} vs centre {centreDelta:F1} levels");
            Check(cardMean <= rawMean * 1.25f + 2, "real wallpaper: the glass does not add colour fringing beyond vibrancy");
            Check(rimDelta > 5f, "real wallpaper: the rim lens visibly moves content");
        }
    }

    /// <summary>The material is rendered on a worker thread and cached, but a large
    /// widget must still not take seconds — the surface re-renders on every move.</summary>
    private static void CheckPerformance(byte[] ramp)
    {
        var glass = new LiquidGlassSettings(12, 28, 24, 65, 18, 225, 50);
        var frame = new LiquidGlassRenderer.Frame(1200, 800, 1, 32, 0, 0, 2560, 1440, 0, 0, 2560, 1440, Material(glass, 0.18), false);
        var wallpaper = new WallpaperSnapshot(ramp, SKColors.Black);
        LiquidGlassRenderer.Render(frame with { Width = 64, Height = 64 }, wallpaper);   // warm up
        var timer = Stopwatch.StartNew();
        var bytes = LiquidGlassRenderer.Render(frame, wallpaper);
        timer.Stop();
        Console.WriteLine($"Performance: 1200×800 (960k px) in {timer.ElapsedMilliseconds} ms, {bytes.Length / 1024} KB png");
        Check(timer.ElapsedMilliseconds < 1500, $"performance: large widget renders in {timer.ElapsedMilliseconds} ms");
    }

    /// <summary>Plot the lens profile and the rim-line cross-section for eyeballing.</summary>
    private static void DrawPlot(string output, byte[] ramp)
    {
        const int width = 900;
        const int height = 420;
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(24, 26, 30));
        using var axis = new SKPaint { Color = new SKColor(90, 96, 108), StrokeWidth = 1, IsAntialias = true };
        using var text = new SKPaint { Color = new SKColor(200, 206, 216), IsAntialias = true, TextSize = 14, Typeface = SKTypeface.FromFamilyName("Segoe UI") };
        canvas.DrawLine(60, 340, 60, 40, axis);
        canvas.DrawLine(60, 340, width - 20, 340, axis);
        canvas.DrawText("displacement (px)", 60, 28, text);
        canvas.DrawText("distance from border (px) →", width - 260, 366, text);

        const float lensWidth = EdgeWidth;
        const float lensShift = 9f;
        var maxShift = 0f;
        for (var depth = 0f; depth <= CardHeight / 2f; depth += 0.25f)
            maxShift = MathF.Max(maxShift, LiquidGlassRenderer.Displacement(depth, lensWidth, lensShift));

        using var curve = new SKPaint { Color = new SKColor(96, 190, 255), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke };
        using var path = new SKPath();
        for (var depth = 0f; depth <= CardHeight / 2f; depth += 0.25f)
        {
            var value = LiquidGlassRenderer.Displacement(depth, lensWidth, lensShift);
            var px = 60 + depth / (CardHeight / 2f) * (width - 100);
            var py = 340 - value / MathF.Max(maxShift, 0.01f) * 280;
            if (depth == 0) path.MoveTo(px, py); else path.LineTo(px, py);
        }
        canvas.DrawPath(path, curve);
        canvas.DrawText($"lens {lensShift:F0} px at the ring, peak {maxShift:F1} px, calm edge", 74, 60, text);

        var glass = new LiquidGlassSettings(Blur: 0, Refraction: 0, EdgeWidth: EdgeWidth, Highlight: 100, Dispersion: 0, LightAngle: 225, EdgeTint: 0);
        var grey = SolidWallpaper(new SKColor(96, 96, 96));
        using var none = Decode(LiquidGlassRenderer.Render(Frame(Material(glass with { Highlight = 0 })), new WallpaperSnapshot(grey, SKColors.Black)));
        using var lit = Decode(LiquidGlassRenderer.Render(Frame(Material(glass)), new WallpaperSnapshot(grey, SKColors.Black)));
        using var rimPath = new SKPath();
        var peak = 1f;
        for (var x = 0; x < 60; x++) peak = MathF.Max(peak, Luminance(lit.GetPixel(x, CardHeight / 2)) - Luminance(none.GetPixel(x, CardHeight / 2)));
        for (var x = 0; x < 60; x++)
        {
            var value = Luminance(lit.GetPixel(x, CardHeight / 2)) - Luminance(none.GetPixel(x, CardHeight / 2));
            var px = 60 + x / 60f * (width - 100);
            var py = 340 - value / peak * 280;
            if (x == 0) rimPath.MoveTo(px, py); else rimPath.LineTo(px, py);
        }
        canvas.DrawPath(rimPath, new SKPaint { Color = new SKColor(255, 190, 90), StrokeWidth = 2, IsAntialias = true, Style = SKPaintStyle.Stroke });
        canvas.DrawText("highlight at 100% (levels)", 74, 84, text);
        canvas.DrawText("blue = lens displacement · orange = rim-line cross-section", 74, 108, text);

        using var strip = Decode(LiquidGlassRenderer.Render(Frame(Material(new LiquidGlassSettings(6, 40, 24, 70, 12, 225, 0))),
            new WallpaperSnapshot(GridWallpaper(WallWidth, WallHeight, 20), SKColors.Black)));
        canvas.DrawBitmap(strip, SKRect.Create(0, 0, 80, 47), SKRect.Create(60, 130, 320, 188));
        canvas.DrawText("rim strip ×4 (grid wallpaper)", 60, 132, text);
        using var data = surface.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "optics-profile.png"), data.ToArray());
        DrawRealPreview(output);
    }

    /// <summary>Preview the material at 1:1 over the real desktop wallpaper, plus a
    /// fine grid that makes the rim lens unmistakable. No downscaling anywhere:
    /// a 2:1 resample of a busy wallpaper invents colour moiré that the material
    /// never produced.</summary>
    private static void DrawRealPreview(string output)
    {
        const int sheetWidth = 1280;
        const int sheetHeight = 720;
        const int cardWidth = 300;
        const int cardHeight = 220;
        using var sheet = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight));
        using var blit = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        using var label = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true, TextSize = 18,
            Typeface = SKTypeface.FromFamilyName("Segoe UI")
        };

        var wallpaperPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        byte[]? wallpaperBytes = File.Exists(wallpaperPath) ? File.ReadAllBytes(wallpaperPath) : null;
        using var wallpaper = wallpaperBytes == null ? null : SKBitmap.Decode(wallpaperBytes);

        if (wallpaper != null)
        {
            // 1:1 crop of the real wallpaper.
            var ox = Math.Max(0, (wallpaper.Width - sheetWidth) / 2);
            var oy = Math.Max(0, (wallpaper.Height - sheetHeight) / 2);
            sheet.Canvas.DrawBitmap(wallpaper, SKRect.Create(ox, oy, sheetWidth, sheetHeight),
                SKRect.Create(0, 0, sheetWidth, sheetHeight), blit);

            var variants = new (string Name, LiquidGlassSettings Glass)[]
            {
                ("iOS reference 12/28/24/65/18", CrispReference),
                ("refraction 100 · 12/100/32/65/18", new LiquidGlassSettings(12, 100, 32, 65, 18, 225, 50))
            };
            for (var i = 0; i < variants.Length; i++)
            {
                var x = 40 + i * 640;
                var y = 30;
                var frame = new LiquidGlassRenderer.Frame(cardWidth, cardHeight, 1, 28, ox + x, oy + y,
                    wallpaper.Width, wallpaper.Height, 0, 0, wallpaper.Width, wallpaper.Height,
                    Material(variants[i].Glass, 0.18), true);
                using var card = Decode(LiquidGlassRenderer.Render(frame, new WallpaperSnapshot(wallpaperBytes, SKColors.Black)));
                sheet.Canvas.DrawBitmap(card, x, y);
                sheet.Canvas.DrawText(variants[i].Name, x + 8, y + cardHeight + 26, label);
            }
        }
        else
        {
            Console.WriteLine("Real wallpaper preview skipped: no TranscodedWallpaper file.");
        }

        // Fine grid: a straight line crossing the rim visibly kinks and stretches.
        const int gridTop = 380;
        const int gridHeight = sheetHeight - gridTop;
        var gridBytes = GridWallpaper(sheetWidth, gridHeight, 20);
        using var grid = SKBitmap.Decode(gridBytes)!;
        sheet.Canvas.DrawBitmap(grid, 0, gridTop);
        var gridVariants = new (string Name, LiquidGlassSettings Glass)[]
        {
            ("grid · iOS reference", CrispReference),
            ("grid · refraction 100", new LiquidGlassSettings(12, 100, 24, 65, 18, 225, 50))
        };
        for (var i = 0; i < gridVariants.Length; i++)
        {
            var x = 40 + i * 640;
            var y = 30;
            var frame = new LiquidGlassRenderer.Frame(cardWidth, cardHeight, 1, 28, x, y,
                sheetWidth, gridHeight, 0, 0, sheetWidth, gridHeight, Material(gridVariants[i].Glass, 0.18), false);
            using var card = Decode(LiquidGlassRenderer.Render(frame, new WallpaperSnapshot(gridBytes, SKColors.Black)));
            sheet.Canvas.DrawBitmap(card, x, gridTop + y);
            sheet.Canvas.DrawText(gridVariants[i].Name, x + 8, gridTop + y + cardHeight + 26, label);
        }

        using var data = sheet.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "liquid-glass-real.png"), data.ToArray());
        Console.WriteLine($"Preview sheet: {Path.Combine(output, "liquid-glass-real.png")}");
    }

    /// <summary>A fine grid + diagonals: straight lines make lens displacement obvious.</summary>
    private static byte[] GridWallpaper(int width, int height, int step)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(206, 206, 206));
        using var line = new SKPaint { Color = new SKColor(70, 70, 70), StrokeWidth = 2, IsAntialias = true };
        for (var x = 0; x < width; x += step) canvas.DrawLine(x, 0, x, height, line);
        for (var y = 0; y < height; y += step) canvas.DrawLine(0, y, width, y, line);
        using var diagonal = new SKPaint { Color = new SKColor(40, 110, 220), StrokeWidth = 3, IsAntialias = true };
        for (var x = -height; x < width; x += 80) canvas.DrawLine(x, 0, x + height, height, diagonal);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>The liquid-glass parameters the app currently has saved (best effort).</summary>
    private static LiquidGlassSettings? SavedGlass()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeskCanvas", "appSettings.json");
            if (!File.Exists(path)) return null;
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty("Theme", out var theme)) return null;
            if (!theme.TryGetProperty("LiquidGlass", out var glass) || glass.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            double Read(string name, double fallback) => glass.TryGetProperty(name, out var value) && value.TryGetDouble(out var d) ? d : fallback;
            return new LiquidGlassSettings(Read("Blur", 12), Read("Refraction", 28), Read("EdgeWidth", 24), Read("Highlight", 65),
                Read("Dispersion", 18), Read("LightAngle", 225), Read("EdgeTint", 50),
                Read("WallpaperOffsetX", 0), Read("WallpaperOffsetY", 0));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static byte[] RampWallpaper()
    {
        // A steep sinusoid plus a slight linear ramp: the local slope turns
        // luminance differences into sub-pixel position estimates, and the ramp
        // breaks the mirror symmetry that a pure sinusoid would have.
        using var surface = SKSurface.Create(new SKImageInfo(WallWidth, WallHeight));
        var canvas = surface.Canvas;
        using var paint = new SKPaint { IsAntialias = false };
        for (var x = 0; x < WallWidth; x++)
        {
            var value = (byte)Math.Clamp(Math.Round(128 + 60 * Math.Sin(2 * Math.PI * x / 40.0) + 0.06 * x), 0, 255);
            paint.Color = new SKColor(value, value, value);
            canvas.DrawRect(x, 0, 1, WallHeight, paint);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static byte[] SolidWallpaper(SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(WallWidth, WallHeight));
        surface.Canvas.Clear(color);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static float SampleRow(SKBitmap source, float x, int y)
    {
        x = Math.Clamp(x, 0, source.Width - 1);
        var x0 = (int)x;
        var x1 = Math.Min(x0 + 1, source.Width - 1);
        var f = x - x0;
        var a = Luminance(source.GetPixel(x0, y));
        var b = Luminance(source.GetPixel(x1, y));
        return a + (b - a) * f;
    }

    private static float RimFringe(SKBitmap bitmap, bool rim)
    {
        double total = 0;
        var count = 0;
        for (var y = Radius; y < CardHeight - Radius; y += 2)
        for (var x = 0; x < EdgeWidth * 2; x++)
        {
            var depth = Math.Min(x, EdgeWidth * 2 - 1 - x);
            if (rim != depth < EdgeWidth / 2) continue;
            var pixel = bitmap.GetPixel(x, y);
            if (pixel.Alpha < 255) continue;
            total += Math.Abs(pixel.Red - pixel.Blue);
            count++;
        }
        return (float)(total / Math.Max(1, count));
    }

    private static float WindowDelta(SKBitmap a, SKBitmap b, int cx, int cy, int half)
    {
        var best = 0f;
        for (var y = cy - half; y <= cy + half; y++)
        for (var x = cx - half; x <= cx + half; x++)
        {
            if (x < 0 || y < 0 || x >= a.Width || y >= a.Height) continue;
            var pa = a.GetPixel(x, y);
            var pb = b.GetPixel(x, y);
            if (pa.Alpha < 255 || pb.Alpha < 255) continue;
            best = MathF.Max(best, Luminance(pa) - Luminance(pb));
        }
        return best;
    }

    private static float Chroma(SKColor color) =>
        Math.Max(color.Red, Math.Max(color.Green, color.Blue)) - Math.Min(color.Red, Math.Min(color.Green, color.Blue));

    private static float Luminance(SKColor color) => (0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue) * color.Alpha / 255f;

    private static SKBitmap Decode(byte[] bytes) => SKBitmap.Decode(bytes)!;

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException($"FAIL: {label}");
        Console.WriteLine($"PASS: {label}");
    }
}
