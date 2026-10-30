using DeskCanvas.Core.Models.Settings;

namespace Clock.Models;

/// <summary>
/// Configuration model for the frameless digital clock widget.
/// <para>
/// Supports following the global surface theme (<see cref="SurfaceOverride"/> == <c>null</c>)
/// or explicitly selecting an independent widget material (<see cref="SurfaceStyle.Acrylic"/>,
/// <see cref="SurfaceStyle.LiquidGlass"/>, <see cref="SurfaceStyle.LiquidGlassV2"/>,
/// <see cref="SurfaceStyle.Solid"/>, <see cref="SurfaceStyle.Colorful"/>).
/// </para>
/// <para>
/// <see cref="FontWeight"/> is a 100–900 OpenType weight request passed to native font/variable-axis
/// resolution without synthetic boolean stroke distortion (takes effect on multi-weight and variable fonts).
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
    double OverlayOpacity = 0.35,
    double LiquidGlassOpacity = 100.0,
    SurfaceStyle? SurfaceOverride = null,
    bool FixedWidthDigits = false,
    bool UseSystemFont = false)
{
    public FramelessClockModel() : this(true) { }
}
