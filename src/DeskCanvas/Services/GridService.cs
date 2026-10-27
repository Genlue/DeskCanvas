using System;
using System.Linq;
using Avalonia;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
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
        var target = ResolveTarget(window, window.Position.X, window.Position.Y);
        window.Position = new PixelPoint(target.X, target.Y);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Resolved against the window's OWN screen, which is what every restore path means by "snap
    /// me back": a window Windows has moved onto another display still belongs to its stored
    /// screen, and its saved cell has to be expressed in that screen's lattice (see the
    /// WidgetAdaptiveChecks relocation case).
    /// </remarks>
    public GridTarget ResolveTarget(Widget window, int proposedX, int proposedY)
    {
        var attached = displayMonitor.FindByConfigId(window.ScreenId)
                       ?? (window.OwningScreen is { } screen ? displayMonitor.Find(screen) : null);
        return Resolve(window, new PixelPoint(proposedX, proposedY), attached);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The grid belongs to the screen the drop is ON, not to the one the widget still owns — a
    /// card dragged across a screen edge must preview (and land in) the target screen's cells.
    /// The proposed rect is used rather than <c>Position</c> because WM_MOVING reports the frame
    /// ahead of the window.
    /// </remarks>
    public GridTarget ResolveDropTarget(Widget window, int proposedX, int proposedY)
    {
        var proposed = new PixelPoint(proposedX, proposedY);
        return Resolve(window, proposed, ResolveScreen(window, proposed));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The occupancy half only runs when 禁止组件重叠 is on: with the setting off the answer is a
    /// plain <see cref="ResolveDropTarget"/> and not one layout entry is read, so a drag in the
    /// long-standing mode cannot change behaviour because of this check.
    /// <para>
    /// The layout is the TARGET screen's (the one under the proposal), never the widget's own:
    /// that is the screen whose cells the card is about to join, and on a cross-screen drag the
    /// widget's entry is still filed under the screen it came from. The dragged widget's own
    /// entry is excluded by identity, so hovering the cell the card already occupies reads free
    /// even though the stored entry is still sitting there for the length of the drag.
    /// </para>
    /// <para>
    /// An unmigrated legacy bucket (<c>Key == null</c>) keeps absolute desktop coordinates, while
    /// the frame works relative to the working area — the same conversion the gallery does before
    /// its scan, so both read one coordinate space. It only allocates in that transitional case.
    /// </para>
    /// </remarks>
    public GridDropTarget ResolveDrop(Widget window, int proposedX, int proposedY)
    {
        var proposed = new PixelPoint(proposedX, proposedY);
        var attached = ResolveScreen(window, proposed);
        var target = Resolve(window, proposed, attached);

        if (!appSettingsProvider.Get().Layout.ProhibitOverlap) return new GridDropTarget(target, false);
        if (attached == null || CurrentConfig(attached) is not { } config) return new GridDropTarget(target, false);

        var grid = config.Grid ?? appSettingsProvider.Get().Grid ?? Grid.Default;
        var area = attached.Screen.WorkingArea;
        var frame = GridMetrics.FrameFor(grid, area.X, area.Y, area.Width, area.Height, target.Scaling);
        var layout = config.Key == null
            ? config.Layout.Select(item => item with { X = item.X - area.X, Y = item.Y - area.Y }).ToList()
            : config.Layout;
        var occupied = GridMetrics.BuildOccupancy(layout, frame, window.StoredLayout);
        var blocked = GridMetrics.IsBlocked(occupied, frame,
            target.X - area.X, target.Y - area.Y, target.Width, target.Height);
        return new GridDropTarget(target, blocked);
    }

    /// <summary>
    /// The attached screen a drop proposal belongs to: the one under the proposal, falling back to
    /// the widget's own screen when the screen list is not queryable (before the platform window
    /// exists, on the headless host).
    /// </summary>
    private AttachedScreen? ResolveScreen(Widget window, PixelPoint proposed) =>
        ScreenUnder(window, proposed)
        ?? displayMonitor.FindByConfigId(window.ScreenId)
        ?? (window.OwningScreen is { } screen ? displayMonitor.Find(screen) : null);

    /// <summary>
    /// The stored configuration of an attached screen, re-read by id: <see cref="AttachedScreen.Config"/>
    /// is a snapshot taken at the last topology refresh, which can lag a just-saved layout or grid
    /// change by a poll interval. One lookup for the grid and the occupancy, so both read the same
    /// entry.
    /// </summary>
    private ScreenLayout? CurrentConfig(AttachedScreen attached) =>
        attached.Config is { } snapshot
            ? displayMonitor.FindByConfigId(snapshot.Id)?.Config ?? snapshot
            : null;

    /// <summary>
    /// The one snap: the target screen's lattice (per-screen grid, falling back to the global
    /// grid then <see cref="Grid.Default"/>), the nearest cell boundary, and the clamp that keeps
    /// the whole card inside the grid area. Both <see cref="SnapPosition"/> and the drag preview
    /// run through here, so the outline can never disagree with the landing.
    /// </summary>
    private GridTarget Resolve(Widget window, PixelPoint proposed, AttachedScreen? attached)
    {
        // Re-read the configuration by id: AttachedScreen.Config is a snapshot taken at the last
        // topology refresh, which can lag a just-saved grid/margin change by a poll interval.
        var config = attached == null ? null : CurrentConfig(attached);
        var grid = config?.Grid ?? appSettingsProvider.Get().Grid ?? Grid.Default;
        var area = attached?.Screen.WorkingArea;
        var scaling = attached?.Screen.Scaling ?? GetScaling(window);

        var (cell, gridX, gridY) = GridMetrics.Resolve(
            grid,
            area?.X ?? 0,
            area?.Y ?? 0,
            area?.Width ?? 1920,
            area?.Height ?? 1080);

        var x = gridX + SnapToCell(proposed.X - gridX, cell);
        var y = gridY + SnapToCell(proposed.Y - gridY, cell);
        // Keep the whole card inside the grid area: a snapped position beyond
        // the last column/row used to leave widgets floating off the grid.
        var width = (int) Math.Round(window.Width * scaling);
        var height = (int) Math.Round(window.Height * scaling);
        x = ClampToGrid(x, gridX, grid.Columns * cell, width);
        y = ClampToGrid(y, gridY, grid.Rows * cell, height);
        return new GridTarget(x, y, width, height, scaling);
    }

    /// <summary>
    /// The attached screen the proposed rectangle sits on. The rect's CENTRE decides: a window
    /// straddling two displays belongs to the one showing most of it, and the centre is the
    /// cheapest stable proxy for that (the same rule the window manager uses to pick a monitor).
    /// <para>
    /// Failure to ask is not failure to answer: before the platform window exists (and on the
    /// headless host) the screen list is not queryable, so this returns null and the caller falls
    /// back to the window's own screen.
    /// </para>
    /// </summary>
    private AttachedScreen? ScreenUnder(Widget window, PixelPoint proposed)
    {
        try
        {
            var scaling = GetScaling(window);
            var centre = new PixelPoint(
                proposed.X + (int) Math.Round(window.Width * scaling / 2),
                proposed.Y + (int) Math.Round(window.Height * scaling / 2));
            return window.Screens.ScreenFromPoint(centre) is { } screen ? displayMonitor.Find(screen) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Window scaling of the screen the widget currently sits on (window sizes are
    /// DIPs while grid metrics are physical pixels).
    /// </summary>
    private static double GetScaling(Widget window)
        => window.OwningScreen?.Scaling ?? 1.0;

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
    /// Snap an offset (physical pixels) to the nearest manual-grid cell boundary.
    /// </summary>
    private static int SnapToCell(int offset, int cellSize)
    {
        var units = (int) Math.Round(offset / (double) cellSize);
        return units * cellSize;
    }
}
