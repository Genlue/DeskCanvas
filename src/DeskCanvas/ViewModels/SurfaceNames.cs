using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;

namespace DeskCanvas.ViewModels;

/// <summary>
/// Display names of the surface materials (主题) — the single mapping shared by the 应用主题
/// preset cards (<c>ThemeButton</c>) and the 失焦自动切换 per-state theme combos.
/// </summary>
public static class SurfaceNames
{
    /// <summary>The display name of a surface material.</summary>
    public static string Of(SurfaceStyle surface) => surface switch
    {
        SurfaceStyle.LiquidGlass => Locale.Settings_Appearance_Surface_LiquidGlass,
        SurfaceStyle.FullColor => Locale.Settings_Appearance_Surface_Colorful,
        _ => Locale.Settings_Appearance_Surface_Frosted // Acrylic
    };
}
