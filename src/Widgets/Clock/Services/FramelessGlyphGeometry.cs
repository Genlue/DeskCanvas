using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace Clock.Services;

/// <summary>
/// The glyph outline factory of the frameless clock: builds simplified, non-overlapping vector
/// outlines for time strings across both bundled iOS Lock Screen artistic fonts and system fonts.
/// <para>
/// Uses Skia PathOps (<see cref="SKPath.Simplify"/> with <see cref="SKPathFillType.Winding"/>) to
/// dissolve internal overlapping stroke contours (common in variable and CJK system fonts such as
/// Bahnschrift, Segoe UI Variable, and Microsoft YaHei) so that semi-transparent glass fills and
/// specular rim strokes never render chaotic internal lines at sharp corners.
/// </para>
/// </summary>
public static class FramelessGlyphGeometry
{
    /// <summary>OpenType 'wght' variation axis tag (0x77676874).</summary>
    private const uint WghtTag = 0x77676874;

    /// <summary>
    /// Map of built-in artistic font family keys to their embedded <c>.ttf</c> filenames under
    /// <c>avares://Clock/Assets/Fonts/</c>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> BundledFontFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HarmonyOS Sans Condensed"] = "HarmonyOS_Sans_Condensed_Black.ttf",
            ["Bebas Neue"] = "BebasNeue-Regular.ttf",
            ["DM Serif Display"] = "DMSerifDisplay-Regular.ttf",
            ["Bungee Inline"] = "BungeeInline-Regular.ttf",
            ["Black Ops One"] = "BlackOpsOne-Regular.ttf",
            ["Titan One"] = "TitanOne-Regular.ttf",
            ["Righteous"] = "Righteous-Regular.ttf",
            ["Orbitron"] = "Orbitron-Variable.ttf"
        };

    private static readonly ConcurrentDictionary<string, SKTypeface?> BaseBundledTypefaces = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string Family, int Weight), SKTypeface> ResolvedTypefaces = new();
    private static readonly ConcurrentDictionary<(string Family, int Weight), TabularMetrics> TabularMetricsCache = new();

    private readonly record struct TabularMetrics(float MaxDigitWidth, float ColonWidth, float MinTop, float MaxBottom);

    /// <summary>
    /// Check whether <paramref name="fontFamily"/> is one of the built-in curated artistic fonts.
    /// </summary>
    public static bool IsBundledArtisticFont(string? fontFamily) =>
        !string.IsNullOrWhiteSpace(fontFamily) && BundledFontFiles.ContainsKey(fontFamily);

    /// <summary>
    /// Build the glyph geometry for one time string, stretched or fitted to <c>[0, 0, targetW, targetH]</c>.
    /// </summary>
    public static Geometry? BuildStretch(
        string text,
        double targetW,
        double targetH,
        string? fontFamily,
        int fontWeight,
        bool stretchFill,
        Theme? theme,
        bool fixedWidthDigits = false)
    {
        if (string.IsNullOrEmpty(text) || targetW <= 0 || targetH <= 0)
            return null;

        var clampedWeight = Math.Clamp(fontWeight, 100, 900);
        var familyKey = string.IsNullOrWhiteSpace(fontFamily)
            ? (theme?.FontFamily ?? "Segoe UI")
            : fontFamily;

        var typeface = ResolveSkTypeface(familyKey, clampedWeight);
        using var font = new SKFont(typeface, 100f)
        {
            Edging = SKFontEdging.SubpixelAntialias,
            Subpixel = true
        };
        using var paint = new SKPaint(font)
        {
            IsAntialias = true,
            SubpixelText = true,
            TextSize = 100f,
            Typeface = typeface
        };

        using var path = new SKPath { FillType = SKPathFillType.Winding };
        SKRect referenceBounds;

        if (!fixedWidthDigits)
        {
            using var rawPath = paint.GetTextPath(text, 0f, 0f);
            if (rawPath == null || rawPath.IsEmpty)
                return null;

            rawPath.FillType = SKPathFillType.Winding;
            using var simplified = SimplifyGlyphPath(rawPath);
            path.AddPath(simplified);
            path.FillType = simplified.FillType;
            referenceBounds = path.TightBounds;
        }
        else
        {
            var metrics = GetTabularMetrics(familyKey, clampedWeight, paint);
            if (metrics.MaxDigitWidth <= 0 || metrics.MaxBottom <= metrics.MinTop)
                return null;

            var gap = metrics.MaxDigitWidth * 0.14f;
            var colonSlotW = Math.Max(metrics.ColonWidth, metrics.MaxDigitWidth * 0.28f);
            float cursorX = 0f;

            using var combined = new SKPath { FillType = SKPathFillType.Winding };
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                bool isColon = ch == ':';
                float slotW = isColon ? colonSlotW : metrics.MaxDigitWidth;

                using var chRaw = paint.GetTextPath(ch.ToString(), 0f, 0f);
                if (chRaw != null && !chRaw.IsEmpty)
                {
                    chRaw.FillType = SKPathFillType.Winding;
                    using var chSimple = SimplifyGlyphPath(chRaw);
                    var cb = chSimple.TightBounds;
                    if (cb.Width > 0 && cb.Height > 0)
                    {
                        float dx = cursorX + (slotW - cb.Width) * 0.5f - cb.Left;
                        float dy = isColon
                            ? (metrics.MinTop + ((metrics.MaxBottom - metrics.MinTop) - cb.Height) * 0.5f - cb.Top)
                            : 0f;
                        combined.AddPath(chSimple, dx, dy);
                    }
                }

                cursorX += slotW;
                if (i < text.Length - 1)
                    cursorX += gap;
            }

            using var finalSimplified = SimplifyGlyphPath(combined);
            path.AddPath(finalSimplified);
            path.FillType = finalSimplified.FillType;
            referenceBounds = new SKRect(0f, metrics.MinTop, cursorX, metrics.MaxBottom);
        }

        if (referenceBounds.Width <= 0 || referenceBounds.Height <= 0 || path.IsEmpty)
            return null;

        float tw = (float)targetW;
        float th = (float)targetH;
        SKMatrix matrix;

        if (stretchFill)
        {
            float sx = tw / referenceBounds.Width;
            float sy = th / referenceBounds.Height;
            matrix = SKMatrix.CreateTranslation(-referenceBounds.Left, -referenceBounds.Top)
                .PostConcat(SKMatrix.CreateScale(sx, sy));
        }
        else
        {
            float scale = Math.Min(tw / referenceBounds.Width, th / referenceBounds.Height);
            float actualW = referenceBounds.Width * scale;
            float actualH = referenceBounds.Height * scale;
            float ox = (tw - actualW) * 0.5f;
            float oy = (th - actualH) * 0.5f;
            matrix = SKMatrix.CreateTranslation(-referenceBounds.Left, -referenceBounds.Top)
                .PostConcat(SKMatrix.CreateScale(scale, scale))
                .PostConcat(SKMatrix.CreateTranslation(ox, oy));
        }

        path.Transform(matrix);
        return ToStreamGeometry(path);
    }

    /// <summary>
    /// Simplify overlapping contours in a glyph path so internal stroke intersections are dissolved
    /// while inner counters (holes inside 0, 4, 6, 8, 9, or inline stripes) are preserved.
    /// </summary>
    private static SKPath SimplifyGlyphPath(SKPath rawPath)
    {
        rawPath.FillType = SKPathFillType.Winding;
        var simplified = rawPath.Simplify();
        if (simplified != null && !simplified.IsEmpty)
            return simplified;

        simplified?.Dispose();
        return new SKPath(rawPath);
    }

    private static TabularMetrics GetTabularMetrics(string familyKey, int weight, SKPaint paint)
    {
        var cacheKey = (familyKey, weight);
        if (TabularMetricsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        float maxDigitW = 0f;
        float minTop = float.MaxValue;
        float maxBottom = float.MinValue;

        for (char c = '0'; c <= '9'; c++)
        {
            using var p = paint.GetTextPath(c.ToString(), 0f, 0f);
            if (p == null || p.IsEmpty) continue;
            var b = p.TightBounds;
            if (b.Width > maxDigitW) maxDigitW = b.Width;
            if (b.Top < minTop) minTop = b.Top;
            if (b.Bottom > maxBottom) maxBottom = b.Bottom;
        }

        float colonW = maxDigitW * 0.25f;
        using (var cp = paint.GetTextPath(":", 0f, 0f))
        {
            if (cp != null && !cp.IsEmpty)
            {
                var cb = cp.TightBounds;
                if (cb.Width > 0) colonW = cb.Width;
            }
        }

        if (minTop >= maxBottom)
        {
            minTop = -80f;
            maxBottom = 0f;
        }

        var metrics = new TabularMetrics(maxDigitW, colonW, minTop, maxBottom);
        TabularMetricsCache[cacheKey] = metrics;
        return metrics;
    }

    private static StreamGeometry ToStreamGeometry(SKPath skPath)
    {
        var sg = new StreamGeometry();
        using var ctx = sg.Open();
        ctx.SetFillRule(skPath.FillType == SKPathFillType.EvenOdd ? FillRule.EvenOdd : FillRule.NonZero);

        using var iter = skPath.CreateRawIterator();
        var pts = new SKPoint[4];
        bool figureOpen = false;

        SKPathVerb verb;
        while ((verb = iter.Next(pts)) != SKPathVerb.Done)
        {
            switch (verb)
            {
                case SKPathVerb.Move:
                    if (figureOpen)
                        ctx.EndFigure(false);
                    ctx.BeginFigure(new Point(pts[0].X, pts[0].Y), isFilled: true);
                    figureOpen = true;
                    break;

                case SKPathVerb.Line:
                    ctx.LineTo(new Point(pts[1].X, pts[1].Y));
                    break;

                case SKPathVerb.Quad:
                    ctx.QuadraticBezierTo(
                        new Point(pts[1].X, pts[1].Y),
                        new Point(pts[2].X, pts[2].Y));
                    break;

                case SKPathVerb.Conic:
                    var quads = SKPath.ConvertConicToQuads(pts[0], pts[1], pts[2], iter.ConicWeight(), 2);
                    for (int q = 0; q < quads.Length - 1; q += 2)
                    {
                        ctx.QuadraticBezierTo(
                            new Point(quads[q + 1].X, quads[q + 1].Y),
                            new Point(quads[q + 2].X, quads[q + 2].Y));
                    }
                    break;

                case SKPathVerb.Cubic:
                    ctx.CubicBezierTo(
                        new Point(pts[1].X, pts[1].Y),
                        new Point(pts[2].X, pts[2].Y),
                        new Point(pts[3].X, pts[3].Y));
                    break;

                case SKPathVerb.Close:
                    if (figureOpen)
                    {
                        ctx.EndFigure(true);
                        figureOpen = false;
                    }
                    break;
            }
        }

        if (figureOpen)
            ctx.EndFigure(false);

        return sg;
    }

    private static SKTypeface ResolveSkTypeface(string familyKey, int weight)
    {
        var cacheKey = (familyKey, weight);
        if (ResolvedTypefaces.TryGetValue(cacheKey, out var cached))
            return cached;

        SKTypeface? baseFace = null;
        if (BundledFontFiles.TryGetValue(familyKey, out var fileName))
        {
            baseFace = LoadBundledTypeface(fileName);
        }

        var style = new SKFontStyle(weight, (int)SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        baseFace ??= SKFontManager.Default.MatchFamily(familyKey, style)
                     ?? SKFontManager.Default.MatchFamily("Segoe UI", style)
                     ?? SKTypeface.Default;

        ResolvedTypefaces[cacheKey] = baseFace;
        return baseFace;
    }

    private static SKTypeface? LoadBundledTypeface(string fileName)
    {
        return BaseBundledTypefaces.GetOrAdd(fileName, static file =>
        {
            try
            {
                var uri = new Uri($"avares://Clock/Assets/Fonts/{file}");
                if (AssetLoader.Exists(uri))
                {
                    using var assetStream = AssetLoader.Open(uri);
                    using var ms = new MemoryStream();
                    assetStream.CopyTo(ms);
                    ms.Position = 0;
                    return SKTypeface.FromStream(ms);
                }
            }
            catch
            {
                // Fall through to disk probe when running in a bare test host without AssetLoader.
            }

            try
            {
                var candidates = new[]
                {
                    Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file),
                    Path.GetFullPath(Path.Combine("src", "Widgets", "Clock", "Assets", "Fonts", file))
                };
                foreach (var candidate in candidates)
                {
                    if (File.Exists(candidate))
                        return SKTypeface.FromFile(candidate);
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        });
    }

    /// <summary>
    /// Rasterise a geometry into an 8-bit alpha mask (the glyph mask the optical renderer runs on).
    /// </summary>
    public static byte[] ExtractMask(Geometry? geometry, double width, double height, double scaling, int pixelW, int pixelH)
    {
        var mask = new byte[pixelW * pixelH];
        if (geometry == null || pixelW <= 0 || pixelH <= 0) return mask;

        using var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96 * scaling, 96 * scaling));
        using (var ctx = rtb.CreateDrawingContext())
        {
            ctx.DrawGeometry(Brushes.Black, null, geometry);
        }

        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(pixelW * pixelH * 4);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), handle.AddrOfPinnedObject(), buffer.Length, pixelW * 4);
            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = buffer[i * 4 + 3];
            }
        }
        finally
        {
            handle.Free();
            // The 4-byte-per-pixel readback buffer is scratch: pooled so a full-size clock does
            // not churn another pixel-width allocation through the LOH on every frame. Only the
            // alpha bytes were copied out; the (garbage) contents are never read.
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
        return mask;
    }
}
