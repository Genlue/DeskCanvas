using System.Text.RegularExpressions;
using DeskCanvas.Services;

/// <summary>
/// The backdrop fetch in the GPU material must be a bilinear read of ONE texel grid.
/// <para>
/// The shader reads the shared backdrop through four single-texel taps plus a hand-written blend
/// (bitmap child shaders sample nearest — see <see cref="LiquidGlassEffect"/>). The taps are
/// <c>base + 0.5</c> / <c>+1.5</c>, so the blend weight must be <c>coord - 0.5 - base</c> with
/// <c>base = floor(coord - 0.5)</c>: texel <c>k</c> covers <c>[k, k+1)</c> and lives at its centre.
/// </para>
/// <para>
/// 4.0 shipped the taps from that grid but the weight from <c>fract(coord - 0.5)</c>. The
/// reconstructed sample position then jumped by a whole texel at every half-texel boundary —
/// harmless at 背景清晰度 100%, where a texel is a screen pixel, but at 25% one texel spans four
/// pixels, so the jump duplicated backdrop blocks and the glass showed the reported 网格状像素.
/// This check runs the generated SkSL's own numbers and demands continuity, so the two halves of
/// the fetch cannot drift apart again.
/// </para>
/// </summary>
internal static class BackdropSamplingCheck
{
    private static int failures;

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("=== 背景采样网格 (grid-free bilinear fetch) ===");

        var source = LiquidGlassEffect.SourceForDiagnostics;
        var fetch = ParseFetch(source, "col");
        if (fetch == null)
        {
            Check(false,
                "the backdrop fetch is one texel-grid bilinear read (base = floor(coord - 0.5), " +
                "taps at base + 0.5 / + 1.5, weight = coord - 0.5 - base)");
            Console.WriteLine($"{failures} SAMPLING CHECK(S) FAILED");
            return failures;
        }

        Check(true,
            "the backdrop fetch is one texel-grid bilinear read (base = floor(coord - 0.5), " +
            "taps at base + 0.5 / + 1.5, weight = coord - 0.5 - base)");
        Check(!Regex.IsMatch(fetch.WeightExpression, @"\bfract\b"),
            $"the blend weight is not a fract() of a shifted coordinate ({fetch.WeightExpression})");
        Check(fetch.Tap00 == 0.5f && fetch.Tap10 == 1.5f,
            $"the two taps are adjacent texel centres ({fetch.Tap00}, {fetch.Tap10})");

        // The reconstruction the shader performs, evaluated over a sweep that crosses every
        // fractional phase many times.
        var worst = 0f;
        var worstAt = 0f;
        for (var i = 0; i <= 20000; i++)
        {
            var coord = i * 0.0173f;
            var sampled = fetch.Sample(coord);
            var error = MathF.Abs(sampled - coord);
            if (error > worst) { worst = error; worstAt = coord; }
        }
        Check(worst < 1e-3f,
            $"the fetched position equals the requested one everywhere (worst {worst:F4} texel at {worstAt:F3})");

        // 背景清晰度 25%: one texel is four screen pixels, so a half-texel slip is 2 px. The old
        // form slipped a full texel (4 px) twice per texel — the visible grid.
        var clarityScale = (float)(DeskCanvas.Core.Models.Settings.LiquidGlassSettings.MinBackdropClarity / 100.0);
        Check(worst * (1f / clarityScale) < 0.5f,
            $"at {clarityScale:P0} clarity the worst slip stays sub-pixel on screen " +
            $"({worst * (1f / clarityScale):F3} px at {clarityScale:P0})");

        Console.WriteLine(failures == 0 ? "backdrop sampling check passed" : $"{failures} SAMPLING CHECK(S) FAILED");
        return failures;
    }

    private sealed record Fetch(
        float BaseX, float BaseY, float Tap00, float Tap10, float Tap01, float Tap11,
        float WeightX, float WeightY, string WeightExpression)
    {
        /// <summary>The reconstructed texture position for a 1-D coordinate on the tap axis.</summary>
        public float Sample(float coord)
        {
            var b = MathF.Floor(coord - BaseX);
            var f = coord - WeightX - b;
            return b + Tap00 + f * (Tap10 - Tap00);
        }
    }

    private static Fetch? ParseFetch(string source, string target)
    {
        var number = @"(?<n>-?\d+(?:\.\d+)?)";
        var baseMatch = Regex.Match(source,
            $@"float2\s+{target}B\s*=\s*floor\((?<coord>.+?)\s*-\s*float2\((?<bx>{number}),\s*(?<by>{number})\)\);");
        if (!baseMatch.Success) return null;
        var coordExpression = baseMatch.Groups["coord"].Value.Trim();

        float Tap(string name)
        {
            var m = Regex.Match(source,
                $@"{target}{name}\s*=\s*contentTexel\({target}B\s*\+\s*float2\((?<tx>{number}),\s*(?<ty>{number})\)\);");
            return m.Success ? Parse(m.Groups["tx"].Value) : float.NaN;
        }

        var tap00 = Tap("00");
        var tap10 = Tap("10");
        var tap01 = Tap("01");
        var tap11 = Tap("11");
        if (!(tap00 == 0.5f && tap10 == 1.5f && tap01 == 0.5f && tap11 == 1.5f)) return null;

        var weight = Regex.Match(source,
            $@"{target}F\s*=\s*(?<expr>[^;]*)\s*-\s*float2\((?<wx>{number}),\s*(?<wy>{number})\)\s*-\s*{target}B\s*;");
        if (!weight.Success) return null;
        var weightExpression = weight.Groups["expr"].Value.Trim();
        if (weightExpression != coordExpression) return null;

        return new Fetch(
            BaseX: Parse(baseMatch.Groups["bx"].Value),
            BaseY: Parse(baseMatch.Groups["by"].Value),
            Tap00: tap00, Tap10: tap10, Tap01: tap01, Tap11: tap11,
            WeightX: Parse(weight.Groups["wx"].Value),
            WeightY: Parse(weight.Groups["wy"].Value),
            WeightExpression: weightExpression);
    }

    private static float Parse(string value) =>
        float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}");
            return;
        }
        Console.WriteLine($"  PASS: {label}");
    }
}
