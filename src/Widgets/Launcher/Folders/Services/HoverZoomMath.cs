namespace Folders.Services;

/// <summary>
/// Shared rules for the pointer-hover zoom the launcher widgets draw.
/// <para>
/// The zoom is a <b>render transform</b>: it never changes layout, so an icon scaled about its
/// centre grows past its own box by <see cref="Growth"/> on every side. The card's rounded edge
/// is not a boundary: the host stops clipping views that declare
/// <c>IWidgetSurfaceHints.AllowsCardOverflow</c>, so a zoomed icon may cross the card edge and
/// paint over the desktop in the widget's transparent margin band. The resting icon therefore
/// keeps its configured size: shrinking it so the zoom would "still fit inside the card" is
/// exactly the behaviour the user rejected.
/// </para>
/// <para>
/// The remaining hard boundary is the widget <b>window</b>, and that window is one grid cell. So
/// the boundary is the <b>cell</b>, not the card: <see cref="ScaleWithin"/> caps the multiplier at
/// the room between the icon's resting edge and the cell edge — the widget margin plus whatever the
/// view leaves inside the card — measured at hover time (see <see cref="CardOverflowRoom.Around"/>).
/// </para>
/// </summary>
public static class HoverZoomMath
{
    /// <summary>No zoom: the icon stays at its layout size.</summary>
    public const double MinPercent = 100;

    /// <summary>
    /// Upper bound offered by the icon-grid sliders (大文件夹 / 文件展示框). A tile that grew
    /// further would cover its neighbours — those widgets share their card with a whole grid.
    /// </summary>
    public const double MaxPercent = 150;

    /// <summary>
    /// Upper bound offered by the single-icon sliders (单文件 / 单链接). Those cards hold one icon,
    /// so a generous ceiling costs nothing: <see cref="ScaleWithin"/> trims the request at the cell
    /// edge anyway, and the slider only ever needs the headroom when 图标大小 leaves the icon small
    /// enough that a big multiplier still fits (at 100 % the icon fills the card, so the cell is
    /// reached at ~128 %).
    /// </summary>
    public const double MaxSinglePercent = 200;

    /// <summary>Default for the icon grids (大文件夹 / 文件展示框).</summary>
    public const double DefaultGridPercent = 115;

    /// <summary>Default for the single-icon cards (单文件 / 单链接).</summary>
    public const double DefaultSinglePercent = 108;

    /// <summary>Clamp a stored percent into the supported range (NaN/infinity mean "no zoom").</summary>
    public static double Normalize(double percent, double max = MaxPercent) =>
        double.IsFinite(percent) ? Math.Clamp(percent, MinPercent, max) : MinPercent;

    /// <summary>The multiplier to hand to a <c>ScaleTransform</c>.</summary>
    public static double ScaleOf(double percent, double max = MaxPercent) => Normalize(percent, max) / 100.0;

    /// <summary>How far a <paramref name="size"/>-wide box grows on each side when zoomed.</summary>
    public static double Growth(double size, double scale) =>
        scale <= 1.0 ? 0 : Math.Max(0, size * (scale - 1) / 2);

    /// <summary>
    /// The largest multiplier whose growth still fits <paramref name="room"/> on every side.
    /// <paramref name="room"/> is the distance from the icon's resting edge to the grid cell's
    /// edge (see <see cref="CardOverflowRoom.Around"/>); a non-finite room means "unknown host"
    /// and leaves the request untouched. A zoom that already fits is returned as asked, so this
    /// never shrinks the effect below what the cell can show — and never touches the resting
    /// size, which is the whole point.
    /// </summary>
    public static double ScaleWithin(double size, double scale, double room)
    {
        if (scale <= 1.0 || size <= 0) return Math.Min(scale, 1.0);
        if (!double.IsFinite(room)) return scale;

        // growth = size * (scale - 1) / 2 <= room  →  scale <= 1 + 2 * room / size
        var ceiling = 1.0 + Math.Max(0, 2 * room) / size;
        return Math.Min(scale, ceiling);
    }
}
