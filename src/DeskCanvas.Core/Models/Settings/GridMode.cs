namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Grid layout mode.
/// </summary>
public enum GridMode
{
    /// <summary>
    /// Custom manual grid (default): the user divides the desktop into an m x n grid of
    /// square cells (<see cref="Grid"/>); widgets snap to cells and adopt the
    /// cell size (or a whole multiple of it) instead of being freely resizable.
    /// <para><c>Manual</c> is the default — the app starts with the grid system active.</para>
    /// </summary>
    Manual = 0,

    /// <summary>
    /// Free layout: no grid at all, widgets can be positioned and sized freely.
    /// </summary>
    Free = 2,

    /// <summary>
    /// Deprecated: Virtual grid mode has been removed.
    /// </summary>
    [System.Obsolete("Virtual grid has been removed. Use Manual or Free.")]
    Virtual = 1
}
