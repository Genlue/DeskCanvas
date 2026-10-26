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

    /// <summary>
    /// A card that reads as glass out of the box: 24 px lens, no dispersion, neutral vibrancy, a
    /// 20% coating, and a top-lit hairline.
    /// </summary>
    public static LiquidGlassParams Default => new()
    {
        RefractionBand = 24f,
        RefractionAmount = 10f,
        DepthEffect = 0.6f,
        Dispersion = 0f,
        Vibrancy = 1.15f,
        Coating = new LiquidGlassCoating(new SKColor(46, 46, 46), 0.2f),
        StrokeHalf = 1f,
        StrokeFeather = 0.25f,
        StrokeAlpha = 0.6f,
        HighlightFalloff = 1f,
        LightDirection = new SKPoint(0f, 1f)
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
        StrokeHalf = Math.Max(0f, StrokeHalf),
        StrokeFeather = Math.Max(0f, StrokeFeather),
        StrokeAlpha = Math.Clamp(StrokeAlpha, 0f, 1f),
        HighlightFalloff = double.IsFinite(HighlightFalloff) ? Math.Max(0.01f, HighlightFalloff) : 1f,
        LightDirection = NormalizeLight(LightDirection)
    };

    private static SKPoint NormalizeLight(SKPoint light)
    {
        var length = MathF.Sqrt(light.X * light.X + light.Y * light.Y);
        return length > 1e-5f ? new SKPoint(light.X / length, light.Y / length) : new SKPoint(0f, 1f);
    }
}
