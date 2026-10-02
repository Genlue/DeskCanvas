using System.Collections.Generic;
using System.Linq;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Services;

/// <summary>
/// One entry of the shared 固定尺寸预设 table: the exact wording shown in the desktop
/// widget context menu, plus the cell span it applies.
/// <para>
/// The desktop menu keeps its literals inline in <c>Views/Widget.axaml</c> (pinned by
/// <c>tests/SidebarSizeMenuChecks</c>, which extracts them with a regex). The sidebar menu
/// generates its entries from this table, so the two menus can never drift apart: changing
/// either side turns the check red.
/// </para>
/// </summary>
/// <param name="Label">Menu header (identical to the desktop literal).</param>
/// <param name="Columns">Target grid column span.</param>
/// <param name="Rows">Target grid row span.</param>
public sealed record WidgetSizePreset(string Label, int Columns, int Rows)
{
    /// <summary>The <c>"CxR"</c> command parameter the resize handler consumes.</summary>
    public string Tag => $"{Columns}x{Rows}";
}

/// <summary>
/// The single source of truth for the 固定尺寸预设 wording shared by the desktop and the
/// sidebar widget menus. See <see cref="WidgetSizePreset"/> for why the desktop keeps its
/// literals inline.
/// </summary>
public static class WidgetSizePresets
{
    /// <summary>4:2 fixed widgets (e.g. the aggregate board).</summary>
    public static readonly IReadOnlyList<WidgetSizePreset> Aggregate =
    [
        new("4×2 (标准 100%)", 4, 2),
        new("6×3 (等比放大 150%)", 6, 3),
        new("8×4 (等比放大 200%)", 8, 4),
    ];

    /// <summary>1:1 fixed widgets (e.g. the single-dial clock).</summary>
    public static readonly IReadOnlyList<WidgetSizePreset> Square =
    [
        new("1×1 (小 100%)", 1, 1),
        new("2×2 (标准 200%)", 2, 2),
        new("3×3 (中 300%)", 3, 3),
        new("4×4 (大 400%)", 4, 4),
    ];

    /// <summary>Quick size presets for the map widget.</summary>
    public static readonly IReadOnlyList<WidgetSizePreset> Map =
    [
        new("2×2 (小号方形 200%)", 2, 2),
        new("4×2 (中号横版 400×200%)", 4, 2),
        new("4×4 (大号全景 400×400%)", 4, 4),
    ];

    /// <summary>
    /// The presets a given widget offers in the "尺寸" sub-menu.
    /// </summary>
    /// <param name="widget">The widget's <see cref="IFixedSizeWidget"/> facet, or <c>null</c> for ordinary widgets.</param>
    /// <param name="isMap">Whether the widget is the map (which ignores <paramref name="widget"/>).</param>
    /// <returns>The presets in menu order; empty for ordinary widgets (they only get the two steppers).</returns>
    public static IReadOnlyList<WidgetSizePreset> For(IFixedSizeWidget? widget, bool isMap)
    {
        if (isMap) return Map;
        if (widget == null) return [];

        // A widget may only ever declare one base span family; (1,1) is checked first so a
        // square widget can never be mistaken for the 4:2 family.
        if (widget.AllowedBaseSpans.Contains((1, 1))) return Square;
        if (widget.AllowedBaseSpans.Contains((4, 2))) return Aggregate;
        return [];
    }

    /// <summary>Whether the widget offers the 4:2 family (mirrors Widget.IsFixed2x1Widget).</summary>
    public static bool IsAggregate(IFixedSizeWidget? widget) =>
        widget != null && widget.AllowedBaseSpans.Contains((4, 2));

    /// <summary>Whether the widget offers the 1:1 family (mirrors Widget.IsFixedSquareWidget).</summary>
    public static bool IsSquare(IFixedSizeWidget? widget) =>
        widget != null && widget.AllowedBaseSpans.Contains((1, 1));

    /// <summary>
    /// Parse a <c>"CxR"</c> preset tag into a span. Returns <c>null</c> for anything malformed,
    /// so a bad command parameter can never resize a widget to a nonsensical span.
    /// </summary>
    public static (int Columns, int Rows)? ParseTag(string? tag)
    {
        if (tag?.Split('x') is not [var columns, var rows]) return null;
        return int.TryParse(columns, out var c) && int.TryParse(rows, out var r) ? (c, r) : null;
    }
}
