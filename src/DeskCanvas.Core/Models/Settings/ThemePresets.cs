namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The built-in theme presets — one catalog shared by every surface picker in the app (the
/// Appearance page's 应用主题 cards, the 失焦自动切换 state combos, the frameless clock's
/// per-widget material list), so the lists can never drift apart.
/// </summary>
public static class ThemePresets
{
    /// <summary>
    /// The surface-material presets, in the order they are shown: frosted glass, solid,
    /// liquid glass, new liquid glass and colorful (macOS).
    /// <para>
    /// 柔光玻璃 is deliberately <b>not</b> a preset any more: it was merged into 液态玻璃, which is
    /// one surface and one pipeline. The liquid-glass preset below keeps the factory optics so the
    /// default material is unchanged.
    /// </para>
    /// </summary>
    public static readonly Theme[] SurfaceTemplates =
    [
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.4, Monochrome: true, UseNativeFrame: false, FontFamily: "Microsoft YaHei UI", Surface: SurfaceStyle.Acrylic),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.20, Monochrome: true, UseNativeFrame: false, FontFamily: "Microsoft YaHei UI", Surface: SurfaceStyle.LiquidGlass,
            LiquidGlass: new LiquidGlassSettings()),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 1.0, Monochrome: false, UseNativeFrame: false, FontFamily: "Microsoft YaHei UI", Surface: SurfaceStyle.FullColor)
    ];
}
