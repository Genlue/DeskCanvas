namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Widget sizing and positioning settings.
/// </summary>
/// <param name="GridMode">Grid layout mode (manual by default / virtual / free).</param>
/// <param name="SnapSize">Should widget's size be snapped to the grid (Virtual mode; ignored in Manual mode where the size is always grid-driven)</param>
/// <param name="LockSize">Disable resizing a widget</param>
/// <param name="SnapPosition">Should widget's position be snapped to the grid (always on in Manual mode)</param>
/// <param name="LockPosition">Disable moving a widget</param>
public record Layout(
    GridMode GridMode,
    bool SnapSize,
    bool LockSize,
    bool SnapPosition,
    bool LockPosition);
