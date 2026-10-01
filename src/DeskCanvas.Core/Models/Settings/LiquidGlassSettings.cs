namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Static glass optics, shared by the two wallpaper-sampled materials
/// (<see cref="SurfaceStyle.LiquidGlass"/> and <see cref="SurfaceStyle.SoftGlow"/>).
/// Distances are in DIPs; strengths are in percent.
/// </summary>
/// <param name="Blur">Backdrop blur strength (0-100); the renderer maps it to a Gaussian sigma.</param>
/// <param name="Refraction">Lens displacement strength (0-100%).</param>
/// <param name="EdgeWidth">
/// Lens band width in DIPs (0-80): how far the refraction reaches inwards.
/// <c>0</c> removes the lens ring entirely — the material then only diffuses, dyes and
/// lights the rim (a pure 柔光 / frosted-glass look with no bending).
/// </param>
/// <param name="Highlight">Rim line + specular strength (0-100).</param>
/// <param name="Dispersion">Chromatic dispersion strength (0-100).</param>
/// <param name="LightAngle">Light direction in degrees (0-360; 225 = top-left).</param>
/// <param name="EdgeTint">Edge colour tint strength (0-100%) — the soft colored rim at the glass border.</param>
/// <param name="WallpaperOffsetX">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="WallpaperOffsetY">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="Glow">
/// 柔光晕 strength (0-100%) of <see cref="SurfaceStyle.SoftGlow"/>: a broad, diffused
/// bloom around the rim instead of the crisp highlight line. <c>0</c> (default) keeps
/// the 液态玻璃 look, so old configurations and the LiquidGlass preset are unchanged.
/// </param>
/// <param name="Spectrum">
/// Spectral dispersion (0-100%) of <see cref="SurfaceStyle.SoftGlow"/>: blends the
/// three-tap RGB split towards a seven-tap spectrum (red→violet). <c>0</c> (default)
/// keeps the historic three-tap dispersion.
/// </param>
/// <param name="DyeSpread">
/// 染色扩散: how far the edge dye of <see cref="SurfaceStyle.SoftGlow"/> reaches
/// <b>inwards</b> (0-100%). <c>0</c> keeps the colour in the outermost band only —
/// a clean dyed rim with no wash over the content — while <c>100</c> spreads it deep
/// into the card. It scales both the dye band's width and the ambient (interior)
/// share of the aura; the sideways 晕染 between neighbouring rim colours is
/// unaffected. Ignored by <see cref="SurfaceStyle.LiquidGlass"/>.
/// </param>
/// <param name="LiveSampling">Continuously sample the wallpaper; false (the default) freezes the latest frame — the static material.</param>
/// <param name="LiveSamplingInterval">Target interval in milliseconds; frames are dropped under load.</param>
/// <param name="BackdropClarity">
/// 背景清晰度: the resolution the shared blurred backdrop is built at, as a percentage of the
/// desktop's long side (100 = native pixels).
/// <para>
/// This is the one knob that trades the sampling round's <i>cost</i> — not its correctness —
/// against detail. Measured on a 2560×1440 desktop, the downscale + blur of the backdrop costs
/// ~26 ms at 100% and ~5 ms at 50%, and it is the blur that costs, not the resampling. The desktop
/// grab itself is ~20 ms and is unaffected either way, because <c>PrintWindow</c> cannot render
/// into a smaller target (it crops). Since the material blurs the backdrop and then samples it
/// through a lens, the reduced grid is not visible.
/// </para>
/// </param>
public record LiquidGlassSettings(
    double Blur = 100,
    double Refraction = 50,
    double EdgeWidth = 10,
    double Highlight = 50,
    double Dispersion = 100,
    double LightAngle = 225,
    double EdgeTint = 25,
    double WallpaperOffsetX = 0,
    double WallpaperOffsetY = 0,
    double Glow = 0,
    double Spectrum = 0,
    double DyeSpread = 0,
    bool LiveSampling = false,
    int LiveSamplingInterval = 5,     // = DefaultLiveSamplingInterval; a primary-constructor default cannot name it
    double BackdropClarity = 25)      // = DefaultBackdropClarity
    : IRenderedGlassSettings          // the sampling-pipeline subset shared with LiquidGlassV2Settings
{
    /// <summary>
    /// Default 染色扩散 (%). The dye stays in the outermost band: a wide wash over the content
    /// reads as dirty, so the factory recipe confines the colour to the rim.
    /// </summary>
    public const double DefaultDyeSpread = 0;

    /// <summary>
    /// True when either of the soft recipe's signature ingredients is switched on. The soft
    /// material is no longer a separate theme; turning up 柔光晕 or 光谱弥散 is what selects it.
    /// </summary>
    public bool IsSoftRecipe => Glow > 0 || Spectrum > 0;

    /// <summary>
    /// Default 背景清晰度 (%). A quarter of the desktop keeps the backdrop build cheap even on
    /// the fastest sampling interval — and the material's own blur hides the reduced grid.
    /// </summary>
    public const double DefaultBackdropClarity = 25;

    /// <summary>
    /// Lowest 背景清晰度 (%). A quarter of the desktop still reads as a blurred wallpaper behind
    /// the lens; below that the backdrop starts to look like flat colour.
    /// </summary>
    public const double MinBackdropClarity = 25;

    /// <summary>Slowest live sampling rate: 1 fps.</summary>
    public const int MaxLiveSamplingInterval = 1000;

    /// <summary>
    /// Fastest live sampling rate: 3 ms — effectively "as fast as this machine can go".
    /// <para>
    /// The sampler is demand-driven: a tick only asks for a new frame, and a new desktop capture
    /// actually happens once the previous one has been consumed and rendered. So a very small
    /// interval does not grab the desktop hundreds of times a second, it just stops the interval
    /// from being the bottleneck.
    /// </para>
    /// </summary>
    public const int MinLiveSamplingInterval = 3;

    /// <summary>
    /// Default live sampling interval: 5 ms — with the demand-driven sampler this reads as
    /// "as fast as this machine can go". The static material (the <c>LiveSampling</c> default)
    /// never samples at all.
    /// </summary>
    public const int DefaultLiveSamplingInterval = 5;

    /// <summary>Dye band width as a fraction of the card's short side, at 染色扩散 = 0 / 100.</summary>
    public const double MinDyeBandFraction = 0.04;

    /// <summary>Dye band width as a fraction of the card's short side at 染色扩散 = 100.</summary>
    public const double MaxDyeBandFraction = 0.14;
    /// <summary>Default edge tint strength (%) — the soft colored rim at the glass border.</summary>
    public const double DefaultEdgeTint = 25;

    /// <summary>How much the auto-derived rim color is chroma-boosted (0-1).</summary>
    public const double DefaultEdgeTintChromaBoost = 0.55;

    /// <summary>Manual wallpaper alignment limit (DIPs) for the calibration dialog.</summary>
    public const double WallpaperOffsetLimit = 1000;

    /// <summary>
    /// Factory optics of the 柔光玻璃 preset: gentle, wide refraction with a diffused
    /// luminous rim (Glow) and a fine spectral dispersion (Spectrum). The heavy lifting
    /// is done by the renderer's soft-glow recipe; these values only steer its intensity.
    /// </summary>
    public static LiquidGlassSettings SoftGlowPreset { get; } = new(
        Blur: 10,
        Refraction: 16,
        EdgeWidth: 40,
        Highlight: 46,
        Dispersion: 24,
        LightAngle: 225,
        EdgeTint: 40,
        WallpaperOffsetX: 0,
        WallpaperOffsetY: 0,
        Glow: 70,
        Spectrum: 62,
        DyeSpread: 55);

    /// <summary>Keep imported or hand-edited values finite and within the UI ranges.</summary>
    public LiquidGlassSettings Normalize() => this with
    {
        Blur = Clamp(Blur, 0, 100, 100),
        Refraction = Clamp(Refraction, 0, 100, 50),
        EdgeWidth = Clamp(EdgeWidth, 0, 80, 10),
        Highlight = Clamp(Highlight, 0, 100, 50),
        Dispersion = Clamp(Dispersion, 0, 100, 100),
        LightAngle = Clamp(LightAngle, 0, 360, 225),
        EdgeTint = Clamp(EdgeTint, 0, 100, DefaultEdgeTint),
        WallpaperOffsetX = Clamp(WallpaperOffsetX, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        WallpaperOffsetY = Clamp(WallpaperOffsetY, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        Glow = Clamp(Glow, 0, 100, 0),
        Spectrum = Clamp(Spectrum, 0, 100, 0),
        DyeSpread = Clamp(DyeSpread, 0, 100, DefaultDyeSpread),
        LiveSamplingInterval = Math.Clamp(LiveSamplingInterval, MinLiveSamplingInterval, MaxLiveSamplingInterval),
        BackdropClarity = Clamp(BackdropClarity, MinBackdropClarity, 100, DefaultBackdropClarity)
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
