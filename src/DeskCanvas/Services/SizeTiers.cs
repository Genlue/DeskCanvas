using System;
using Avalonia;
using Avalonia.VisualTree;

namespace DeskCanvas.Services;

/// <summary>
/// The size tier a widget view should render for, resolved from the hosting
/// widget's grid span (phone-style conventions: 2×2 = small card, 4×2 = medium,
/// 4×4 = large).
/// </summary>
public enum WidgetTier
{
    /// <summary>1×1: a single cell — only the bare minimum fits (temperature, today, one dial).</summary>
    Cell,

    /// <summary>2×2: the default small card.</summary>
    Small,

    /// <summary>4×2 (≥3 wide × 2 tall): the medium card.</summary>
    Medium,

    /// <summary>4×4 (≥3×≥3): the large card.</summary>
    Large,

    /// <summary>Any other custom span (2×1, 1×2, 2×3, …): generic fallback layouts.</summary>
    Other,
}

/// <summary>
/// Shared size-tier resolution for widget content adaptation.
/// <para>
/// Tiers are resolved from the hosting widget's GRID SPAN (via the visual-tree
/// ancestor), not from absolute pixels — cell sizes vary hugely between users
/// (a 5% manual grid on a 2560px screen is ~100 DIP, a 10% one is ~205), so
/// pixel bands would misclassify. Views that adapt by READABILITY (font sizes,
/// hiding details on tiny dials) keep using their own pixel thresholds.
/// </para>
/// <para>
/// Outside a widget host (gallery previews) it falls back to coarse pixel bands
/// calibrated for the default virtual grid.
/// </para>
/// </summary>
public static class SizeTiers
{
    /// <summary>Largest short side (DIP) still belonging to the single-cell band (fallback).</summary>
    public const double CellSide = 100;

    /// <summary>Minimum width/height ratio for a card to count as a wide row (fallback).</summary>
    public const double WideRatio = 1.5;

    /// <summary>Single-cell band: both sides are tiny (pixel fallback).</summary>
    public static bool IsCell(Size size) => size.Width <= CellSide && size.Height <= CellSide;

    /// <summary>Wide-row band: a wide card whose height is still single-cell (pixel fallback).</summary>
    public static bool IsRow(Size size) =>
        !IsCell(size) && size.Height <= CellSide && size.Width >= size.Height * WideRatio;

    /// <summary>
    /// Resolve the tier for a widget view control from its hosting widget's cell span.
    /// </summary>
    /// <param name="view">The widget view (used to find the hosting widget window).</param>
    /// <param name="pixelSize">
    /// The view's current size, used only for the no-host fallback (pass
    /// <c>SizeChangedEventArgs.NewSize</c> — <see cref="Visual.Bounds"/> still
    /// carries the previous size while a SizeChanged handler runs).
    /// </param>
    public static WidgetTier ResolveTier(Visual view, Size? pixelSize = null)
    {
        var span = FindHostSpan(view);
        if (span == null)
        {
            // No widget host (gallery preview / tests): fall back to the pixel bands.
            var size = pixelSize ?? view.Bounds.Size;
            if (IsCell(size)) return WidgetTier.Cell;
            if (IsRow(size)) return WidgetTier.Other;
            if (size.Width <= CellSide * 2.5 && size.Height <= CellSide * 2.5 && Math.Abs(size.Width - size.Height) <= 60)
                return WidgetTier.Small;
            if (size.Width >= size.Height * 1.35 && size.Height <= CellSide * 2.5)
                return WidgetTier.Medium;
            if (size.Width > CellSide * 2.2 && size.Height > CellSide * 2.2)
                return WidgetTier.Large;
            return size.Width > size.Height * 1.35 ? WidgetTier.Medium : WidgetTier.Small;
        }

        var (columns, rows) = span.Value;
        return (columns, rows) switch
        {
            (1, 1) => WidgetTier.Cell,
            (2, 2) => WidgetTier.Small,
            (>= 3, 2) => WidgetTier.Medium,
            (>= 3, >= 3) => WidgetTier.Large,
            _ => WidgetTier.Other,
        };
    }

    private static (int Columns, int Rows)? FindHostSpan(Visual view)
    {
        for (var node = view.GetVisualParent(); node != null; node = node.GetVisualParent())
            if (node is DeskCanvas.Views.Widget widget)
                return widget.CurrentSpan;
        return null;
    }
}
