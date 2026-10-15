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
    /// one surface and one pipeline, and the soft look is reached through the 柔光晕 / 光谱弥散
    /// knobs. Its recipe survives as <see cref="LiquidGlassSettings.SoftGlowPreset"/> for stored
    /// configurations, and the liquid-glass preset below keeps the factory optics so the
    /// default material is unchanged by the merge.
    /// </para>
    /// </summary>
    public static readonly Theme[] SurfaceTemplates =
    [
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.4, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Acrylic),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 1.0, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Solid),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.18, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.LiquidGlass,
            LiquidGlass: new LiquidGlassSettings()),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 0.20, Monochrome: true, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.LiquidGlassV2,
            LiquidGlassV2: new LiquidGlassV2Settings()),
        new(DarkMode: null, AccentColor: null, OpacityLevel: 1.0, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Colorful)
    ];
}
