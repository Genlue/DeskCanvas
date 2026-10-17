namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Static optics of the 液态玻璃 material (<see cref="SurfaceStyle.LiquidGlass"/>), a faithful
/// port of Kyant0/AndroidLiquidGlass 2.0's optical model:
/// <list type="bullet">
///   <item><description><b>Vibrancy</b> — saturation boost (vibrancy() is saturation ×1.5).</description></item>
///   <item><description><b>Light blur</b> — light backdrop blur; readable background.</description></item>
///   <item><description><b>Lens</b> — rounded-rect refraction rim band capped by corner radius.</description></item>
///   <item><description><b>Depth effect</b> — rim gradient blended with radial direction.</description></item>
///   <item><description><b>Highlight</b> — specular highlight along outline lit by direction |dot(normal, light)|^falloff.</description></item>
/// </list>
/// </summary>
/// <param name="Blur">模糊 (0-100): light backdrop blur.</param>
/// <param name="Refraction">折射 (0-100): lens displacement strength.</param>
/// <param name="Highlight">边缘高光 (0-100): hairline rim stroke additive strength.</param>
/// <param name="HighlightAngle">镜面高光反射角度 (0-360°), 默认 90.0°.</param>
/// <param name="Vibrancy">活力 (0-100): vibrancy colour boost.</param>
/// <param name="Dispersion">色散 (0-100): seven-tap spectral dispersion.</param>
/// <param name="WallpaperOffsetX">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="WallpaperOffsetY">Manual wallpaper alignment offset in DIPs.</param>
/// <param name="LiveSampling">Continuously sample the wallpaper; false (default) freezes the latest frame.</param>
/// <param name="LiveSamplingInterval">Target interval in milliseconds; frames are dropped under load.</param>
/// <param name="BackdropClarity">背景清晰度: backdrop resolution percentage (100 = native pixels).</param>
public record LiquidGlassSettings(
    double Blur = 50,
    double Refraction = 100,
    double Highlight = 50,
    double HighlightAngle = 90.0,
    double Vibrancy = 33,
    double Dispersion = 30,
    double WallpaperOffsetX = 0,
    double WallpaperOffsetY = 0,
    bool LiveSampling = false,
    int LiveSamplingInterval = 10,
    double BackdropClarity = 40) : IRenderedGlassSettings
{
    /// <summary>Peak rim displacement as a fraction of the card's short side at <see cref="Refraction"/> = 100.</summary>
    public const double RefractionAmountMaxFrac = 0.4;

    /// <summary>Refraction band width as a fraction of the peak displacement.</summary>
    public const double BandToAmountRatio = 0.5;

    /// <summary>Highlight strength reference.</summary>
    public const double HighlightReference = 100.0;

    /// <summary>Nominal stroke width in DIPs.</summary>
    public const double StrokeWidthDips = 0.5;

    /// <summary>Stroke softness in DIPs.</summary>
    public const double StrokeFeatherDips = 0.25;

    /// <summary>Light-direction falloff exponent of the highlight.</summary>
    public const double HighlightFalloff = 1.0;

    /// <summary>Default specular highlight angle in degrees (90°).</summary>
    public const double DefaultHighlightAngle = 90.0;

    /// <summary>Historic angle constant alias.</summary>
    public const double HighlightAngleDegrees = 90.0;

    /// <summary>Light angle alias for backward compatibility.</summary>
    public double LightAngle => HighlightAngle;

    /// <summary>Saturation boost added by <see cref="Vibrancy"/> = 100.</summary>
    public const double VibrancySaturationBoost = 1.5;

    /// <summary>The gradient radius factor of the lens/highlight field.</summary>
    public const double GradRadiusFactor = 1.5;

    /// <summary>Corner-aware ceiling on the refraction band, as a multiple of corner radius.</summary>
    public const double CornerBandCapFactor = 0.8;

    /// <summary>Width of the smooth cap's transition, as a fraction of the cap itself.</summary>
    public const double CornerBandCapTransitionFrac = 0.1;

    /// <summary>Slowest live sampling rate: 1 fps.</summary>
    public const int MaxLiveSamplingInterval = 1000;

    /// <summary>Fastest live sampling rate: ~100 fps.</summary>
    public const int MinLiveSamplingInterval = 10;

    /// <summary>Default live sampling interval: 10 ms.</summary>
    public const int DefaultLiveSamplingInterval = 10;

    /// <summary>Default live sampling state (false = static wallpaper mode).</summary>
    public const bool DefaultLiveSampling = false;

    /// <summary>Lowest 背景清晰度 (%).</summary>
    public const double MinBackdropClarity = 25.0;

    /// <summary>Default 背景清晰度 (%).</summary>
    public const double DefaultBackdropClarity = 40.0;

    /// <summary>Manual wallpaper alignment limit (DIPs).</summary>
    public const double WallpaperOffsetLimit = 200.0;

    /// <summary>Keep imported or hand-edited values finite and within the UI ranges.</summary>
    public LiquidGlassSettings Normalize() => this with
    {
        Blur = Clamp(Blur, 0, 100, 50),
        Refraction = Clamp(Refraction, 0, 100, 100),
        Highlight = Clamp(Highlight, 0, 100, 50),
        HighlightAngle = Clamp(HighlightAngle, 0, 360, DefaultHighlightAngle),
        Vibrancy = Clamp(Vibrancy, 0, 100, 33),
        Dispersion = Clamp(Dispersion, 0, 100, 30),
        WallpaperOffsetX = Clamp(WallpaperOffsetX, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        WallpaperOffsetY = Clamp(WallpaperOffsetY, -WallpaperOffsetLimit, WallpaperOffsetLimit, 0),
        LiveSamplingInterval = Math.Clamp(LiveSamplingInterval, MinLiveSamplingInterval, MaxLiveSamplingInterval),
        BackdropClarity = Clamp(BackdropClarity, MinBackdropClarity, 100, DefaultBackdropClarity)
    };

    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
