namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The widget surface material. This is the primary distinction between the
/// appearance presets.
/// </summary>
public enum SurfaceStyle
{
    /// <summary>亚克力 (Acrylic / 原毛玻璃): OS-level backdrop material.</summary>
    Acrylic = 0,

    /// <summary>液态玻璃 (Liquid Glass): static wallpaper refraction, adjustable blur and specular highlights (Kyant0 optical model).</summary>
    LiquidGlass = 2,

    /// <summary>LiquidGlassV2 alias for LiquidGlass.</summary>
    LiquidGlassV2 = LiquidGlass,

    /// <summary>全彩 (Full Color / 原多彩): rich multi-color elements compatible with OS acrylic blur and solid fill.</summary>
    FullColor = 4,

    /// <summary>多彩 (Colorful) alias for FullColor.</summary>
    Colorful = FullColor
}
