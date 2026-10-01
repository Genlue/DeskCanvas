namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Title bar style of the settings window.
/// </summary>
public enum TitleBarStyle
{
    /// <summary>
    /// Native / original look: the app-extended title bar area with the
    /// system window buttons (minimize/maximize/close) provided by the OS.
    /// </summary>
    Native = 0,

    /// <summary>
    /// macOS style: custom-drawn traffic-light buttons (close / minimize /
    /// maximize) at the top-left of a 36px title bar, exactly like macOS.
    /// </summary>
    TrafficLights = 1
}
