using DeskCanvas.Core.Models.Settings;

namespace Clock.Services;

/// <summary>The material a frameless clock resolved to for the current frame.</summary>
/// <param name="IsAcrylic">毛玻璃: the OS-level acrylic backdrop behind the numerals.</param>
/// <param name="IsLiquidGlass">液态玻璃 or 新液态玻璃: the app-rendered wallpaper materials.</param>
/// <param name="IsSolid">纯色: a plain vector fill.</param>
/// <param name="IsSoftGlow">柔光玻璃 recipe: the 液态玻璃 pipeline with the soft recipe.</param>
public readonly record struct FramelessMaterial(bool IsAcrylic, bool IsLiquidGlass, bool IsSolid, bool IsSoftGlow)
{
    /// <summary>
    /// True for the materials the app renders itself from a desktop snapshot
    /// (液态玻璃 including its 柔光 recipe, and 新液态玻璃): the numerals go through the
    /// glyph glass pipeline.
    /// </summary>
    public bool IsRenderedGlass => IsLiquidGlass || IsSoftGlow;
}

/// <summary>
/// Maps the <b>global</b> theme onto the frameless clock's rendering branches.
///
/// The clock has no per-widget theme override: a global 液态玻璃 / 新液态玻璃 has to produce the
/// glyph glass pipeline, a global 毛玻璃 the OS acrylic backdrop and a global 纯色 a plain fill.
/// 柔光玻璃 is no longer a separate material — it was merged into 液态玻璃 and selected by the
/// 柔光晕 / 光谱弥散 optics — so <see cref="FramelessMaterial.IsSoftGlow"/> simply reports whether
/// the global theme carries the soft recipe. Under 新液态玻璃 the glyph pass keeps its own
/// lens recipe and reads the legacy optics (which a V2 theme leaves at their defaults). Exposed
/// as a pure function (rather than inlined in the view) so the mapping can be asserted in
/// <c>tests/ClockThemeChecks</c>.
/// </summary>
public static class FramelessThemeResolver
{
    /// <summary>
    /// Resolve the material from the global theme. A missing theme falls back to 毛玻璃, the
    /// historic default.
    /// </summary>
    public static FramelessMaterial Resolve(Theme? globalTheme)
    {
        var acrylic = globalTheme?.UsesNativeBlur ?? true;
        var liquidGlass = globalTheme is { IsLiquidGlass: true } or { IsLiquidGlassV2: true };

        return new FramelessMaterial(
            acrylic,
            liquidGlass,
            !acrylic && !liquidGlass,
            globalTheme?.IsSoftGlow ?? false);
    }
}
