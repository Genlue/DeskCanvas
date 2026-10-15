namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Dictates how theme accent and monochrome colors are resolved.
/// </summary>
public enum ThemeColorPolicy
{
    /// <summary>
    /// Standard color policy: respects custom accent color or system accent,
    /// and respects monochrome overrides when enabled.
    /// </summary>
    Normal = 0,

    /// <summary>
    /// Fixed color policy: used exclusively by Colorful surface.
    /// Accent color and monochrome settings are strictly ignored,
    /// and colors are provided by the Colorful palette closure.
    /// </summary>
    ColorfulFixed = 1,
}
