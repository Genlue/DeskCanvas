namespace Folders.Services;

/// <summary>
/// Shared rules for the pointer-hover zoom the launcher widgets draw.
/// <para>
/// The zoom is a <b>render transform</b>: it never changes layout, so an icon scaled about its
/// centre grows past its own box by <see cref="Growth"/> on every side. Anything that clips —
/// the widget card's rounded edge, the folder's scroll viewport — then cuts the enlarged icon.
/// Every consumer therefore reserves that growth (or caps the base size with <see cref="Fit"/>)
/// before drawing, instead of letting the transform decide.
/// </para>
/// </summary>
public static class HoverZoomMath
{
    /// <summary>No zoom: the icon stays at its layout size.</summary>
    public const double MinPercent = 100;

    /// <summary>Upper bound offered by the settings sliders; beyond this the growth eats the grid.</summary>
    public const double MaxPercent = 150;

    /// <summary>Default for the icon grids (大文件夹 / 文件展示框).</summary>
    public const double DefaultGridPercent = 115;

    /// <summary>Default for the single-icon cards (单文件 / 单链接).</summary>
    public const double DefaultSinglePercent = 108;

    /// <summary>Clamp a stored percent into the supported range (NaN/infinity mean "no zoom").</summary>
    public static double Normalize(double percent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, MinPercent, MaxPercent) : MinPercent;

    /// <summary>The multiplier to hand to a <c>ScaleTransform</c>.</summary>
    public static double ScaleOf(double percent) => Normalize(percent) / 100.0;

    /// <summary>How far a <paramref name="size"/>-wide box grows on each side when zoomed.</summary>
    public static double Growth(double size, double scale) =>
        scale <= 1.0 ? 0 : Math.Max(0, size * (scale - 1) / 2);

    /// <summary>
    /// The largest base size that still fits <paramref name="available"/> once zoomed — used by the
    /// single-icon cards, whose icon is a percentage of the card and would otherwise be clipped by
    /// the card edge at high percentages.
    /// </summary>
    public static double Fit(double available, double scale) =>
        scale <= 1.0 ? available : available / scale;
}
