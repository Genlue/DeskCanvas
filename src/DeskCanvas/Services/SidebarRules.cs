using System;
using System.Collections.Generic;
using System.Linq;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Pure sidebar rules — width clamping, ordering, grid geometry — kept free of any window
/// or Win32 state so they can be exercised directly (see <c>tests/SidebarLayoutChecks</c> /
/// <c>tests/SidebarOrderingChecks</c>).
/// </summary>
public static class SidebarRules
{
    /// <summary>Gap kept between the sidebar's left edge and the screen's left edge.</summary>
    public const double EdgeGutterDip = 32;

    /// <summary>
    /// Total gap between two neighbouring cards when nothing is configured — twice the per-side
    /// inset the user sets (<see cref="SidebarSettings.DefaultWidgetMarginDip"/>). Half of it is
    /// kept on each side of a card, so a full row of <c>n</c> cells ends with the same outer inset
    /// it started with, and two neighbours end up exactly one gutter apart.
    /// </summary>
    public const double GutterDip = SidebarSettings.DefaultWidgetMarginDip * 2;

    /// <summary>
    /// The gutter actually drawn for a cell of <paramref name="cellSide"/>: the configured total
    /// gap, capped at half a cell. A cell is the smallest unit the grid can place, so a larger
    /// inset would make two neighbouring 1×1 cards overlap rather than separate.
    /// </summary>
    public static double EffectiveGutter(double cellSide, double gutterDip) =>
        Math.Clamp(gutterDip, 0, Math.Max(0, cellSide / 2));

    /// <summary>
    /// Side of one square grid cell: the sidebar's full width divided by its column count
    /// (360 DIP across 4 columns = 90 DIP cells). Deliberately the <i>whole</i> width — the
    /// sidebar's own edge inset comes out of <see cref="GutterDip"/>, so the arithmetic the user
    /// reads off the settings ("360 / 4 = 90 per column") is the arithmetic the grid uses.
    /// </summary>
    public static double CellSide(double sidebarWidthDip, int columns)
    {
        var count = Math.Clamp(columns, SidebarLayout.MinColumns, SidebarLayout.MaxColumns);
        return sidebarWidthDip <= 0 ? 0 : sidebarWidthDip / count;
    }

    /// <summary>The card size for a span inside a cell grid — the cell block minus the gutter.</summary>
    /// <param name="cellSide">Side of one square cell.</param>
    /// <param name="columns">Columns the card spans.</param>
    /// <param name="rows">Rows the card spans.</param>
    /// <param name="gutterDip">Total gap to leave, already resolved from the user's per-side inset.</param>
    public static (double Width, double Height) CardSize(double cellSide, int columns, int rows,
        double gutterDip = GutterDip)
    {
        var gutter = EffectiveGutter(cellSide, gutterDip);
        return (Math.Max(1, columns * cellSide - gutter),
            Math.Max(1, rows * cellSide - gutter));
    }

    /// <summary>
    /// Clamp a requested span to a grid of <paramref name="gridColumns"/> columns. Fixed-size
    /// widgets pass their legal spans as <paramref name="options"/>, so the result always lands on
    /// the widget's own grid (a 4:2 aggregate can only ever be 4×2, 6×3 or 8×4) instead of being
    /// squeezed into a shape its content cannot render. An empty list means "free": the request is
    /// simply clamped.
    /// </summary>
    public static (int Columns, int Rows) FitSpan((int Columns, int Rows) requested,
        IReadOnlyList<(int Columns, int Rows)> options, int gridColumns)
    {
        var max = Math.Clamp(gridColumns, SidebarLayout.MinColumns, SidebarLayout.MaxColumns);
        var wantColumns = Math.Clamp(requested.Columns, 1, max);
        var wantRows = Math.Max(1, requested.Rows);

        if (options.Count == 0) return (wantColumns, wantRows);

        // Only spans that physically fit the sidebar are candidates.
        var fitting = options.Where(option => option.Columns >= 1 && option.Columns <= max && option.Rows >= 1).ToList();
        if (fitting.Count == 0)
        {
            // Nothing fits (a fixed family wider than the whole grid): keep the family's shape and
            // let it fill the grid rather than inventing a span the widget would refuse.
            var smallest = options.OrderBy(option => option.Columns).ThenBy(option => option.Rows).First();
            return (Math.Clamp(smallest.Columns, 1, max), Math.Max(1, smallest.Rows));
        }

        return fitting
            .OrderBy(option => Math.Abs(option.Columns - wantColumns) * 1000 + Math.Abs(option.Rows - wantRows))
            .First();
    }

    /// <summary>One item's placement in the sidebar grid, in cell units.</summary>
    /// <param name="Column">Leftmost cell column (0-based).</param>
    /// <param name="Row">Topmost cell row (0-based).</param>
    /// <param name="Columns">Occupied column count (already fitted to the grid).</param>
    /// <param name="Rows">Occupied row count.</param>
    public sealed record GridPlacement(int Column, int Row, int Columns, int Rows);

    /// <summary>
    /// Row-major first-fit packing for the sidebar grid: every span is placed in the first block
    /// of free cells big enough for it, scanning rows top→bottom and columns left→right.
    /// <para>
    /// First fit (rather than "wrap to the next row when the current one is full") is what lets a
    /// 1×1 widget drop into the gap a 4×2 left at the end of a row instead of being pushed down —
    /// with a mix of wide and narrow widgets the wrapped variant wastes most of the grid.
    /// </para>
    /// </summary>
    /// <param name="spans">Requested span of every item, in display order.</param>
    /// <param name="columns">Grid column count.</param>
    /// <returns>One placement per input span (same order) and the total row count.</returns>
    public static (List<GridPlacement> Placements, int Rows) PackGrid(
        IReadOnlyList<(int Columns, int Rows)> spans, int columns)
    {
        var width = Math.Clamp(columns, SidebarLayout.MinColumns, SidebarLayout.MaxColumns);
        var placements = new List<GridPlacement>(spans.Count);
        var occupied = new HashSet<(int Column, int Row)>();
        var rows = 0;

        foreach (var span in spans)
        {
            var spanColumns = Math.Clamp(span.Columns, 1, width);
            var spanRows = Math.Max(1, span.Rows);

            // Row `rows` and below are empty by construction (nothing has been placed there yet),
            // so including it in the scan guarantees a placement without an arbitrary row bound.
            for (var row = 0; row <= rows; row++)
            {
                var placed = false;
                for (var column = 0; column + spanColumns <= width; column++)
                {
                    if (!IsFree(occupied, column, row, spanColumns, spanRows)) continue;
                    Mark(occupied, column, row, spanColumns, spanRows);
                    placements.Add(new GridPlacement(column, row, spanColumns, spanRows));
                    rows = Math.Max(rows, row + spanRows);
                    placed = true;
                    break;
                }

                if (placed) break;
            }
        }

        return (placements, rows);
    }

    private static bool IsFree(HashSet<(int Column, int Row)> occupied, int column, int row, int columns, int rows)
    {
        for (var c = column; c < column + columns; c++)
            for (var r = row; r < row + rows; r++)
                if (occupied.Contains((c, r)))
                    return false;
        return true;
    }

    private static void Mark(HashSet<(int Column, int Row)> occupied, int column, int row, int columns, int rows)
    {
        for (var c = column; c < column + columns; c++)
            for (var r = row; r < row + rows; r++)
                occupied.Add((c, r));
    }

    /// <summary>
    /// The shadow overhang actually used: the requested reach past the leftmost card, capped so the
    /// widened window (card strip + overhang) still fits on the screen it is pinned to. Without the
    /// cap a maximised strip on a narrow screen would push the window off its own screen and the
    /// tail would spill onto a neighbouring monitor.
    /// </summary>
    /// <param name="requestedDip">Reach the settings ask for.</param>
    /// <param name="sidebarWidthDip">The card strip's own width.</param>
    /// <param name="screenWidthDip">Width of the screen the sidebar is pinned to.</param>
    public static double ClampShadowExtent(double requestedDip, double sidebarWidthDip, double screenWidthDip) =>
        Math.Clamp(requestedDip, 0, Math.Max(0, screenWidthDip - sidebarWidthDip));

    /// <summary>
    /// The resolved horizontal geometry of the sidebar's diffuse shadow, in window-relative DIPs.
    /// </summary>
    /// <param name="FadeEnd">Where the wash has faded to fully transparent (the leftmost point it reaches).</param>
    /// <param name="FadeStart">Where the wash starts to weaken going left (full opacity to its right).</param>
    /// <param name="HoldStop">The gradient stop (0-1) at which <paramref name="FadeStart"/> sits.</param>
    public readonly record struct ShadowSpan(double FadeEnd, double FadeStart, double HoldStop);

    /// <summary>
    /// Resolve the sidebar shadow's horizontal geometry: flat behind the cards, weakening from
    /// <paramref name="anchorXDip"/> (the leftmost card's left edge) leftwards over the feather
    /// length, and reaching its fully transparent end <see cref="SidebarShadowSettings.EffectiveExtendDip"/>
    /// DIPs to the left of that card — which is the extra width the window is laid out with.
    /// <para>
    /// Both ends are clamped into the window so a degenerate configuration (a feather longer than
    /// the window, a negative fade start, an anchor at the very edge) can never produce a reversed or
    /// zero-length gradient, which Avalonia would render as a hard edge instead of a fade.
    /// </para>
    /// </summary>
    /// <param name="windowWidthDip">Full window width, including the shadow overhang.</param>
    /// <param name="anchorXDip">Leftmost card's left edge, in window-relative DIPs.</param>
    /// <param name="shadow">The shadow settings.</param>
    public static ShadowSpan ResolveShadow(double windowWidthDip, double anchorXDip, SidebarShadowSettings shadow)
    {
        var width = Math.Max(1, windowWidthDip);
        var anchor = Math.Clamp(anchorXDip, 0, width);

        var fadeEnd = Math.Clamp(anchor - shadow.EffectiveFeatherDip, 0, Math.Max(0, width - 0.5));
        var fadeStart = Math.Clamp(anchor + shadow.EffectiveFadeStartOffsetDip, fadeEnd + 0.5, width);
        var span = Math.Max(0.5, width - fadeEnd);

        return new ShadowSpan(fadeEnd, fadeStart, Math.Clamp((fadeStart - fadeEnd) / span, 0, 1));
    }

    /// <summary>
    /// Clamp a requested sidebar width to the configured range and to the target screen
    /// (a sidebar may never be wider than the screen minus <see cref="EdgeGutterDip"/>).
    /// </summary>
    public static double ClampWidth(double width, SidebarSettings settings, double screenWidthDip)
    {
        var min = settings.MinWidthDip;
        // On a screen narrower than Min + gutter the minimum wins, but never below a usable size.
        var max = Math.Max(min, Math.Min(settings.MaxWidthDip, screenWidthDip - EdgeGutterDip));
        if (double.IsNaN(width)) width = settings.DefaultWidthDip;
        return Math.Clamp(width, min, max);
    }

    /// <summary>
    /// The width to use for a screen: its stored width when present, otherwise the default —
    /// always clamped against the target screen.
    /// </summary>
    public static double ResolveWidth(SidebarLayout? layout, SidebarSettings settings, double screenWidthDip) =>
        ClampWidth(layout?.WidthDip ?? settings.DefaultWidthDip, settings, screenWidthDip);

    /// <summary>
    /// Sort a sidebar's entries by <see cref="SidebarWidgetEntry.Order"/> and renumber them
    /// <c>0..n-1</c>, so a hand-edited or partially-reordered file heals on load.
    /// </summary>
    public static SidebarLayout Normalize(SidebarLayout sidebar) =>
        sidebar with { Widgets = Renumber([..sidebar.Items.OrderBy(entry => entry.Order)]) };

    /// <summary>
    /// The insertion index for a drag: the first item whose vertical centre lies below the
    /// pointer. The result is an "insert before" index in <c>0..count</c>.
    /// </summary>
    public static int InsertionIndex(IReadOnlyList<double> itemCenters, double pointerY)
    {
        for (var i = 0; i < itemCenters.Count; i++)
            if (pointerY < itemCenters[i]) return i;
        return itemCenters.Count;
    }

    /// <summary>
    /// Move the entry at <paramref name="fromIndex"/> to an "insert before" position
    /// (<paramref name="insertIndex"/>) and renumber the result. Out-of-range input is
    /// tolerated (the list is simply renumbered) so a drag that ends after the layout changed
    /// cannot corrupt the order.
    /// </summary>
    public static List<SidebarWidgetEntry> Move(IReadOnlyList<SidebarWidgetEntry> ordered, int fromIndex, int insertIndex)
    {
        var list = ordered.ToList();
        if (fromIndex < 0 || fromIndex >= list.Count) return Renumber(list);

        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        // Removing an earlier item shifts every later position one to the left.
        if (insertIndex > fromIndex) insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, list.Count);
        list.Insert(insertIndex, item);
        return Renumber(list);
    }

    /// <summary>
    /// Move the entry identified by <paramref name="instanceId"/> to the final index
    /// <paramref name="targetIndex"/> and renumber. An unknown instance id leaves the order
    /// (renumbered) as-is — never appends a phantom entry.
    /// </summary>
    public static List<SidebarWidgetEntry> Reorder(IReadOnlyList<SidebarWidgetEntry> ordered, string instanceId, int targetIndex)
    {
        var fromIndex = IndexOf(ordered, instanceId);
        if (fromIndex < 0) return Renumber(ordered.ToList());

        var list = ordered.ToList();
        var item = list[fromIndex];
        list.RemoveAt(fromIndex);
        list.Insert(Math.Clamp(targetIndex, 0, list.Count), item);
        return Renumber(list);
    }

    /// <summary>Index of an entry by its stable instance id (<c>-1</c> when absent).</summary>
    public static int IndexOf(IReadOnlyList<SidebarWidgetEntry> ordered, string instanceId)
    {
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].InstanceId == instanceId) return i;
        return -1;
    }

    /// <summary>Assign <c>Order = index</c> to every entry.</summary>
    public static List<SidebarWidgetEntry> Renumber(List<SidebarWidgetEntry> entries)
    {
        for (var i = 0; i < entries.Count; i++)
            entries[i] = entries[i] with { Order = i };
        return entries;
    }
}
