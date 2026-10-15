namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Light/dark mode selection of one focus state, in the same shape as the pair on
/// <see cref="Theme"/>: <see cref="DarkMode"/> <c>null</c> = follow the system,
/// <see cref="AutoTheme"/> = follow the wallpaper brightness (which wins over
/// <see cref="DarkMode"/>).
/// </summary>
/// <param name="DarkMode">Explicit dark/light flag; <c>null</c> follows the system.</param>
/// <param name="AutoTheme">Resolve light/dark from the desktop wallpaper instead.</param>
public record FocusMode(bool? DarkMode = null, bool AutoTheme = false)
{
    /// <summary>The follow-the-system choice.</summary>
    public static FocusMode System { get; } = new();
}

/// <summary>
/// 失焦自动切换 (auto theme switching on desktop focus loss).
/// <para>
/// Both states share every appearance parameter of the Appearance page (surface, accent,
/// opacity, glass optics, font …) — the only per-state setting is the light/dark mode pair.
/// When <see cref="Enabled"/> is false the whole feature is dormant and the theme's own
/// <see cref="Theme.DarkMode"/>/<see cref="Theme.AutoTheme"/> drive the app as before.
/// </para>
/// <para>
/// The evaluation is uniform across the whole desktop: while any non-whitelisted
/// application window occupies any screen the state is "unfocused", and the moment the
/// last one is minimized, closed or hidden the state snaps back to "focused". There is no
/// per-screen switching any more — one state, applied everywhere through the same global
/// theme switch the settings page's theme buttons use.
/// </para>
/// </summary>
/// <param name="Enabled">Master switch; default off.</param>
/// <param name="Focused">Light/dark pair used while the desktop is focused.</param>
/// <param name="Unfocused">Light/dark pair used while an application window is focused.</param>
/// <param name="FocusedSurface">
/// Surface material (主题) of the focused state; <c>null</c> follows the shared theme's own
/// surface. Only the material is swapped — every shared appearance parameter (accent, opacity,
/// glass optics, font …) stays the one configured in the Appearance page.
/// </param>
/// <param name="UnfocusedSurface">Surface material of the unfocused state; <c>null</c> follows the shared theme.</param>
/// <param name="Whitelist">
/// Process names (with or without <c>.exe</c>, matched case-insensitively) that count as part
/// of the desktop: while one of them is in the foreground the state stays "focused".
/// </param>
/// <param name="SwitchDelayMs">
/// Anti-flicker dwell: how long a new focus state must persist before the switch is applied
/// (0–1000 ms, clamped; 0 = switch immediately). Transient windows — an app flashing open and
/// shut, a fast Alt+Tab round trip — never outlast the dwell, so the theme no longer flickers.
/// </param>
// The parameter default cannot reference the record's own constants (CS0103) — keep it in
// sync with <see cref="DefaultSwitchDelayMs"/>.
public record FocusLinkSettings(
    bool Enabled,
    FocusMode? Focused = null,
    FocusMode? Unfocused = null,
    SurfaceStyle? FocusedSurface = null,
    SurfaceStyle? UnfocusedSurface = null,
    string[]? Whitelist = null,
    int SwitchDelayMs = 150)
{
    /// <summary>Lower bound of the anti-flicker dwell: 0 means switch immediately.</summary>
    public const int SwitchDelayMinMs = 0;

    /// <summary>Upper bound of the anti-flicker dwell.</summary>
    public const int SwitchDelayMaxMs = 1000;

    /// <summary>The dwell a configuration without an explicit value starts from.</summary>
    public const int DefaultSwitchDelayMs = 150;

    /// <summary>Never-null accessor for the focused-state mode.</summary>
    public FocusMode EffectiveFocused => Focused ?? FocusMode.System;

    /// <summary>Never-null accessor for the unfocused-state mode.</summary>
    public FocusMode EffectiveUnfocused => Unfocused ?? FocusMode.System;

    /// <summary>Never-null accessor for the allowlist.</summary>
    public string[] EffectiveWhitelist => Whitelist ?? [];

    /// <summary>The anti-flicker dwell, clamped to the allowed 0–1000 ms range.</summary>
    public int EffectiveSwitchDelayMs => Math.Clamp(SwitchDelayMs, SwitchDelayMinMs, SwitchDelayMaxMs);

    /// <summary>
    /// The surface override of a state, normalized: 柔光玻璃 folds into 液态玻璃 (the historic
    /// merge), and a null stays null ("follow the shared theme").
    /// </summary>
    public static SurfaceStyle? NormalizeSurface(SurfaceStyle? surface) =>
        surface == SurfaceStyle.SoftGlow ? SurfaceStyle.LiquidGlass : surface;

    /// <summary>
    /// The settings a first-time enable starts from: both states seeded with the theme's
    /// current light/dark pair and no surface override, so flipping the switch changes
    /// nothing visually until the user gives the two states different modes or themes.
    /// </summary>
    public static FocusLinkSettings CreateDefault(bool? darkMode, bool autoTheme) =>
        new(false, new FocusMode(darkMode, autoTheme), new FocusMode(darkMode, autoTheme));
}
