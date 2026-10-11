using System;
using System.Linq;
using Avalonia;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

public class GridService(IAppSettingsProvider appSettingsProvider, DisplayMonitorService displayMonitor) : IGridService<Widget>
{
    public void SetSize(Widget window, int columns, int rows)
    {
        // Manual grid mode: widget size is driven by the grid cell size (span × cell).
        // Grid metrics are physical pixels; window sizes are DIPs → convert.
        var cell = GetGridMetrics(window).cell / GetScaling(window);
        window.Width = columns * cell;
        window.Height = rows * cell;
    }

    public void SnapSize(Widget window)
    {
        var cell = GetGridMetrics(window).cell / GetScaling(window);
        window.Width = Math.Max(cell, (int) Math.Round(window.Width / (double) cell) * cell);
        window.Height = Math.Max(cell, (int) Math.Round(window.Height / (double) cell) * cell);
    }

    public void SnapPosition(Widget window)
    {
        var scaling = GetScaling(window);
        var (cell, gridX, gridY) = GetGridMetrics(window);
        var x = gridX + SnapToCell(window.Position.X - gridX, cell);
        var y = gridY + SnapToCell(window.Position.Y - gridY, cell);
        // Keep the whole card inside the grid area: a snapped position beyond
        // the last column/row used to leave widgets floating off the grid.
        var (columns, rows) = GetGridExtent(window);
        var width = (int) Math.Round(window.Width * scaling);
        var height = (int) Math.Round(window.Height * scaling);
        x = ClampToGrid(x, gridX, columns * cell, width);
        y = ClampToGrid(y, gridY, rows * cell, height);
        window.Position = new PixelPoint(x, y);
    }

    /// <summary>
    /// Window scaling of the screen the widget currently sits on (window sizes are
    /// DIPs while grid metrics are physical pixels).
    /// </summary>
    private static double GetScaling(Widget window)
        => window.OwningScreen?.Scaling ?? 1.0;

    /// <summary>
    /// The manual grid of the screen the widget currently sits on: the per-screen
    /// configuration grid, falling back to the global <see cref="AppSettings.Grid"/>,
    /// then <see cref="Grid.Default"/>.
    /// </summary>
    private static Grid GetGrid(Widget window, IAppSettingsProvider appSettingsProvider, DisplayMonitorService displayMonitor)
    {
        var perScreen = window.ScreenConfig?.Grid;
        if (perScreen != null) return perScreen;
        return appSettingsProvider.Get().Grid ?? Grid.Default;
    }

    /// <summary>
    /// Number of columns and rows of the manual grid of the screen the widget
    /// currently sits on (the grid's total extent is columns × cell wide).
    /// </summary>
    private (int Columns, int Rows) GetGridExtent(Widget window)
    {
        var grid = GetGrid(window, appSettingsProvider, displayMonitor);
        return (grid.Columns, grid.Rows);
    }

    /// <summary>
    /// Clamp a snapped position so a window of the given size stays inside the
    /// grid area [origin, origin + extent]. Windows larger than the whole grid
    /// are pinned to the grid origin.
    /// </summary>
    private static int ClampToGrid(int position, int origin, int extent, int size)
    {
        var max = origin + Math.Max(0, extent - size);
        return Math.Min(Math.Max(position, origin), max);
    }

    /// <summary>
    /// Resolve the manual grid metrics (cell size in pixels, grid origin in pixels)
    /// from the percentage settings of the screen the widget currently sits on.
    /// </summary>
    private (int cell, int gridX, int gridY) GetGridMetrics(Widget window)
    {
        var grid = GetGrid(window, appSettingsProvider, displayMonitor);
        var screen = window.OwningScreen;
        var area = screen?.WorkingArea;
        var (cell, gridX, gridY) = GridMetrics.Resolve(
            grid,
            area?.X ?? 0,
            area?.Y ?? 0,
            area?.Width ?? 1920,
            area?.Height ?? 1080);
        return (cell, gridX, gridY);
    }

    /// <summary>
    /// Snap an offset (physical pixels) to the nearest manual-grid cell boundary.
    /// </summary>
    private static int SnapToCell(int offset, int cellSize)
    {
        var units = (int) Math.Round(offset / (double) cellSize);
        return units * cellSize;
    }
}
