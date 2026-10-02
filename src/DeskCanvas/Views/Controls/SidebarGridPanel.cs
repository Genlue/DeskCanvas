using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// A sidebar item that knows how many grid cells it occupies.
/// </summary>
public interface ISidebarGridItem
{
    /// <summary>The span this item wants, in grid cells (columns, rows).</summary>
    (int Columns, int Rows) GridSpan { get; }
}

/// <summary>
/// The sidebar's grid: a square-cell flow layout.
/// <para>
/// The panel is exactly as wide as the sidebar, and one cell is <c>width / Columns</c> — a 360 DIP
/// sidebar across 4 columns gets 90×90 DIP cells, so four 1×1 widgets sit on one row. Items are
/// placed row-major with first-fit packing (<see cref="SidebarRules.PackGrid"/>), and every card is
/// inset by half of <see cref="Gutter"/> on each side — the user's per-side 组件内边距, doubled —
/// which leaves an even gap between neighbours and the same inset at the outer edges. The gutter is
/// capped at half a cell (see <see cref="SidebarRules.EffectiveGutter"/>) so a large inset can never
/// make neighbouring cards overlap.
/// </para>
/// <para>
/// Height is content-driven: the panel reports <c>rows × cellSide</c> to the enclosing scroll
/// viewer, so an over-full sidebar scrolls instead of squeezing its cards.
/// </para>
/// </summary>
public sealed class SidebarGridPanel : Panel
{
    /// <summary>Grid column count. Changing it re-packs and re-sizes every child.</summary>
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<SidebarGridPanel, int>(nameof(Columns), SidebarLayout.DefaultColumns);

    /// <summary>
    /// Total gap (DIPs) left between a card and its cell — i.e. twice the per-side inset the user
    /// configured. Changing it re-packs and re-sizes every child.
    /// </summary>
    public static readonly StyledProperty<double> GutterProperty =
        AvaloniaProperty.Register<SidebarGridPanel, double>(nameof(Gutter), SidebarRules.GutterDip);

    static SidebarGridPanel()
    {
        AffectsMeasure<SidebarGridPanel>(ColumnsProperty, GutterProperty);
    }

    /// <summary>Grid column count.</summary>
    public int Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Total gap between a card and its cell, in DIPs (see <see cref="SidebarRules.EffectiveGutter"/>).</summary>
    public double Gutter
    {
        get => GetValue(GutterProperty);
        set => SetValue(GutterProperty, value);
    }

    /// <summary>The column count actually used, clamped to the supported range.</summary>
    public int EffectiveColumns => Math.Clamp(Columns, SidebarLayout.MinColumns, SidebarLayout.MaxColumns);

    /// <summary>The gutter actually drawn at the current cell size.</summary>
    private double EffectiveGutter(double cell) => SidebarRules.EffectiveGutter(cell, Gutter);

    /// <summary>Side of one cell, derived from the width this panel is being laid out at.</summary>
    public double CellSide => SidebarRules.CellSide(Bounds.Width > 0 ? Bounds.Width : Width, EffectiveColumns);

    private sealed record Slot(Control Child, double Width, double Height, int Column, int Row);

    /// <summary>
    /// Re-pack and re-measure the children. Called when a widget changes its span, when the column
    /// count changes and when the sidebar is resized — the panel cannot see any of those on its own
    /// (a child's span is not an Avalonia property).
    /// </summary>
    public void Relayout()
    {
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ResolveWidth(availableSize.Width);
        var cell = SidebarRules.CellSide(width, EffectiveColumns);
        var (slots, rows) = BuildSlots(cell);

        foreach (var slot in slots)
            slot.Child.Measure(new Size(slot.Width, slot.Height));

        // Content height, so the scroll viewer can scroll instead of squeezing.
        return new Size(width, rows * cell);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var cell = SidebarRules.CellSide(finalSize.Width, EffectiveColumns);
        var (slots, rows) = BuildSlots(cell);
        var gutter = EffectiveGutter(cell);

        foreach (var slot in slots)
            slot.Child.Arrange(new Rect(
                slot.Column * cell + gutter / 2,
                slot.Row * cell + gutter / 2,
                slot.Width,
                slot.Height));

        return new Size(finalSize.Width, Math.Max(finalSize.Height, rows * cell));
    }

    private (List<Slot> Slots, int Rows) BuildSlots(double cell)
    {
        var spans = new List<(int Columns, int Rows)>(Children.Count);
        foreach (var child in Children)
            spans.Add(child is ISidebarGridItem item ? item.GridSpan : (1, 1));

        var (placements, rows) = SidebarRules.PackGrid(spans, EffectiveColumns);
        var gutter = EffectiveGutter(cell);

        var slots = new List<Slot>(Children.Count);
        for (var i = 0; i < Children.Count && i < placements.Count; i++)
        {
            var placement = placements[i];
            var (cardWidth, cardHeight) = SidebarRules.CardSize(cell, placement.Columns, placement.Rows, gutter);
            slots.Add(new Slot(Children[i], cardWidth, cardHeight, placement.Column, placement.Row));
        }

        return (slots, rows);
    }

    /// <summary>
    /// The width to lay the grid out at. Inside the sidebar's scroll viewer this is always the
    /// viewport width (the scroll viewer disables horizontal scrolling), but the very first
    /// measure pass can arrive unconstrained; falling back to the last known bounds keeps the
    /// cell size — and therefore every card and its corner radius — from jumping on load.
    /// </summary>
    private double ResolveWidth(double available)
    {
        if (double.IsFinite(available) && available > 0) return available;
        if (Bounds.Width > 0) return Bounds.Width;
        return SidebarLayout.DefaultColumns * 90.0;
    }
}
