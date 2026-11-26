using System;
using SkiaSharp;

namespace DeskCanvas.Services.LiquidGlass;

/// <summary>
/// The surface's own colour, <b>fused into the material</b> rather than painted behind it.
/// <para>
/// A glass card is not "a coloured rectangle with glass on top". The base colour is the material's
/// coating: the refracted backdrop is mixed toward <see cref="Color"/> by <see cref="Strength"/>,
/// inside the same pass that does the refraction, dispersion and highlight. That is what lets a
/// host draw <b>one</b> layer — an animated card has no second rectangle to keep in sync with the
/// glass, and no seam can appear between them at any scale or radius.
/// </para>
/// <para>
/// <see cref="Strength"/> 0 is pure glass (the backdrop shows through untouched); 1 is an opaque
/// card in that colour. The theme's 不透明度 maps straight onto it.
/// </para>
/// </summary>
public readonly record struct LiquidGlassCoating(SKColor Color, float Strength)
{
    /// <summary>No coating: pure refraction of the backdrop.</summary>
    public static LiquidGlassCoating None => new(SKColors.Transparent, 0f);

    /// <summary>The coating with its strength clamped to 0…1 (a NaN strength becomes 0).</summary>
    public LiquidGlassCoating Normalized() => this with
    {
        Strength = double.IsFinite(Strength) ? Math.Clamp(Strength, 0f, 1f) : 0f
    };
}

/// <summary>
/// Everything optical a liquid-glass surface needs, in host-neutral terms: how hard the edge bends
/// the backdrop, how much colour separates at the rim, how vivid the result is, what colour the
/// material itself carries, and how the hairline highlight is lit.
/// <para>
/// The defaults are a readable card: a mid-strength lens, no dispersion, neutral vibrancy and a
/// soft top highlight. A host that only has a backdrop and a shape gets something that looks like
/// glass without configuring anything.
/// </para>
/// <para>
/// The sampling <i>resolution</i> is not here on purpose — it belongs to
/// <see cref="LiquidGlassBackdrop.Scale"/>, because it describes the pixels, not the optics.
/// </para>
/// </summary>
public readonly record struct LiquidGlassParams
{
    /// <summary>Depth of the refracting band at the edge, in destination pixels (0 = no lens).</summary>
    public float RefractionBand { get; init; }

    /// <summary>Peak displacement at the very edge, in destination pixels.</summary>
    public float RefractionAmount { get; init; }

    /// <summary>How much the displacement also follows the radial direction (0 = edge-only lens).</summary>
    public float DepthEffect { get; init; }

    /// <summary>Chromatic split at the rim, 0…1.</summary>
    public float Dispersion { get; init; }

    /// <summary>Saturation multiplier applied to the refracted colour (1 = unchanged).</summary>
    public float Vibrancy { get; init; }

    /// <summary>The surface's own colour, fused into the material.</summary>
    public LiquidGlassCoating Coating { get; init; }

    /// <summary>Half-width of the outline hairline, in destination pixels.</summary>
    public float StrokeHalf { get; init; }

    /// <summary>Anti-alias feather of the outline hairline, in destination pixels.</summary>
    public float StrokeFeather { get; init; }

    /// <summary>Highlight strength, 0…1 (scaled by the light term).</summary>
    public float StrokeAlpha { get; init; }

    /// <summary>Highlight falloff exponent: 1 is a wide glow, higher tightens it onto the normal.</summary>
    public float HighlightFalloff { get; init; }

    /// <summary>Unit vector the highlight is measured against (the light direction).</summary>
    public SKPoint LightDirection { get; init; }

    /// <summary>Inward corner indentation of the highlight, in destination pixels.</summary>
    public float CornerGap { get; init; }

    /// <summary>Backlit-side highlight ratio relative to the lit side, 0…1.</summary>
    public float HighlightBottom { get; init; }

    /// <summary>Inner volume sheen strength inside the specular rim, 0…1.</summary>
    public float Sheen { get; init; }

    /// <summary>Inner volume sheen depth, in destination pixels.</summary>
    public float SheenDepth { get; init; }

    /// <summary>Outer thin dark rim width on unlit edges, in destination pixels.</summary>
    public float DarkWidth { get; init; }

    /// <summary>Outer thin dark rim opacity on unlit edges, 0…1.</summary>
    public float DarkAlpha { get; init; }

    /// <summary>Light accent colour used for the specular rim and sheen (default white).</summary>
    public SKColor? HighlightColor { get; init; }

    /// <summary>Dark accent colour used for the unlit outer rim (default black).</summary>
    public SKColor? DarkColor { get; init; }

    /// <summary>Superellipse corner continuity exponent K (1.0 = circular, 1.15 = continuous squircle).</summary>
    public float CornerContinuity { get; init; }

    internal SKColor EffectiveHighlightColor => HighlightColor ?? SKColors.White;

    internal SKColor EffectiveDarkColor => DarkColor ?? SKColors.Black;

    internal float EffectiveCornerContinuity =>
        float.IsFinite(CornerContinuity) && CornerContinuity >= 1f ? Math.Clamp(CornerContinuity, 1f, 2f) : 1.15f;

    /// <summary>
    /// A card that reads as glass out of the box: 24 px lens, no dispersion, neutral vibrancy, a
    /// 20% coating, and a top-lit hairline with subtle volume sheen and unlit-side rim darkening.
    /// </summary>
    public static LiquidGlassParams Default => new()
    {
        RefractionBand = 24f,
        RefractionAmount = 10f,
        DepthEffect = 0.6f,
        Dispersion = 0f,
        Vibrancy = 1.15f,
        Coating = new LiquidGlassCoating(new SKColor(46, 46, 46), 0.2f),
        StrokeHalf = 0.6f,
        StrokeFeather = 0.24f,
        StrokeAlpha = 0.6f,
        HighlightFalloff = 1f,
        LightDirection = new SKPoint(0f, 1f),
        CornerGap = 0.5f,
        HighlightBottom = 0.88f,
        Sheen = 0.12f,
        SheenDepth = 4.5f,
        DarkWidth = 0.6f,
        DarkAlpha = 0.22f,
        HighlightColor = SKColors.White,
        DarkColor = SKColors.Black,
        CornerContinuity = 1.15f
    };

    /// <summary>The same optics with a different coating — the one knob a theme switch moves.</summary>
    public LiquidGlassParams WithCoating(SKColor color, float strength) =>
        this with { Coating = new LiquidGlassCoating(color, strength).Normalized() };

    /// <summary>Clamp the fields whose range is defined, so a bad settings file cannot break the pass.</summary>
    public LiquidGlassParams Normalized() => this with
    {
        RefractionBand = Math.Max(0f, RefractionBand),
        RefractionAmount = Math.Max(0f, RefractionAmount),
        DepthEffect = Math.Clamp(DepthEffect, 0f, 1f),
        Dispersion = Math.Clamp(double.IsFinite(Dispersion) ? Dispersion : 0f, 0f, 1f),
        Vibrancy = double.IsFinite(Vibrancy) ? Math.Max(0f, Vibrancy) : 1f,
        Coating = Coating.Normalized(),
        StrokeHalf = float.IsFinite(StrokeHalf) ? Math.Max(0f, StrokeHalf) : 0f,
        StrokeFeather = float.IsFinite(StrokeFeather) ? Math.Max(0f, StrokeFeather) : 0f,
        StrokeAlpha = float.IsFinite(StrokeAlpha) ? Math.Clamp(StrokeAlpha, 0f, 1f) : 0f,
        HighlightFalloff = double.IsFinite(HighlightFalloff) ? Math.Max(0.01f, HighlightFalloff) : 1f,
        LightDirection = NormalizeLight(LightDirection),
        CornerGap = float.IsFinite(CornerGap) ? Math.Max(0f, CornerGap) : 0f,
        HighlightBottom = float.IsFinite(HighlightBottom) ? Math.Clamp(HighlightBottom, 0f, 1f) : 0.88f,
        Sheen = float.IsFinite(Sheen) ? Math.Clamp(Sheen, 0f, 1f) : 0f,
        SheenDepth = float.IsFinite(SheenDepth) ? Math.Max(0f, SheenDepth) : 0f,
        DarkWidth = float.IsFinite(DarkWidth) ? Math.Max(0f, DarkWidth) : 0f,
        DarkAlpha = float.IsFinite(DarkAlpha) ? Math.Clamp(DarkAlpha, 0f, 1f) : 0f,
        CornerContinuity = EffectiveCornerContinuity
    };

    private static SKPoint NormalizeLight(SKPoint light)
    {
        var length = MathF.Sqrt(light.X * light.X + light.Y * light.Y);
        return length > 1e-5f ? new SKPoint(light.X / length, light.Y / length) : new SKPoint(0f, 1f);
    }
}
