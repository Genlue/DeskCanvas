namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// Custom manual grid settings (<see cref="GridMode.Manual"/>).
/// <para>
/// The grid is a rectangular area on the desktop divided into
/// <c>Columns</c> × <c>Rows</c> square cells. The area does not have to cover
/// the whole desktop; the user can move it and resize the cells.
/// </para>
/// <para>
/// Values are stored as <b>percentages</b> so the grid keeps its proportions on
/// different resolutions: <see cref="XPercent"/> and <see cref="CellPercent"/> are
/// relative to the working-area <b>width</b>, <see cref="YPercent"/> (the position
/// of the grid's top line) to the working-area <b>height</b>. The cell's vertical
/// length reuses the horizontal pixel length, so cells are always square on any
/// aspect ratio.
/// </para>
/// </summary>
/// <param name="Columns">Number of columns (n).</param>
/// <param name="Rows">Number of rows (m).</param>
/// <param name="XPercent">X of the grid area's top-left corner, % of the working-area width (0–100).</param>
/// <param name="YPercent">Y of the grid area's top-left corner, % of the working-area height (0–100).</param>
/// <param name="CellPercent">Cell edge length, % of the working-area width (vertical length follows the horizontal pixels, so cells stay square).</param>
public record Grid(
    int Columns,
    int Rows,
    double XPercent,
    double YPercent,
    double CellPercent)
{
    /// <summary>
    /// Default grid: 8×6 cells of 5% width, X centered on the desktop.
    /// </summary>
    public static Grid Default { get; } = new(8, 6, 30, 10, 5);
}
