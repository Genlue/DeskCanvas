using System;
using System.Text;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// GPU compositor for the liquid-glass material: the whole per-pixel optical model of
/// <see cref="LiquidGlassRenderer"/> — meniscus lens, chromatic dispersion, the dye/aura bloom,
/// vibrancy, adaptive frost and the iOS rim/specular light rig — evaluated by Skia's runtime
/// shader on the render thread.
/// <para>
/// <b>Why a runtime shader and not the CPU loop.</b> The CPU pass costs ~110 ms for a 1200×800
/// card, which is fine for a cached static material but hopeless once the backdrop is re-sampled
/// live: a handful of widgets at 10 fps would saturate every core. The per-pixel work is also
/// exactly the shape a GPU is idle for. Only the backdrop texture is built on the CPU (one
/// downscale + blur per capture, shared by every widget); everything downstream is here.
/// </para>
/// <para>
/// <b>Backend restriction — this is load-bearing.</b> SkRuntimeEffect shaders only work on
/// Skia's GPU (Ganesh) backend. On the raster backend drawing one terminates the process with an
/// uncatchable SEHException, so callers <b>must</b> check that the lease actually has a
/// <c>GrContext</c> before using the shader (see <see cref="LiquidGlassSurface"/>); the CPU
/// renderer remains the fallback. Construction and compilation are safe anywhere, which is what
/// <see cref="IsSupported"/> and the check tests rely on.
/// </para>
/// <para>
/// <b>Where <c>sample()</c> may appear — this is the other load-bearing rule.</b> A child shader
/// is sampled by emitting a call to the child's fragment-processor chain, and Skia builds that
/// call with the <i>entry point's</i> input parameter (<c>_input</c>). That only compiles if the
/// call ends up inside <c>main</c>: a sampling helper survives as a real function whenever Skia's
/// inliner declines to inline it, and the emitted program then references <c>_input</c> in a
/// function that has no such parameter. The device backend reports this as
/// <c>unknown identifier '_input'</c> and draws <b>nothing at all</b> — a silently transparent
/// card, which is exactly the failure this material shipped with.
/// </para>
/// <para>
/// Skia's inliner is not something to build on: measured on this machine (see
/// <c>tests/GlassGpuProbe</c>, which drives a real ANGLE/EGL window and reads pixels back), a
/// four-tap helper is inlined when called once and <b>not</b> when called twice, a twelve-tap
/// helper behaves the same way, and a helper that samples a <i>single</i> texel is inlined even at
/// forty call sites. So the only shape that is safe by construction is:
/// <list type="bullet">
///   <item><description>every <c>sample()</c> the material needs is written lexically inside
///   <c>main</c> (via the <c>@fetch</c> expansion below), and</description></item>
///   <item><description>the one-tap helpers <c>contentTexel</c> / <c>auraTexel</c> hold exactly one
///   <c>sample()</c> each, which the inliner always takes.</description></item>
/// </list>
/// <see cref="GpuMaterialCheck"/> asserts both properties on the expanded source, because a
/// violation is invisible to every offline test.
/// </para>
/// <para>
/// The uniform derivation below deliberately mirrors the coefficient block at the top of
/// <see cref="LiquidGlassRenderer.Render"/> and reads the same constants and
/// <see cref="LiquidGlassRenderer.GetAdaptiveOptics"/> tiers, so the two paths stay in step.
/// </para>
/// </summary>
internal static class LiquidGlassGpuEffect
{
    /// <summary>Rim displacement in DIPs at refraction = 100% (mirrors <c>LiquidGlassRenderer.LensDips</c>).</summary>
    private const float LensDips = LiquidGlassRenderer.LensDips;

    private static readonly object Gate = new();
    private static SKRuntimeEffect? cachedEffect;
    private static bool compileAttempted;

    /// <summary>
    /// True when the runtime shader compiles on this build. Always check this before selecting
    /// the GPU path: compilation is the only part that can be verified without a GPU.
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
    /// Every value the shader needs, derived from the same frame the CPU renderer would use.
    /// </summary>
    internal readonly record struct Params(
        float Width, float Height, float Radius,
        float DestOriginX, float DestOriginY, float DestToRender,
        float SourceOriginX, float SourceOriginY, float SourceScale,
        float LensWidth, float InvLens, float LensShift,
        float AuraWidth, float InvAura, float InnerSpread,
        float DispersionStrength, float UnitScale,
        float Soft, float GlowStrength, float HaloWidth, float SpectrumStrength, float AmbientWeight,
        SKColor Coating, float Tint, float EdgeTint, float HighlightFactor,
        float LightX, float LightY, float Light3X, float Light3Y, float Light3Z,
        float RimLineWidth, float RimWidth, float RimBaseLight, float RimDirLight,
        float RimLightScale, float SpreadMult, float Saturation,
        float FrostFloor, float FrostLift, float FrostCap,
        bool AuraEnabled, float AuraColumns, float AuraRows, float AuraStep, float AuraMargin);

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

        // The shader's own coordinates are whatever the canvas is in at draw time — device
        // independent pixels, not the render pixels the frame is measured in — so it is told how to
        // convert. Everything geometric below stays in render pixels and is mapped from there.
        if (destWidth <= 0f || destHeight <= 0f)
        {
            destWidth = width;
            destHeight = height;
        }
        var destToRender = width / destWidth;

        var widthDip = width / scale;
        var heightDip = height / scale;
        var adaptive = LiquidGlassRenderer.GetAdaptiveOptics(frame, Math.Min(widthDip, heightDip), Math.Max(widthDip, heightDip));

        var radius = Math.Clamp(frame.Radius * scale, 0f, minSide / 2f);
        var maxLensWidth = Math.Max(0.5f, minSide * adaptive.MaxLensFrac);
        var minLensWidth = Math.Min(2.5f * scale, maxLensWidth);
        var lensWidth = optics.EdgeWidth <= 0
            ? 0f
            : Math.Clamp((float)optics.EdgeWidth * scale * adaptive.EdgeScale, minLensWidth, maxLensWidth);
        var lensShift = (float)(optics.Refraction / 100.0) * LensDips * scale * adaptive.ShiftScale;
        var rimLineWidth = MathF.Max(adaptive.RimDips * scale, 0.65f);

        var soft = frame.Theme.IsSoftGlow;
        var glowStrength = soft ? (float)Math.Clamp(optics.Glow, 0, 100) / 100f : 0f;
        var spectrumStrength = soft ? (float)Math.Clamp(optics.Spectrum, 0, 100) / 100f : 0f;
        var rimWidth = soft ? rimLineWidth * 7f : rimLineWidth;
        if (soft)
        {
            if (lensWidth > 0f) lensWidth = Math.Clamp(lensWidth * 1.55f, minLensWidth, maxLensWidth);
            lensShift *= 0.45f;
        }

        var dyeSpread = soft ? (float)Math.Clamp(optics.DyeSpread, 0, 100) / 100f : 0f;
        var dyeBandFraction = LiquidGlassSettings.MinDyeBandFraction
                              + (LiquidGlassSettings.MaxDyeBandFraction - LiquidGlassSettings.MinDyeBandFraction) * dyeSpread;
        var auraWidth = soft ? Math.Max(lensWidth, minSide * (float)dyeBandFraction) : lensWidth;
        var innerSpread = Math.Min(minSide * 0.45f, auraWidth * adaptive.InnerMult);
        var ambientWeight = soft ? 0.35f * dyeSpread : 0.35f;
        var haloWidth = Math.Min(
            Math.Max(lensWidth * 1.6f, minSide * 0.08f),
            minSide * 0.22f);

        var colorHex = frame.Dark
            ? frame.Theme.EffectiveSolidBackgroundDark
            : frame.Theme.EffectiveSolidBackgroundLight;
        if (!SKColor.TryParse(colorHex, out var coating)) coating = frame.Dark ? new SKColor(46, 46, 46) : SKColors.White;
        var tint = (float)(double.IsFinite(frame.Theme.OpacityLevel) ? Math.Clamp(frame.Theme.OpacityLevel, 0, 1) : 0.18);

        var angle = optics.LightAngle * Math.PI / 180.0;
        var lx = (float)Math.Cos(angle);
        var ly = (float)Math.Sin(angle);

        return new Params(
            Width: width, Height: height, Radius: radius,
            DestOriginX: 0f, DestOriginY: 0f, DestToRender: destToRender,
            // Desktop-space origin of the card, in <b>render pixels</b> — the shader multiplies the
            // whole sum by srcScale exactly once, so pre-scaling this would square the factor and
            // slide the sample point across the wallpaper. That only looked correct while the
            // backdrop happened to be built at native resolution (srcScale == 1), which is why it
            // survived until 背景清晰度 made srcScale a user choice.
            // The 对齐 offset is deliberately NOT added: the backdrop texture is built by
            // DrawWallpaper, which has already shifted the image by -offset, and adding it again
            // would apply the user's alignment twice.
            SourceOriginX: frame.DesktopX,
            SourceOriginY: frame.DesktopY,
            SourceScale: sourceScale,
            LensWidth: lensWidth,
            InvLens: lensWidth > 0f ? 1f / lensWidth : 0f,
            LensShift: lensShift,
            AuraWidth: auraWidth,
            InvAura: auraWidth > 0f ? 1f / auraWidth : 0f,
            InnerSpread: innerSpread,
            DispersionStrength: (float)(optics.Dispersion / 100.0),
            UnitScale: scale,
            Soft: soft ? 1f : 0f,
            GlowStrength: glowStrength,
            HaloWidth: haloWidth,
            SpectrumStrength: spectrumStrength,
            AmbientWeight: ambientWeight,
            Coating: coating,
            Tint: tint,
            EdgeTint: (float)(optics.EdgeTint / 100.0),
            HighlightFactor: (float)(optics.Highlight / LiquidGlassRenderer.HighlightReference),
            LightX: lx, LightY: ly,
            Light3X: lx * 0.65f, Light3Y: ly * 0.65f, Light3Z: 0.76f,
            RimLineWidth: rimLineWidth, RimWidth: rimWidth,
            RimBaseLight: adaptive.RimBaseLight, RimDirLight: adaptive.RimDirLight,
            RimLightScale: adaptive.RimLightScale, SpreadMult: adaptive.SpreadMult,
            Saturation: LiquidGlassRenderer.Saturation,
            FrostFloor: LiquidGlassRenderer.FrostFloor,
            FrostLift: LiquidGlassRenderer.FrostLift,
            FrostCap: LiquidGlassRenderer.FrostCap,
            AuraEnabled: false, AuraColumns: 1f, AuraRows: 1f, AuraStep: 1f, AuraMargin: 0f);
    }

    /// <summary>
    /// Build a shader for one card. <paramref name="source"/> is the shared blurred backdrop and
    /// <paramref name="aura"/> the optional soft-glow dye field (see
    /// <see cref="LiquidGlassRenderer.AuraField"/>). Returns null when the shader cannot be built,
    /// in which case the caller must fall back to the CPU bitmap.
    /// </summary>
    public static SKShader? Create(SKBitmap source, SKBitmap? aura, in Params p)
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
                ["lensWidth"] = p.LensWidth,
                ["invLens"] = p.InvLens,
                ["lensShift"] = p.LensShift,
                ["auraWidth"] = p.AuraWidth,
                ["invAura"] = p.InvAura,
                ["innerSpread"] = p.InnerSpread,
                ["dispStrength"] = p.DispersionStrength,
                ["unitScale"] = p.UnitScale,
                ["soft"] = p.Soft,
                ["glowStrength"] = p.GlowStrength,
                ["haloWidth"] = p.HaloWidth,
                ["spectrumStrength"] = p.SpectrumStrength,
                ["ambientWeight"] = p.AmbientWeight,
                // 0-255, exactly like the CPU renderer's Channel(value, coat): the shader mixes the
                // coating into a colour that is already in 0-255 units, so a normalised 0-1 coating
                // contributes ~255x too little and the tint degenerates into a plain darkening —
                // the card reads as if it were under a black mask, whatever colour is configured.
                ["coating"] = new[] { (float)p.Coating.Red, (float)p.Coating.Green, (float)p.Coating.Blue },
                ["tint"] = p.Tint,
                ["edgeTint"] = p.EdgeTint,
                ["highlightFactor"] = p.HighlightFactor,
                ["lightDir"] = new[] { p.LightX, p.LightY },
                ["light3"] = new[] { p.Light3X, p.Light3Y, p.Light3Z },
                ["rimParams"] = new[] { p.RimLineWidth, p.RimWidth, p.RimBaseLight, p.RimDirLight },
                ["rimLightScale"] = p.RimLightScale,
                ["spreadMult"] = p.SpreadMult,
                ["saturation"] = p.Saturation,
                ["frost"] = new[] { p.FrostFloor, p.FrostLift, p.FrostCap },
                ["auraSize"] = new[] { Math.Max(1f, p.AuraColumns), Math.Max(1f, p.AuraRows) },
                ["auraStep"] = Math.Max(0.001f, p.AuraStep),
                ["auraMargin"] = p.AuraMargin,
                ["auraEnabled"] = p.AuraEnabled ? 1f : 0f
            };

            var children = new SKRuntimeEffectChildren(effect);
            // No local matrix on the backdrop: the shader performs the render-px -> backdrop-px
            // mapping itself via srcScale, and applying it here as well would square it.
            using var backdropChild = source.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            // The dye grid is already addressed in normalised cell coordinates by the shader.
            using var auraChild = (aura ?? source).ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            children["content"] = backdropChild;
            children["aura"] = auraChild;

            // Both children are refcounted into the returned shader, so releasing our handles here
            // is safe and keeps the render thread from leaking one shader per frame.
            return effect.ToShader(false, uniforms, children);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The optical model's SkSL source, for the in-app staged diagnostic probe.</summary>
    internal static string SourceForDiagnostics => Source;

    // -------------------------------------------------------------------------------------------
    // source assembly
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The optical model with its child-shader fetches written out. <c>@fetch</c> / <c>@fetchAura</c>
    /// are expanded by <see cref="Expand"/>; see the class remarks for why the fetches cannot live
    /// in helper functions.
    /// </summary>
    private static readonly string Source = Expand(RawSource);

    /// <summary>
    /// One bilinear fetch of the backdrop, addressed by the target variable it assigns.
    /// <para>
    /// The four taps call <c>contentTexel</c>, which holds a single <c>sample()</c> and is therefore
    /// always inlined. Interpolation is by hand because a bitmap shader in this Skia samples
    /// nearest — neither <c>SKBitmap.ToShader</c> nor <c>SKShader.CreateImage</c> is filtered, there
    /// is no sampling-options overload in SkiaSharp 2.88 — and nearest would snap the lens
    /// displacement to whole texels.
    /// </para>
    /// </summary>
    private static string FetchBlock(string target, string coordinate, bool aura)
    {
        var texel = aura ? "auraTexel" : "contentTexel";
        var s = new StringBuilder();
        s.Append("{ float2 _p = (").Append(coordinate).Append("); float2 _b = floor(_p); float2 _f = _p - _b;\n      ");
        s.Append(target).Append(" = mix(mix(").Append(texel).Append("(_b), ").Append(texel).Append("(_b + float2(1.0, 0.0)), _f.x),\n");
        s.Append("                       mix(").Append(texel).Append("(_b + float2(0.0, 1.0)), ")
         .Append(texel).Append("(_b + float2(1.0, 1.0)), _f.x), _f.y); }");
        return s.ToString();
    }

    /// <summary>
    /// Replace every <c>@fetch(target, coordinate)</c> / <c>@fetchAura(target, coordinate)</c> with
    /// the corresponding <see cref="FetchBlock"/>, so the emitted program contains no sampling
    /// helper beyond the single-tap texel lookups.
    /// </summary>
    internal static string Expand(string template)
    {
        var result = new StringBuilder(template.Length * 2);
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
            var cursor = at + "@fetch".Length;
            var aura = template.AsSpan(cursor).StartsWith("Aura", StringComparison.Ordinal);
            if (aura) cursor += "Aura".Length;

            var open = template.IndexOf('(', cursor);
            var close = MatchingParenthesis(template, open);
            var arguments = template[(open + 1)..close];
            var comma = arguments.IndexOf(',');
            result.Append(FetchBlock(
                arguments[..comma].Trim(),
                arguments[(comma + 1)..].Trim(),
                aura));
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
    /// The optical model, transcribed from the per-pixel loop of
    /// <see cref="LiquidGlassRenderer.Render"/>. Kept in SkSL's lowest common denominator: no
    /// struct returns (unsupported by this Skia), no dynamic loop bounds, no arrays, no
    /// <c>out</c> parameters, no early <c>return</c> from <c>main</c>.
    /// </summary>
    private const string RawSource = @"
uniform shader content;   // blurred, downscaled desktop backdrop
uniform shader aura;      // dye bloom grid for the soft recipe

uniform float2 size;      // card size, render px
uniform float radius;     // card corner radius, render px
uniform float2 destOrigin; // top-left of the drawn rect, in the shader's own coordinate space
uniform float destToRender; // multiply shader coordinates by this to get render px
uniform float2 srcOrigin; // card origin, in render px (scaled once, with everything else, by srcScale)
uniform float srcScale;   // render px -> backdrop px

uniform float lensWidth;
uniform float invLens;
uniform float lensShift;
uniform float auraWidth;
uniform float invAura;
uniform float innerSpread;
uniform float dispStrength;
uniform float unitScale;
uniform float soft;
uniform float glowStrength;
uniform float haloWidth;
uniform float spectrumStrength;
uniform float ambientWeight;

uniform float3 coating;
uniform float tint;
uniform float edgeTint;
uniform float highlightFactor;
uniform float2 lightDir;
uniform float3 light3;
uniform float4 rimParams;   // x = rim line width, y = soft rim width, z = base light, w = directional light
uniform float rimLightScale;
uniform float spreadMult;
uniform float saturation;
uniform float3 frost;       // x = floor, y = lift, z = cap

uniform float2 auraSize;
uniform float auraStep;
uniform float auraMargin;
uniform float auraEnabled;

const float PI = 3.14159265;

float lumaOf(float3 c) { return 0.2126 * c.r + 0.7152 * c.g + 0.0722 * c.b; }

/// Heron's-step smoothstep. This Skia's SkSL has no smoothstep builtin, so the CPU helper of the
/// same name is transcribed rather than approximated.
float smoothStep(float edge0, float edge1, float value) {
  if (edge1 <= edge0) return value >= edge1 ? 1.0 : 0.0;
  float t = clamp((value - edge0) / (edge1 - edge0), 0.0, 1.0);
  return t * t * (3.0 - 2.0 * t);
}

/// SkSL here has no two-argument atan either. The bevel's interior branch only ever needs it for
/// two non-negative components, where atan(y/x) is exact and the epsilon keeps a straight edge
/// (x == 0) at exactly pi/2.
float atan2NonNegative(float y, float x) { return atan(y / max(x, 1e-6)); }

/// One exact texel of the backdrop. Bitmap shaders in this Skia sample nearest — SKBitmap.ToShader
/// offers no sampling options — so every lookup below is interpolated by hand. Without this the
/// lens would snap its displacement to whole texels (visible stair-stepping around the rim, and the
/// CPU material would not match), and the downscaled backdrop would upsample as visible blocks.
/// <para>
/// A single sample() per function is what keeps the inliner's hand forced: see the class remarks.
/// </para>
float3 contentTexel(float2 texel) { return float3(sample(content, texel + float2(0.5)).rgb) * 255.0; }

/// One exact cell of the dye bloom grid. Single-sample, same as contentTexel.
float3 auraTexel(float2 cell) {
  float2 c = clamp(cell, float2(0.0), auraSize - float2(1.0));
  return float3(sample(aura, (c + float2(0.5)) / auraSize).rgb) * 255.0;
}

/// Port of LiquidGlassRenderer.BevelField.Evaluate: outward normal + inward depth.
/// <para>
/// Returns them packed as float3 (xy = normal, z = depth) instead of using <c>out</c> parameters.
/// The GPU backend compiles runtime effects down to ES2-compatible code, which is a far narrower
/// dialect than the compiler used by <c>SKRuntimeEffect.Create</c> accepts — an out-parameter
/// function passes that first check and then fails to build on the device, which renders as a
/// silently transparent card.
/// </para>
float3 bevelAt(float2 p, float2 h, float r) {
  float2 v = p - h;
  float2 s = float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
  float2 a = abs(v);
  float2 q = a - (h - float2(r));
  if (q.x > 0.0 && q.y > 0.0) {
    float len = length(q);
    if (len > 1e-5) return float3(q.x / len * s.x, q.y / len * s.y, r - len);
    return float3(0.7071 * s.x, 0.7071 * s.y, r);
  }
  if (q.x <= 0.0 && q.y > 0.0) return float3(0.0, s.y, h.y - a.y);
  if (q.x > 0.0 && q.y <= 0.0) return float3(s.x, 0.0, h.x - a.x);
  float angle = atan2NonNegative(-q.y + 1e-4, -q.x + 1e-4);
  return float3(cos(angle) * s.x, sin(angle) * s.y, min(h.x - a.x, h.y - a.y));
}

/// Port of LiquidGlassRenderer.Displacement: meniscus lens profile, zero in the flat centre.
float displacementAt(float depth, float lensW, float shift) {
  if (depth <= 0.0 || depth >= lensW || lensW <= 0.0) return 0.0;
  float t = depth / lensW;
  return shift * max(0.0, sin(PI * t) * pow(1.0 - t, 1.4) / 0.45);
}

/// SKColor.ToHsl equivalent: h in degrees, s/l in percent.
/// <para>
/// Returns them as float3 rather than through <c>out</c> parameters: the ES2-compatible dialect the
/// GPU backend compiles runtime effects into rejects out-parameter functions, and a program that
/// fails to build there draws nothing at all instead of reporting an error.
/// </para>
float3 rgbToHsl(float3 c) {
  float r = c.r / 255.0;
  float g = c.g / 255.0;
  float b = c.b / 255.0;
  float mx = max(r, max(g, b));
  float mn = min(r, min(g, b));
  float l = (mx + mn) * 0.5;
  float d = mx - mn;
  float h = 0.0;
  float s = 0.0;
  if (d > 1e-5) {
    s = l > 0.5 ? d / max(2.0 - mx - mn, 1e-5) : d / max(mx + mn, 1e-5);
    if (mx == r) h = (g - b) / d + (g < b ? 6.0 : 0.0);
    else if (mx == g) h = (b - r) / d + 2.0;
    else h = (r - g) / d + 4.0;
    h = h / 6.0;
  }
  return float3(h * 360.0, s * 100.0, l * 100.0);
}

/// SKColor.FromHsl equivalent.
float3 hslToRgb(float hh, float ss, float ll) {
  float h = fract(hh / 360.0) * 6.0;
  float s = clamp(ss, 0.0, 100.0) / 100.0;
  float l = clamp(ll, 0.0, 100.0) / 100.0;
  float c = (1.0 - abs(2.0 * l - 1.0)) * s;
  // No mod() builtin here either.
  float modH = h - 2.0 * floor(h / 2.0);
  float x = c * (1.0 - abs(modH - 1.0));
  float m = l - c * 0.5;
  float3 rgb;
  if (h < 1.0) rgb = float3(c, x, 0.0);
  else if (h < 2.0) rgb = float3(x, c, 0.0);
  else if (h < 3.0) rgb = float3(0.0, c, x);
  else if (h < 4.0) rgb = float3(0.0, x, c);
  else if (h < 5.0) rgb = float3(x, 0.0, c);
  else rgb = float3(c, 0.0, x);
  return (rgb + float3(m)) * 255.0;
}

/// Vibrancy + adaptive frost + coating, i.e. the CPU sampler's Channel() local function.
float channelOf(float value, float coat, float luma, float adapt, float localTint) {
  float c = luma + (value - luma) * saturation;
  c += (255.0 - c) * adapt;
  return c * (1.0 - localTint) + coat * localTint;
}

half4 main(float2 xy) {
  // The canvas handed to a custom draw operation works in its own units (device independent
  // pixels), while the whole optical model is measured in render pixels. Everything below is in
  // render pixels: without this conversion the rounded-rect field and the lens would be scaled by
  // the display's DPI factor — rounded corners in the wrong place and, on a scaled display, an
  // inset or oversized card.
  float2 rxy = (xy - destOrigin) * destToRender;

  float2 h = size * 0.5;
  float2 p = rxy - h;

  // Rounded-rect coverage. The card is drawn opaque inside the radius and fully
  // transparent outside, matching the CPU path's DrawRoundRect output surface.
  float2 q = abs(p) - h + float2(radius);
  float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
  // No early return here: the ES2-compatible dialect the GPU backend compiles to does not take an
  // early return from main, and the mask already zeroes every channel outside the card anyway.
  float mask = 1.0 - smoothStep(-0.75, 0.75, sd);

  // The bevel field is addressed in <b>card-local absolute</b> coordinates, exactly like
  // LiquidGlassRenderer.BevelField.Evaluate(x + 0.5, y + 0.5): it subtracts the half-size itself.
  // Passing the already centre-relative p here subtracted it twice, which put the whole normal and
  // depth field half a card off — the material rendered as a flat wash with a stray dark rounded
  // rectangle where the field happened to fold back over itself.
  float3 bevel = bevelAt(rxy, h, radius);
  float2 n = bevel.xy;
  float depth = bevel.z;

  float shift = displacementAt(depth, lensWidth, lensShift);

  // Chromatic dispersion: wavelength-dependent refraction along the curved meniscus,
  // tapering to nothing at both the outer border and the flat centre.
  float rimT = (lensWidth > 0.0 && depth < lensWidth) ? clamp(depth / max(lensWidth, 1e-5), 0.0, 1.0) : 1.0;
  float rimFalloff = (1.0 - rimT) * (1.0 - rimT);
  float dispShape = sin(PI * rimT) * rimFalloff / 0.35;
  float split = dispStrength * ((shift * 0.07 + 0.95 * unitScale) * dispShape);
  float tintSplit = soft > 0.5 ? split * 1.60 : split;

  // The backdrop beyond the card edge is real desktop content, not a clamped edge
  // pixel: refraction around the rim therefore bends in what is genuinely behind it.
  //
  // Every child-shader fetch is expanded here, in the entry point, rather than through a sampling
  // helper: Skia's inliner leaves a multi-tap helper as a real function, and the device program it
  // then emits references the entry point's _input from that function and fails to build. See the
  // class remarks.
  float2 base = rxy - n * shift + srcOrigin;
  float3 middle;
  @fetch(middle, base * srcScale)
  float3 reddish = middle;
  float3 bluish = middle;
  if (tintSplit > 0.002) {
    @fetch(reddish, (base + n * tintSplit) * srcScale)
    @fetch(bluish, (base - n * tintSplit) * srcScale)
  }
  float cRed = reddish.r;
  float cGreen = middle.g;
  float cBlue = bluish.b;

  // Full-spectrum dispersion (seven taps) blended in over the three-tap split.
  //#probe:spectrum:begin
  if (spectrumStrength > 0.01 && tintSplit > 0.002) {
    float2 step1 = n * tintSplit;
    float3 t1;
    @fetch(t1, (base + step1) * srcScale)
    float3 t2;
    @fetch(t2, (base + step1 * (2.0 / 3.0)) * srcScale)
    float3 t3;
    @fetch(t3, (base + step1 / 3.0) * srcScale)
    float3 t5;
    @fetch(t5, (base - step1 / 3.0) * srcScale)
    float3 t6;
    @fetch(t6, (base - step1 * (2.0 / 3.0)) * srcScale)
    float3 t7;
    @fetch(t7, (base - step1) * srcScale)
    float specRed = (t1.r + t2.r + t3.r) / 3.5 + t7.r / 7.0;
    float specGreen = t2.g / 7.0 + (t3.g + middle.g + t5.g) / 3.5;
    float specBlue = (t5.b + t6.b + t7.b) / 3.0;
    cRed = mix(cRed, specRed, spectrumStrength);
    cGreen = mix(cGreen, specGreen, spectrumStrength);
    cBlue = mix(cBlue, specBlue, spectrumStrength);
  }
  //#probe:spectrum:end

  // Crystal clarity at the meniscus, fading into the frosted coating inside.
  float clarityRamp = (tint >= 0.99)
      ? 0.0
      : (1.0 - smoothStep(0.0, min(lensWidth * 1.5, min(size.x, size.y) * 0.40), depth));
  float localTint = tint * (1.0 - 0.28 * clarityRamp);

  float luma = lumaOf(float3(cRed, cGreen, cBlue));
  float adapt = clamp(frost.x + (1.0 - luma / 255.0) * frost.y, 0.0, frost.z);
  if (soft > 0.5) adapt = min(adapt * 1.15, 0.11);

  float r = channelOf(cRed, coating.r, luma, adapt, localTint);
  float g = channelOf(cGreen, coating.g, luma, adapt, localTint);
  float b = channelOf(cBlue, coating.b, luma, adapt, localTint);

  // Dual-scale organic aura: chromatic glaze on the meniscus plus a wide interior wash.
  float u1 = auraWidth > 0.0 ? clamp(depth * invAura, 0.0, 1.0) : 1.0;
  float bezelAura = (auraWidth > 0.0 && depth < auraWidth) ? 0.5 * (1.0 + cos(PI * u1)) : 0.0;
  float u2 = (innerSpread > 0.0 && depth < innerSpread) ? clamp(depth / innerSpread, 0.0, 1.0) : 1.0;
  float ambientDiffusion = (innerSpread > 0.0 && depth < innerSpread) ? 0.5 * (1.0 + cos(PI * u2)) : 0.0;
  float softAura = (1.0 - ambientWeight) * bezelAura + ambientWeight * ambientDiffusion;

  float hlR = 255.0;
  float hlG = 255.0;
  float hlB = 255.0;
  float rimGlow = 0.0;
  if (edgeTint > 0.001 && softAura > 0.001) {
    // The soft recipe reads a *bloomed* colour field so a small patch spreads along
    // the edge like a light source; everything else dyes from the pixel itself.
    float3 dyeSource = middle;
    if (auraEnabled > 0.5) {
      // The dye grid is ~30x24 texels magnified over the whole card, so nearest sampling here
      // would read as a mosaic rather than as a soft 晕染.
      float2 g = clamp((rxy + float2(auraMargin)) / max(auraStep, 0.001) - 0.5,
                       float2(0.0), auraSize - float2(1.0));
      @fetchAura(dyeSource, g)
    }
    float chroma = max(dyeSource.r, max(dyeSource.g, dyeSource.b))
                 - min(dyeSource.r, min(dyeSource.g, dyeSource.b));
    float chromaWeight = smoothStep(soft > 0.5 ? 9.0 : 14.0, soft > 0.5 ? 24.0 : 32.0, chroma);
    float lumaGate = smoothStep(8.0, 28.0, lumaOf(dyeSource));
    float colorWeight = chromaWeight * lumaGate;

    if (colorWeight > 0.001) {
      float3 hsl = rgbToHsl(dyeSource);
      float3 pureGlow = hslToRgb(hsl.x,
          clamp(hsl.y * 2.2 + 25.0 * colorWeight, 0.0, 100.0),
          clamp(hsl.z * 0.15 + 48.0, 46.0, 60.0));
      float glowR = (1.0 - colorWeight) * 255.0 + colorWeight * pureGlow.r;
      float glowG = (1.0 - colorWeight) * 255.0 + colorWeight * pureGlow.g;
      float glowB = (1.0 - colorWeight) * 255.0 + colorWeight * pureGlow.b;

      float glazeMix = edgeTint * softAura * (soft > 0.5 ? 0.62 : 0.35) * colorWeight;
      r += (glowR - r) * glazeMix;
      g += (glowG - g) * glazeMix;
      b += (glowB - b) * glazeMix;

      float hlMix = clamp((soft > 0.5 ? 1.15 : 1.0) * pow(edgeTint, 0.70) * softAura * colorWeight, 0.0, 1.0);
      hlR = (1.0 - hlMix) * 255.0 + hlMix * pureGlow.r;
      hlG = (1.0 - hlMix) * 255.0 + hlMix * pureGlow.g;
      hlB = (1.0 - hlMix) * 255.0 + hlMix * pureGlow.b;
    }

    rimGlow = edgeTint * softAura * softAura * (soft > 0.5 ? 0.5 : 0.35);
  }

  // 柔光晕: a broad halo hugging the rim, applied as light rather than as paint.
  float softHalo = 0.0;
  if (glowStrength > 0.001 && haloWidth > 0.0 && depth < haloWidth) {
    float wrap = 0.62 + 0.38 * max(0.0, n.x * lightDir.x + n.y * lightDir.y);
    softHalo = glowStrength * pow(1.0 - depth / haloWidth, 2.4) * wrap * 0.55;
  }

  // Luminous glass light rig: omnidirectional rim stroke with a directional boost…
  float cosL = n.x * lightDir.x + n.y * lightDir.y;
  float rimEdge = soft > 0.5
      ? pow(1.0 - smoothStep(0.0, rimParams.y, depth), 2.0)
      : 1.0 - smoothStep(0.0, rimParams.x, depth);
  float directional = max(0.0, cosL);
  float rimLight = soft > 0.5
      ? rimEdge * (rimParams.z * 0.55 + rimParams.w * 0.55 * pow(directional, 0.60)) * 0.75
      : rimEdge * (rimParams.z + rimParams.w * pow(directional, 0.85));

  // …plus the meniscus reflection and the continuous surface specular spread.
  float meniscusLight = 0.0;
  float spreadWidth = min(min(size.x, size.y) * 0.45, lensWidth * spreadMult);
  if (depth < spreadWidth) {
    float t2 = lensWidth > 0.0 ? clamp(depth * invLens, 0.0, 1.0) : 0.0;
    float tilt = (depth < lensWidth) ? pow(1.0 - t2, 2.0) : 0.0;
    float nx2 = n.x * tilt * 0.82;
    float ny2 = n.y * tilt * 0.82;
    float nz = sqrt(max(0.01, 1.0 - nx2 * nx2 - ny2 * ny2));
    float ndotl = max(0.0, nx2 * light3.x + ny2 * light3.y + nz * light3.z);
    float specGlint = pow(ndotl, soft > 0.5 ? 3.0 : 28.0);
    float specGlow = pow(ndotl, soft > 0.5 ? 2.0 : 8.0);
    float fresnel = pow(1.0 - nz, 3.0) * (soft > 0.5 ? 0.16 : 0.35);
    float bevelLight = ((0.70 * specGlint + 0.30 * specGlow) * max(0.0, cosL) * (soft > 0.5 ? 0.38 : 0.90)
                        + fresnel * (soft > 0.5 ? 0.18 : 0.25)) * (1.0 - t2) * (1.0 - t2);
    float innerRoll = 0.5 * (1.0 + cos(PI * (depth / spreadWidth)));
    float innerSheen = pow(ndotl, soft > 0.5 ? 4.0 : 6.0) * max(0.0, cosL)
                       * (soft > 0.5 ? 0.04 : 0.08) * innerRoll;
    meniscusLight = bevelLight + innerSheen;
  }

  float ambientLuster = max(0.0, 1.0 - rxy.y / size.y) * (soft > 0.5 ? 0.04 : 0.025);
  float totalLight = highlightFactor * (rimLight * rimLightScale + meniscusLight * 0.70 + ambientLuster) + rimGlow;
  float lightMix = clamp(totalLight, 0.0, 1.0);

  r += (hlR - r) * lightMix;
  g += (hlG - g) * lightMix;
  b += (hlB - b) * lightMix;

  if (softHalo > 0.001) {
    float cast = 0.30 * edgeTint;
    r += (mix(255.0, hlR, cast) - r) * softHalo;
    g += (mix(255.0, hlG, cast) - g) * softHalo;
    b += (mix(255.0, hlB, cast) - b) * softHalo;
  }

  float3 rgb = clamp(float3(r, g, b) / 255.0, 0.0, 1.0) * mask;
  return half4(half3(rgb), half(mask));
}
";
}
