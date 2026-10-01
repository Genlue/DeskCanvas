namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Frequency of automatic software update checks.
/// </summary>
public enum UpdateCheckInterval
{
    /// <summary>Disabled: Never check for updates automatically.</summary>
    Disabled = 0,

    /// <summary>Check for updates once on application startup.</summary>
    OnStartup = 1,

    /// <summary>Check for updates every 6 hours.</summary>
    Every6Hours = 2,

    /// <summary>Check for updates every 12 hours.</summary>
    Every12Hours = 3,

    /// <summary>Check for updates once a day (default).</summary>
    Daily = 4,

    /// <summary>Check for updates once a week.</summary>
    Weekly = 5
}
