using System.Collections.Generic;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Implemented by widgets belonging to the fixed widgets category (固定组件类)
/// that only allow specific base spans (e.g. 2x2, 4x2, 4x4) and their integer proportional multiples.
/// </summary>
public interface IFixedSizeWidget
{
    /// <summary>
    /// The allowed base spans for this widget (e.g. [(4, 2)] for the aggregate widget).
    /// </summary>
    IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans { get; }

    /// <summary>
    /// Check whether a given column and row span is allowed (must be an integer multiple >= 1 of an allowed base span).
    /// </summary>
    bool IsAllowedSpan(int columns, int rows);

    /// <summary>
    /// Snaps a given requested span to the closest valid proportional span.
    /// </summary>
    (int Columns, int Rows) SnapSpan(int columns, int rows);

    /// <summary>
    /// Pre-defined standard spans available for quick switching in the context menu.
    /// E.g. [(4, 2), (8, 4)]
    /// </summary>
    IReadOnlyList<(int Columns, int Rows)> PresetSpans { get; }
}
