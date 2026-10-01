namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Static optics of the 新液态玻璃 material (<see cref="SurfaceStyle.LiquidGlassV2"/>), a faithful
/// port of Kyant0/AndroidLiquidGlass 2.0's optical model:
/// <list type="bullet">
///   <item><description><b>Vibrancy</b> — a saturation boost (the library's <c>vibrancy()</c> is
///   saturation ×1.5), the environment keeps and intensifies its colour instead of being
///   frosted over.</description></item>
///   <item><description><b>Light blur</b> — the backdrop stays readable; the library's demos use
///   2–8 px blurs or none at all.</description></item>
///   <item><description><b>Lens</b> — refraction lives in a rim band whose width is half the
///   peak displacement, the displacement follows a quarter-circle (<c>1 - sqrt(1 - x²)</c>) that
///   peaks exactly at the outline and is zero across the interior, and it samples inward — the
///   background reads as magnified behind a thick glass edge
///   (<see cref="Refraction"/> → <see cref="RefractionAmountMaxFrac"/> of the short side).</description></item>
///   <item><description><b>Depth effect</b> — the rim gradient blends with the radial direction,
///   giving the glass thickness (library's <c>depthEffect = true</c>).</description></item>
///   <item><description><b>Highlight</b> — a hairline stroke (0.5 DIP, feathered) along the
///   outline, white at 50% alpha in Plus blending, lit by |dot(edge normal, light)|^falloff:
///   the top-left and bottom-right diagonals glow, the other two stay dark
///   (<see cref="Highlight"/> scales the alpha).</description></item>
/// </list>
/// Distances are in DIPs; strengths are in percent.
/// </summary>
/// <param name="Blur">模糊 (0-100): the light backdrop blur; the pipeline maps it to a Gaussian sigma.</param>
/// <param name="Refraction">
/// 折射 (0-100): lens strength. Peak displacement at the outline is
/// <see cref="RefractionAmountMaxFrac"/> × this value of the card's short side; the band width is
/// half of that, matching the library's height/amount ratio.
/// </param>
/// <param name="Highlight">
/// 边缘高光 (0-100): the hairline rim stroke's additive strength; 0 removes it.
/// </param>
/// <param name="Vibrancy">
/// 活力 (0-100): the iOS vibrancy colour boost; the saturation multiplier is
/// 1 + <see cref="VibrancySaturationBoost"/> × this value (33 ≈ the library's ×1.5).
/// </param>
/// <param name="Dispersion">
/// 色散 (0-100): the seven-tap spectral split along the refracted displacement, scaled by the
/// diagonal quadrant factor of the library's chromatic aberration.
/// </param>
/// <param name="WallpaperOffsetX">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="WallpaperOffsetY">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="LiveSampling">Continuously sample the wallpaper so animated backgrounds stay live behind the glass.</param>
/// <param name="LiveSamplingInterval">Target interval in milliseconds; frames are dropped under load.</param>
/// <param name="BackdropClarity">
/// 背景清晰度: the resolution the shared blurred backdrop is built at, as a percentage of the
/// desktop's long side (100 = native pixels). The material leans on the lens rather than a heavy
/// pre-blur, so it defaults richer than 液态玻璃's 25%.
/// </param>
public record LiquidGlassV2Settings(
    double Blur = 50,
    double Refraction = 100,
    double Highlight = 50,
    double Vibrancy = 33,
    double Dispersion = 30,
    double WallpaperOffsetX = 0,
    double WallpaperOffsetY = 0,
    bool LiveSampling = true,
    int LiveSamplingInterval = 10,
    double BackdropClarity = 40) : IRenderedGlassSettings
{
    /// <summary>Peak rim displacement as a fraction of the card's short side at <see cref="Refraction"/> = 100.
    /// The library's playground default is 0.2 of the short side.</summary>
    public const double RefractionAmountMaxFrac = 0.4;

    /// <summary>Refraction band width as a fraction of the peak displacement — the library's
    /// height/amount ratio (12dp/24dp, 24dp/48dp).</summary>
    public const double BandToAmountRatio = 0.5;

    /// <summary>
    /// 边缘高光 (0-100): the hairline rim stroke's additive strength, 100 = the library default.
    /// The library forces the stroke colour's alpha to 1 and blends additively (Plus), so 100
    /// really is full white — tone it down here.
    /// </summary>
    public const double HighlightReference = 100.0;

    /// <summary>
    /// Nominal stroke width in DIPs. The library's paint layer widens it to
    /// <c>ceil(0.5dp in px) × 2</c> and clips to the outline, so the visible inner band is
    /// <c>ceil(0.5dp in px)</c> — at least one whole pixel, unlike the raw 0.5dp.
    /// </summary>
    public const double StrokeWidthDips = 0.5;

    /// <summary>Stroke softness in DIPs (the library blurs the highlight paint by width / 2).</summary>
    public const double StrokeFeatherDips = 0.25;

    /// <summary>Light-direction falloff exponent of the highlight (the library's Default style).</summary>
    public const double HighlightFalloff = 1.0;

    /// <summary>Light direction in degrees, screen coordinates (45° with |dot| lighting lights the
    /// top-left and bottom-right diagonals, the iOS double rim).</summary>
    public const double HighlightAngleDegrees = 45.0;

    /// <summary>Saturation boost added by <see cref="Vibrancy"/> = 100 (the library's vibrancy is ×1.5 at the factory).</summary>
    public const double VibrancySaturationBoost = 1.5;

    /// <summary>The gradient radius of the lens/highlight field (the library's <c>min(radius * 1.5, min half size)</c>).</summary>
    public const double GradRadiusFactor = 1.5;

    /// <summary>Slowest live sampling rate: 1 fps.</summary>
    public const int MaxLiveSamplingInterval = LiquidGlassSettings.MaxLiveSamplingInterval;

    /// <summary>Fastest live sampling rate — shared with 液态玻璃's demand-driven sampler.</summary>
    public const int MinLiveSamplingInterval = LiquidGlassSettings.MinLiveSamplingInterval;

    /// <summary>Default live sampling interval: "as fast as this machine can go".</summary>
    public const int DefaultLiveSamplingInterval = LiquidGlassSettings.DefaultLiveSamplingInterval;

    /// <summary>Lowest 背景清晰度 (%), shared with 液态玻璃.</summary>
    public const double MinBackdropClarity = LiquidGlassSettings.MinBackdropClarity;

    /// <summary>Manual wallpaper alignment limit (DIPs) for the calibration dialog.</summary>
    public const double WallpaperOffsetLimit = LiquidGlassSettings.WallpaperOffsetLimit;

    /// <summary>Keep imported or hand-edited values finite and within the UI ranges.</summary>
    public LiquidGlassV2Settings Normalize() => this with
    {
        Blur = Clamp(Blur, 0, 100, 50),
        Refraction = Clamp(Refraction, 0, 100, 100),
        Highlight = Clamp(Highlight, 0, 100, 50),
        Vibrancy = Clamp(Vibrancy, 0, 100, 33),
        Dispersion = Clamp(Dispersion, 0, 100, 30),
        WallpaperOffsetX = Clamp(WallpaperOffsetX, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        WallpaperOffsetY = Clamp(WallpaperOffsetY, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        LiveSamplingInterval = Math.Clamp(LiveSamplingInterval, MinLiveSamplingInterval, MaxLiveSamplingInterval),
        BackdropClarity = Clamp(BackdropClarity, MinBackdropClarity, 100, 40)
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
