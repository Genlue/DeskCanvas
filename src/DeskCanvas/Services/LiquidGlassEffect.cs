using System;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// GPU compositor for the 液态玻璃 material (<see cref="SurfaceStyle.LiquidGlass"/>): a faithful
/// port of Kyant0/AndroidLiquidGlass 2.0's optical model — its rounded-rect refraction lens with
/// depth effect and diagonal chromatic aberration, plus its hairline outline highlight with
/// configurable highlight angle — evaluated by Skia's runtime shader on the render thread.
/// </summary>
public static class LiquidGlassEffect
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
                cachedEffect = SKRuntimeEffect.CreateShader(Source, out var errors);
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
    /// Every value the shader needs, derived from the frame and the 液态玻璃 optics.
    /// </summary>
    public readonly record struct Params(
        float Width, float Height, float Radius,
        float DestOriginX, float DestOriginY, float DestToRender,
        float SourceOriginX, float SourceOriginY, float SourceScale,
        float RefrHeight, float RefrAmount, float DepthEffect, float GradRadius,
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
        var optics = frame.Theme.EffectiveLiquidGlass;
        var scale = frame.Scale;
        var width = frame.Width;
        var height = frame.Height;
        var minSide = Math.Min(width, height);

        if (destWidth <= 0f || destHeight <= 0f)
        {
            destWidth = width;
            destHeight = height;
        }
        var destToRender = width / destWidth;

        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);
        var lens = LiquidGlassV2Lens.Compute(optics.Refraction, minSide, radius);

        var colorHex = frame.Dark
            ? frame.Theme.EffectiveSolidBackgroundDark
            : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.20);

        var angle = (float)(optics.HighlightAngle * Math.PI / 180.0);
        var strokeHalf = MathF.Ceiling((float)LiquidGlassSettings.StrokeWidthDips * scale);

        return new Params(
            Width: width, Height: height, Radius: radius,
            DestOriginX: 0f, DestOriginY: 0f, DestToRender: destToRender,
            SourceOriginX: frame.DesktopX,
            SourceOriginY: frame.DesktopY,
            SourceScale: sourceScale,
            RefrHeight: lens.Band,
            RefrAmount: lens.Amount,
            DepthEffect: lens.DepthEffect,
            GradRadius: lens.GradRadius,
            Chroma: (float)(optics.Dispersion / 100.0),
            Saturation: 1f + (float)LiquidGlassSettings.VibrancySaturationBoost * (float)(optics.Vibrancy / 100.0),
            Tint: tint,
            Coating: coating,
            StrokeHalf: strokeHalf,
            StrokeFeather: (float)LiquidGlassSettings.StrokeFeatherDips * scale,
            StrokeAlpha: (float)(optics.Highlight / LiquidGlassSettings.HighlightReference),
            Falloff: (float)LiquidGlassSettings.HighlightFalloff,
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
                ["gradRadius"] = p.GradRadius,
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
            using var backdropChild = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            children["content"] = backdropChild;

            return effect.ToShader(uniforms, children);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The optical model's SkSL source, for diagnostics.</summary>
    public static string SourceForDiagnostics => Source;

    private static readonly string Source = Expand(RawSource);

    /// <summary>
    /// One bilinear fetch of the backdrop at <paramref name="coord"/>, written as four single-texel
    /// taps plus a hand-written blend (a bitmap shader samples nearest, so Skia's own filtering is
    /// not available here — see <see cref="Expand"/>).
    /// <para>
    /// <b>The tap base and the blend weight must come from the same texel grid.</b> A texel
    /// <c>k</c> covers <c>[k, k+1)</c> and is stored at its centre <c>k+0.5</c>, so the two texels a
    /// point <c>c</c> lies between are <c>floor(c-0.5)+0.5</c> and <c>+1</c>, and the weight is
    /// <c>c-0.5-floor(c-0.5)</c>. Pairing those centres with <c>fract(c)</c> instead only adds a
    /// constant half-texel shift (3.8.2's form, invisible); pairing them with
    /// <c>fract(c-0.5)</c> — the shape this file shipped with — makes the reconstructed position
    /// jump by a whole texel at every half-texel boundary. At 背景清晰度 25% one texel spans four
    /// screen pixels, so that jump showed up as the reported 网格状像素: regular blocks of
    /// duplicated backdrop, worst with 模糊 high and 清晰度 low, absent at 100%.
    /// </para>
    /// </summary>
    private static string SampleBackdrop(string target, string coord) =>
        $"float2 {target}B = floor({coord} - float2(0.5, 0.5));\n" +
        $"{target}00 = contentTexel({target}B + float2(0.5, 0.5));\n" +
        $"{target}10 = contentTexel({target}B + float2(1.5, 0.5));\n" +
        $"{target}01 = contentTexel({target}B + float2(0.5, 1.5));\n" +
        $"{target}11 = contentTexel({target}B + float2(1.5, 1.5));\n" +
        $"{target}F = {coord} - float2(0.5, 0.5) - {target}B;\n" +
        $"{target} = mix(mix({target}00, {target}10, {target}F.x), mix({target}01, {target}11, {target}F.x), {target}F.y);\n";

    private static string Expand(string raw)
    {
        var result = raw;
        int idx;
        while ((idx = result.IndexOf("@fetch(", StringComparison.Ordinal)) >= 0)
        {
            int depth = 1;
            int end = -1;
            for (int i = idx + 7; i < result.Length; i++)
            {
                if (result[i] == '(') depth++;
                else if (result[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }
            if (end < 0) break;
            var args = result.Substring(idx + 7, end - (idx + 7)).Split(',', 2);
            var target = args[0].Trim();
            var coord = args[1].Trim();
            var expansion = SampleBackdrop(target, coord);
            int skip = end + 1;
            if (skip < result.Length && result[skip] == ';') skip++;
            result = result.Substring(0, idx) + expansion + result.Substring(skip);
        }
        return result;
    }

    private const string RawSource = @"
uniform float2 size;
uniform float radius;
uniform float2 destOrigin;
uniform float destToRender;
uniform float2 srcOrigin;
uniform float srcScale;
uniform float refrHeight;
uniform float refrAmount;
uniform float depthEffect;
uniform float gradRadius;
uniform float chroma;
uniform float saturation;
uniform float tint;
uniform float3 coating;
uniform float strokeHalf;
uniform float strokeFeather;
uniform float strokeAlpha;
uniform float falloff;
uniform float2 lightDir;

uniform shader content;

float smoothStep(float edge0, float edge1, float x) {
  float t = clamp((x - edge0) / (edge1 - edge0), 0.0, 1.0);
  return t * t * (3.0 - 2.0 * t);
}

float sdRoundedRect(float2 p, float2 b, float r) {
  float2 q = abs(p) - b + float2(r);
  return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - r;
}

float2 sgnCoord(float2 v) {
  return float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
}

float2 gradSdRoundedRect(float2 p, float2 b, float r) {
  float2 corner = abs(p) - (b - float2(r));
  float2 sgn = sgnCoord(p);
  if (corner.x >= 0.0 && corner.y >= 0.0) {
    float mx = max(corner.x, 1e-5);
    float my = max(corner.y, 1e-5);
    return float2(sgn.x * mx, sgn.y * my) / length(float2(mx, my));
  }
  if (corner.x >= 0.0) return float2(sgn.x, 0.0);
  if (corner.y >= 0.0) return float2(0.0, sgn.y);
  float ex = max(-corner.x, 1e-5);
  float ey = max(-corner.y, 1e-5);
  return float2(sgn.x * ey, sgn.y * ex) / length(float2(ex, ey));
}

float3 contentTexel(float2 p) {
  // Skia m130+ removed sample(shader, coord); child.eval(coord) is the replacement.
  return content.eval(p).rgb * 255.0;
}

half4 main(float2 fragCoord) {
  float2 p = (fragCoord - destOrigin) * destToRender;
  float2 halfSize = size * 0.5;
  float2 centeredCoord = p - halfSize;

  float sdRaw = sdRoundedRect(centeredCoord, halfSize, radius);
  float mask = 1.0 - smoothStep(-0.75, 0.75, sdRaw);
  float sd = min(sdRaw, 0.0);

  float depth01 = refrHeight > 0.0 ? clamp(-sd / refrHeight, 0.0, 1.0) : 1.0;
  float lensX = 1.0 - depth01;
  float d = (1.0 - sqrt(max(0.0, 1.0 - lensX * lensX))) * refrAmount;

  float2 grad = gradSdRoundedRect(centeredCoord, halfSize, gradRadius);
  float radialLen = length(centeredCoord);
  float2 radial = radialLen > 1e-4 ? centeredCoord / radialLen : float2(0.0);
  float2 g = grad + depthEffect * radial;
  float glen = length(g);
  g = glen > 1e-5 ? g / glen : float2(0.0);

  float2 baseSamplePos = (srcOrigin + p - g * d) * srcScale;

  float3 col;
  float3 col00; float3 col10; float3 col01; float3 col11; float2 colF;
  @fetch(col, baseSamplePos);

  if (chroma > 0.001 && d > 0.0) {
    float dispersionIntensity = chroma * (centeredCoord.x * centeredCoord.y) / max(halfSize.x * halfSize.y, 1e-5);
    float2 delta = d * g * dispersionIntensity * srcScale;
    float3 cRed; float3 cRed00; float3 cRed10; float3 cRed01; float3 cRed11; float2 cRedF;
    @fetch(cRed, baseSamplePos + delta);
    float3 cOrange; float3 cOrange00; float3 cOrange10; float3 cOrange01; float3 cOrange11; float2 cOrangeF;
    @fetch(cOrange, baseSamplePos + delta * (2.0 / 3.0));
    float3 cYellow; float3 cYellow00; float3 cYellow10; float3 cYellow01; float3 cYellow11; float2 cYellowF;
    @fetch(cYellow, baseSamplePos + delta / 3.0);
    float3 cGreen = col;
    float3 cCyan; float3 cCyan00; float3 cCyan10; float3 cCyan01; float3 cCyan11; float2 cCyanF;
    @fetch(cCyan, baseSamplePos - delta / 3.0);
    float3 cBlue; float3 cBlue00; float3 cBlue10; float3 cBlue01; float3 cBlue11; float2 cBlueF;
    @fetch(cBlue, baseSamplePos - delta * (2.0 / 3.0));
    float3 cPurple; float3 cPurple00; float3 cPurple10; float3 cPurple01; float3 cPurple11; float2 cPurpleF;
    @fetch(cPurple, baseSamplePos - delta);

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

  float luma = 0.2126 * col.r + 0.7152 * col.g + 0.0722 * col.b;
  col = luma + (col - luma) * saturation;

  col = col * (1.0 - tint) + coating * tint;

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
