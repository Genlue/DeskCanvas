namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Service for resizing and positioning windows in a grid.
/// </summary>
/// <typeparam name="T">Type of window. For Avalonia, use <c>Avalonia.Controls.Window</c></typeparam>
public interface IGridService<in T>
{
    /// <summary>
    /// Set the width and height of the window in grid units.
    /// </summary>
    /// <example>
    /// <c>SetSize(this, 2, 2);</c> - small size<br/>
    /// <c>SetSize(this, 4, 2);</c> - medium size<br/>
    /// <c>SetSize(this, 4, 4);</c> - large size<br/>
    /// <c>SetSize(this, 8, 4);</c> - extra large size
    /// </example>
    /// <param name="window">Window to resize</param>
    /// <param name="columns">Columns span</param>
    /// <param name="rows">Rows span</param>
    public void SetSize(T window, int columns, int rows);
    
    /// <summary>
    /// Set the width and height of the window to the nearest whole number of grid units.
    /// </summary>
    /// <param name="window">Window to resize</param>
    public void SnapSize(T window);
    
    /// <summary>
    /// Set the position of the window to the nearest whole number of grid units.
    /// </summary>
    /// <param name="window">Window to reposition</param>
    public void SnapPosition(T window);

    /// <summary>
    /// The rectangle <see cref="SnapPosition"/> would move <paramref name="window"/> to, without
    /// moving it: the widget's own screen lattice, clamped inside the grid.
    /// </summary>
    /// <param name="window">Window to resolve for.</param>
    /// <param name="proposedX">Proposed window origin X (physical pixels).</param>
    /// <param name="proposedY">Proposed window origin Y (physical pixels).</param>
    public GridTarget ResolveTarget(T window, int proposedX, int proposedY);

    /// <summary>
    /// As <see cref="ResolveTarget"/>, but resolved against the screen the PROPOSED rectangle is
    /// on rather than the one the window belongs to — the preview a cross-screen drag needs.
    /// </summary>
    /// <param name="window">Window to resolve for.</param>
    /// <param name="proposedX">Proposed window origin X (physical pixels).</param>
    /// <param name="proposedY">Proposed window origin Y (physical pixels).</param>
    public GridTarget ResolveDropTarget(T window, int proposedX, int proposedY);

    /// <summary>
    /// <see cref="ResolveDropTarget"/> plus the occupancy verdict for the same rectangle: the
    /// target, and whether another widget already occupies the cells it lands on.
    /// <para>
    /// The layout lookup stays inside the implementation — the interface only speaks about the
    /// window and the proposal — so a non-desktop host (or a test harness) never has to know how
    /// widget entries are stored. <c>Blocked</c> is <c>false</c> whenever 禁止组件重叠 is off,
    /// which keeps a drag in that mode identical to <see cref="ResolveDropTarget"/>.
    /// </para>
    /// </summary>
    /// <param name="window">Window being dragged.</param>
    /// <param name="proposedX">Proposed window origin X (physical pixels).</param>
    /// <param name="proposedY">Proposed window origin Y (physical pixels).</param>
    public GridDropTarget ResolveDrop(T window, int proposedX, int proposedY);
}
