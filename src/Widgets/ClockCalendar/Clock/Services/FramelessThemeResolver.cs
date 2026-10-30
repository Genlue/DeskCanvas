using Clock.Models;
using DeskCanvas.Core.Models.Settings;

namespace Clock.Services;

/// <summary>The material a frameless clock resolved to for the current frame.</summary>
/// <param name="IsAcrylic">毛玻璃: the OS-level acrylic backdrop behind the numerals.</param>
/// <param name="IsLiquidGlass">液态玻璃 or 新液态玻璃: the app-rendered wallpaper materials.</param>
/// <param name="IsSolid">纯色 or 多彩: a vector fill without backdrop blur/refraction.</param>
/// <param name="IsSoftGlow">柔光玻璃 recipe: the 液态玻璃 pipeline with the soft recipe.</param>
/// <param name="IsColorful">多彩: vibrant gradient numeral fill.</param>
/// <param name="EffectiveSurface">The resolved <see cref="SurfaceStyle"/>.</param>
public readonly record struct FramelessMaterial(
    bool IsAcrylic,
    bool IsLiquidGlass,
    bool IsSolid,
    bool IsSoftGlow,
    bool IsColorful = false,
    SurfaceStyle EffectiveSurface = SurfaceStyle.Acrylic)
{
    /// <summary>
    /// True for the materials the app renders itself from a desktop snapshot
    /// (液态玻璃 including its 柔光 recipe, and 新液态玻璃): the numerals go through the
    /// glyph glass pipeline.
    /// </summary>
    public bool IsRenderedGlass => IsLiquidGlass || IsSoftGlow;
}

/// <summary>
/// Maps the global <see cref="Theme"/> and the widget's optional <see cref="FramelessClockModel.SurfaceOverride"/>
/// onto the frameless clock's rendering branches.
/// </summary>
public static class FramelessThemeResolver
{
    private static readonly Theme DefaultTheme = new(
        DarkMode: null,
        AccentColor: null,
        OpacityLevel: 0.18,
        Monochrome: false,
        UseNativeFrame: false,
        FontFamily: "Segoe UI",
        Surface: SurfaceStyle.Acrylic);

    /// <summary>
    /// Produce the effective <see cref="Theme"/> for the frameless clock by merging any per-widget
    /// <paramref name="surfaceOverride"/> onto <paramref name="globalTheme"/>.
    /// </summary>
    public static Theme EffectiveTheme(Theme? globalTheme, SurfaceStyle? surfaceOverride = null)
    {
        var baseTheme = globalTheme ?? DefaultTheme;
        if (surfaceOverride is { } explicitSurface && explicitSurface != baseTheme.EffectiveSurface)
        {
            return explicitSurface switch
            {
                SurfaceStyle.Colorful => baseTheme with { Surface = SurfaceStyle.Colorful, OpacityLevel = 1.0, Monochrome = false },
                SurfaceStyle.LiquidGlass => baseTheme with
                {
                    Surface = SurfaceStyle.LiquidGlass,
                    OpacityLevel = baseTheme.OpacityLevel >= 0.9 ? 0.18 : baseTheme.OpacityLevel,
                    LiquidGlass = baseTheme.LiquidGlass ?? new LiquidGlassSettings()
                },
                _ => baseTheme with
                {
                    Surface = SurfaceStyle.Acrylic,
                    OpacityLevel = baseTheme.OpacityLevel >= 0.9 ? 0.40 : baseTheme.OpacityLevel
                }
            };
        }
        return baseTheme;
    }

    /// <summary>
    /// Resolve the material from the global theme (no per-widget override). A missing theme falls
    /// back to 毛玻璃, the historic default.
    /// </summary>
    public static FramelessMaterial Resolve(Theme? globalTheme) =>
        Resolve(globalTheme, surfaceOverride: null);

    /// <summary>
    /// Resolve the material from the widget model and the global theme.
    /// </summary>
    public static FramelessMaterial Resolve(FramelessClockModel? model, Theme? globalTheme) =>
        Resolve(globalTheme, model?.SurfaceOverride);

    /// <summary>
    /// Resolve the material from the global theme and an optional per-widget surface override.
    /// </summary>
    public static FramelessMaterial Resolve(Theme? globalTheme, SurfaceStyle? surfaceOverride)
    {
        if (globalTheme == null && surfaceOverride == null)
        {
            return new FramelessMaterial(
                IsAcrylic: true,
                IsLiquidGlass: false,
                IsSolid: false,
                IsSoftGlow: false,
                IsColorful: false,
                EffectiveSurface: SurfaceStyle.Acrylic);
        }

        var effective = EffectiveTheme(globalTheme, surfaceOverride);
        var surface = effective.EffectiveSurface;
        var acrylic = effective.UsesNativeBlur;
        var liquidGlass = effective.IsLiquidGlass;
        var colorful = effective.IsColorful;
        var solid = false;
        var softGlow = false;

        return new FramelessMaterial(
            IsAcrylic: acrylic,
            IsLiquidGlass: liquidGlass,
            IsSolid: solid,
            IsSoftGlow: softGlow,
            IsColorful: colorful,
            EffectiveSurface: surface);
    }
}
