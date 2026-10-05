namespace Clock.Models;

/// <summary>
/// Configuration model for the frameless digital clock widget.
/// <para>
/// The clock is <b>locked to the 毛玻璃 material</b>: its numerals always render over the OS's
/// native acrylic backdrop regardless of the global surface theme, so it carries no material or
/// glass-optics settings at all. The historical <c>ThemeMode</c> (old separate 液态玻璃 /
/// 柔光玻璃), <c>DyeIntensity</c> (边缘染色强度), <c>RefractionWidth</c> (边缘折射宽度) and
/// <c>LiquidGlassOpacity</c> (液态玻璃透明度) properties were all removed along with the
/// rendered-glass pipeline; stale values in <c>layout.json</c> are ignored by deserialization.
/// </para>
/// <para>
/// <c>FontWeight</c> is a 100-900 <b>position</b> on the widget's own weight axis (the 字体粗细
/// slider), not a raw OpenType request: the widget maps it onto the faces the selected family
/// actually provides and grows the strokes synthetically above the reference weight, so the upper
/// part of the slider responds even for a family that ships a single heavy face. See
/// <c>FramelessDigital.ResolveWeightedTypeface</c>.
/// </para>
/// </summary>
public record FramelessClockModel(
    bool Use24Hours = true,
    bool ShowSeconds = false,
    string? TimeZoneId = null,
    string? FontFamily = null,
    int FontWeight = 700,
    bool StretchFill = true,
    bool EnableOverlay = false,
    bool FollowAccentColor = true,
    string OverlayColor = "#400078D4",
    double OverlayOpacity = 0.35)
{
    public FramelessClockModel() : this(true) { }
}
