namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// How monochrome widgets color their text and decorative elements (icons, hands, rings).
/// </summary>
public enum MonochromeStyle
{
    /// <summary>
    /// 黑白: text stays black in light mode and white in dark mode; the accent is strictly
    /// achromatic too — <b>black in light mode, white in dark mode</b> — so accent-following
    /// elements never pick up the background colors.
    /// </summary>
    BlackWhite = 0,

    /// <summary>强调色: accent color (darker variant in light mode, lighter in dark mode).</summary>
    Accent = 1,

    /// <summary>
    /// 背景色: all text AND accent colors lock to the inverted background — light mode
    /// renders the dark-mode background color, dark mode the light-mode one.
    /// </summary>
    BackgroundColor = 2
}
