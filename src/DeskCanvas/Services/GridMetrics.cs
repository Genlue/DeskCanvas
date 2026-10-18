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
    /// size steppers, the size menu, the cross-screen drag — reads it through this
    /// one heuristic, so they all agree about how much of the grid is taken.
    /// </para>
    /// <para>
    /// Placement occupancy (<see cref="FindFirstFitCell"/>) deliberately does NOT use
    /// this heuristic: it marks the physical rectangle the window will really occupy,
    /// which is what the restored entry actually becomes on screen (<c>Width = stored
    /// DIP</c>, so a legacy physical size lands 1.25 cells wide on a 125% display).
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
    /// A resolved placement frame: the grid lattice (cell edge, origin) together with the
    /// working area the card has to stay inside.
    /// <para>
    /// <see cref="Columns"/>/<see cref="Rows"/> are the <b>configured</b> grid extent, which
    /// is not the same thing as the <b>placeable</b> extent. A grid may hang off the working
    /// area — Y at 50% with ten 96 px rows reaches 1480 px on a 1040 px screen — and a cell
    /// outside the working area is not a slot: the window placed there is pulled back onto
    /// the screen by <c>Widget.ClampRestoredPosition</c> at display time and lands on
    /// whatever already occupies the visible spot, so two cards end up in the same place.
    /// See <see cref="PlaceableColumns"/> / <see cref="PlaceableRows"/>.
    /// </para>
    /// </summary>
    /// <param name="Cell">Grid cell edge in physical pixels (<see cref="Resolve"/>).</param>
    /// <param name="Scaling">Scaling of the target screen (stored sizes are DIPs).</param>
    /// <param name="GridX">Grid origin X relative to the working area (physical pixels).</param>
    /// <param name="GridY">Grid origin Y relative to the working area (physical pixels).</param>
    /// <param name="Columns">Configured grid column count.</param>
    /// <param name="Rows">Configured grid row count.</param>
    /// <param name="AreaWidth">Working-area width (physical pixels).</param>
    /// <param name="AreaHeight">Working-area height (physical pixels).</param>
    public readonly record struct PlacementFrame(
        int Cell,
        double Scaling,
        int GridX,
        int GridY,
        int Columns,
        int Rows,
        int AreaWidth,
        int AreaHeight)
    {
        /// <summary>Columns of whole cells the grid still has inside the working area.</summary>
        public int PlaceableColumns => Cell <= 0
            ? 0
            : Math.Max(0, Math.Min(Columns, (AreaWidth - GridX) / Cell));

        /// <summary>Rows of whole cells the grid still has inside the working area.</summary>
        public int PlaceableRows => Cell <= 0
            ? 0
            : Math.Max(0, Math.Min(Rows, (AreaHeight - GridY) / Cell));

        /// <summary>Physical pixels of a stored (DIP) width on this screen.</summary>
        public int ToPhysicalWidth(double dipWidth) => Math.Max(1, (int) Math.Round(dipWidth * Scaling));

        /// <summary>Physical pixels of a stored (DIP) height on this screen.</summary>
        public int ToPhysicalHeight(double dipHeight) => Math.Max(1, (int) Math.Round(dipHeight * Scaling));
    }

    /// <summary>
    /// Row-major FIRST-FIT placement: scan the placeable cells from (0,0) left → right,
    /// top → bottom and return the first (column, row) whose whole
    /// <paramref name="spanCols"/>×<paramref name="spanRows"/> block is free.
    /// <para>
    /// Occupancy is tracked as a boolean cell matrix built from the rectangle each stored
    /// widget is <b>actually displayed at</b>: its stored position is clamped onto the
    /// working area (exactly like <c>Widget.ClampRestoredPosition</c>) before the cells it
    /// covers are marked. A widget stored outside the area — a bigger desktop before a
    /// resolution change, a grid hanging off the bottom edge — is displayed somewhere else,
    /// and marking its stored rectangle instead would let a new card land right on top of
    /// it. The whole pixel rectangle is marked (floor on the leading edge, ceil on the
    /// trailing edge), so a widget parked off the cell lattice blocks the boundary cells
    /// its edges reach into and a candidate can never slip into the sliver between its
    /// rect and the lattice.
    /// </para>
    /// <para>
    /// Only blocks whose whole span stays inside the working area are considered, and a
    /// span larger than the grid (or than the visible part of it) never fits.
    /// </para>
    /// <para>
    /// Returns <c>(-1, -1)</c> when no block fits — the grid is genuinely full, the card is
    /// larger than the grid, or the working area has no room left for it. Callers must stop
    /// there: the gallery's old fallback to the grid origin "ignoring occupancy" stacked the
    /// new card exactly onto the first placed one (the 吞组件 overlap).
    /// </para>
    /// </summary>
    /// <param name="layout">Stored widgets of the target screen, positions RELATIVE to the
    /// screen's working area, sizes in DIPs.</param>
    /// <param name="frame">Grid lattice + working area of the target screen.</param>
    /// <param name="spanCols">Span to place, in cells.</param>
    /// <param name="spanRows">Span to place, in cells.</param>
    public static (int Column, int Row) FindFirstFitCell(
        IReadOnlyList<WidgetLayout> layout, PlacementFrame frame, int spanCols, int spanRows)
    {
        var columns = frame.PlaceableColumns;
        var rows = frame.PlaceableRows;
        var occupied = new bool[rows, columns];

        foreach (var widget in layout)
        {
            var width = frame.ToPhysicalWidth(widget.Width);
            var height = frame.ToPhysicalHeight(widget.Height);

            // The rectangle the window really shows: restored position clamped onto the
            // working area, relative to the grid origin.
            var left = Math.Clamp(widget.X, 0, Math.Max(0, frame.AreaWidth - width)) - frame.GridX;
            var top = Math.Clamp(widget.Y, 0, Math.Max(0, frame.AreaHeight - height)) - frame.GridY;
            var col0 = (int) Math.Floor(left / (double) frame.Cell);
            var row0 = (int) Math.Floor(top / (double) frame.Cell);
            var col1 = (int) Math.Ceiling((left + width) / (double) frame.Cell) - 1;
            var row1 = (int) Math.Ceiling((top + height) / (double) frame.Cell) - 1;

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
