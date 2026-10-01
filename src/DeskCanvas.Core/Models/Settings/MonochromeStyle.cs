namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// How monochrome widgets color their decorative elements (icons, hands, rings).
/// </summary>
public enum MonochromeStyle
{
    /// <summary>黑白: black in light mode, white in dark mode.</summary>
    BlackWhite = 0,

    /// <summary>强调色: accent color (darker variant in light mode, lighter in dark mode).</summary>
    Accent = 1
}
