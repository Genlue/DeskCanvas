using System.Reflection;
using System.Text.RegularExpressions;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// Validation for the GPU material (LiquidGlass on the GPU via LiquidGlassEffect).
/// Checks shader compilation, uniformity, finiteness, absence of invalid dialects, and GrContext safety.
/// </summary>
internal static class GpuMaterialCheck
{
    private static readonly Regex OutParameterPattern =
        new(@"\b(out|inout)\s+(float|half|int|bool)", RegexOptions.Compiled);

    private static readonly Regex LoopOrSwitchPattern =
        new(@"\b(while|do|switch|discard)\b|\bfor\s*\(", RegexOptions.Compiled);

    private static int failures;

    public static int Run()
    {
        Console.WriteLine("=== GPU liquid glass material ===");

        Check(LiquidGlassEffect.IsSupported,
            "optical shader compiles on this Skia" +
            (LiquidGlassEffect.IsSupported ? "" : $" :: {LiquidGlassEffect.CompileError}"));

        if (!LiquidGlassEffect.IsSupported)
        {
            Console.WriteLine($"{failures} GPU CHECK(S) FAILED");
            return failures;
        }

        using var backdrop = MakeBackdrop(640, 400);

        // Standard optics
        var defaultTheme = Material(new LiquidGlassSettings());
        var defaultFrame = Frame(defaultTheme, 400, 260);
        var p = LiquidGlassEffect.BuildParams(defaultFrame, 0.67f);

        CheckAllFinite(p, "liquid glass optics uniforms are finite");
        Check(p.Radius > 0f, "corner radius reaches the shader");
        Check(p.SourceScale > 0f, "backdrop scale is positive");
        Check(p.LightX != 0f || p.LightY != 0f, "light direction vector is non-zero");

        // Highlight angle dynamic uniform verification
        var angle45Theme = Material(new LiquidGlassSettings(HighlightAngle: 45.0));
        var p45 = LiquidGlassEffect.BuildParams(Frame(angle45Theme, 400, 260), 1.0f);
        CheckAllFinite(p45, "highlight angle 45.0 uniforms are finite");
        var expectedCos = MathF.Cos(45f * MathF.PI / 180f);
        var expectedSin = MathF.Sin(45f * MathF.PI / 180f);
        Check(MathF.Abs(p45.LightX - expectedCos) < 1e-4f && MathF.Abs(p45.LightY - expectedSin) < 1e-4f,
            $"highlight angle 45° produces unit vector ({expectedCos:F3}, {expectedSin:F3})");

        // Degenerate geometry must not produce NaN uniforms
        CheckAllFinite(LiquidGlassEffect.BuildParams(Frame(defaultTheme, 1, 1), 1f), "1x1 card optics are finite");
        CheckAllFinite(LiquidGlassEffect.BuildParams(Frame(defaultTheme, 24, 900), 1f), "extreme aspect card optics are finite");

        // Create runtime shader
        using var shader = LiquidGlassEffect.Create(backdrop, p);
        Check(shader != null, "runtime shader created successfully with valid uniforms");

        // Check dialect safety
        var skSL = StripSkSLComments(LiquidGlassEffect.SourceForDiagnostics);
        Check(!OutParameterPattern.IsMatch(skSL),
            "the optical shader declares no out/inout parameters");
        Check(!LoopOrSwitchPattern.IsMatch(skSL),
            "the optical shader has no loops, switch or discard");

        var mainStart = skSL.IndexOf("half4 main(", StringComparison.Ordinal);
        Check(mainStart >= 0, "the shader declares main entry point");

        // Check GrContext guard in LiquidGlassSurface.cs
        var surfaceSource = Path.GetFullPath(@"src/DeskCanvas/Views/Controls/LiquidGlassSurface.cs");
        var text = File.Exists(surfaceSource) ? File.ReadAllText(surfaceSource) : "";
        Check(text.Contains("lease.GrContext == null"),
            "software-backend guard is present in the glass draw operation");
        Check(text.Contains("LiquidGlassEffect.Create"),
            "LiquidGlassEffect.Create is used in LiquidGlassSurface");

        Console.WriteLine(failures == 0 ? "All GPU material checks passed." : $"{failures} GPU CHECK(S) FAILED");
        return failures;
    }

    private static Theme Material(LiquidGlassSettings glass) =>
        new(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass, LiquidGlass: glass);

    private static LiquidGlassRenderer.Frame Frame(Theme theme, int width, int height) =>
        new(width, height, 1f, 26f, 100f, 100f, 2560f, 1440f, 0f, 0f, 2560f, 1440f, theme, false,
            SettingsSurface: false, PixelScale: 1f, Columns: 2, Rows: 2);

    private static void CheckAllFinite(LiquidGlassEffect.Params p, string label)
    {
        foreach (var field in typeof(LiquidGlassEffect.Params).GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (field.FieldType != typeof(float)) continue;
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

    private static string StripSkSLComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
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
