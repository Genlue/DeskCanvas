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
}
