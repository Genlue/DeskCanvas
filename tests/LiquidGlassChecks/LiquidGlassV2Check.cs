using System.Text.Json;
using System.Text.RegularExpressions;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using SkiaSharp;

/// <summary>
/// Validation for the 新液态玻璃 material (<see cref="DeskCanvas.Services.LiquidGlassV2Effect"/>).
/// <para>
/// The GPU SkSL can only be compile-checked headless (see <see cref="GpuMaterialCheck"/> for why
/// drawing a runtime shader here is out of scope), so three things carry the corner-defect
/// regression: the refraction <i>direction field</i>, which is shared verbatim with the CPU twin
/// (<see cref="DeskCanvas.Services.LiquidGlassV2Renderer.GradSd"/>) and can be walked numerically;
/// the shared coefficient solver (<see cref="LiquidGlassV2Lens"/>), which both render paths call;
/// and the structure of the SkSL itself, which is checked for the seam-causing patterns rather
/// than drawn.
/// </para>
/// <para>
/// The defect being pinned: the library's field hard-switches the axis along the card diagonals,
/// collapses both components to the diagonal exactly on the arc-centre lines, and tests its
/// corner sector with <c>&gt;</c> so the lines <c>q.x = 0</c> / <c>q.y = 0</c> answer the wrong
/// axis — a 90° point value sitting on a seam. Wherever the refraction band reached that deep
/// (band wider than ~0.8× the corner radius on any large card, the factory optics included) the
/// corners showed hard seams, spikes and squeeze points. The restructured field plus the
/// corner-capped band remove all three; these checks pin the field's continuity, its edge-normal
/// anchors, the cap's contract, and the fact that the arc centre — the field's single point with
/// no unique limit — is left in the zero-displacement region.
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

    private const float Half = 140f;      // half of the 280×280 test card, render px
    private const float CornerRadius = 20f;
    private const float GradRadius = 30f; // min(1.5 × the 20 px corner radius, half size)

    /// <summary>The arc-centre lines: the corner sector of the inflated field starts here.</summary>
    private const float Inner = Half - GradRadius;

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
        CheckDirectionField();
        CheckArcCentreMasked();
        CheckCoefficients();
        CheckSmoothCap();
        CheckShaderTakesSharedCoefficients();
        CheckCpuRender();

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

    /// <summary>Angle between two directions in degrees (both are unit vectors).</summary>
    private static float Turn((float X, float Y) a, (float X, float Y) b)
    {
        var dot = Math.Clamp(a.X * b.X + a.Y * b.Y, -1f, 1f);
        return MathF.Acos(dot) * 180f / MathF.PI;
    }

    /// <summary>
    /// The direction field's own contract, over the whole card: finite, exactly unit, and with no
    /// point value that disagrees with its one-sided limits. The library's corner test used
    /// <c>&gt;</c>, which answered the horizontal strip's normal on <c>q.x = 0</c> where both
    /// sides are the vertical strip's — the 90° spike that sat exactly on the arc-centre lines.
    /// The field is deliberately <i>not</i> continuous at the inflated arc centre (a genuinely
    /// path-dependent point); the cap below is what makes that point unobservable.
    /// </summary>
    private static void CheckDirectionField()
    {
        // On the arc-centre line and its three mirrors: the vertical strip's normal.
        foreach (var (x, y) in new[]
                 {
                     (Inner, Inner + 10f), (-Inner, Inner + 10f),
                     (Inner, -Inner - 10f), (-Inner, -Inner - 10f)
                 })
        {
            var g = LiquidGlassV2Renderer.GradSd(x, y, Half, Half, GradRadius);
            Check(MathF.Abs(g.X) < 1e-3f && MathF.Abs(MathF.Abs(g.Y) - 1f) < 1e-3f,
                $"field on the arc-centre line ({x:F0},{y:F0}) is the strip normal, not the 90°-flipped one");
        }

        // ...and it agrees with both one-sided limits, so there is no spike across that line.
        var below = LiquidGlassV2Renderer.GradSd(Inner - 0.5f, Inner + 10f, Half, Half, GradRadius);
        var on = LiquidGlassV2Renderer.GradSd(Inner, Inner + 10f, Half, Half, GradRadius);
        var above = LiquidGlassV2Renderer.GradSd(Inner + 0.5f, Inner + 10f, Half, Half, GradRadius);
        Console.WriteLine($"Arc-centre line: turn {Turn(on, below):F2}° / {Turn(on, above):F2}° per 0.5 px");
        Check(Turn(on, below) < 5f && Turn(on, above) < 5f,
            "no direction spike across the arc-centre line");

        // The horizontal mirror of the same boundary, to pin the other branch order.
        var cornerSide = LiquidGlassV2Renderer.GradSd(Inner + 10f, Inner, Half, Half, GradRadius);
        Check(MathF.Abs(cornerSide.X - 1f) < 1e-3f && MathF.Abs(cornerSide.Y) < 1e-3f,
            "field on the horizontal arc-centre line is the horizontal strip normal");

        // The inflated arc centres: one per corner, each with no unique limit, resolved by the same
        // max(..., 1e-5) floor the shader uses. It must be finite and unit in both paths.
        foreach (var (x, y) in new[] { (Inner, Inner), (-Inner, Inner), (Inner, -Inner), (-Inner, -Inner) })
        {
            var g = LiquidGlassV2Renderer.GradSd(x, y, Half, Half, GradRadius);
            var length = MathF.Sqrt(g.X * g.X + g.Y * g.Y);
            Check(float.IsFinite(g.X) && float.IsFinite(g.Y) && MathF.Abs(length - 1f) < 1e-4f,
                $"inflated arc centre ({x:F0},{y:F0}) falls back to a finite unit direction");
        }

        // Whole-card sweep: every branch normalizes, so the sweep must be exactly unit — a
        // non-unit sample would mean a branch (or a degenerate special case) leaked a raw vector.
        var nonFinite = 0;
        var worstLength = 0f;
        for (var y = 0f; y <= Half * 2f; y += 2f)
        for (var x = 0f; x <= Half * 2f; x += 2f)
        {
            var g = LiquidGlassV2Renderer.GradSd(x - Half, y - Half, Half, Half, GradRadius);
            if (!float.IsFinite(g.X) || !float.IsFinite(g.Y)) { nonFinite++; continue; }
            worstLength = MathF.Max(worstLength, MathF.Abs(MathF.Sqrt(g.X * g.X + g.Y * g.Y) - 1f));
        }
        Check(nonFinite == 0, "the direction field is finite over the whole card");
        Check(worstLength < 1e-4f,
            $"the direction field is unit over the whole card (worst deviation {worstLength:E2})");
    }

    /// <summary>
    /// The corner defect end to end: the refracted source coordinate as it approaches the inflated
    /// arc centre, walked along eight directions. With the corner-capped band the displacement
    /// there is exactly zero, so every path agrees; with the library's uncapped short-side band it
    /// jumps by more than ten pixels, which is the four-corner squeeze the fix exists for. The
    /// uncapped walk is kept so this check can never pass vacuously.
    /// </summary>
    private static void CheckArcCentreMasked()
    {
        // The report's 4×4 case: L = 352, radius = 24, so the arc centre sits 1.5 × 24 = 36 px
        // inside the outline — nearly twice the 0.8 × 24 = 19.2 px the cap allows.
        const float side = 352f;
        const float radius = 24f;
        var capped = LiquidGlassV2Lens.Compute(100, side, radius);
        var library = new LiquidGlassRefractionLens(
            (float)(LiquidGlassV2Settings.RefractionAmountMaxFrac * side),
            (float)(LiquidGlassV2Settings.RefractionAmountMaxFrac * LiquidGlassV2Settings.BandToAmountRatio * side),
            MathF.Min(radius * (float)LiquidGlassV2Settings.GradRadiusFactor, side / 2f),
            1f);

        var cappedSpread = ArcCentreMapSpread(capped, side, radius);
        var librarySpread = ArcCentreMapSpread(library, side, radius);
        Console.WriteLine($"Arc centre map spread: capped {cappedSpread:F3} px, library {librarySpread:F3} px");
        Check(librarySpread > 5f,
            "the library's uncapped band really does jump at the arc centre (this check is not vacuous)");
        Check(cappedSpread < 0.01f,
            "the corner-capped band makes the sampling map converge at the arc centre");
    }

    /// <summary>
    /// Spread of the <i>displacement</i> over eight directions approaching the inflated arc centre,
    /// in px. The mapped positions themselves still differ by the input separation — what has to
    /// vanish for a path-independent mapping is the difference beyond that, i.e. the spread of the
    /// offsets. Zero means every path refracts the arc centre identically.
    /// </summary>
    private static float ArcCentreMapSpread(LiquidGlassRefractionLens lens, float side, float radius)
    {
        var half = side / 2f;
        // The V2 CPU renderer takes its surface depth from this field, and it agrees with the
        // shader's rounded-rect SDF to the last bit on and inside the card.
        var field = new LiquidGlassRenderer.BevelField((int)side, (int)side, radius);
        var centre = half - lens.GradRadius;

        var minX = float.MaxValue; var maxX = float.MinValue;
        var minY = float.MaxValue; var maxY = float.MinValue;
        foreach (var step in new[] { 0.01f, 0.05f, 0.2f })
        {
            for (var i = 0; i < 8; i++)
            {
                var angle = i * MathF.PI / 4f;
                var cx = centre + step * MathF.Cos(angle);
                var cy = centre + step * MathF.Sin(angle);
                var offset = SampleOffset(field, lens, cx, cy, half);
                minX = MathF.Min(minX, offset.X); maxX = MathF.Max(maxX, offset.X);
                minY = MathF.Min(minY, offset.Y); maxY = MathF.Max(maxY, offset.Y);
            }
        }
        return MathF.Max(maxX - minX, maxY - minY);
    }

    /// <summary>Mirror of the shader's per-pixel displacement, for one centred-coordinate sample.</summary>
    private static (float X, float Y) SampleOffset(LiquidGlassRenderer.BevelField field,
        LiquidGlassRefractionLens lens, float cx, float cy, float half)
    {
        var sdRaw = -field.Evaluate(cx + half, cy + half).Depth;
        var depth01 = lens.Band > 0f ? Math.Clamp(-MathF.Min(sdRaw, 0f) / lens.Band, 0f, 1f) : 1f;
        var lensX = 1f - depth01;
        var d = (1f - MathF.Sqrt(MathF.Max(0f, 1f - lensX * lensX))) * lens.Amount;

        var grad = LiquidGlassV2Renderer.GradSd(cx, cy, half, half, lens.GradRadius);
        var radialLength = MathF.Sqrt(cx * cx + cy * cy);
        var gx = grad.X + lens.DepthEffect * cx / MathF.Max(radialLength, 1e-4f);
        var gy = grad.Y + lens.DepthEffect * cy / MathF.Max(radialLength, 1e-4f);
        var length = MathF.Sqrt(gx * gx + gy * gy);
        if (length > 1e-5f) { gx /= length; gy /= length; }
        else { gx = 0f; gy = 0f; }
        return (d * gx, d * gy);
    }

    /// <summary>
    /// The shared refraction solver both render paths call. The library's amount/band ratio must
    /// survive, the corner cap must bite exactly where the analysis says, everything must stay
    /// finite on degenerate geometry, and the result must be linear in the render-pixel geometry so
    /// a DPI change or a quality downscale cannot re-derive a different corner behaviour.
    /// </summary>
    private static void CheckCoefficients()
    {
        var off = LiquidGlassV2Lens.Compute(0, 352f, 24f);
        Check(off.Amount == 0f && off.Band == 0f, "refraction 0 leaves no lens at all");

        var ratioKept = true;
        var capKept = true;
        var centreMasked = true;
        foreach (var (side, radius) in new[]
                 {
                     (80f, 24f), (160f, 24f), (256f, 24f), (352f, 24f), (480f, 24f),
                     (352f, 8f), (300f, 40f), (120f, 60f), (96f, 48f), (720f, 16f)
                 })
        {
            var lens = LiquidGlassV2Lens.Compute(100, side, radius);
            if (MathF.Abs(lens.Amount - 2f * lens.Band) > 1e-3f) ratioKept = false;
            if (lens.Band > 0.8f * radius + 1e-3f) capKept = false;
            // The cap only has a point because the arc centre is 1.5 × radius deep: keeping the
            // band under that puts the field's one non-smooth point beyond the displacement.
            if (lens.Band >= 1.5f * radius) centreMasked = false;
        }
        Check(ratioKept, "the library's amount = 2 × band ratio survives the cap at every size");
        Check(capKept, "the band never exceeds 0.8 × the corner radius");
        Check(centreMasked, "the band never reaches the 1.5 × radius deep arc centre");

        var big = LiquidGlassV2Lens.Compute(100, 352f, 24f);
        var small = LiquidGlassV2Lens.Compute(100, 80f, 24f);
        Console.WriteLine($"352px r24: amount {big.Amount:F1} / band {big.Band:F1} (library: 140.8 / 70.4); " +
                          $"80px r24: band {small.Band:F1} (library: 16.0)");
        Check(MathF.Abs(big.Band - 0.8f * 24f) < 1e-3f, "a large card's band saturates at 0.8 × radius");
        Check(big.Amount < 0.4f * 352f * 0.5f, "the cap cuts the library's displacement on a large card");
        Check(MathF.Abs(small.Band - 0.2f * 80f) < 1e-3f,
            "below the cap the library's short-side ratio passes through untouched");

        var growsWithSlider = true;
        var previous = LiquidGlassV2Lens.Compute(0, 352f, 24f);
        for (var percent = 5.0; percent <= 100.0; percent += 5.0)
        {
            var lens = LiquidGlassV2Lens.Compute(percent, 352f, 24f);
            if (lens.Band < previous.Band || lens.Amount < previous.Amount) growsWithSlider = false;
            previous = lens;
        }
        Check(growsWithSlider, "the lens grows monotonically with 折射 across the whole slider");

        var growsWithCard = true;
        previous = LiquidGlassV2Lens.Compute(100, 40f, 24f);
        for (var side = 60f; side <= 720f; side += 20f)
        {
            var lens = LiquidGlassV2Lens.Compute(100, side, 24f);
            if (lens.Band < previous.Band) growsWithCard = false;
            previous = lens;
        }
        Check(growsWithCard, "the lens grows monotonically with the card's short side");

        var unit = LiquidGlassV2Lens.Compute(60, 352f, 24f);
        var doubled = LiquidGlassV2Lens.Compute(60, 704f, 48f);
        Check(MathF.Abs(doubled.Amount - 2f * unit.Amount) < 1e-2f
              && MathF.Abs(doubled.Band - 2f * unit.Band) < 1e-2f
              && MathF.Abs(doubled.GradRadius - 2f * unit.GradRadius) < 1e-2f,
            "the lens is linear in the render-pixel geometry (a DPI change cannot alter the corners)");

        var degenerateFinite = true;
        foreach (var (side, radius) in new[]
                 {
                     (0f, 0f), (0f, 24f), (24f, 0f), (100f, 0f), (200f, 100f), (1f, 1f)
                 })
        {
            var lens = LiquidGlassV2Lens.Compute(100, side, radius);
            if (!float.IsFinite(lens.Amount) || !float.IsFinite(lens.Band) || !float.IsFinite(lens.GradRadius)
                || lens.Amount < 0f || lens.Band < 0f || lens.GradRadius < 0f) degenerateFinite = false;
        }
        Check(degenerateFinite, "degenerate geometry (no radius, no side, a capsule, a 1 px card) stays finite");

        var nonFinite = LiquidGlassV2Lens.Compute(double.NaN, float.NaN, float.NaN);
        Check(float.IsFinite(nonFinite.Amount) && float.IsFinite(nonFinite.Band) && float.IsFinite(nonFinite.GradRadius),
            "non-finite inputs fall back to a finite lens");

        // radius 0: the cap closes the lens outright, which is a deliberate and visible change for
        // square-cornered cards. Pinned here so it cannot change silently in either direction.
        var squareCard = LiquidGlassV2Lens.Compute(100, 200f, 0f);
        Check(squareCard.Amount == 0f && squareCard.Band == 0f,
            "a square card (radius 0) gets no refraction band — the cap's documented side effect");
    }

    /// <summary>
    /// The cap's own contract: identity → quadratic → flat, monotonic, and with matching one-sided
    /// slopes at both joins. A hard <c>min</c> would satisfy everything but the slopes, and the
    /// derivative step shows up as the rim snapping while a card is dragged across the threshold.
    /// </summary>
    private static void CheckSmoothCap()
    {
        const float cap = 10f;
        var transition = cap * (float)LiquidGlassV2Settings.CornerBandCapTransitionFrac;

        Check(LiquidGlassV2Lens.SmoothCap(0f, cap) == 0f, "the cap keeps zero at zero");
        Check(LiquidGlassV2Lens.SmoothCap(cap * 0.3f, cap) == cap * 0.3f,
            "the cap passes the linear range through unchanged");
        Check(LiquidGlassV2Lens.SmoothCap(cap * 3f, cap) == cap, "the cap saturates at the cap");
        Check(LiquidGlassV2Lens.SmoothCap(5f, 0f) == 0f, "a zero cap closes the lens");
        Check(LiquidGlassV2Lens.SmoothCap(-5f, cap) == 0f, "a negative input cannot produce a negative band");
        Check(float.IsFinite(LiquidGlassV2Lens.SmoothCap(float.NaN, cap)), "a non-finite input stays finite");

        var monotonic = true;
        var previous = 0f;
        for (var i = 0; i <= 20000; i++)
        {
            var value = LiquidGlassV2Lens.SmoothCap(i * cap * 2f / 20000f, cap);
            if (value < previous - 1e-6f || value > cap + 1e-4f) monotonic = false;
            previous = value;
        }
        Check(monotonic, "the cap is monotonic and never overshoots the cap");

        const float epsilon = 1e-3f;
        var lower = (LiquidGlassV2Lens.SmoothCap(cap - transition + epsilon, cap)
                     - LiquidGlassV2Lens.SmoothCap(cap - transition - epsilon, cap)) / (2f * epsilon);
        var upper = (LiquidGlassV2Lens.SmoothCap(cap + transition + epsilon, cap)
                     - LiquidGlassV2Lens.SmoothCap(cap + transition - epsilon, cap)) / (2f * epsilon);
        Console.WriteLine($"Smooth cap slope at the joins: {lower:F4} (linear side), {upper:F4} (saturation side)");
        Check(MathF.Abs(lower - 1f) < 0.02f, "the cap meets the linear segment with slope 1");
        Check(MathF.Abs(upper) < 0.02f, "the cap meets saturation with slope 0");
    }

    /// <summary>
    /// The GPU path must take its coefficients from the shared solver rather than re-deriving them,
    /// and must use the same sign convention as the CPU twin. Both are cheap source-level pins on
    /// the exact drift the fix removed; the numeric behaviour is pinned above.
    /// </summary>
    private static void CheckShaderTakesSharedCoefficients()
    {
        if (!LiquidGlassV2Effect.IsSupported) return;
        var source = LineCommentPattern.Replace(LiquidGlassV2Effect.SourceForDiagnostics, "");
        Check(source.Contains("uniform float gradRadius"),
            "the shader takes the gradient radius from the shared solver, not a local literal");
        Check(!source.Contains("radius * 1.5"),
            "the shader no longer re-derives the gradient radius itself");
        Check(source.Contains("sgnCoord(") && !source.Contains("sign(coord)"),
            "the shader uses the same >= 0 sign convention as the CPU twin");
    }

    /// <summary>
    /// The CPU twin rendered through the real production path. The model checks above walk the
    /// mathematics; this one makes sure the new coefficients survive the actual pixel loop — the
    /// case the corner fix exists for is a 4×4 card, which takes the cap at full strength, so it
    /// has to stay opaque in the middle, clipped at the corner, and still refract visibly along
    /// the rim (a cap that flattened the material would be a regression dressed as a fix).
    /// </summary>
    private static void CheckCpuRender()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1200, 800),
                   new[]
                   {
                       new SKColor(20, 90, 122), new SKColor(65, 139, 150),
                       new SKColor(220, 161, 112), new SKColor(105, 60, 127)
                   }, null, SKShaderTileMode.Clamp))
        using (var fill = new SKPaint { Shader = shader, IsAntialias = true })
        {
            surface.Canvas.DrawPaint(fill);
        }
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var wallpaper = new WallpaperSnapshot(encoded.ToArray(), SKColors.Black);

        var theme = JsonSerializer.Deserialize<Theme>("""
            {"DarkMode":null,"AccentColor":null,"OpacityLevel":0.2,"Monochrome":true,"UseNativeFrame":false,"FontFamily":"Inter"}
            """)! with { Surface = SurfaceStyle.LiquidGlassV2 };
        // The report's 4×4 case: a 352 px card, radius 24, refraction at its default 100.
        var frame = new LiquidGlassRenderer.Frame(352, 352, 1f, 24f, 0f, 0f, 1200f, 800f, 0f, 0f, 1200f, 800f, theme, false);

        using var full = LiquidGlassV2Renderer.RenderBitmap(frame, wallpaper);
        using var flat = LiquidGlassV2Renderer.RenderBitmap(
            frame with { Theme = theme with { LiquidGlassV2 = new LiquidGlassV2Settings(Refraction: 0) } }, wallpaper);
        Check(full != null && flat != null, "the 4×4 CPU render produces a bitmap");
        if (full == null || flat == null) return;

        Check(full.Width == 352 && full.Height == 352, "the 4×4 CPU render keeps the card size");
        Check(full.GetPixel(176, 176).Alpha == 255, "the 4×4 CPU render is opaque at the centre");
        Check(full.GetPixel(0, 0).Alpha == 0, "the 4×4 CPU render keeps the rounded corner clip");

        var rimDelta = RimDifference(full, flat);
        Console.WriteLine($"4×4 CPU rim delta, 折射 100 vs 0: {rimDelta:F2}");
        Check(rimDelta > 1.5, "the capped lens still refracts visibly along a large card's rim");
    }

    /// <summary>Mean absolute RGB difference over the rim band, where the lens acts.</summary>
    private static double RimDifference(SKBitmap a, SKBitmap b)
    {
        double total = 0;
        var count = 0;
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            // The 24 px corner radius plus the capped 19.2 px band; nothing outside it moves.
            var inset = Math.Min(Math.Min(x, y), Math.Min(a.Width - 1 - x, a.Height - 1 - y));
            if (inset > 26) continue;
            var p = a.GetPixel(x, y);
            var q = b.GetPixel(x, y);
            if (p.Alpha < 255 || q.Alpha < 255) continue;
            total += Math.Abs(p.Red - q.Red) + Math.Abs(p.Green - q.Green) + Math.Abs(p.Blue - q.Blue);
            count += 3;
        }
        return total / Math.Max(1, count);
    }

    private static void Check(bool condition, string label)
    {
        Console.WriteLine($"  {(condition ? "PASS" : "FAIL")}: {label}");
        if (!condition) failures++;
    }
}
