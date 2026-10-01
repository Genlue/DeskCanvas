using System.Text.RegularExpressions;
using DeskCanvas.Services;

/// <summary>
/// Validation for the 新液态玻璃 material (<see cref="DeskCanvas.Services.LiquidGlassV2Effect"/>).
/// <para>
/// The GPU SkSL can only be compile-checked headless (see <see cref="GpuMaterialCheck"/> for why
/// drawing a runtime shader here is out of scope), but the part that decides whether the rim
/// shows a hard seam at the corners — the refraction <i>direction field</i> — is shared verbatim
/// with the CPU twin (<see cref="DeskCanvas.Services.LiquidGlassV2Renderer.GradSd"/>) and can be
/// walked numerically. The library's field hard-switches the axis along the card diagonals and
/// collapses to a 45° spike exactly on the arc-centre lines; wherever the refraction band reaches
/// that deep — band wider than ~1.2× the corner radius, i.e. the factory optics on any
/// small-radius card — both sliced hard seams into the rim at all four corners. The restructured
/// field reflects the radial across the diagonal; these checks pin its continuity and its
/// edge-normal anchors.
/// </para>
/// </summary>
internal static class LiquidGlassV2Check
{
    /// <summary>SkSL line comments, stripped before the dialect patterns run.</summary>
    private static readonly Regex LineCommentPattern = new(@"//[^\n]*", RegexOptions.Compiled);

    /// <summary>An <c>out</c>/<c>inout</c> parameter declaration inside the SkSL source.</summary>
    private static readonly Regex OutParameterPattern =
        new(@"\b(out|inout)\s+(float|half|int|bool)", RegexOptions.Compiled);

    /// <summary>Loops, switch and discard — all outside the dialect the GPU backend accepts.</summary>
    private static readonly Regex LoopOrSwitchPattern =
        new(@"\b(while|do|switch|discard)\b|\bfor\s*\(", RegexOptions.Compiled);

    private const float Half = 140f;   // half of the 280×280 test card, render px
    private const float GradRadius = 30f; // min(1.5 × the 20 px corner radius, half size)

    private static int failures;

    public static int Run()
    {
        Console.WriteLine("=== 新液态玻璃 (LiquidGlassV2) ===");

        Check(LiquidGlassV2Effect.IsSupported,
            "optical shader compiles on this Skia" +
            (LiquidGlassV2Effect.IsSupported ? "" : $" :: {LiquidGlassV2Effect.CompileError}"));
        if (LiquidGlassV2Effect.IsSupported)
        {
            // The dialect patterns are about language constructs — scan the code, not the
            // comments (which may legitimately name the constructs they replaced).
            var source = LineCommentPattern.Replace(LiquidGlassV2Effect.SourceForDiagnostics, "");
            Check(!OutParameterPattern.IsMatch(source), "the optical shader declares no out/inout parameters");
            Check(!LoopOrSwitchPattern.IsMatch(source), "the optical shader has no loops, switch or discard");
        }

        CheckCornerContinuity();

        Console.WriteLine(failures == 0
            ? "All 新液态玻璃 checks passed."
            : $"{failures} 新液态玻璃 CHECK(S) FAILED");
        return failures;
    }

    /// <summary>
    /// The refraction direction field must stay continuous through the card diagonals and the
    /// inflated arc-centre lines, or the lens shows a hard mirrored seam per corner; and it must
    /// keep the library's edge normals where the deep band hugs an edge.
    /// </summary>
    private static void CheckCornerContinuity()
    {
        // Across the card diagonal, 50 px inside the inflated arc-centre point: the library's
        // hard axis switch turned the direction 90° in a single step here.
        var diagonal = MaxTurnPerStep(t => (-60f + t, -60f - t), -20f, 20f);
        Console.WriteLine($"Diagonal: max direction turn {diagonal:F2}° per 0.5 px");
        Check(diagonal < 5f, "corner: the refraction direction is continuous across the card diagonal");

        // Across the vertical and horizontal arc-centre lines (deep-interior ↔ strip
        // transition): continuous in the library's field too — pin it so it stays so.
        var stripX = MaxTurnPerStep(t => (t, -40f), -130f, -90f);
        var stripY = MaxTurnPerStep(t => (-40f, t), -130f, -90f);
        Console.WriteLine($"Strip: max direction turn {MathF.Max(stripX, stripY):F2}° per 0.5 px");
        Check(MathF.Max(stripX, stripY) < 5f,
            "corner: the refraction direction is continuous across the arc-centre lines");

        // The blend keeps the library's edge normals where the deep band hugs an edge.
        var left = LiquidGlassV2Renderer.GradSd(-105f, -40f, Half, Half, GradRadius);
        var top = LiquidGlassV2Renderer.GradSd(-40f, -105f, Half, Half, GradRadius);
        Check(MathF.Abs(left.X + 1f) < 0.05f && MathF.Abs(left.Y) < 0.1f,
            "corner: deep field stays the left-edge normal near the left edge");
        Check(MathF.Abs(top.Y + 1f) < 0.05f && MathF.Abs(top.X) < 0.1f,
            "corner: deep field stays the top-edge normal near the top edge");
    }

    /// <summary>Worst turn (degrees) between consecutive field samples along a walk.</summary>
    private static float MaxTurnPerStep(Func<float, (float X, float Y)> point, float from, float to)
    {
        var maxTurn = 0f;
        (float X, float Y)? previous = null;
        for (var t = from; t <= to; t += 0.5f)
        {
            var (x, y) = point(t);
            var grad = LiquidGlassV2Renderer.GradSd(x, y, Half, Half, GradRadius);
            if (previous is { } p)
            {
                var dot = Math.Clamp(grad.X * p.X + grad.Y * p.Y, -1f, 1f);
                maxTurn = MathF.Max(maxTurn, MathF.Acos(dot) * 180 / MathF.PI);
            }
            previous = grad;
        }
        return maxTurn;
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {label}");
        if (!condition) failures++;
    }
}
