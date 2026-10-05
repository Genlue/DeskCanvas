using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DeskCanvas.Core.Models.Settings;

namespace Clock.Services;

/// <summary>
/// The glyph outline factory of the frameless clock: the geometry for one time string and the
/// font resolution it is built on.
/// <para>
/// One entry point matters: the geometry-clip, the native window region and the specular rim all
/// have to work off exactly the same outline, so the stretch lives here instead of being
/// re-derived per consumer.
/// </para>
/// </summary>
internal static class FramelessGlyphGeometry
{
    /// <summary>Where the synthetic stroke starts to grow: the widget's default 字体粗细. Everything
    /// at or below it is rendered by the family's real faces, exactly as before.</summary>
    private const int SyntheticWeightReference = 700;

    /// <summary>Stem growth at the top of the slider, in em — ≈ one Regular→Bold step, so 900 reads
    /// as "the heaviest this font can go" rather than as an accident.</summary>
    private const double SyntheticWeightMaxEm = 0.06;

    /// <summary>
    /// The glyph geometry for one time string: the typeface for the current family and weight,
    /// stretched to fill the target box.
    /// </summary>
    public static Geometry? BuildStretch(string text, double targetW, double targetH,
        string? fontFamily, int fontWeight, bool stretchFill, Theme? theme)
    {
        var (typeface, syntheticPenWidth) = ResolveWeightedTypeface(fontFamily, fontWeight, theme);

        var formattedText = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            100.0,
            Brushes.Black);

        var rawGeometry = formattedText.BuildGeometry(new Point(0, 0));
        if (rawGeometry == null) return null;

        // Synthetic weight: a stroked outline grows every stem by the pen width (half per side).
        // The union with the filled glyph is what makes it a heavier *letter* rather than a hollow
        // outline — and it is taken as a union because Geometry.GetWidenedGeometry is documented as
        // the stroke of the outline, which combined with the fill is the thickened shape either way.
        var outline = rawGeometry;
        if (syntheticPenWidth > 0.001)
        {
            var pen = new Pen(Brushes.Black, syntheticPenWidth)
            {
                LineJoin = PenLineJoin.Round,
                LineCap = PenLineCap.Round
            };
            if (rawGeometry.GetWidenedGeometry(pen) is { } stroked)
                outline = new CombinedGeometry(GeometryCombineMode.Union, rawGeometry, stroked);
        }

        // The stretch is measured on the outline we will actually draw, so a heavier weight fills
        // the cell instead of overflowing it.
        var tight = outline.Bounds;
        if (tight.Width <= 0 || tight.Height <= 0) return null;

        Matrix matrix;
        if (stretchFill)
        {
            var sx = targetW / tight.Width;
            var sy = targetH / tight.Height;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y) * Matrix.CreateScale(sx, sy);
        }
        else
        {
            var scale = Math.Min(targetW / tight.Width, targetH / tight.Height);
            var actualW = tight.Width * scale;
            var actualH = tight.Height * scale;
            var ox = (targetW - actualW) / 2.0;
            var oy = (targetH - actualH) / 2.0;
            matrix = Matrix.CreateTranslation(-tight.X, -tight.Y)
                   * Matrix.CreateScale(scale, scale)
                   * Matrix.CreateTranslation(ox, oy);
        }

        var stretched = outline.Clone();
        stretched.Transform = new MatrixTransform(matrix);
        return stretched;
    }

    /// <summary>
    /// The typeface for the current settings, plus the width of the synthetic weight stroke to add
    /// to the outline (in the 100-unit em the geometry is built at; 0 for no stroke).
    ///
    /// <para>
    /// 字体粗细 is a <b>position</b> on the widget's own axis (100-900, the slider's travel), not a
    /// raw OpenType request, because a family only has the weights its font files provide. The
    /// previous control offered nine labelled weights, and on the curated fonts — 华为锁屏超窄体
    /// ships a single Black face, Impact a single Regular — every one of the nine resolved to the
    /// same face, so the setting was completely dead; on the default Inter, whose heaviest face is
    /// Bold, the top two steps were dead as well.
    /// </para>
    /// <para>
    /// So the position is resolved in two parts: the family's real faces answer everything up to
    /// the reference weight (the widget default, 700), and above it the strokes are grown
    /// synthetically. Nothing at or below the reference changes — the historic look is preserved
    /// bit for bit — while 700→900 is live for every font, including the single-face ones.
    /// </para>
    /// </summary>
    private static (Typeface Typeface, double SyntheticPenWidth) ResolveWeightedTypeface(string? fontFamily, int fontWeight, Theme? theme)
    {
        var familyKey = fontFamily ?? theme?.FontFamily ?? string.Empty;
        var position = Math.Clamp(fontWeight, 100, 900);

        var family = ResolveFontFamily(familyKey);
        var actual = ResolveFaceWeight(familyKey, family, (FontWeight)position);

        var growthEm = position <= SyntheticWeightReference
            ? 0.0
            : (position - SyntheticWeightReference) / (double)(900 - SyntheticWeightReference) * SyntheticWeightMaxEm;

        return (new Typeface(family, FontStyle.Normal, (FontWeight)position), growthEm * 100.0);
    }

    /// <summary>
    /// The OpenType weight of the face the font manager actually picks for <paramref name="weight"/>
    /// — the only way to find out whether a family can honour a request at all. Cached per
    /// (family, weight): it runs on the UI thread for every render (the geometry is rebuilt per
    /// frame), and the answer never changes while the app runs.
    /// </summary>
    private static int ResolveFaceWeight(string familyKey, FontFamily family, FontWeight weight)
    {
        var cacheKey = (familyKey, (int)weight);
        if (FaceWeightCache.TryGetValue(cacheKey, out var cached)) return cached;

        var resolved = (int)weight;
        if (FontManager.Current.TryGetGlyphTypeface(
                new Typeface(family, FontStyle.Normal, weight), out var glyphTypeface))
            resolved = (int)glyphTypeface.Weight;

        FaceWeightCache[cacheKey] = resolved;
        return resolved;
    }

    private static readonly Dictionary<(string Family, int Weight), int> FaceWeightCache = new();

    private static FontFamily ResolveFontFamily(string? fontName)
    {
        if (string.IsNullOrWhiteSpace(fontName))
            return new FontFamily("Segoe UI");

        if (fontName.Equals("HarmonyOS Sans Condensed", StringComparison.OrdinalIgnoreCase))
        {
            return new FontFamily("avares://Clock/Assets/Fonts#HarmonyOS Sans Condensed, HarmonyOS Sans Condensed, Segoe UI");
        }

        return new FontFamily(fontName);
    }
}
