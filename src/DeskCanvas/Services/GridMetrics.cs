using System;
using DeskCanvas.Core.Models;
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

    /// <summary>
    /// Row-major FIRST-FIT placement: scan the grid's cells from (0,0) left → right,
    /// top → bottom and return the first (column, row) whose whole
    /// <paramref name="spanCols"/>×<paramref name="spanRows"/> block is free.
    /// <para>
    /// Occupancy is tracked as a boolean cell matrix. Every widget already stored on
    /// the screen occupies every cell its own footprint covers: the anchor is the
    /// stored top-left corner's cell, the span is resolved through
    /// <see cref="ResolveSpan"/> (DIP today, legacy physical pixels supported), and
    /// the whole pixel rectangle is marked — a widget parked off the cell lattice
    /// (legacy entries, grid-origin edits) blocks the boundary cells its edges reach
    /// into, so a candidate can never slip into the sliver between its pixel rect and
    /// the lattice and partially overlap it.
    /// </para>
    /// <para>
    /// Returns <c>(-1, -1)</c> when no block fits — the grid is genuinely full (or the
    /// widget is larger than the grid). Callers decide the fallback; the gallery's
    /// placement used to fall back to the grid origin "ignoring occupancy", which
    /// stacked the new card exactly onto the first placed one (the 吞组件 overlap).
    /// </para>
    /// </summary>
    /// <param name="layout">Stored widgets of the target screen, positions RELATIVE to the
    /// screen's working area, sizes in DIPs (legacy physical accepted via <see cref="ResolveSpan"/>).</param>
    /// <param name="cell">Grid cell edge in physical pixels (<see cref="Resolve"/>).</param>
    /// <param name="scaling">Scaling of the target screen.</param>
    /// <param name="gridRelX">Grid origin X relative to the working area (physical pixels).</param>
    /// <param name="gridRelY">Grid origin Y relative to the working area (physical pixels).</param>
    /// <param name="columns">Grid column count.</param>
    /// <param name="rows">Grid row count.</param>
    /// <param name="spanCols">Span to place, in cells.</param>
    /// <param name="spanRows">Span to place, in cells.</param>
    public static (int Column, int Row) FindFirstFitCell(
        IReadOnlyList<WidgetLayout> layout, int cell, double scaling,
        int gridRelX, int gridRelY, int columns, int rows, int spanCols, int spanRows)
    {
        var occupied = new bool[rows, columns];
        foreach (var widget in layout)
        {
            var spanC = Math.Max(1, ResolveSpan(widget.Width, cell, scaling));
            var spanR = Math.Max(1, ResolveSpan(widget.Height, cell, scaling));

            // Cells covered by the widget's pixel rect, relative to the grid origin:
            // floor on the leading edge, ceil on the trailing edge — a grid-aligned
            // widget marks exactly its span, an off-grid one marks the boundary cells
            // its footprint reaches into (conservative, never under-covers).
            var left = widget.X - gridRelX;
            var top = widget.Y - gridRelY;
            var col0 = (int) Math.Floor(left / (double) cell);
            var row0 = (int) Math.Floor(top / (double) cell);
            var col1 = (int) Math.Ceiling((left + spanC * (double) cell) / cell) - 1;
            var row1 = (int) Math.Ceiling((top + spanR * (double) cell) / cell) - 1;

            for (var r = Math.Max(row0, 0); r <= Math.Min(row1, rows - 1); r++)
                for (var c = Math.Max(col0, 0); c <= Math.Min(col1, columns - 1); c++)
                    occupied[r, c] = true;
        }

        for (var row = 0; row + spanRows <= rows; row++)
        {
            for (var col = 0; col + spanCols <= columns; col++)
            {
                var free = true;
                for (var r = row; free && r < row + spanRows; r++)
                    for (var c = col; free && c < col + spanCols; c++)
                        free = !occupied[r, c];

                if (free) return (col, row);
            }
        }

        return (-1, -1);
    }
}
