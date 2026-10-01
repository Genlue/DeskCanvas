namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The infrastructure-level part of a wallpaper-sampled glass material: the fields the
/// sampling pipeline itself consumes, independent of the optical recipe drawn on top.
/// <para>
/// Both <see cref="LiquidGlassSettings"/> (液态玻璃) and <see cref="LiquidGlassV2Settings"/>
/// (新液态玻璃) implement this, so the shared machinery — the backdrop cache, the wallpaper
/// alignment offsets and the live sampler — reads the active material through
/// <see cref="Theme.EffectiveGlass"/> without branching per surface everywhere.
/// </para>
/// </summary>
public interface IRenderedGlassSettings
{
    /// <summary>Backdrop blur strength (0-100); the pipeline maps it to a Gaussian sigma.</summary>
    double Blur { get; }

    /// <summary>
    /// The resolution the shared blurred backdrop is built at, as a percentage of the
    /// desktop's long side (100 = native pixels).
    /// </summary>
    double BackdropClarity { get; }

    /// <summary>Manual wallpaper alignment offset in DIPs, horizontal.</summary>
    double WallpaperOffsetX { get; }

    /// <summary>Manual wallpaper alignment offset in DIPs, vertical.</summary>
    double WallpaperOffsetY { get; }

    /// <summary>Continuously sample the wallpaper; false freezes the latest frame.</summary>
    bool LiveSampling { get; }

    /// <summary>Target sampling interval in milliseconds; frames are dropped under load.</summary>
    int LiveSamplingInterval { get; }
}
