using System;
using SkiaSharp;
using uWidgets.Core.Models.Settings;

namespace uWidgets.Services;

/// <summary>
/// GPU compositor for the 新液态玻璃 material (<see cref="SurfaceStyle.LiquidGlassV2"/>): a
/// faithful port of Kyant0/AndroidLiquidGlass 2.0's optical model — its rounded-rect refraction
/// lens with depth effect and diagonal chromatic aberration, plus its hairline outline
/// highlight — evaluated by Skia's runtime shader on the render thread.
/// <para>
/// The ported model, per pixel:
/// </para>
/// <list type="number">
///   <item><description><b>Refraction lens</b> — the interior is passed through untouched; only a
///   rim band of <c>refrHeight</c> px is refracted. The displacement follows the library's
///   <c>circleMap</c> quarter-circle (<c>1 - sqrt(1 - x²)</c>, peaking exactly at the outline),
///   points inward (the background reads as magnified behind a thick glass edge), and its
///   direction is the rounded-rect SDF gradient at an inflated radius, blended with the radial
///   direction when the depth effect is on. One deliberate deviation: the gradient is
///   restructured to be continuous everywhere — the library's field hard-switches the axis
///   along the card diagonals and collapses to a 45° spike exactly on the arc-centre lines,
///   and wherever the band grew past ~1.2× the corner radius both sliced hard seams into the
///   rim at all four corners.</description></item>
///   <item><description><b>Chromatic aberration</b> — the library's seven-tap spectral split,
///   scaled by the diagonal quadrant factor <c>(x·y)/(hx·hy)</c>: fringes appear on the
///   light-facing diagonal and reverse on the other.</description></item>
///   <item><description><b>Vibrancy</b> — a saturation boost (the library's <c>vibrancy()</c> is
///   ×1.5), not a frost: the environment keeps and intensifies its colour.</description></item>
///   <item><description><b>Surface</b> — the solid background colour at <c>tint</c>, the app-side
///   scrim that stands in for the library's <c>onDrawSurface</c> container colour.</description></item>
///   <item><description><b>Highlight</b> — the library's outline stroke: a hairline (0.5 DIP,
///   feathered) of white at 50% alpha in Plus blending, lit by
///   |dot(edge normal, light)|^falloff — the top-left and bottom-right diagonals glow.</description></item>
/// </list>
/// <para>
/// <b>The same load-bearing restrictions as <see cref="LiquidGlassGpuEffect"/> apply.</b>
/// Runtime shaders only work on the Ganesh backend (callers must check the lease for a
/// <c>GrContext</c>); every <c>sample()</c> must be written lexically inside <c>main</c> (via
/// the <c>@fetch</c> expansion) except the single-tap <c>contentTexel</c>, which the inliner
/// always takes; the SkSL dialect is ES2-compatible (no struct returns, no <c>out</c>
/// parameters, no dynamic loops, no early <c>return</c> from <c>main</c>, no
/// <c>smoothstep</c>/two-arg <c>atan</c>/<c>mod</c> builtins). The library's early
/// <c>return</c> for the untouched interior is therefore rewritten as arithmetic: past the band
/// the circleMap evaluates to zero, so the refraction vanishes on its own.
/// </para>
/// </summary>
internal static class LiquidGlassV2Effect
{
    private static readonly object Gate = new();
    private static SKRuntimeEffect? cachedEffect;
    private static bool compileAttempted;

    /// <summary>
    /// True when the runtime shader compiles on this build. Always check this before selecting
    /// the GPU path.
    /// </summary>
    public static bool IsSupported
    {
        get
        {
            EnsureEffect();
            lock (Gate) return cachedEffect != null;
        }
    }

    /// <summary>The compilation error, when <see cref="IsSupported"/> is false.</summary>
    public static string? CompileError { get; private set; }

    private static void EnsureEffect()
    {
        lock (Gate)
        {
            if (compileAttempted) return;
            compileAttempted = true;
            try
            {
                cachedEffect = SKRuntimeEffect.Create(Source, out var errors);
                CompileError = cachedEffect == null ? errors : null;
            }
            catch (Exception ex)
            {
                CompileError = ex.Message;
                cachedEffect = null;
            }
        }
    }

    /// <summary>
    /// Every value the shader needs, derived from the frame and the 新液态玻璃 optics.
    /// </summary>
    internal readonly record struct Params(
        float Width, float Height, float Radius,
        float DestOriginX, float DestOriginY, float DestToRender,
        float SourceOriginX, float SourceOriginY, float SourceScale,
        float RefrHeight, float RefrAmount, float DepthEffect,
        float Chroma, float Saturation, float Tint,
        SKColor Coating,
        float StrokeHalf, float StrokeFeather, float StrokeAlpha, float Falloff,
        float LightX, float LightY);

    /// <summary>
    /// Mirror of the CPU coefficient block. <paramref name="sourceScale"/> converts render pixels
    /// to backdrop-texture pixels.
    /// </summary>
    public static Params BuildParams(LiquidGlassRenderer.Frame frame, float sourceScale,
        float destWidth = 0f, float destHeight = 0f)
    {
        var optics = frame.Theme.EffectiveLiquidGlassV2;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var minSide = Math.Min(width, height);

        // The shader's own coordinates are whatever the canvas is in at draw time, so it is told
        // how to convert to the render pixels everything geometric below is measured in.
        if (destWidth <= 0f || destHeight <= 0f)
        {
            destWidth = width;
            destHeight = height;
        }
        var destToRender = width / destWidth;

        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);

        // Lens: the library ties the band to the displacement (height = amount / 2) and measures
        // both against the card's short side, so the material scales with the widget the way the
        // phone controls do. No adaptive shrinking — the small controls in the library carry the
        // proportionally largest lenses, which is exactly their look.
        var amount = (float)(optics.Refraction / 100.0) * (float)LiquidGlassV2Settings.RefractionAmountMaxFrac * minSide;
        var refrHeight = amount * (float)LiquidGlassV2Settings.BandToAmountRatio;

        var colorHex = frame.Dark
            ? frame.Theme.EffectiveSolidBackgroundDark
            : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.20);

        var angle = (float)(LiquidGlassV2Settings.HighlightAngleDegrees * Math.PI / 180.0);

            // Outline highlight — the library's paint layer, which is where the actual width
            // comes from: `strokeWidth = ceil(0.5dp in px) × 2`, blurred by 0.25dp and clipped
            // to the outline, so the visible inner band is ceil(0.5dp in px) — at least one
            // whole pixel. The stroke colour's alpha is forced to 1 in the library's shader and
            // blended additively (Plus), so the strength is the full white scaled by the slider.
            var strokeHalf = MathF.Ceiling((float)LiquidGlassV2Settings.StrokeWidthDips * scale);
            return new Params(
                Width: width, Height: height, Radius: radius,
                DestOriginX: 0f, DestOriginY: 0f, DestToRender: destToRender,
                // Desktop-space origin of the card, in render pixels, scaled exactly once by the
                // shader (see the lens shader's remarks on why this must not be pre-scaled). The
                // 对齐 offset is already baked into the backdrop by DrawWallpaper.
                SourceOriginX: frame.DesktopX,
                SourceOriginY: frame.DesktopY,
                SourceScale: sourceScale,
                RefrHeight: refrHeight,
                RefrAmount: amount,
                DepthEffect: 1f,
                Chroma: (float)(optics.Dispersion / 100.0),
                Saturation: 1f + (float)LiquidGlassV2Settings.VibrancySaturationBoost * (float)(optics.Vibrancy / 100.0),
                Tint: tint,
                Coating: coating,
                StrokeHalf: strokeHalf,
                StrokeFeather: (float)LiquidGlassV2Settings.StrokeFeatherDips * scale,
                StrokeAlpha: (float)(optics.Highlight / LiquidGlassV2Settings.HighlightReference),
                Falloff: (float)LiquidGlassV2Settings.HighlightFalloff,
                LightX: MathF.Cos(angle), LightY: MathF.Sin(angle));
    }

    /// <summary>
    /// Build a shader for one card. <paramref name="source"/> is the shared blurred backdrop.
    /// Returns null when the shader cannot be built, in which case the caller must fall back to
    /// the CPU bitmap.
    /// </summary>
    public static SKShader? Create(SKBitmap source, in Params p)
    {
        if (source == null) return null;
        EnsureEffect();
        SKRuntimeEffect? effect;
        lock (Gate) effect = cachedEffect;
        if (effect == null) return null;

        try
        {
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { p.Width, p.Height },
                ["radius"] = p.Radius,
                ["destOrigin"] = new[] { p.DestOriginX, p.DestOriginY },
                ["destToRender"] = p.DestToRender,
                ["srcOrigin"] = new[] { p.SourceOriginX, p.SourceOriginY },
                ["srcScale"] = p.SourceScale,
                ["refrHeight"] = p.RefrHeight,
                ["refrAmount"] = p.RefrAmount,
                ["depthEffect"] = p.DepthEffect,
                ["chroma"] = p.Chroma,
                ["saturation"] = p.Saturation,
                ["tint"] = p.Tint,
                ["coating"] = new[] { (float)p.Coating.Red, (float)p.Coating.Green, (float)p.Coating.Blue },
                ["strokeHalf"] = p.StrokeHalf,
                ["strokeFeather"] = p.StrokeFeather,
                ["strokeAlpha"] = p.StrokeAlpha,
                ["falloff"] = p.Falloff,
                ["lightDir"] = new[] { p.LightX, p.LightY }
            };

            var children = new SKRuntimeEffectChildren(effect);
            // No local matrix on the backdrop: the shader performs the render-px -> backdrop-px
            // mapping itself via srcScale.
            using var backdropChild = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            children["content"] = backdropChild;

            // The child is refcounted into the returned shader, so releasing our handle here is
            // safe and keeps the render thread from leaking one shader per frame.
            return effect.ToShader(false, uniforms, children);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The optical model's SkSL source, for diagnostics.</summary>
    internal static string SourceForDiagnostics => Source;

    // -------------------------------------------------------------------------------------------
    // source assembly
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The optical model with its child-shader fetches written out. <c>@fetch</c> is expanded by
    /// <see cref="Expand"/>; see the class remarks for why the fetches cannot live in helper
    /// functions.
    /// </summary>
    private static readonly string Source = Expand(RawSource);

    /// <summary>
    /// One bilinear fetch of the backdrop, addressed by the target variable it assigns. The four
    /// taps call <c>contentTexel</c>, which holds a single <c>sample()</c> and is therefore always
    /// inlined; interpolation is by hand because a bitmap shader in this Skia samples nearest.
    /// </summary>
    private static string FetchBlock(string target, string coordinate)
    {
        return "{ float2 _p = (" + coordinate + "); float2 _b = floor(_p); float2 _f = _p - _b;\n      " +
               target + " = mix(mix(contentTexel(_b), contentTexel(_b + float2(1.0, 0.0)), _f.x),\n" +
               "                       mix(contentTexel(_b + float2(0.0, 1.0)), contentTexel(_b + float2(1.0, 1.0)), _f.x), _f.y); }";
    }

    /// <summary>
    /// Replace every <c>@fetch(target, coordinate)</c> with the bilinear block, so the emitted
    /// program contains no sampling helper beyond the single-tap texel lookup.
    /// </summary>
    internal static string Expand(string template)
    {
        var result = new System.Text.StringBuilder(template.Length * 2);
        var i = 0;
        while (true)
        {
            var at = template.IndexOf("@fetch", i, StringComparison.Ordinal);
            if (at < 0)
            {
                result.Append(template, i, template.Length - i);
                return result.ToString();
            }

            result.Append(template, i, at - i);
            var open = template.IndexOf('(', at);
            var close = MatchingParenthesis(template, open);
            var arguments = template[(open + 1)..close];
            var comma = arguments.IndexOf(',');
            result.Append(FetchBlock(
                arguments[..comma].Trim(),
                arguments[(comma + 1)..].Trim()));
            i = close + 1;
        }
    }

    private static int MatchingParenthesis(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')' && --depth == 0) return i;
        }
        throw new InvalidOperationException("unbalanced parenthesis in the glass shader template");
    }

    /// <summary>
    /// The optical model, transcribed from Kyant0/AndroidLiquidGlass 2.0's
    /// RoundedRectRefraction(WithDispersion)Shader and DefaultHighlightShader. Kept in SkSL's
    /// lowest common denominator: no struct returns, no dynamic loop bounds, no arrays, no
    /// <c>out</c> parameters, no early <c>return</c> from <c>main</c>, no <c>smoothstep</c>
    /// builtin.
    /// </summary>
    private const string RawSource = @"
uniform shader content;   // lightly blurred, downscaled desktop backdrop

uniform float2 size;      // card size, render px
uniform float radius;     // card corner radius, render px
uniform float2 destOrigin; // top-left of the drawn rect, in the shader's own coordinate space
uniform float destToRender; // multiply shader coordinates by this to get render px
uniform float2 srcOrigin; // card origin, in render px (scaled once, with everything else, by srcScale)
uniform float srcScale;   // render px -> backdrop px

uniform float refrHeight;  // refraction band width, render px
uniform float refrAmount;  // peak displacement at the outline, render px (applied inward)
uniform float depthEffect; // 1 = blend the rim gradient with the radial direction

uniform float chroma;      // chromatic aberration strength 0-1
uniform float saturation;  // vibrancy multiplier
uniform float tint;        // surface scrim mix
uniform float3 coating;    // surface scrim colour, 0-255

uniform float strokeHalf;   // half hairline stroke width, render px
uniform float strokeFeather; // stroke softness, render px
uniform float strokeAlpha;  // highlight strength
uniform float falloff;      // light falloff exponent
uniform float2 lightDir;    // light direction (unit)

const float PI = 3.14159265;

/// The library's rounded-rect SDF, verbatim.
float sdRoundedRect(float2 coord, float2 halfSize, float radius) {
  float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
  float outside = length(max(cornerCoord, 0.0)) - radius;
  float inside = min(max(cornerCoord.x, cornerCoord.y), 0.0);
  return outside + inside;
}

/// Gradient of the SDF at an inflated radius, restructured to be continuous everywhere.
/// The library's field radial-fans the corner sector, clamps negative components to an
/// epsilon elsewhere and hard-switches the axis along the card diagonals; the clamp
/// collapses both components to the diagonal exactly on the arc-centre lines, and the
/// axis switch mirrors the refraction across the diagonals — wherever the refraction
/// band reaches that deep (band wider than ~1.2x the corner radius, i.e. the factory
/// optics on any small-radius card) both sliced hard seams into the rim at all four
/// corners. Here the corner sector keeps the radial fan, the edge strips keep exact
/// edge normals (matching the radial's limits at the sector boundary), and the deep
/// interior reflects the radial across the diagonal so it continues the fan smoothly.
float2 gradSdRoundedRect(float2 coord, float2 halfSize, float radius) {
  float2 cornerCoord = abs(coord) - (halfSize - float2(radius));
  if (cornerCoord.x > 0.0 && cornerCoord.y > 0.0) {
    return sign(coord) * normalize(cornerCoord);
  }
  if (cornerCoord.x >= 0.0) {
    return sign(coord) * float2(1.0, 0.0);
  }
  if (cornerCoord.y >= 0.0) {
    return sign(coord) * float2(0.0, 1.0);
  }
  return sign(coord) * normalize(max(float2(-cornerCoord.y, -cornerCoord.x), float2(1e-5)));
}

float smoothStep(float edge0, float edge1, float value) {
  if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
  float t = clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
  return t * t * (3.0 - 2.0 * t);
}

/// One exact texel of the backdrop. Bitmap shaders in this Skia sample nearest, so every lookup
/// below is interpolated by hand; a single sample() per function keeps the inliner's hand forced.
float3 contentTexel(float2 texel) { return float3(sample(content, texel + float2(0.5)).rgb) * 255.0; }

half4 main(float2 xy) {
  // The canvas works in its own units; the optical model is measured in render pixels.
  float2 rxy = (xy - destOrigin) * destToRender;

  float2 halfSize = size * 0.5;
  float2 centeredCoord = rxy - halfSize;

  float sdRaw = sdRoundedRect(centeredCoord, halfSize, radius);
  // Rounded-rect coverage: opaque inside the radius, fully transparent outside. The device
  // dialect takes no early return from main, so the library's interior passthrough is
  // expressed by the circleMap vanishing past the band instead.
  float mask = 1.0 - smoothStep(-0.75, 0.75, sdRaw);

  // ---- Refraction lens ------------------------------------------------------------
  // sd clamped to the inside; depth runs 0 (at the outline) to refrHeight (band end).
  // circleMap(1 - depth/height) = 1 - sqrt(1 - x^2): a quarter circle that peaks at the
  // outline and is exactly zero across the whole interior — the untouched interior of
  // the library's early return, for free.
  float sd = min(sdRaw, 0.0);
  float depth01 = clamp(-sd / max(refrHeight, 1e-5), 0.0, 1.0);
  float x = 1.0 - depth01;
  float d = (1.0 - sqrt(max(0.0, 1.0 - x * x))) * refrAmount;

  // Displacement direction: the SDF gradient at an inflated radius, blended with the
  // radial direction for glass thickness (depthEffect). The sample moves inward —
  // the library passes refractionAmount negative — so the rim magnifies the backdrop.
  float gradRadius = min(radius * 1.5, min(halfSize.x, halfSize.y));
  float2 radial = centeredCoord / max(length(centeredCoord), 1e-4);
  float2 grad = normalize(gradSdRoundedRect(centeredCoord, halfSize, gradRadius) + depthEffect * radial);

  float2 refracted = rxy - d * grad;
  float2 sampleBase = (refracted + srcOrigin) * srcScale;

  float3 col;
  @fetch(col, sampleBase)

  // ---- Chromatic aberration --------------------------------------------------------
  // The library's seven-tap spectral split, scaled by the diagonal quadrant factor:
  // fringes follow the displacement on the light diagonal and reverse on the other.
  if (chroma > 0.001 && d > 0.0) {
    float dispersionIntensity = chroma * (centeredCoord.x * centeredCoord.y) / max(halfSize.x * halfSize.y, 1e-5);
    float2 dispersed = d * grad * dispersionIntensity;
    float3 cRed;   @fetch(cRed,   (refracted + dispersed + srcOrigin) * srcScale)
    float3 cOrange; @fetch(cOrange, (refracted + dispersed * (2.0 / 3.0) + srcOrigin) * srcScale)
    float3 cYellow; @fetch(cYellow, (refracted + dispersed * (1.0 / 3.0) + srcOrigin) * srcScale)
    float3 cGreen; @fetch(cGreen, (refracted + srcOrigin) * srcScale)
    float3 cCyan;  @fetch(cCyan,  (refracted - dispersed * (1.0 / 3.0) + srcOrigin) * srcScale)
    float3 cBlue;  @fetch(cBlue,  (refracted - dispersed * (2.0 / 3.0) + srcOrigin) * srcScale)
    float3 cPurple; @fetch(cPurple, (refracted - dispersed + srcOrigin) * srcScale)
    col = float3(0.0);
    col.r += cRed.r / 3.5;
    col.r += cOrange.r / 3.5;
    col.r += cYellow.r / 3.5;
    col.r += cPurple.r / 7.0;
    col.g += cOrange.g / 7.0;
    col.g += cYellow.g / 3.5;
    col.g += cGreen.g / 3.5;
    col.g += cCyan.g / 3.5;
    col.b += cCyan.b / 3.0;
    col.b += cBlue.b / 3.0;
    col.b += cPurple.b / 3.0;
  }

  // ---- Vibrancy ---------------------------------------------------------------------
  // The library's vibrancy() colour matrix: saturation boost around Rec.601 luma.
  float luma = 0.2126 * col.r + 0.7152 * col.g + 0.0722 * col.b;
  col = luma + (col - luma) * saturation;

  // ---- Surface scrim ------------------------------------------------------------------
  // The app-side stand-in for onDrawSurface: the solid background colour at tint.
  col = col * (1.0 - tint) + coating * tint;

  // ---- Outline highlight ----------------------------------------------------------------
  // The library's paint layer: `strokeWidth = ceil(0.5dp in px) × 2`, blurred by 0.25dp and
  // clipped to the outline — the visible inner band is strokeHalf (≥ one pixel), NOT the raw
  // 0.5dp. The stroke colour's alpha is forced to 1 there and blended additively (Plus), so
  // this adds full white scaled by the slider: the top-left and bottom-right diagonals glow,
  // the other two stay dark, exactly the library's Default style. Only the inner half of the
  // stroke survives the card mask, as in the library's clipped layer.
  float strokeMask = 1.0 - smoothStep(strokeHalf - strokeFeather, strokeHalf + strokeFeather, abs(sdRaw));
  float2 hgrad = gradSdRoundedRect(centeredCoord, halfSize, gradRadius);
  float dH = dot(hgrad, lightDir);
  float intensity = pow(abs(dH), falloff);
  col += float3(255.0) * intensity * strokeMask * strokeAlpha;

  float3 rgb = clamp(col / 255.0, 0.0, 1.0) * mask;
  return half4(half3(rgb), half(mask));
}
";
}
