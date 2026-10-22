using System;

namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// The launcher grid's derived geometry.
/// <para>
/// The icon size is <b>not</b> a user setting: it falls out of how many icons a row holds and how
/// much of the cell is gutter. That is the only way the grid can be "N icons, evenly spread" at any
/// window width — an independently chosen icon size always leaves either a dead strip on the right
/// (the reported empty column) or icons that overflow their cell.
/// </para>
/// <para>
/// The model is one line of algebra, so it lives here (pure, no Avalonia) instead of in the window:
/// </para>
/// <code>
/// cell  = width / columns
/// gap   = cell × ratio
/// icon  = cell − gap            (= cell × (1 − ratio))
/// </code>
/// <para>
/// <b>Why <c>cell = width / columns</c> and not a gutter-aware divisor:</b> the panel places every
/// tile on a fixed pitch of <c>cell</c>, so the gutter lives <i>inside</i> the cell (half on each
/// side of the icon). Subtracting the gutters from the divisor made the pitch smaller than the row
/// allowed, which let one extra tile slip in and left the reported "empty column" on the right.
/// </para>
/// <para>
/// <c>ratio</c> is the gap as a fraction of a cell, which is what makes the setting scale: a gap
/// expressed in DIP would be invisible at 3 columns and impossible at 12.
/// </para>
/// </summary>
/// <param name="CellWidth">The tile pitch — one column's full width, gutter included.</param>
/// <param name="Gap">The gutter between two neighbouring icons.</param>
/// <param name="IconSize">The square the icon is drawn in — the cell minus its gutter.</param>
/// <param name="RowHeight">One row's height: icon + label block + the vertical gutter.</param>
/// <param name="LabelHeight">Height reserved for the name under the icon (0 when names are off).</param>
/// <param name="Columns">The column count the metrics were computed for.</param>
public readonly record struct LauncherGridMetrics(
    double CellWidth,
    double Gap,
    double IconSize,
    double RowHeight,
    double LabelHeight,
    int Columns)
{
    /// <summary>Space between the icon and its label, in DIP.</summary>
    public const double LabelSpacing = 6;

    /// <summary>
    /// One line of the 12 px label font, in DIP. Every tile reserves a label box of
    /// <c>lines × this</c> — that fixed box is what keeps every icon in a row on the same
    /// baseline: a one-line name leaves empty space under it instead of shrinking the tile.
    /// </summary>
    public const double LabelLineHeight = 17;

    /// <summary>Two lines of the label font, so a wrapped name never pushes the row taller.</summary>
    public const double TwoLineLabelHeight = LabelLineHeight * 2;

    /// <summary>
    /// Derive the grid from the space the grid actually has.
    /// </summary>
    /// <param name="availableWidth">The list's real content width (scrollbar already removed).</param>
    /// <param name="columns">Requested columns; clamped into the supported range.</param>
    /// <param name="gapPercent">Gutter as a percentage of a cell (0–45).</param>
    /// <param name="showNames">Whether the tiles draw a name under the icon.</param>
    /// <param name="rowGapPercent">
    /// Vertical gutter as a percentage of a cell (0–60). Appended after <paramref name="showNames"/>
    /// so every existing four-argument call keeps meaning "the default gutter".
    /// </param>
    /// <param name="nameLines">
    /// How many lines a tile name may occupy (1–3, the launcher's 应用名称行数 setting). The label
    /// block is always reserved for exactly this many lines, so the icons of one row cannot move
    /// when one app's name wraps and another's does not.
    /// </param>
    public static LauncherGridMetrics Compute(
        double availableWidth, int columns, int gapPercent, bool showNames,
        int rowGapPercent = LauncherSettings.DefaultRowGapPercent,
        int nameLines = LauncherSettings.DefaultNameLines)
    {
        var safeColumns = Math.Clamp(columns, 1, LauncherSettings.MaxColumns);
        var width = double.IsFinite(availableWidth) && availableWidth > 1 ? availableWidth : 1;

        // 45% is the ceiling that still leaves a visible icon at 12 columns; anything above it
        // makes the icon smaller than the gutter it sits in.
        var ratio = Math.Clamp(gapPercent, LauncherSettings.MinGapPercent, LauncherSettings.MaxGapPercent) / 100.0;

        var cell = width / safeColumns;
        var gap = cell * ratio;
        var icon = Math.Max(1, cell - gap);
        // A fixed label box, not "however tall this name happens to wrap": the tile must have the
        // same height for every app or the vertically centred tile shifts its icon.
        var lines = Math.Clamp(nameLines, LauncherSettings.MinNameLines, LauncherSettings.MaxNameLines);
        var label = showNames ? lines * LabelLineHeight : 0;

        // The vertical gutter is its own setting (行间距): a grid with names on already pays for the
        // label block, and deriving the row gutter from the column gap on top of that is what made
        // the rows look stretched with no way to tighten them. 0 is a legal value — the rows then
        // touch, which is what an icon-forward grid wants.
        var rowGapRatio = Math.Clamp(rowGapPercent,
            LauncherSettings.MinRowGapPercent, LauncherSettings.MaxRowGapPercent) / 100.0;
        var rowGap = cell * rowGapRatio;
        var rowHeight = icon + (label > 0 ? label + LabelSpacing : 0) + rowGap;

        return new LauncherGridMetrics(cell, gap, icon, rowHeight, label, safeColumns);
    }

    /// <summary>
    /// The height the tile's own content occupies inside a row — the icon plus, when names are on,
    /// the gap and the two-line label block. The difference to <see cref="RowHeight"/> is the row
    /// gutter, which is what every highlight / halo inset is measured against.
    /// </summary>
    public double ContentHeight
        => IconSize + (LabelHeight > 0 ? LabelSpacing + LabelHeight : 0);
}
