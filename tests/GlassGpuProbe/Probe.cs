using System.Text;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace GlassGpuProbe;

/// <summary>
/// The on-device acceptance test for the liquid-glass GPU material, plus the minimal
/// reproductions that explain why the shader is shaped the way it is.
/// <para>
/// The device program for a Skia runtime effect is built at <i>draw</i> time, from a narrower
/// dialect than <c>SKRuntimeEffect.Create</c> accepts, and a program that fails there draws
/// nothing and reports nothing — the card just comes out transparent. Every offline test passes
/// while that happens, so the only way to know is to draw on a real GPU and read the pixels back.
/// </para>
/// <para>
/// Usage: <c>dotnet run --project tests/GlassGpuProbe</c> writes <c>gpu-probe-log.txt</c> next to
/// the binary and prints the table to stdout. Exit code = number of stages that produced no pixels.
/// </para>
/// </summary>
internal static class Probe
{
    private static readonly string File = Path.Combine(AppContext.BaseDirectory, "gpu-probe-log.txt");
    private static readonly object Gate = new();

    public static int Failures { get; private set; }

    public static void Reset()
    {
        try { System.IO.File.Delete(File); } catch { }
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try { System.IO.File.AppendAllText(File, message + Environment.NewLine); } catch { }
            Console.WriteLine(message);
        }
    }

    // ---------------------------------------------------------------------------------------
    // the battery
    // ---------------------------------------------------------------------------------------

    public static void Run(GRContext grContext)
    {
        var real = LiquidGlassGpuEffect.SourceForDiagnostics;
        using var backdrop = MakeBackdrop(512, 320);
        using var aura = MakeAura(29, 18);
        using var contentChild = backdrop.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
        using var auraChild = aura.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);

        var crisp = Params(LiquidGlassSettingsReference.Crisp, auraEnabled: false);
        var soft = Params(LiquidGlassSettingsReference.Soft, auraEnabled: true);
        var softNoAura = Params(LiquidGlassSettingsReference.Soft, auraEnabled: false);

        Write("=== liquid-glass GPU device-program acceptance ===");
        Write($"expanded source: {real.Length} chars");
        ReportChildSampling();

        var stages = new List<(string Label, string Source, LiquidGlassGpuEffect.Params Uniforms, bool ExpectEmpty)>
        {
            // ---- the material that ships ----
            ("00 real shader, crisp recipe", real, crisp, false),
            ("01 real shader, soft recipe + dye grid", real, soft, false),
            ("02 real shader, soft recipe without dye grid", real, softNoAura, false),
            ("03 real shader, lens switched off", real, Params(LiquidGlassSettingsReference.NoLens, false), false),
            ("04 real shader, 1x1 card", real, crisp with { Width = 1, Height = 1, Radius = 0.5f }, false),

            // ---- the expansion itself is the fix; these prove the pieces are sound ----
            ("10 expanded source still compiles as a whole", real, crisp, false),
            ("11 helpers only (entry point replaced by a constant)", HelpersOnly(real), crisp, false),
            ("12 uniform block only", UniformsOnly(real), crisp, false),
            ("13 trivial", "half4 main(float2 p) { return half4(0.2, 0.8, 0.4, 1.0); }", crisp, false),

            // ---- why the fetches are written out in main: Skia's inliner rule.
            //      The three marked stages MUST come back empty: they are the failure mode the
            //      material shipped with, kept here so the rule cannot silently regress. ----
            ("20 1-tap helper, 40 call sites (always inlined)", Raw(OneTap(40)), crisp, false),
            ("21 4-tap helper, 1 call site (inlined)", Raw(Taps(1)), crisp, false),
            ("22 4-tap helper, 2 call sites (NOT inlined -> must be empty)", Raw(Taps(2)), crisp, true),
            ("23 12-tap helper, 1 call site (inlined)", Raw(MultiTap(3, 1)), crisp, false),
            ("24 12-tap helper, 2 call sites (NOT inlined -> must be empty)", Raw(MultiTap(3, 2)), crisp, true),
            ("25 helper with an early return, sampling (NOT inlined -> must be empty)", Raw(
                "uniform shader content;\n" +
                "float3 h(float2 p, float k) { if (k > 1e-5) return float3(sample(content, p).rgb); return float3(0.5); }\n" +
                "half4 main(float2 p) { return half4(half3(h(p, p.x)), 1.0); }"), crisp, true),

            // ---- the dialect itself is fine ----
            ("40 fract / atan / pow / sin / cos / sqrt", Raw(Const(
                "float a = fract(2.5) + atan(0.75 / max(0.5, 1e-6)) + pow(0.6, 1.4)\n" +
                "        + sin(1.0) * cos(1.0) + sqrt(length(float2(3.0, 4.0)));\n" +
                "  return half4(half3(float3(a * 0.1)), 1.0);")), crisp, false),
            ("41 uninitialised decl then if/else chain", Raw(Const(
                "float h = 3.5;\n  float3 rgb;\n  if (h < 1.0) rgb = float3(1.0, 0.0, 0.0);\n" +
                "  else if (h < 3.0) rgb = float3(0.0, 1.0, 0.0);\n  else rgb = float3(0.2, 0.3, 0.4);\n" +
                "  return half4(half3(rgb), 1.0);")), crisp, false),
            ("42 scalar/vector min/max/mix/clamp", Raw(Const(
                "float2 q = max(float2(2.0, -3.0), 0.0);\n  float3 c = min(float3(300.0, -20.0, 128.0) / 255.0, 1.0);\n" +
                "  float m = mix(255.0, 40.0, 0.3);\n  return half4(half3(c), 1.0) + half4(half(m / 255.0));")), crisp, false),
        };

        foreach (var (label, source, uniforms, expectEmpty) in stages)
            Measure(label, source, contentChild, auraChild, uniforms, expectEmpty, grContext);

        Compare.Run(grContext);
        Perf.Run();
    }

    private static LiquidGlassGpuEffect.Params Params(LiquidGlassSettings glass, bool auraEnabled)
    {
        var theme = new Theme(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass, LiquidGlass: glass);
        var frame = new LiquidGlassRenderer.Frame(512, 320, 1f, 26f, 100f, 100f, 2560f, 1440f,
            0f, 0f, 2560f, 1440f, theme, false, SettingsSurface: false, PixelScale: 1f, Columns: 2, Rows: 2);
        return LiquidGlassGpuEffect.BuildParams(frame, 0.67f, 512f, 320f)
               with { AuraEnabled = auraEnabled, AuraColumns = 29, AuraRows = 18, AuraStep = 11f, AuraMargin = 11f };
    }

    private static void Measure(string label, string source, SKShader content, SKShader aura,
        LiquidGlassGpuEffect.Params parameters, bool expectEmpty, GRContext grContext)
    {
        SKRuntimeEffect? effect;
        try
        {
            effect = SKRuntimeEffect.Create(source, out var errors);
            if (effect == null)
            {
                Failures++;
                Write($"{label,-58} SKSL COMPILE FAILED :: {errors.Trim()}");
                return;
            }
        }
        catch (Exception ex)
        {
            Failures++;
            Write($"{label,-58} SKSL COMPILE THREW :: {ex.Message}");
            return;
        }

        using (effect)
        {
            SKShader? shader;
            try
            {
                var uniforms = new SKRuntimeEffectUniforms(effect);
                Bind(uniforms, parameters);
                var children = new SKRuntimeEffectChildren(effect);
                foreach (var child in effect.Children)
                    children[child] = child == "aura" ? aura : content;
                shader = effect.ToShader(false, uniforms, children);
            }
            catch (Exception ex)
            {
                Failures++;
                Write($"{label,-58} ToShader THREW :: {ex.Message}");
                return;
            }

            if (shader == null)
            {
                Failures++;
                Write($"{label,-58} ToShader returned NULL");
                return;
            }

            using (shader)
            {
                var info = new SKImageInfo(256, 160, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(grContext, false, info);
                if (surface == null)
                {
                    Failures++;
                    Write($"{label,-58} could not create an offscreen GPU surface");
                    return;
                }

                surface.Canvas.Clear(SKColors.Transparent);
                using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
                    surface.Canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), paint);
                surface.Canvas.Flush();

                using var image = surface.Snapshot();
                using var bitmap = new SKBitmap(info);
                if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
                {
                    Failures++;
                    Write($"{label,-58} READBACK FAILED");
                    return;
                }

                var opaque = 0;
                long alphaSum = 0;
                for (var y = 0; y < info.Height; y += 4)
                for (var x = 0; x < info.Width; x += 4)
                {
                    var a = bitmap.GetPixel(x, y).Alpha;
                    alphaSum += a;
                    if (a > 8) opaque++;
                }

                var total = (info.Width / 4) * (info.Height / 4);
                var centre = bitmap.GetPixel(info.Width / 2, info.Height / 2);
                var drew = opaque > 0;
                if (drew == expectEmpty) Failures++;
                Write($"{label,-58} {(drew ? "OK   " : "EMPTY")} opaque={opaque,4}/{total} " +
                      $"avgAlpha={alphaSum / (double)total:F1} centre={centre}" +
                      (expectEmpty ? "  [expected empty]" : ""));
            }
        }
    }

    /// <summary>Exactly the uniform/child binding <c>LiquidGlassGpuEffect.Create</c> performs.</summary>
    private static void Bind(SKRuntimeEffectUniforms u, LiquidGlassGpuEffect.Params p)
    {
        void Set(string name, SKRuntimeEffectUniform value)
        {
            if (u.Contains(name)) u[name] = value;
        }

        Set("size", new[] { p.Width, p.Height });
        Set("radius", p.Radius);
        Set("destOrigin", new[] { p.DestOriginX, p.DestOriginY });
        Set("destToRender", p.DestToRender);
        Set("srcOrigin", new[] { p.SourceOriginX, p.SourceOriginY });
        Set("srcScale", p.SourceScale);
        Set("lensWidth", p.LensWidth);
        Set("invLens", p.InvLens);
        Set("lensShift", p.LensShift);
        Set("auraWidth", p.AuraWidth);
        Set("invAura", p.InvAura);
        Set("innerSpread", p.InnerSpread);
        Set("dispStrength", p.DispersionStrength);
        Set("unitScale", p.UnitScale);
        Set("soft", p.Soft);
        Set("glowStrength", p.GlowStrength);
        Set("haloWidth", p.HaloWidth);
        Set("spectrumStrength", p.SpectrumStrength);
        Set("ambientWeight", p.AmbientWeight);
        Set("coating", new[] { (float)p.Coating.Red, (float)p.Coating.Green, (float)p.Coating.Blue });
        Set("tint", p.Tint);
        Set("edgeTint", p.EdgeTint);
        Set("highlightFactor", p.HighlightFactor);
        Set("lightDir", new[] { p.LightX, p.LightY });
        Set("light3", new[] { p.Light3X, p.Light3Y, p.Light3Z });
        Set("rimParams", new[] { p.RimLineWidth, p.RimWidth, p.RimBaseLight, p.RimDirLight });
        Set("rimLightScale", p.RimLightScale);
        Set("spreadMult", p.SpreadMult);
        Set("saturation", p.Saturation);
        Set("frost", new[] { p.FrostFloor, p.FrostLift, p.FrostCap });
        Set("auraSize", new[] { Math.Max(1f, p.AuraColumns), Math.Max(1f, p.AuraRows) });
        Set("auraStep", Math.Max(0.001f, p.AuraStep));
        Set("auraMargin", p.AuraMargin);
        Set("auraEnabled", p.AuraEnabled ? 1f : 0f);
    }

    /// <summary>
    /// Does a child shader built with <c>SKShader.CreateImage</c> filter linearly? The material
    /// hand-rolls a four-tap bilinear because <c>SKBitmap.ToShader</c> samples nearest. Confirmed
    /// nearest for every constructor available in SkiaSharp 2.88 — there is no sampling-options
    /// overload — so the taps cannot be collapsed into one <c>sample()</c>. Measured on a raster
    /// canvas: no runtime effect involved, so this is safe to run anywhere.
    /// </summary>
    private static void ReportChildSampling()
    {
        using var bmp = new SKBitmap(new SKImageInfo(4, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var x = 0; x < 4; x++)
            bmp.SetPixel(x, 0, new SKColor((byte)(x * 80), (byte)(x * 80), (byte)(x * 80)));
        bmp.SetImmutable();
        using var image = SKImage.FromBitmap(bmp);

        foreach (var (label, shader) in new (string, SKShader)[]
        {
            ("SKBitmap.ToShader           ", bmp.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)),
            ("SKShader.CreateImage        ", SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp)),
            ("SKShader.CreateImage+matrix ", SKShader.CreateImage(image, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, SKMatrix.CreateIdentity()))
        })
        {
            using (shader)
            {
                using var surface = SKSurface.Create(new SKImageInfo(400, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
                surface.Canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { Shader = shader };
                surface.Canvas.Scale(100f, 1f);
                surface.Canvas.DrawRect(new SKRect(0, 0, 4, 1), paint);

                using var snap = surface.Snapshot();
                using var outBmp = new SKBitmap(new SKImageInfo(400, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
                snap.ReadPixels(outBmp.Info, outBmp.GetPixels(), outBmp.RowBytes, 0, 0);

                var distinct = new HashSet<byte>();
                for (var x = 0; x < 400; x++) distinct.Add(outBmp.GetPixel(x, 0).Red);
                Write($"{label} 4x1 image scaled x100 -> {distinct.Count} distinct value(s) " +
                      $"{(distinct.Count <= 4 ? "=> NEAREST" : "=> LINEAR")}");
            }
        }
    }

    // ---------------------------------------------------------------------------------------
    // source surgery, used to isolate one construct at a time
    // ---------------------------------------------------------------------------------------

    private static string UniformsOnly(string source)
    {
        var cut = source.IndexOf("const float PI", StringComparison.Ordinal);
        return (cut < 0 ? "" : source[..cut]) + "\nhalf4 main(float2 p) { return half4(0.2, 0.8, 0.4, 1.0); }\n";
    }

    private static string HelpersOnly(string source)
    {
        var start = source.IndexOf("half4 main(", StringComparison.Ordinal);
        return start < 0
            ? source
            : source[..start] + "half4 main(float2 p) { return half4(0.2, 0.8, 0.4, 1.0); }\n";
    }

    private static string Const(string body) => "half4 main(float2 p) {\n  " + body + "\n}\n";

    private static string Raw(string source) => source + "\n";

    private static string Taps(int calls)
    {
        var body = new StringBuilder();
        body.Append("uniform shader content;\n");
        body.Append("float3 f(float2 p) {\n");
        body.Append("  float2 b = floor(p);\n  float2 fr = p - b;\n");
        body.Append("  float3 c00 = float3(sample(content, b + float2(0.5)).rgb) * 255.0;\n");
        body.Append("  float3 c10 = float3(sample(content, b + float2(1.5, 0.5)).rgb) * 255.0;\n");
        body.Append("  float3 c01 = float3(sample(content, b + float2(0.5, 1.5)).rgb) * 255.0;\n");
        body.Append("  float3 c11 = float3(sample(content, b + float2(1.5, 1.5)).rgb) * 255.0;\n");
        body.Append("  return mix(mix(c00, c10, fr.x), mix(c01, c11, fr.x), fr.y);\n}\n");
        body.Append("half4 main(float2 p) {\n  float3 acc = float3(0.0);\n");
        for (var i = 0; i < calls; i++) body.Append($"  acc += f(p + float2({i}.0, {i}.0));\n");
        body.Append($"  return half4(half3(acc / 255.0 / {calls}.0), 1.0);\n}}\n");
        return body.ToString();
    }

    private static string OneTap(int calls)
    {
        var body = new StringBuilder();
        body.Append("uniform shader content;\n");
        body.Append("float3 f(float2 p) { return float3(sample(content, p).rgb) * 255.0; }\n");
        body.Append("half4 main(float2 p) {\n  float3 acc = float3(0.0);\n");
        for (var i = 0; i < calls; i++) body.Append($"  acc += f(p + float2({i}.0, {i}.0));\n");
        body.Append($"  return half4(half3(acc / 255.0 / {calls}.0), 1.0);\n}}\n");
        return body.ToString();
    }

    private static string MultiTap(int blocks, int calls)
    {
        var body = new StringBuilder();
        body.Append("uniform shader content;\n");
        body.Append("float3 f(float2 p) {\n  float3 acc = float3(0.0);\n");
        for (var b = 0; b < blocks; b++)
        {
            body.Append($"  {{ float2 q = p + float2({b}.25, {b}.75); float2 bi = floor(q); float2 fr = q - bi;\n");
            body.Append("    float3 a1 = float3(sample(content, bi + float2(0.5)).rgb) * 255.0;\n");
            body.Append("    float3 a2 = float3(sample(content, bi + float2(1.5, 0.5)).rgb) * 255.0;\n");
            body.Append("    float3 a3 = float3(sample(content, bi + float2(0.5, 1.5)).rgb) * 255.0;\n");
            body.Append("    float3 a4 = float3(sample(content, bi + float2(1.5, 1.5)).rgb) * 255.0;\n");
            body.Append("    acc += mix(mix(a1, a2, fr.x), mix(a3, a4, fr.x), fr.y); }\n");
        }
        body.Append($"  return acc / {blocks}.0;\n}}\n");
        body.Append("half4 main(float2 p) {\n  float3 acc = float3(0.0);\n");
        for (var i = 0; i < calls; i++) body.Append($"  acc += f(p + float2({i}.0, {i}.0));\n");
        body.Append($"  return half4(half3(acc / 255.0 / {calls}.0), 1.0);\n}}\n");
        return body.ToString();
    }

    // ---------------------------------------------------------------------------------------
    // inputs
    // ---------------------------------------------------------------------------------------

    private static SKBitmap MakeBackdrop(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(120, 150, 190));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(210, 90, 70);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height / 3f), paint);
        paint.Color = new SKColor(70, 160, 220);
        surface.Canvas.DrawRect(new SKRect(0, height / 3f, width, height * 2f / 3f), paint);
        paint.Color = new SKColor(90, 190, 120);
        surface.Canvas.DrawRect(new SKRect(0, height * 2f / 3f, width, height), paint);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image).Copy();
    }

    private static SKBitmap MakeAura(int columns, int rows)
    {
        var bitmap = new SKBitmap(new SKImageInfo(columns, rows, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(120 + x * 4), (byte)(y * 8), 150));
        bitmap.SetImmutable();
        return bitmap;
    }
}

/// <summary>The optical presets the battery exercises.</summary>
internal static class LiquidGlassSettingsReference
{
    public static LiquidGlassSettings Crisp { get; } = new();
    public static LiquidGlassSettings Soft { get; } = LiquidGlassSettings.SoftGlowPreset;
    public static LiquidGlassSettings NoLens { get; } = new(EdgeWidth: 0);
}
