using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// Validation for the GPU material (液态玻璃 on the GPU).
/// <para>
/// <b>What can and cannot be checked here.</b> Skia's runtime-effect shaders are GPU-only in this
/// SkiaSharp build: compiling one is safe anywhere, but <i>drawing</i> one on a raster canvas
/// terminates the process with an uncatchable SEHException. That is exactly the trap
/// <see cref="DeskCanvas.Views.Controls.LiquidGlassSurface"/> guards against with its GrContext
/// check, and it is why this file stops at shader construction: it proves the SkSL compiles, that
/// every uniform the C# side binds exists, and that the derived optics are finite and sane. Actual
/// pixels have to be eyeballed on a real GPU.
/// </para>
/// </summary>
internal static class GpuMaterialCheck
{
    /// <summary>An <c>out</c>/<c>inout</c> parameter declaration inside the SkSL source.</summary>
    private static readonly Regex OutParameterPattern =
        new(@"\b(out|inout)\s+(float|half|int|bool)", RegexOptions.Compiled);

    /// <summary>Loops, switch and discard — all outside the dialect the GPU backend accepts.</summary>
    private static readonly Regex LoopOrSwitchPattern =
        new(@"\b(while|do|switch|discard)\b|\bfor\s*\(", RegexOptions.Compiled);

    private static int failures;

    public static int Run()
    {
        Console.WriteLine("=== GPU liquid glass material ===");

        Check(LiquidGlassGpuEffect.IsSupported,
            "optical shader compiles on this Skia" +
            (LiquidGlassGpuEffect.IsSupported ? "" : $" :: {LiquidGlassGpuEffect.CompileError}"));

        if (!LiquidGlassGpuEffect.IsSupported)
        {
            Console.WriteLine($"{failures} GPU CHECK(S) FAILED");
            return failures;
        }

        using var backdrop = MakeBackdrop(640, 400);
        using var aura = MakeAura(29, 18);

        // --- crisp (iOS reference) recipe: the historic factory optics, pinned explicitly
        // because the shipped defaults now sit at the heavy-blur end of the range -------------
        var crispTheme = Material(OpticsProfile.CrispReference);
        var crispFrame = Frame(crispTheme, 400, 260);
        var crisp = LiquidGlassGpuEffect.BuildParams(crispFrame, 0.67f);

        CheckAllFinite(crisp, "crisp optics uniforms are finite");
        Check(crisp.Soft == 0f, "crisp recipe does not take the soft branch");
        Check(crisp.GlowStrength == 0f, "crisp recipe has no halo");
        Check(Math.Abs(crisp.DispersionStrength - 0.18f) < 1e-6, "dispersion maps to 0.18 at the iOS reference");
        Check(crisp.LensWidth > 0f && crisp.LensShift > 0f, "crisp recipe has a real meniscus lens");
        Check(Math.Abs(crisp.InvLens - 1f / crisp.LensWidth) < 1e-4, "invLens is the reciprocal of the lens width");
        Check(Math.Abs(crisp.HighlightFactor - 1.0) < 1e-6, "highlight 65 reproduces the measured iOS material");
        Check(crisp.Radius > 0f, "corner radius reaches the shader");
        Check(crisp.SourceScale > 0f, "backdrop scale is positive");

        // --- soft recipe: the merged theme's 柔光 controls ---------------------------------
        var softTheme = Material(LiquidGlassSettings.SoftGlowPreset);
        var softFrame = Frame(softTheme, 400, 260);
        var soft = LiquidGlassGpuEffect.BuildParams(softFrame, 0.67f);

        CheckAllFinite(soft, "soft optics uniforms are finite");
        Check(soft.Soft == 1f, "Glow > 0 selects the soft recipe");
        Check(Math.Abs(soft.GlowStrength - 0.70f) < 1e-6, "halo strength follows the Glow knob");
        Check(Math.Abs(soft.SpectrumStrength - 0.62f) < 1e-6, "spectrum strength follows the Spectrum knob");
        Check(soft.AuraWidth >= soft.LensWidth, "the dye band is never narrower than the lens");
        Check(soft.RimWidth > soft.RimLineWidth, "the soft recipe widens the rim stroke into a halo");
        Check(soft.InnerSpread > 0f, "the interior dye wash has a width");

        // --- the lens can be switched off entirely (EdgeWidth = 0) -------------------------
        var noLens = LiquidGlassGpuEffect.BuildParams(
            Frame(Material(new LiquidGlassSettings(EdgeWidth: 0)), 400, 260), 0.67f);
        CheckAllFinite(noLens, "lens-free optics are finite (no division by zero)");
        Check(noLens.LensWidth == 0f && noLens.InvLens == 0f, "EdgeWidth 0 removes the lens cleanly");

        // --- binding every uniform must actually succeed ------------------------------------
        var crispShader = LiquidGlassGpuEffect.Create(backdrop, null, crisp);
        Check(crispShader != null, "crisp shader binds all uniforms and children");
        crispShader?.Dispose();

        var softShader = LiquidGlassGpuEffect.Create(backdrop, aura,
            soft with { AuraEnabled = true, AuraColumns = 29, AuraRows = 18, AuraStep = 11f, AuraMargin = 11f });
        Check(softShader != null, "soft shader binds the dye-grid child");
        softShader?.Dispose();

        // A missing aura must degrade rather than throw: the soft recipe then dyes from the
        // backdrop pixel, which is exactly the crisp dye behaviour.
        var noAuraShader = LiquidGlassGpuEffect.Create(backdrop, null, soft);
        Check(noAuraShader != null, "soft shader survives a missing dye grid");
        noAuraShader?.Dispose();

        // The shader's coordinates come from the canvas (device independent pixels) while the whole
        // optical model is in render pixels, so the conversion must be supplied and must be exact.
        // Getting this wrong is silent: the rounded-rect field and the lens simply scale with the
        // display's DPI factor.
        Check(Math.Abs(crisp.DestToRender - 1f) < 1e-6,
            "with no destination size the render-px conversion is the identity");
        var scaled = LiquidGlassGpuEffect.BuildParams(Frame(crispTheme, 640, 400), 1f, 320f, 200f);
        Check(Math.Abs(scaled.DestToRender - 2f) < 1e-6,
            $"a half-size destination scales shader coordinates by 2 (got {scaled.DestToRender:F3})");
        var offset = scaled with { DestOriginX = 12f, DestOriginY = 34f };
        CheckAllFinite(offset, "a destination origin does not produce non-finite uniforms");

        // Degenerate geometry must not produce NaN uniforms that would poison the shader.
        CheckAllFinite(LiquidGlassGpuEffect.BuildParams(Frame(crispTheme, 1, 1), 1f), "1x1 card optics are finite");
        CheckAllFinite(LiquidGlassGpuEffect.BuildParams(Frame(crispTheme, 24, 900), 1f), "extreme aspect card optics are finite");

        // The 对齐 offset is baked into the backdrop by DrawWallpaper (which shifts the image by
        // -offset), so the shader's card origin must not add it a second time — that would apply
        // the user's calibration twice and make the alignment dialog impossible to satisfy.
        var alignedTheme = Material(new LiquidGlassSettings(WallpaperOffsetX: 40, WallpaperOffsetY: -25));
        var aligned = LiquidGlassGpuEffect.BuildParams(Frame(alignedTheme, 400, 260), 0.67f);
        Check(Math.Abs(aligned.SourceOriginX - crisp.SourceOriginX) < 1e-6 &&
              Math.Abs(aligned.SourceOriginY - crisp.SourceOriginY) < 1e-6,
            "the wallpaper alignment offset is not applied twice");

        // The backdrop's resolution must not move *where* the material samples from. The shader
        // multiplies its whole render-pixel sum by srcScale exactly once, so the origin handed to it
        // has to stay in render pixels at every scale. Pre-scaling it here squares the factor and
        // slides the sample point across the wallpaper — which is invisible while the backdrop is
        // built at native resolution (srcScale == 1) and glaring the moment 背景清晰度 lowers it, so
        // it is asserted across the whole range the setting can produce.
        var originFrame = Frame(crispTheme, 400, 260);
        var atFull = LiquidGlassGpuEffect.BuildParams(originFrame, 1.0f);
        var atDefault = LiquidGlassGpuEffect.BuildParams(originFrame, 0.5f);
        var atFloor = LiquidGlassGpuEffect.BuildParams(originFrame, (float)(LiquidGlassSettings.MinBackdropClarity / 100.0));
        Check(Math.Abs(atFull.SourceOriginX - atDefault.SourceOriginX) < 1e-6 &&
              Math.Abs(atFull.SourceOriginY - atDefault.SourceOriginY) < 1e-6 &&
              Math.Abs(atFull.SourceOriginX - atFloor.SourceOriginX) < 1e-6 &&
              Math.Abs(atFull.SourceOriginY - atFloor.SourceOriginY) < 1e-6,
            "the backdrop scale does not move the sampling origin (100% / 50% / floor agree)");
        Check(Math.Abs(atFull.SourceOriginX - originFrame.DesktopX) < 1e-6 &&
              Math.Abs(atFull.SourceOriginY - originFrame.DesktopY) < 1e-6,
            "the sampling origin is the card's desktop position in render pixels, unscaled");
        Check(atFull.SourceScale == 1.0f && Math.Abs(atDefault.SourceScale - 0.5f) < 1e-6 &&
              atDefault.SourceScale < atFull.SourceScale,
            "the backdrop scale reaches the shader (100% -> 1.0, 50% -> 0.5)");

        // Bitmap shaders here sample nearest, so the material interpolates its own lookups. Losing
        // that would show up as stair-stepping around the lens and a blocky backdrop.
        var gpuSourcePath = Path.GetFullPath(@"src/DeskCanvas/Services/LiquidGlassGpuEffect.cs");
        var gpuSource = File.Exists(gpuSourcePath) ? File.ReadAllText(gpuSourcePath) : "";
        Check(gpuSource.Contains("contentTexel") && gpuSource.Contains("auraTexel"),
            "the GPU material interpolates its backdrop and dye-grid lookups by hand");

        // The coating is mixed into a colour that is already in 0-255 units (the CPU renderer's
        // Channel(value, coat) does exactly the same), so normalising it to 0-1 makes it contribute
        // ~255x too little and the tint collapses into a plain darkening — the card then looks like
        // it is under a black mask whatever 纯色 background is configured, and worse the higher
        // 不透明度 is. Asserted at source level because it is a units mistake, not a pixel one.
        Check(!gpuSource.Contains("Coating.Red / 255f") && gpuSource.Contains("(float)p.Coating.Red"),
            "the coating reaches the shader in 0-255 units, like the CPU renderer's Channel(value, coat)");

        // --- the dialect the GPU backend actually accepts ------------------------------------
        // The compile check above only proves SkSL -> IR. The *device* program is built later, from
        // a narrower ES2-compatible dialect, and a program that fails there draws nothing at all
        // rather than reporting an error — the card simply renders transparent. That failure is
        // invisible to every offline test, so the forbidden constructs are asserted at source level.
        // This was found the hard way: an `out`-parameter helper passed SKRuntimeEffect.Create and
        // then silently produced an empty card on a real GPU.
        var skSL = StripSkSLComments(LiquidGlassGpuEffect.SourceForDiagnostics);
        Check(!OutParameterPattern.IsMatch(skSL),
            "the optical shader declares no out/inout parameters (unbuildable on the GPU backend)");
        Check(!LoopOrSwitchPattern.IsMatch(skSL),
            "the optical shader has no loops, switch or discard (outside the GPU backend's dialect)");

        var mainStart = skSL.IndexOf("half4 main(", StringComparison.Ordinal);
        Check(mainStart >= 0, "the shader declares an entry point");
        if (mainStart >= 0)
        {
            var mainBody = skSL[mainStart..];
            var returns = Regex.Matches(mainBody, @"\breturn\b").Count;
            Check(returns == 1, $"main has exactly one return, at the end (found {returns})");
        }

        // --- child-shader sampling must only happen where the inliner is forced -----------------
        // A child shader is sampled by emitting a call to the child's fragment-processor chain, and
        // Skia builds that call with the *entry point's* input parameter. If the call survives
        // inside a real function the device program references `_input` where it does not exist,
        // fails to build, and draws a transparent card — the defect this material shipped with.
        //
        // Measured against a real ANGLE/EGL device (tests/GlassGpuProbe, which drives a window and
        // reads pixels back): a helper holding a single sample() is inlined even at forty call
        // sites, while a helper holding four is NOT inlined once it has two call sites. So the
        // material only ever samples through the one-tap texel lookups, and every other fetch is
        // expanded straight into main by the @fetch template.
        Check(!LiquidGlassGpuEffect.SourceForDiagnostics.Contains("@fetch", StringComparison.Ordinal),
            "every @fetch placeholder is expanded before the shader is compiled");

        var functions = ExtractFunctions(skSL);
        Check(functions.ContainsKey("main"), "the function scan finds the entry point");
        foreach (var (name, body) in functions)
        {
            var samples = Regex.Matches(body, @"\bsample\s*\(").Count;
            if (samples == 0) continue;
            if (name == "main")
            {
                Check(true, "main samples its child shaders directly (always safe: main is never inlined)");
                continue;
            }

            Check(name is "contentTexel" or "auraTexel",
                $"only the one-tap texel helpers sample a child shader (found sample() in {name})");
            Check(samples == 1,
                $"{name} holds exactly one sample() so the inliner always takes it (found {samples})");
        }

        Check(functions.TryGetValue("main", out var entry) &&
              Regex.Matches(entry, @"\bsample\s*\(").Count == 0,
            "main reads the backdrop through the texel helpers, never by calling sample() itself");

        // The expansion has to cover every fetch, not just the easy call sites: the crisp recipe
        // needs three bilinear lookups (twelve texel taps) and the soft recipe nine (thirty-six).
        var fullSource = LiquidGlassGpuEffect.SourceForDiagnostics;
        var texelLookups = Regex.Matches(fullSource, @"contentTexel\(").Count;
        Check(fullSource.Contains("@fetch", StringComparison.Ordinal) == false && texelLookups >= 36,
            $"all backdrop fetches are expanded into the entry point ({texelLookups} texel lookups)");

        // Every child a shader declares must be bound at draw time, or the program fails to build.
        Check(!skSL.Contains("uniform shader aura") || gpuSource.Contains("children[\"aura\"]"),
            "every declared child shader is bound when the material is created");

        // --- the GrContext guard must remain in place ---------------------------------------
        // Drawing a runtime-effect shader without a GPU context kills the process outright, so
        // the draw operation has to ask the lease for one before it draws. Avalonia.Skia is not
        // referenced here, so the guard is asserted at the source level.
        var surfaceSource = Path.GetFullPath(@"src/DeskCanvas/Views/Controls/LiquidGlassSurface.cs");
        var text = File.Exists(surfaceSource) ? File.ReadAllText(surfaceSource) : "";
        Check(text.Contains("lease.GrContext == null"),
            "software-backend guard is present in the glass draw operation");
        Check(text.IndexOf("GrContext == null", StringComparison.Ordinal)
              < text.IndexOf("LiquidGlassGpuEffect.Create", StringComparison.Ordinal),
            "the guard runs before the runtime shader is created");
        Check(!text.Contains("gpuBitmap"),
            "the unused CPU-rendered GPU bitmap path is gone");

        Console.WriteLine(failures == 0 ? "All GPU material checks passed." : $"{failures} GPU CHECK(S) FAILED");
        return failures;
    }

    private static Theme Material(LiquidGlassSettings glass) =>
        new(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass, LiquidGlass: glass);

    private static LiquidGlassRenderer.Frame Frame(Theme theme, int width, int height) =>
        new(width, height, 1f, 26f, 100f, 100f, 2560f, 1440f, 0f, 0f, 2560f, 1440f, theme, false,
            SettingsSurface: false, PixelScale: 1f, Columns: 2, Rows: 2);

    /// <summary>Every float the shader receives must be finite: one NaN turns a whole card black.</summary>
    private static void CheckAllFinite(LiquidGlassGpuEffect.Params p, string label)
    {
        foreach (var field in typeof(LiquidGlassGpuEffect.Params).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.PropertyType != typeof(float)) continue;
            var value = (float)field.GetValue(p)!;
            if (!float.IsFinite(value))
            {
                Check(false, $"{label} ({field.Name} = {value})");
                return;
            }
        }
        Check(true, label);
    }

    private static SKBitmap MakeBackdrop(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(new SKColor(30, 34, 44));
        using var paint = new SKPaint { IsAntialias = true };
        paint.Color = new SKColor(210, 70, 60);
        surface.Canvas.DrawRect(new SKRect(0, 0, width, height / 3f), paint);
        paint.Color = new SKColor(60, 140, 210);
        surface.Canvas.DrawRect(new SKRect(0, height / 3f, width, height * 2f / 3f), paint);
        paint.Color = new SKColor(80, 180, 110);
        surface.Canvas.DrawRect(new SKRect(0, height * 2f / 3f, width, height), paint);
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image).Copy();
    }

    private static SKBitmap MakeAura(int columns, int rows)
    {
        var bitmap = new SKBitmap(new SKImageInfo(columns, rows, SKColorType.Bgra8888, SKAlphaType.Opaque));
        for (var y = 0; y < rows; y++)
        for (var x = 0; x < columns; x++)
            bitmap.SetPixel(x, y, new SKColor((byte)(x * 8), (byte)(y * 12), 90));
        bitmap.SetImmutable();
        return bitmap;
    }

    /// <summary>
    /// The SkSL with its comments removed. The checks below look for forbidden *code* constructs,
    /// and the source is heavily commented in English — "return", "for" and "do" all appear in
    /// prose, which would otherwise trip them.
    /// </summary>
    private static string StripSkSLComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
    }

    /// <summary>
    /// Every top-level function in the shader, mapped to its body text. Used to find which
    /// functions sample a child shader, because that is the property the device backend turns into
    /// a build failure.
    /// </summary>
    private static Dictionary<string, string> ExtractFunctions(string skSL)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var declaration = new Regex(@"(?m)^[ \t]*(?:half4|float3|float2|float|half|void)\s+(\w+)\s*\([^)]*\)\s*\{",
            RegexOptions.Compiled);
        foreach (Match match in declaration.Matches(skSL))
        {
            var open = skSL.IndexOf('{', match.Index);
            var depth = 0;
            var i = open;
            for (; i < skSL.Length; i++)
            {
                if (skSL[i] == '{') depth++;
                else if (skSL[i] == '}' && --depth == 0) break;
            }
            if (i >= skSL.Length) break;
            result[match.Groups[1].Value] = skSL[(open + 1)..i];
        }
        return result;
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}");
        }
        else
        {
            Console.WriteLine($"  PASS: {label}");
        }
    }
}
