using System;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Resolves percentage-based manual grid settings into physical pixels
/// for a given screen working area. Shared by the grid editor and the grid service.
/// <para>
/// Horizontal: cell length = % of the working-area width. Vertical: the cell
/// reuses the horizontal pixel length (square cells), only the grid's top line
/// position is a % of the working-area height.
/// </para>
/// </summary>
public static class GridMetrics
{
    /// <summary>
    /// Resolve the manual grid metrics.
    /// </summary>
    /// <param name="grid">Percentage-based grid settings (<c>null</c> → <see cref="Grid.Default"/>).</param>
    /// <param name="originX">Working-area origin X (physical pixels).</param>
    /// <param name="originY">Working-area origin Y (physical pixels).</param>
    /// <param name="width">Working-area width (physical pixels).</param>
    /// <param name="height">Working-area height (physical pixels).</param>
    /// <returns>Cell size in pixels and the grid origin (physical pixels).</returns>
    public static (int Cell, int X, int Y) Resolve(Grid? grid, int originX, int originY, int width, int height)
    {
        grid ??= Grid.Default;
        // Horizontal cell length: % of the working-area width.
        var cell = (int) Math.Round(width * grid.CellPercent / 100.0);
        var x = originX + (int) Math.Round(width * grid.XPercent / 100.0);
        // Vertical: only the top line position is a % of the height;
        // the cell length reuses the horizontal pixels → always square.
        var y = originY + (int) Math.Round(height * grid.YPercent / 100.0);
        return (Math.Max(cell, 8), x, y);
    }

    /// <summary>
    /// Resolve a stored widget size into a whole number of grid cells.
    /// <para>
    /// Stored sizes are DIPs in the current format, but layouts written by older
    /// builds carry physical pixels (e.g. 575 px on a 125% display for a 5-cell
    /// span) and are never rewritten until the widget happens to be moved or
    /// resized. Every consumer that turns a stored size back into a span — the
    /// widget's own restore path and the gallery's occupancy test for a new
    /// widget — must therefore read it through this one heuristic, or the two
    /// disagree about how much of the grid is taken.
    /// </para>
    /// <para>
    /// The DIP reading is trusted whenever it lands close to a whole number of
    /// cells; the physical reading is only used when the DIP one clearly is not a
    /// whole span. Taking whichever is closer used to report the wrong span when
    /// both readings were equidistant (5 cells on a 125% display: 460 DIP = 4.0
    /// cells physical = 5.0 cells DIP), so the size steppers showed 4 and setting
    /// 4 was a no-op ("adjusting columns 5 to 4 does nothing").
    /// </para>
    /// </summary>
    /// <param name="size">Stored size (DIPs, or physical pixels in legacy layouts).</param>
    /// <param name="cellPx">Cell edge in physical pixels.</param>
    /// <param name="scaling">Scaling of the screen the size was stored for.</param>
    /// <returns>Span in cells, never less than 1.</returns>
    public static int ResolveSpan(double size, double cellPx, double scaling)
    {
        var cellDip = cellPx / scaling;
        if (cellDip <= 0) return 1;

        var dip = size / cellDip;
        var dipError = Math.Abs(dip - Math.Round(dip));
        if (dipError <= 0.15) return Math.Max(1, (int) Math.Round(dip));

        var physical = size / cellPx;
        if (Math.Abs(physical - Math.Round(physical)) <= 0.05)
            return Math.Max(1, (int) Math.Round(physical));

        return Math.Max(1, (int) Math.Round(dip));
    }
}
