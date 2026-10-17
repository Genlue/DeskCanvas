using System.Text.Json.Serialization;

namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Transition alias for <see cref="LiquidGlassSettings"/>.
/// </summary>
[method: JsonConstructor]
public record LiquidGlassV2Settings(
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
    double BackdropClarity = 40)
    : LiquidGlassSettings(Blur, Refraction, Highlight, HighlightAngle, Vibrancy, Dispersion, WallpaperOffsetX, WallpaperOffsetY, LiveSampling, LiveSamplingInterval, BackdropClarity)
{
    /// <inheritdoc/>
    protected override Type EqualityContract => typeof(LiquidGlassSettings);

    /// <summary>Copy constructor from <see cref="LiquidGlassSettings"/>.</summary>
    public LiquidGlassV2Settings(LiquidGlassSettings s)
        : this(s.Blur, s.Refraction, s.Highlight, s.HighlightAngle, s.Vibrancy, s.Dispersion, s.WallpaperOffsetX, s.WallpaperOffsetY, s.LiveSampling, s.LiveSamplingInterval, s.BackdropClarity) { }

    /// <summary>Normalizes liquid glass settings.</summary>
    public new LiquidGlassV2Settings Normalize() => new(base.Normalize());
}
