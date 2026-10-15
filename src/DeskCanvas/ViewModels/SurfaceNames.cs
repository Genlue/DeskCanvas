using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;

namespace DeskCanvas.ViewModels;

/// <summary>
/// Display names of the surface materials (主题) — the single mapping shared by the 应用主题
/// preset cards (<c>ThemeButton</c>) and the 失焦自动切换 per-state theme combos, so the two
/// lists can never drift apart.
/// </summary>
public static class SurfaceNames
{
    /// <summary>The display name of a surface material.</summary>
    public static string Of(SurfaceStyle surface) => surface switch
    {
        SurfaceStyle.LiquidGlassV2 => Locale.Settings_Appearance_Surface_LiquidGlassV2,
        SurfaceStyle.LiquidGlass or SurfaceStyle.SoftGlow => Locale.Settings_Appearance_Surface_LiquidGlass,
        SurfaceStyle.Colorful => Locale.Settings_Appearance_Surface_Colorful,
        SurfaceStyle.Solid => Locale.Settings_Appearance_Surface_Solid,
        _ => Locale.Settings_Appearance_Surface_Frosted // Acrylic / OutlinedAcrylic
    };
}
