namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The widget surface material. This is the primary distinction between the
/// appearance presets.
/// </summary>
public enum SurfaceStyle
{
    /// <summary>毛玻璃 / frosted glass: translucent background + blur.</summary>
    Acrylic = 0,

    /// <summary>纯色: fully opaque background.</summary>
    Solid = 1,

    /// <summary>液态玻璃: static wallpaper refraction, adjustable blur and specular highlights.</summary>
    LiquidGlass = 2,

    /// <summary>
    /// Outlined frosted glass (描边毛玻璃), kept only for compatibility with
    /// configurations saved by earlier builds. Merged into <see cref="Acrylic"/>
    /// since 1.2.0: the outline is now an option of the frost theme itself,
    /// enabled by <see cref="Theme.OutlineWidth"/> &gt; 0 with
    /// <see cref="Theme.OutlineColor"/>.
    /// </summary>
    OutlinedAcrylic = 3,

    /// <summary>多彩 / macOS vibrant widget style: rich multi-color elements compatible with OS acrylic blur and solid fill.</summary>
    Colorful = 4,

    /// <summary>
    /// 柔光玻璃 (soft glow glass): the same wallpaper-sampled static material as
    /// <see cref="LiquidGlass"/>, but tuned for a gentle, luminous look — wide and
    /// shallow refraction instead of a meniscus ring, no crisp highlight line,
    /// a broad diffused glow around the rim (see <see cref="LiquidGlassSettings.Glow"/>)
    /// and an optional full-spectrum dispersion
    /// (see <see cref="LiquidGlassSettings.Spectrum"/>).
    /// </summary>
    SoftGlow = 5,

    /// <summary>
    /// 新液态玻璃 (new liquid glass): an independent optical material built from scratch
    /// against the macOS/iOS 26 Liquid Glass spec — highly transparent, low-saturation,
    /// environment-adaptive glass. Multi-scale diffusion keeps large colour masses while
    /// suppressing fine detail, refraction stays at the rim and in the single-digit
    /// percent range, and the rim light is a thin, soft, environment-tinted highlight
    /// rather than a drawn border. Shares the wallpaper sampling pipeline (capture +
    /// shared blurred backdrop) but runs its own shader and CPU renderer; see
    /// <see cref="LiquidGlassV2Settings"/>.
    /// </summary>
    LiquidGlassV2 = 6
}
