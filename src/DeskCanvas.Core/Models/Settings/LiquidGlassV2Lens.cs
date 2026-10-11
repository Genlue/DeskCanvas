using System;

namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The 新液态玻璃 (V2) refraction lens, solved for one card. All lengths are render pixels —
/// the caller must convert the frame (bounds, DPI, quality) once, up front.
/// </summary>
/// <param name="Amount">Peak displacement at the outline, render px (applied inward).</param>
/// <param name="Band">Width of the refracted rim band, render px.</param>
/// <param name="GradRadius">Inflated corner radius used by the direction field, render px.</param>
/// <param name="DepthEffect">Blend weight of the radial direction into the rim gradient.</param>
public readonly record struct LiquidGlassRefractionLens(
    float Amount, float Band, float GradRadius, float DepthEffect);

/// <summary>
/// The single source of truth for the 新液态玻璃 (V2) refraction coefficients, shared by the GPU
/// shader (<c>LiquidGlassV2Effect</c>) and the CPU fallback (<c>LiquidGlassV2Renderer</c>) so the
/// two paths cannot drift apart — the previous duplicate blocks could, and did.
/// <para>
/// The library's own relation is <c>band = amount / 2</c>, both measured against the card's short
/// side. That scales the lens with the card but leaves the corner geometry fixed, so on a large
/// card the band eventually swallows the corner: see
/// <see cref="LiquidGlassV2Settings.CornerBandCapFactor"/> for what breaks and why the band is
/// capped against the corner radius instead. The cap closes the band <i>and</i> the displacement
/// together — shrinking the band while keeping the displacement would only steepen the same
/// derivatives inside a narrower rim.
/// </para>
/// </summary>
public static class LiquidGlassV2Lens
{
    /// <summary>
    /// Solve the refraction lens for one card. Every output is finite and non-negative, whatever
    /// <paramref name="refraction"/>, <paramref name="minSide"/> and <paramref name="radius"/> are
    /// given — degenerate geometry (a square card, a card thinner than a pixel, a circle) is
    /// handled by construction rather than by callers remembering to check.
    /// </summary>
    /// <param name="refraction">The 折射 slider, 0-100.</param>
    /// <param name="minSide">The card's short side, render px.</param>
    /// <param name="radius">The card's corner radius after DPI conversion and clamping, render px.</param>
    public static LiquidGlassRefractionLens Compute(double refraction, float minSide, float radius)
    {
        var fraction = double.IsFinite(refraction) ? Math.Clamp(refraction, 0.0, 100.0) / 100.0 : 1.0;
        if (!float.IsFinite(minSide) || minSide <= 0f) minSide = 0f;
        if (!float.IsFinite(radius) || radius <= 0f) radius = 0f;

        // The library's rule: displacement proportional to the short side, band half of it.
        var rawBand = (float)(fraction * LiquidGlassV2Settings.RefractionAmountMaxFrac
                                      * LiquidGlassV2Settings.BandToAmountRatio) * minSide;

        // ...capped by the corner, so the corners never fold and the direction field's single
        // non-smooth point (the inflated arc centre, 1.5 × radius deep) always carries zero
        // displacement. A radius of 0 closes the lens outright — a deliberate, visible change
        // for square-cornered cards, not an accident of the formula.
        var band = SmoothCap(rawBand, (float)LiquidGlassV2Settings.CornerBandCapFactor * radius);

        var amount = band / (float)LiquidGlassV2Settings.BandToAmountRatio;
        var gradRadius = MathF.Min(radius * (float)LiquidGlassV2Settings.GradRadiusFactor, minSide * 0.5f);
        return new LiquidGlassRefractionLens(amount, band, gradRadius, 1f);
    }

    /// <summary>
    /// A C1 smooth saturation: identity below <c>cap × (1 - 0.1)</c>, flat at <paramref name="cap"/>
    /// above <c>cap × (1 + 0.1)</c>, and a quadratic that meets both segments with matching slopes
    /// in between. A hard <c>min</c> would work numerically but steps the first derivative, which
    /// shows up as the rim visibly snapping when a card is dragged across the threshold.
    /// <para>
    /// Returns 0 for a non-positive <paramref name="cap"/> (no corner radius, no refraction) and
    /// for non-finite or negative input, so no caller can produce a NaN displacement from it.
    /// </para>
    /// </summary>
    public static float SmoothCap(float value, float cap)
    {
        if (!(cap > 0f) || !(value > 0f)) return 0f;

        var w = cap * (float)LiquidGlassV2Settings.CornerBandCapTransitionFrac;
        if (!(w > 0f)) return MathF.Min(value, cap);
        if (value <= cap - w) return value;
        if (value >= cap + w) return cap;

        var remaining = cap + w - value;
        return cap - remaining * remaining / (4f * w);
    }
}
