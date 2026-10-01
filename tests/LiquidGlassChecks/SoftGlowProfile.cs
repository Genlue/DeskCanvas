using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

/// <summary>
/// Checks for 柔光玻璃 (soft glow glass, <see cref="SurfaceStyle.SoftGlow"/>).
///
/// It shares the whole pipeline with 液态玻璃 — same BevelField normals, vibrancy,
/// frosted coating and light model — and only swaps the recipe: a wide, shallow lens
/// instead of a meniscus ring, a diffused halo instead of a hairline, and an optional
/// seven-tap spectrum. These checks pin down exactly that difference, and pin down that
/// the LiquidGlass path did not move:
///
/// 1. the model/preset wiring (enum, flags, preset optics, config compatibility),
/// 2. the rim is a wide diffuse halo, not a 1 px line,
/// 3. the halo responds monotonically to the Glow slider,
/// 4. the Spectrum slider changes the rim dispersion and nothing in the flat centre,
/// 5. refraction is gentler than 液态玻璃 at the same slider value,
/// 6. the extra spectrum taps stay within budget.
/// </summary>
public static class SoftGlowProfile
{
    private const int CardWidth = 400;
    private const int CardHeight = 300;
    private const int Radius = 28;
    private const int WallWidth = 800;
    private const int WallHeight = 400;

    /// <summary>Where the green scrap of <see cref="RedFieldWithGreenScrap"/> starts (wallpaper y).</summary>
    private const int GreenScrapY = 110;

    public static void Run(string output)
    {
        Directory.CreateDirectory(output);
        CheckModel();

        var grey = SolidWallpaper(new SKColor(96, 96, 96));
        CheckSoftRim(grey);
        CheckGlowMonotonic(grey);

        var ramp = GridWallpaper(WallWidth, WallHeight, 20);
        CheckSpectrum(CheckerWallpaper(WallWidth, WallHeight, 16));
        CheckRefractionIsGentler(RampWallpaper());
        CheckDyeBloom();
        CheckDyeSpread();
        CheckLensCanBeSwitchedOff();
        CheckPerformance(ramp);
        DrawPreview(output, ramp);

        Console.WriteLine("Soft glow (柔光玻璃) checks passed.");
    }

    // ------------------------------------------------------------- edge dye ----

    /// <summary>
    /// The defining difference between the two materials' edge dye: 液态玻璃 dyes each rim pixel
    /// with the colour underneath it, so a green scrap next to a red field is cut off exactly
    /// where the scrap ends. 柔光玻璃 samples a bloomed colour field, so the green bleeds into the
    /// red like a light source and the rim shows a soft gradient instead of a seam.
    /// </summary>
    private static void CheckDyeBloom()
    {
        // Fully opaque coating + no lens + no blur + no highlight: the card is a pure dye canvas,
        // so the scan measures the dye rather than the wallpaper behind it.
        var dyeOnly = new LiquidGlassSettings(Blur: 0, Refraction: 0, EdgeWidth: 0, Highlight: 0,
            Dispersion: 0, LightAngle: 225, EdgeTint: 100, Glow: 0, Spectrum: 0);
        var wallpaper = RedFieldWithGreenScrap();

        using var crisp = Render(Material(SurfaceStyle.LiquidGlass, dyeOnly with { EdgeWidth = 24 }, 1.0), wallpaper);
        using var bloom = Render(Material(SurfaceStyle.SoftGlow, dyeOnly, 1.0), wallpaper);

        var crispRamp = MeasureDyeRamp(crisp);
        var bloomRamp = MeasureDyeRamp(bloom);
        Console.WriteLine($"Dye ramp at the scrap's edge: 液态玻璃 {crispRamp.Width} px ({crispRamp.PlateauDelta:F0} levels) | " +
                          $"柔光玻璃 {bloomRamp.Width} px ({bloomRamp.PlateauDelta:F0} levels)");
        Console.WriteLine($"  profile (G−R at y = 96…128): 液态玻璃 {crispRamp.Profile} | 柔光玻璃 {bloomRamp.Profile}");

        Check(crispRamp.Width <= 3, "液态玻璃 dyes point-wise: the colour stops where the scrap ends (≤3 px)");
        Check(bloomRamp.Width >= crispRamp.Width * 3, $"柔光玻璃 blooms the dye over {bloomRamp.Width} px (≥3x the point-wise seam)");
        Check(bloomRamp.Width >= 10, "柔光玻璃's dye ramp is wide enough to read as 晕染, not as a seam");
        Check(bloomRamp.PlateauDelta > 40, "柔光玻璃 still reaches the scrap's own colour at the plateau (it bleeds, it does not wash out)");
    }

    /// <summary>
    /// EdgeWidth 0 = the lens ring is switched off. Refraction must then have no effect at all,
    /// the rim must keep its dye/halo, and the rounded clipping must stay intact.
    /// </summary>
    private static void CheckLensCanBeSwitchedOff()
    {
        var optics = LiquidGlassSettings.SoftGlowPreset with { Blur = 0, Highlight = 0, Dispersion = 0, EdgeTint = 60, Glow = 70 };
        var ramp = GridWallpaper(WallWidth, WallHeight, 12);

        using var off0 = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 0, Refraction = 0 }), ramp);
        using var off100 = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 0, Refraction = 100 }), ramp);
        using var on0 = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 24, Refraction = 0 }), ramp);
        using var on100 = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 24, Refraction = 100 }), ramp);
        // The crisp baselines have to zero both soft-recipe ingredients: since the merge,
        // 柔光晕 *or* 光谱弥散 above 0 selects the soft material on any surface.
        using var crisp0 = Render(Material(SurfaceStyle.LiquidGlass, optics with { EdgeWidth = 24, Refraction = 0, Glow = 0, Spectrum = 0 }), ramp);
        using var crisp100 = Render(Material(SurfaceStyle.LiquidGlass, optics with { EdgeWidth = 24, Refraction = 100, Glow = 0, Spectrum = 0 }), ramp);

        var offDelta = MeanDelta(off0, off100, 0, 120);
        var onDelta = MeanDelta(on0, on100, 0, 120);
        var crispDelta = MeanDelta(crisp0, crisp100, 0, 120);
        Console.WriteLine($"EdgeWidth 0: refraction 0 vs 100 changes {offDelta:F3} levels; " +
                          $"at 24 DIP it changes {onDelta:F2} (柔光) / {crispDelta:F2} (液态)");
        Check(offDelta < 0.01f, "EdgeWidth 0 switches the lens off completely (refraction no longer matters)");
        Check(onDelta > 1f && crispDelta > 1f, "EdgeWidth > 0 still bends the backdrop in both materials");
        Check(off0.GetPixel(0, 0).Alpha == 0 && off0.GetPixel(CardWidth / 2, CardHeight / 2).Alpha == 255,
            "EdgeWidth 0 keeps the rounded clipping and the solid interior");

        using var offDye = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 0, Refraction = 0, Glow = 70 }), ramp);
        using var noDye = Render(Material(SurfaceStyle.SoftGlow, optics with { EdgeWidth = 0, Refraction = 0, Glow = 0, EdgeTint = 0 }), ramp);
        Check(MeanDelta(offDye, noDye, 0, 60) > 0.5f,
            "EdgeWidth 0 keeps 柔光玻璃's own edge treatment (dye + halo) alive");
    }

    /// <summary>
    /// 染色扩散 controls how far the dyed rim reaches <b>inwards</b>. At 0 the colour stays in
    /// the outermost band — a clean rim with nothing washing over the content — while 100 spreads
    /// it deep into the card. Measured over a uniform backdrop, so the only thing that can change
    /// the pixels is the dye itself; 液态玻璃 has no such knob and must be unaffected by it.
    /// </summary>
    private static void CheckDyeSpread()
    {
        var wallpaper = SolidWallpaper(new SKColor(220, 30, 40));
        var basis = LiquidGlassSettings.SoftGlowPreset with
        {
            Blur = 0, Refraction = 0, EdgeWidth = 0, Highlight = 0, Dispersion = 0, Spectrum = 0,
            EdgeTint = 100, Glow = 0
        };

        using var undyed = Render(Material(SurfaceStyle.SoftGlow, basis with { EdgeTint = 0, DyeSpread = 0 }), wallpaper);
        using var rimOnly = Render(Material(SurfaceStyle.SoftGlow, basis with { DyeSpread = 0 }), wallpaper);
        using var half = Render(Material(SurfaceStyle.SoftGlow, basis with { DyeSpread = 50 }), wallpaper);
        using var deep = Render(Material(SurfaceStyle.SoftGlow, basis with { DyeSpread = 100 }), wallpaper);

        var rimReach = InwardReach(undyed, rimOnly);
        var halfReach = InwardReach(undyed, half);
        var deepReach = InwardReach(undyed, deep);
        Console.WriteLine($"Dye reach inward: 染色扩散 0/50/100 → {rimReach}/{halfReach}/{deepReach} px " +
                          $"(band fractions {LiquidGlassSettings.MinDyeBandFraction:P0}–{LiquidGlassSettings.MaxDyeBandFraction:P0} of the short side)");

        Check(rimReach > 2, "染色扩散 0 still dyes the outermost band (a clean rim, not no dye)");
        Check(rimReach <= 20, $"染色扩散 0 keeps the dye in the rim ({rimReach} px, no wash over the content)");
        Check(rimReach < halfReach && halfReach < deepReach,
            $"染色扩散 is monotone ({rimReach} → {halfReach} → {deepReach} px)");
        Check(deepReach >= rimReach * 3 && deepReach >= 25,
            $"染色扩散 100 spreads far deeper ({deepReach} px vs {rimReach} px)");

        // 液态玻璃 has no dye spread: the knob must be inert there (regression guard for the
        // material that existed long before it).
        var crispBasis = new LiquidGlassSettings(Blur: 0, Refraction: 0, EdgeWidth: 24, Highlight: 0,
            Dispersion: 0, LightAngle: 225, EdgeTint: 100);
        using var crisp0 = Render(Material(SurfaceStyle.LiquidGlass, crispBasis with { DyeSpread = 0 }), wallpaper);
        using var crisp100 = Render(Material(SurfaceStyle.LiquidGlass, crispBasis with { DyeSpread = 100 }), wallpaper);
        Check(MeanDelta(crisp0, crisp100, 0, CardWidth) == 0f,
            "染色扩散 does not touch 液态玻璃 (its dye stays point-wise)");

        // Validation: clamped, NaN-safe, and the stored-theme default is the moderate value.
        Check(new LiquidGlassSettings(DyeSpread: -5).Normalize().DyeSpread == 0
              && new LiquidGlassSettings(DyeSpread: 300).Normalize().DyeSpread == 100
              && new LiquidGlassSettings(DyeSpread: double.NaN).Normalize().DyeSpread == LiquidGlassSettings.DefaultDyeSpread,
            "染色扩散 is clamped to 0–100 and falls back to the default for NaN");
        Check(new LiquidGlassSettings().DyeSpread == LiquidGlassSettings.DefaultDyeSpread,
            "a theme that predates 染色扩散 renders with the moderate default, not the widest spread");
    }

    /// <summary>
    /// How far (px) the dye changes the card, walking inward along the left edge at mid-height.
    /// A delta of 2 levels per channel counts as "the dye is still visible here".
    /// </summary>
    private static int InwardReach(SKBitmap reference, SKBitmap dyed)
    {
        var y = CardHeight / 2;
        var reach = 0;
        for (var x = 0; x < CardWidth / 2; x++)
        {
            var a = reference.GetPixel(x, y);
            var b = dyed.GetPixel(x, y);
            var delta = Math.Abs(a.Red - b.Red) + Math.Abs(a.Green - b.Green) + Math.Abs(a.Blue - b.Blue);
            if (delta > 6) reach = x;
        }
        return reach;
    }

    private readonly record struct DyeRamp(int Width, float PlateauDelta, string Profile);

    /// <summary>
    /// Width of the red→green transition along the dyed left edge: how many rows the dye takes to
    /// travel between the two plateaus. <c>GreenScrapY</c> is where the scrap starts.
    /// </summary>
    private static DyeRamp MeasureDyeRamp(SKBitmap card)
    {
        var values = new float[CardHeight];
        for (var y = 0; y < CardHeight; y++)
        {
            var color = card.GetPixel(3, y);
            values[y] = color.Green - color.Red;
        }

        var low = values.Take(GreenScrapY - 30).Average();
        var high = values.Skip(GreenScrapY + 20).Take(40).Average();
        var delta = high - low;
        if (Math.Abs(delta) < 15) return new DyeRamp(0, 0, "flat");

        var t10 = low + delta * 0.10f;
        var t90 = low + delta * 0.90f;
        var first = -1;
        var last = -1;
        for (var y = GreenScrapY - 30; y < GreenScrapY + 30; y++)
        {
            if (first < 0 && values[y] >= t10) first = y;
            if (first >= 0 && last < 0 && values[y] >= t90) last = y;
        }

        var profile = string.Join("/", new[] { 96, 104, 110, 116, 122, 128 }.Select(y => $"{values[y]:F0}"));
        return new DyeRamp(first < 0 || last < 0 ? 0 : last - first, delta, profile);
    }

    /// <summary>Red field with one green scrap straddling the card's left edge.</summary>
    private static byte[] RedFieldWithGreenScrap() => Wallpaper(canvas =>
    {
        using var red = new SKPaint { Color = new SKColor(220, 30, 40) };
        canvas.DrawRect(0, 0, WallWidth, WallHeight, red);
        using var green = new SKPaint { Color = new SKColor(20, 200, 60) };
        canvas.DrawRect(0, GreenScrapY, 120, 80, green);
    });

    // ---------------------------------------------------------------- model ----

    private static void CheckModel()
    {
        Check((int)SurfaceStyle.SoftGlow == 5, "SurfaceStyle.SoftGlow has enum value 5 (appended, so stored values stay valid)");

        // 柔光玻璃 was merged into 液态玻璃: one surface, one pipeline. The soft look is now a
        // recipe selected by the 柔光晕 / 光谱弥散 knobs rather than a separate theme.
        var theme = Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset);
        Check(theme.EffectiveSurface == SurfaceStyle.LiquidGlass, "the legacy soft surface resolves to 液态玻璃");
        Check(theme.IsGlass, "the merged theme counts as a glass surface (outline rows stay available)");
        Check(theme.UsesRenderedGlass, "the merged theme is rendered from the desktop snapshot");
        Check(theme.IsLiquidGlass, "the merged theme IS liquid glass — one theme, not two");
        Check(theme.IsSoftGlow, "a stored soft configuration still selects the soft look");
        Check(!theme.UsesNativeBlur, "liquid glass never asks for the OS acrylic backdrop");
        Check(!theme.IsColorful, "the merged theme is not the colorful material");

        // Migration folds the surface but must not touch the optics.
        var migrated = theme.NormalizeMaterial();
        Check(migrated.Surface == SurfaceStyle.LiquidGlass, "NormalizeMaterial folds the legacy surface into liquid glass");
        Check(migrated.EffectiveLiquidGlass == LiquidGlassSettings.SoftGlowPreset,
            "migration preserves the saved soft optics exactly");
        Check(migrated.IsSoftGlow, "a migrated soft configuration keeps the soft look");

        // The factory optics must stay crisp — that is the macOS-faithful material the merge
        // is meant to default to, and it is what every pre-1.9.9 configuration already had.
        var crisp = Material(SurfaceStyle.LiquidGlass, new LiquidGlassSettings());
        Check(!crisp.IsSoftGlow, "the factory liquid-glass optics are the crisp recipe");
        Check(!crisp.EffectiveLiquidGlass.IsSoftRecipe, "Glow/Spectrum at 0 means the soft recipe is off");
        Check(Material(SurfaceStyle.LiquidGlass, new LiquidGlassSettings(Glow: 1)).IsSoftGlow,
            "turning 柔光晕 up selects the soft recipe");
        Check(Material(SurfaceStyle.LiquidGlass, new LiquidGlassSettings(Spectrum: 1)).IsSoftGlow,
            "turning 光谱弥散 up selects the soft recipe");

        // The preset must survive its own validation untouched.
        Check(LiquidGlassSettings.SoftGlowPreset.Normalize() == LiquidGlassSettings.SoftGlowPreset,
            "the soft-glow preset is already inside every validated range");
        Check(LiquidGlassSettings.SoftGlowPreset.Glow > 0 && LiquidGlassSettings.SoftGlowPreset.Spectrum > 0,
            "the soft-glow preset actually enables the halo and the spectrum");

        // Old configurations keep the historic optics: the new knobs default to 0,
        // which is what the crisp liquid-glass recipe relies on.
        var legacy = new LiquidGlassSettings();
        Check(legacy.Glow == 0 && legacy.Spectrum == 0,
            "the historic default optics leave Glow/Spectrum off (液态玻璃 is unchanged)");
        var legacyJson = """{"Blur":7,"Refraction":55,"EdgeWidth":32,"Highlight":80,"Dispersion":40,"LightAngle":225}""";
        var restored = System.Text.Json.JsonSerializer.Deserialize<LiquidGlassSettings>(legacyJson)!;
        Check(restored.Glow == 0 && restored.Spectrum == 0,
            "a stored pre-1.9.4 optics object deserializes with Glow/Spectrum off");
        var roundTrip = System.Text.Json.JsonSerializer.Deserialize<LiquidGlassSettings>(
            System.Text.Json.JsonSerializer.Serialize(LiquidGlassSettings.SoftGlowPreset))!;
        Check(roundTrip == LiquidGlassSettings.SoftGlowPreset, "soft-glow optics survive a JSON round trip");

        var clamped = new LiquidGlassSettings(Glow: double.NaN, Spectrum: 500).Normalize();
        Check(clamped.Glow == 0 && clamped.Spectrum == 100, "Glow/Spectrum are clamped and NaN-safe");

        // Preset lookup (what the theme button loads) must agree with the merged model:
        // asking for the legacy surface hands back the liquid-glass theme.
        var settings = new AppSettings(
            Material(SurfaceStyle.LiquidGlass, new LiquidGlassSettings()),
            [],
            new Layout(GridMode.Manual, false, false, true, false),
            new Dimensions(100, 12, 10),
            new Region("zh-Hans"),
            false,
            null);
        var preset = settings.GetThemeForSurface(SurfaceStyle.SoftGlow);
        Check(preset.EffectiveSurface == SurfaceStyle.LiquidGlass,
            "GetThemeForSurface(SoftGlow) resolves to the merged liquid-glass theme");
        Check(preset.EffectiveLiquidGlass == new LiquidGlassSettings(),
            "GetThemeForSurface(SoftGlow) hands over the crisp liquid-glass defaults");

        // Once the legacy surface has been folded in, the two lookup keys must be the same entry.
        var folded = settings.WithThemeForSurface(SurfaceStyle.SoftGlow,
            Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset));
        Check(!folded.SurfaceThemes!.ContainsKey(nameof(SurfaceStyle.SoftGlow)),
            "WithThemeForSurface(SoftGlow) stores under the merged surface, not the legacy key");
        Check(folded.GetThemeForSurface(SurfaceStyle.LiquidGlass).IsSoftGlow,
            "the folded-in soft optics are reachable through the liquid-glass key");
    }

    // ------------------------------------------------------------- rim/glow ----

    /// <summary>
    /// Over a flat backdrop the only thing that changes between Highlight=0 and
    /// Highlight=100 is the lighting itself, which is exactly the rim profile.
    /// 液态玻璃 draws a 1 px line on the border; 柔光玻璃 must instead spread a
    /// much wider, lower halo that reaches into the card.
    /// </summary>
    private static void CheckSoftRim(byte[] grey)
    {
        var optics = LiquidGlassSettings.SoftGlowPreset with { Dispersion = 0, Glow = 100 };
        // The crisp baseline has to switch both soft-recipe ingredients off: since the merge,
        // 柔光晕 *or* 光谱弥散 above 0 selects the soft material.
        var crisp = Rim(LiquidGlassSettings.SoftGlowPreset with { Dispersion = 0, Glow = 0, Spectrum = 0, Highlight = 100 }, grey, true);
        var soft = Rim(optics, grey, false);

        Console.WriteLine($"Rim: 液态玻璃 peak {crisp.Peak:F1} levels, 25% width {crisp.Width} px, " +
                          $"integral(40) {crisp.Integral(40):F0} | " +
                          $"柔光玻璃 peak {soft.Peak:F1}, 25% width {soft.Width} px, integral(40) {soft.Integral(40):F0}");

        Check(crisp.PeakAt <= 3, "液态玻璃 keeps its crisp line on the border (regression guard)");
        Check(crisp.Width <= 6, "液态玻璃's line stays narrow (regression guard)");

        Check(soft.Width >= crisp.Width * 3, $"柔光玻璃's halo is at least 3x wider ({soft.Width} vs {crisp.Width} px)");
        Check(soft.Integral(40) > crisp.Integral(40) * 1.5f,
            "柔光玻璃 spreads much more total light than 液态玻璃's line (soft, not hard)");
        Check(soft.At(30) > crisp.At(30) + 2f,
            "柔光玻璃 still lights the inner band that 液态玻璃 has already left dark");
        Check(soft.Peak < crisp.Peak, "柔光玻璃 trades peak brightness for spread (no hard white line)");
    }

    /// <summary>
    /// The halo must respond to its own slider, monotonically, and it must stay a rim
    /// effect (the flat interior never blooms). Measured as the rendered pixel delta
    /// between Glow=0 and Glow=N with the highlight switched off, so nothing else moves.
    /// </summary>
    private static void CheckGlowMonotonic(byte[] grey)
    {
        var optics = LiquidGlassSettings.SoftGlowPreset with { Dispersion = 0, Highlight = 0 };
        using var off = Render(Material(SurfaceStyle.SoftGlow, optics with { Glow = 0 }), grey);
        using var half = Render(Material(SurfaceStyle.SoftGlow, optics with { Glow = 50 }), grey);
        using var full = Render(Material(SurfaceStyle.SoftGlow, optics with { Glow = 100 }), grey);

        var halfRim = MeanDelta(off, half, 0, 60);
        var fullRim = MeanDelta(off, full, 0, 60);
        // The true interior: at least 130 px away from every border, so no halo band
        // (which wraps all four edges by design) can reach it.
        var fullCentre = MeanDelta(off, full, 130, CardWidth - 130, 130, CardHeight - 130);
        Console.WriteLine($"Glow rim delta (0→50 / 0→100): {halfRim:F2} / {fullRim:F2} levels, " +
                          $"flat centre 0→100: {fullCentre:F2}");

        Check(halfRim > 0.2f, "the 柔光晕 slider adds light to the rim");
        Check(fullRim > halfRim * 1.5f, "the 柔光晕 slider is monotone (100% spreads clearly more than 50%)");
        Check(fullCentre < fullRim * 0.25f, "the halo stays on the rim and never blooms over the content area");
    }

    // -------------------------------------------------------------- spectrum ----

    /// <summary>
    /// Spectrum only re-weights the dispersion taps: the rim must change, the flat
    /// centre (beyond the lens band) must not, and the whole thing stays subtle.
    /// Measured over a fine checkerboard — a smooth gradient barely reacts to a
    /// 2-px sampling offset, and wallpapers are busy, so the busy pattern is the
    /// honest probe for a dispersion feature.
    /// </summary>
    private static void CheckSpectrum(byte[] ramp)
    {
        // Blur is switched off so the probe isolates the tap weighting: the preset's
        // own blur (σ≈2 px) would smooth the pattern below what any tap can resolve.
        var optics = LiquidGlassSettings.SoftGlowPreset with { Dispersion = 100, Blur = 0 };
        using var threeTap = Render(Material(SurfaceStyle.SoftGlow, optics with { Spectrum = 0 }), ramp);
        using var sevenTap = Render(Material(SurfaceStyle.SoftGlow, optics with { Spectrum = 100 }), ramp);

        var rim = MeanDelta(threeTap, sevenTap, 0, 90);
        var rimPeak = MaxDelta(threeTap, sevenTap, 0, 90);
        var centre = MaxDelta(threeTap, sevenTap, 160, CardWidth - 160, 150, CardHeight - 150);
        Console.WriteLine($"Spectrum: rim mean |delta| {rim:F2} levels, rim peak {rimPeak:F0} levels, flat centre peak {centre:F2} levels");
        Check(rimPeak > 8f, "光谱弥散 visibly changes the rim pixels");
        Check(centre < 0.01f, "光谱弥散 leaves the flat centre untouched (dispersion stays in the lens band)");

        // Dispersion must remain a rim phenomenon: switching it off only moves the band.
        using var off = Render(Material(SurfaceStyle.SoftGlow, optics with { Dispersion = 0, Spectrum = 100 }), ramp);
        Check(MeanDelta(sevenTap, off, 0, 90) > rim, "光谱弥散 is still dispersion — it scales with the strength slider");
        Check(MeanDelta(threeTap, off, 0, 90) < 60f, "光谱弥散 stays restrained at 100% (no rainbow banding)");
    }

    /// <summary>
    /// Same slider value, gentler glass. Measured over a smooth horizontal ramp: on a
    /// gradient the pixel change is proportional to the displacement itself, whereas a
    /// grid saturates (an edge either moves or it does not) and hides the difference.
    /// </summary>
    private static void CheckRefractionIsGentler(byte[] ramp)
    {
        var soft = LiquidGlassSettings.SoftGlowPreset with { Highlight = 0, Dispersion = 0, Spectrum = 0, Refraction = 100 };
        // Same optics, but on the crisp recipe: 柔光晕 has to be off as well, or the merged
        // model reads this as the soft material and the two sides become identical.
        var crisp = LiquidGlassSettings.SoftGlowPreset with { Highlight = 0, Dispersion = 0, Spectrum = 0, Glow = 0, Refraction = 100 };

        using var softOn = Render(Material(SurfaceStyle.SoftGlow, soft), ramp);
        using var softOff = Render(Material(SurfaceStyle.SoftGlow, soft with { Refraction = 0 }), ramp);
        using var crispOn = Render(Material(SurfaceStyle.LiquidGlass, crisp), ramp);
        using var crispOff = Render(Material(SurfaceStyle.LiquidGlass, crisp with { Refraction = 0 }), ramp);

        var softDelta = MeanDelta(softOn, softOff, 0, 120);
        var crispDelta = MeanDelta(crispOn, crispOff, 0, 120);
        var softPeak = MaxDelta(softOn, softOff, 0, 120);
        var crispPeak = MaxDelta(crispOn, crispOff, 0, 120);
        Console.WriteLine($"Refraction @100%: 柔光玻璃 mean {softDelta:F2} / peak {softPeak:F0}, " +
                          $"液态玻璃 mean {crispDelta:F2} / peak {crispPeak:F0} levels");
        Check(softDelta > 0.25f && softPeak >= 3f,
            $"柔光玻璃 still refracts at 100% (mean {softDelta:F2}, peak {softPeak:F0} levels)");
        Check(softDelta < crispDelta * 0.9f,
            $"柔光玻璃 bends the backdrop more gently at the same slider value ({softDelta:F3} vs {crispDelta:F3}; " +
            $"softRecipe={Material(SurfaceStyle.SoftGlow, soft).IsSoftGlow}, " +
            $"crispRecipe={Material(SurfaceStyle.LiquidGlass, crisp).IsSoftGlow})");

        using var softCentre = Render(Material(SurfaceStyle.SoftGlow, soft), ramp);
        using var softCentreOff = Render(Material(SurfaceStyle.SoftGlow, soft with { Refraction = 0 }), ramp);
        Check(MeanDelta(softCentre, softCentreOff, 170, CardWidth - 170, 160, CardHeight - 160) < 0.01f,
            "柔光玻璃 keeps the flat centre pristine (zero displacement there)");
    }

    private static void CheckPerformance(byte[] ramp)
    {
        var theme = Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset with { Spectrum = 100 });
        var big = new LiquidGlassRenderer.Frame(1200, 800, 1, 32, 0, 0, WallWidth, WallHeight,
            0, 0, WallWidth, WallHeight, theme, false);
        var wallpaper = new WallpaperSnapshot(ramp, SKColors.Black);
        LiquidGlassRenderer.Render(big with { Width = 64, Height = 64 }, wallpaper);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var bytes = LiquidGlassRenderer.Render(big, wallpaper);
        Console.WriteLine($"Performance: 柔光玻璃 1200×800 (960k px, spectrum on) in {timer.ElapsedMilliseconds} ms");
        Check(timer.ElapsedMilliseconds < 1500, $"performance: soft glow renders in {timer.ElapsedMilliseconds} ms");
        Check(bytes.Length > 0, "performance: soft glow produced a bitmap");
    }

    // -------------------------------------------------------------- preview ----

    private static void DrawPreview(string output, byte[] ramp)
    {
        var wallpaper = new WallpaperSnapshot(ramp, SKColors.Black);
        using var crisp = SKBitmap.Decode(LiquidGlassRenderer.Render(
            Frame(Material(SurfaceStyle.LiquidGlass, LiquidGlassSettings.SoftGlowPreset with { Glow = 0, Spectrum = 0 })), wallpaper));
        using var soft = SKBitmap.Decode(LiquidGlassRenderer.Render(
            Frame(Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset)), wallpaper));
        using var vivid = SKBitmap.Decode(LiquidGlassRenderer.Render(
            Frame(Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset with { Glow = 100, Spectrum = 100, EdgeTint = 100 })), wallpaper));

        using var strip = SKSurface.Create(new SKImageInfo(CardWidth * 3 + 80, CardHeight + 40));
        var canvas = strip.Canvas;
        canvas.Clear(new SKColor(22, 24, 28));
        canvas.DrawBitmap(crisp, 20, 20);
        canvas.DrawBitmap(soft, 40 + CardWidth, 20);
        canvas.DrawBitmap(vivid, 60 + CardWidth * 2, 20);
        using var data = strip.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "soft-glow-compare.png"), data.ToArray());
        Console.WriteLine($"Wrote soft-glow-compare.png (液态玻璃 | 柔光玻璃 | 柔光晕+光谱 100%)");

        using var rim = SKSurface.Create(new SKImageInfo(360, 60));
        rim.Canvas.Clear(new SKColor(22, 24, 28));
        rim.Canvas.DrawBitmap(crisp, SKRect.Create(0, 0, 90, 300), SKRect.Create(0, 0, 120, 60));
        rim.Canvas.DrawBitmap(soft, SKRect.Create(0, 0, 90, 300), SKRect.Create(120, 0, 120, 60));
        rim.Canvas.DrawBitmap(vivid, SKRect.Create(0, 0, 90, 300), SKRect.Create(240, 0, 120, 60));
        using var rimData = rim.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "soft-glow-rim-strip.png"), rimData.ToArray());

        DrawRealPreview(output);
    }

    /// <summary>
    /// The two materials side by side at 1:1 over the real desktop wallpaper — the
    /// synthetic strips above prove the optics, this one shows what the user will see.
    /// </summary>
    private static void DrawRealPreview(string output)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (!File.Exists(path))
        {
            Console.WriteLine("Real wallpaper preview skipped: no TranscodedWallpaper file.");
            return;
        }

        using var wallpaper = SKBitmap.Decode(File.ReadAllBytes(path));
        if (wallpaper is null) return;

        const int sheetWidth = 1000;
        const int sheetHeight = 400;
        var ox = Math.Max(0, (wallpaper.Width - sheetWidth) / 2);
        var oy = Math.Max(0, (wallpaper.Height - sheetHeight) / 2);

        using var sheet = SKSurface.Create(new SKImageInfo(sheetWidth, sheetHeight));
        using var blit = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        sheet.Canvas.DrawBitmap(wallpaper, SKRect.Create(ox, oy, sheetWidth, sheetHeight),
            SKRect.Create(0, 0, sheetWidth, sheetHeight), blit);

        var variants = new (string Label, Theme Theme)[]
        {
            ("液态玻璃 同参数", Material(SurfaceStyle.LiquidGlass,
                LiquidGlassSettings.SoftGlowPreset with { Glow = 0, Spectrum = 0 }, Theme.DefaultSoftGlowOpacity)),
            ("柔光玻璃 preset", Material(SurfaceStyle.SoftGlow, LiquidGlassSettings.SoftGlowPreset, Theme.DefaultSoftGlowOpacity)),
            ("柔光玻璃 glow/spectrum 100%", Material(SurfaceStyle.SoftGlow,
                LiquidGlassSettings.SoftGlowPreset with { Glow = 100, Spectrum = 100 }, Theme.DefaultSoftGlowOpacity))
        };

        for (var i = 0; i < variants.Length; i++)
        {
            var x = 30 + i * 320;
            var y = 60;
            // Every card samples the SAME wallpaper region (blitted side by side), so the
            // only difference on screen is the material itself, not the backdrop brightness.
            var frame = new LiquidGlassRenderer.Frame(CardWidth, CardHeight, 1, Radius, ox + 60, oy + 80,
                wallpaper.Width, wallpaper.Height, 0, 0, wallpaper.Width, wallpaper.Height,
                variants[i].Theme, true);
            using var card = SKBitmap.Decode(LiquidGlassRenderer.Render(
                frame, new WallpaperSnapshot(File.ReadAllBytes(path), SKColors.Black)));
            sheet.Canvas.DrawBitmap(card, x, y);

            // How much light the material itself adds in the middle of the card.
            var interior = 0f;
            var samples = 0;
            for (var sy = CardHeight / 2 - 30; sy < CardHeight / 2 + 30; sy += 3)
                for (var sx = CardWidth / 2 - 30; sx < CardWidth / 2 + 30; sx += 3)
                {
                    interior += Luminance(card.GetPixel(sx, sy));
                    samples++;
                }
            Console.WriteLine($"  {variants[i].Label}: interior mean luminance {interior / samples:F1}");
        }

        using var data = sheet.Snapshot().Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(output, "soft-glow-real-wallpaper.png"), data.ToArray());
        Console.WriteLine("Wrote soft-glow-real-wallpaper.png (1:1 over the real desktop wallpaper)");
    }

    // ------------------------------------------------------------- helpers ----

    private readonly record struct Profile(float[] Values, int PeakAt, float Peak)
    {
        /// <summary>Luminance delta at an inward depth (index) from the border.</summary>
        public float At(int depth) => depth >= 0 && depth < Values.Length ? Values[depth] : 0f;

        /// <summary>Sum of the profile over the first <paramref name="count"/> pixels.</summary>
        public float Integral(int count)
        {
            var sum = 0f;
            for (var i = 0; i < Math.Min(count, Values.Length); i++) sum += Values[i];
            return sum;
        }

        /// <summary>How many pixels exceed a quarter of the peak (the halo's visible width).
        /// A hairline scores 1–3, a soft halo scores tens.</summary>
        public int Width
        {
            get
            {
                var width = 0;
                for (var i = 0; i < Values.Length; i++) if (Values[i] > Peak * 0.25f) width++;
                return width;
            }
        }
    }

    /// <summary>Highlight contribution (0 vs 100%) across the left edge at mid-height.</summary>
    private static Profile Rim(LiquidGlassSettings optics, byte[] wallpaper, bool crisp)
    {
        var surface = crisp ? SurfaceStyle.LiquidGlass : SurfaceStyle.SoftGlow;
        using var lit = Render(Material(surface, optics with { Highlight = 100 }), wallpaper);
        using var dark = Render(Material(surface, optics with { Highlight = 0 }), wallpaper);

        var values = new float[80];
        var peak = 0f;
        var peakAt = 0;
        for (var x = 0; x < values.Length; x++)
        {
            values[x] = Luminance(lit.GetPixel(x, CardHeight / 2)) - Luminance(dark.GetPixel(x, CardHeight / 2));
            if (values[x] > peak) { peak = values[x]; peakAt = x; }
        }
        return new Profile(values, peakAt, peak);
    }

    private static Theme Material(SurfaceStyle surface, LiquidGlassSettings glass, double opacity = 0) =>
        new(null, null, opacity, false, false, "Inter", surface, LiquidGlass: glass);

    private static LiquidGlassRenderer.Frame Frame(Theme material) =>
        new(CardWidth, CardHeight, 1, Radius, 0, 0, WallWidth, WallHeight,
            0, 0, WallWidth, WallHeight, material, false);

    private static SKBitmap Render(Theme material, byte[] wallpaper) =>
        SKBitmap.Decode(LiquidGlassRenderer.Render(Frame(material), new WallpaperSnapshot(wallpaper, SKColors.Black)))!;

    private static float MeanDelta(SKBitmap a, SKBitmap b, int fromX, int toX, int fromY = 0, int toY = int.MaxValue)
    {
        double sum = 0;
        var count = 0;
        for (var y = Math.Max(0, fromY); y < Math.Min(toY, a.Height); y += 2)
        {
            for (var x = Math.Max(0, fromX); x < Math.Min(toX, a.Width); x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                sum += Math.Abs(pa.Red - pb.Red) + Math.Abs(pa.Green - pb.Green) + Math.Abs(pa.Blue - pb.Blue);
                count++;
            }
        }
        return count == 0 ? 0f : (float)(sum / (count * 3.0));
    }

    private static float MaxDelta(SKBitmap a, SKBitmap b, int fromX, int toX, int fromY = 0, int toY = int.MaxValue)
    {
        var peak = 0;
        for (var y = Math.Max(0, fromY); y < Math.Min(toY, a.Height); y++)
        {
            for (var x = Math.Max(0, fromX); x < Math.Min(toX, a.Width); x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                peak = Math.Max(peak, Math.Abs(pa.Red - pb.Red));
                peak = Math.Max(peak, Math.Abs(pa.Green - pb.Green));
                peak = Math.Max(peak, Math.Abs(pa.Blue - pb.Blue));
            }
        }
        return peak;
    }

    private static float Luminance(SKColor color) => 0.2126f * color.Red + 0.7152f * color.Green + 0.0722f * color.Blue;

    private static byte[] SolidWallpaper(SKColor color) => Wallpaper(canvas =>
    {
        using var paint = new SKPaint { Color = color };
        canvas.DrawRect(0, 0, WallWidth, WallHeight, paint);
    });

    private static byte[] GridWallpaper(int width, int height, int step) => Wallpaper(canvas =>    {
        using (var gradient = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(width, height),
                   new[] { new SKColor(20, 90, 122), new SKColor(65, 139, 150), new SKColor(220, 161, 112), new SKColor(105, 60, 127) },
                   null, SKShaderTileMode.Clamp))
        using (var paint = new SKPaint { Shader = gradient })
            canvas.DrawRect(0, 0, width, height, paint);

        using var grid = new SKPaint { Color = new SKColor(255, 255, 255, 90), StrokeWidth = 1, IsAntialias = false };
        for (var x = 0; x <= width; x += step) canvas.DrawLine(x, 0, x, height, grid);
        for (var y = 0; y <= height; y += step) canvas.DrawLine(0, y, width, y, grid);
    });

    /// <summary>A smooth horizontal ramp: pixel change under refraction is proportional to displacement.</summary>
    private static byte[] RampWallpaper() => Wallpaper(canvas =>
    {
        using var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(WallWidth, 0),
            new[] { new SKColor(16, 18, 22), new SKColor(240, 240, 236) }, null, SKShaderTileMode.Clamp);
        using var paint = new SKPaint { Shader = shader };
        canvas.DrawRect(0, 0, WallWidth, WallHeight, paint);
    });

    /// <summary>A fine checkerboard: high-frequency content that any sampling offset shows up on.</summary>
    private static byte[] CheckerWallpaper(int width, int height, int size) => Wallpaper(canvas =>
    {
        using var light = new SKPaint { Color = new SKColor(232, 236, 240) };
        using var dark = new SKPaint { Color = new SKColor(26, 30, 38) };
        canvas.DrawRect(0, 0, width, height, light);
        for (var y = 0; y < height; y += size)
            for (var x = 0; x < width; x += size)
                if (((x / size) + (y / size)) % 2 == 0)
                    canvas.DrawRect(x, y, size, size, dark);
    });

    private static byte[] Wallpaper(Action<SKCanvas> draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(WallWidth, WallHeight));
        draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {message}");
            Console.ResetColor();
            Environment.Exit(1);
        }
        Console.WriteLine($"  [PASS] {message}");
    }
}
