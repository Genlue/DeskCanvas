namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// A resolved grid cell: the window rectangle a release would snap to (physical screen pixels)
/// plus the scaling of the screen that rectangle belongs to.
/// <para>
/// The rect is returned instead of assigned so that the drop outline, the landing flight and the
/// persisted layout entry all read <b>one</b> value. Computing the preview with its own copy of
/// the snap math is exactly how a white outline ends up one cell away from where the card
/// actually lands.
/// </para>
/// <para>
/// <see cref="Scaling"/> is part of the answer, not an afterthought: window sizes are DIPs while
/// the grid is physical pixels, so a consumer that has to convert the widget's own DIP insets
/// (margin, corner radius) for drawing must use the same scale the rectangle was computed with.
/// </para>
/// </summary>
/// <param name="X">Window origin X (physical pixels).</param>
/// <param name="Y">Window origin Y (physical pixels).</param>
/// <param name="Width">Window width (physical pixels).</param>
/// <param name="Height">Window height (physical pixels).</param>
/// <param name="Scaling">Scaling of the target screen.</param>
public readonly record struct GridTarget(int X, int Y, int Width, int Height, double Scaling);
