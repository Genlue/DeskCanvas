namespace Clock.Models;

/// <summary>
/// Controls how seconds are presented when <see cref="FlipClockModel.ShowSeconds"/> is enabled.
/// </summary>
public enum FlipSecondsStyle
{
    /// <summary>Three equal split-flap cards side-by-side (<c>HH : MM : SS</c>).</summary>
    FlipCard = 0,

    /// <summary>Two large hero cards (<c>HH : MM</c>) with seconds tucked into the bottom-right corner of the minute card.</summary>
    Badge = 1
}

/// <summary>
/// Controls where the date and weekday are displayed when <see cref="FlipClockModel.ShowDate"/> is enabled.
/// </summary>
public enum FlipDatePosition
{
    /// <summary>Compact header row above the flip cards.</summary>
    TopBar = 0,

    /// <summary>Compact footer row below the flip cards.</summary>
    BottomBar = 1,

    /// <summary>Embedded inside the top-right corner of the minute flip card so the cards fill the full height.</summary>
    CardOverlay = 2
}

/// <summary>
/// Configuration model for the 4×2 mechanical split-flap clock widget.
/// </summary>
public record FlipClockModel(
    bool Use24Hours = false,
    bool ShowSeconds = false,
    FlipSecondsStyle SecondsStyle = FlipSecondsStyle.FlipCard,
    bool ShowDate = true,
    FlipDatePosition DatePosition = FlipDatePosition.TopBar,
    string? TimeZoneId = null)
{
    public FlipClockModel() : this(false) { }
}
