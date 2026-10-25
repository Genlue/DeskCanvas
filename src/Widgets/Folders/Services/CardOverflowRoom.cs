using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Folders.Services;

/// <summary>
/// How much room a launcher view has for painting outside its card.
/// <para>
/// The hard boundary of the hover zoom is the widget <b>window</b>, and the window is exactly one
/// grid cell (<c>GridService.SetSize</c>: span × cell). Inside that cell the card is the cell inset
/// by <c>Widget.WidgetMargin</c>; whatever the view then leaves inside the card — the host's
/// content inset, its own padding, the centring gap of a small icon — is still inside the cell and
/// still usable by the zoom. This class measures that whole distance instead of assuming it equals
/// the widget margin: with 图标大小 70 % the icon sits 10.8 DIP inside the card on top of the 10 DIP
/// band, so the margin-only rule capped the zoom at half the room it really had.
/// </para>
/// <para>
/// The boundary is the <b>cell</b>, not the desktop: a zoomed icon still cannot paint over a
/// neighbouring widget, because past the window edge the OS clips.
/// </para>
/// <para>
/// Outside a widget host (the 组件库 preview, the render checks) there is no window edge to
/// respect, so the room is reported as infinite and the requested zoom is drawn as-is.
/// </para>
/// </summary>
public static class CardOverflowRoom
{
    /// <summary>
    /// Distance from <paramref name="icon"/>'s resting rectangle to the widget window's edge —
    /// i.e. how far the icon may grow on its tightest side before the cell boundary, in the same
    /// (unscaled) units as the icon's own size — or <see cref="double.PositiveInfinity"/> when
    /// there is no widget window to respect.
    /// </summary>
    public static double Around(Control icon)
    {
        if (TopLevel.GetTopLevel(icon) is not DeskCanvas.Views.Widget widget)
            return double.PositiveInfinity;

        var client = widget.ClientSize;
        if (client.Width <= 0 || client.Height <= 0)
            return double.PositiveInfinity;

        // Everything is measured in the ICON PARENT's space, which is also the space the zoom's
        // growth is expressed in (icon.Width × (scale − 1) / 2). Two reasons:
        //   • the icon's own render transform *is* the zoom, so translating the icon would fold the
        //     scale in flight into its origin and the room would shrink as the zoom grows;
        //   • the host may scale the whole content (内容缩放 ≠ 1×) on the presenter above the view,
        //     and asking the window for its rect in the parent's space lets that scale cancel out
        //     instead of mixing two unit systems.
        if (icon.GetVisualParent() is { } parent
            && widget.TranslatePoint(new Point(0, 0), parent) is { } near
            && widget.TranslatePoint(new Point(client.Width, client.Height), parent) is { } far)
        {
            return RoomIn(new Rect(near, far), icon.Bounds);
        }

        // Layout has not run yet: assume what the host does anyway, i.e. an icon centred in the
        // cell, so the zoom is capped by the cell rather than by nothing.
        var side = Math.Min(icon.Bounds.Width, icon.Bounds.Height);
        return Math.Max(0, (Math.Min(client.Width, client.Height) - side) / 2);
    }

    /// <summary>
    /// The arithmetic behind <see cref="Around"/>, split out so a check can feed it the rectangles
    /// a real layout produced: the smallest distance from <paramref name="box"/> to the edge of the
    /// grid cell it sits in. Both rectangles must share one coordinate space.
    /// </summary>
    public static double RoomIn(Rect cell, Rect box) =>
        Math.Max(0, Math.Min(
            Math.Min(box.X - cell.X, box.Y - cell.Y),
            Math.Min(cell.Right - box.Right, cell.Bottom - box.Bottom)));
}
