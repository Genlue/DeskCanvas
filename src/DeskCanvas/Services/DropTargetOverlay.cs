using Avalonia;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Owns the single <see cref="DropTargetOverlayWindow"/> every widget drag shares, and nothing
/// else: one instance for the process lifetime, shown on the first drag and hidden — never
/// closed — afterwards.
/// <para>
/// One instance, not one per widget, because only one native move loop can run at a time: a
/// second drag can only start after the first has ended, and the outline is dismissed on
/// <c>WM_EXITSIZEMOVE</c>, so a per-widget overlay would just be a second top-level window
/// fighting for the same z-band.
/// </para>
/// <para>
/// The callers drive it purely from the drag bracket (<c>WM_ENTERSIZEMOVE</c> /
/// <c>WM_MOVING</c> / <c>WM_EXITSIZEMOVE</c>) and must also call <see cref="Hide"/> when the
/// widget is torn down or position locking is on: a native move loop that never sends its exit
/// message (the window was destroyed mid-drag) would otherwise leave the outline on screen
/// forever.
/// </para>
/// </summary>
public static class DropTargetOverlay
{
    private static DropTargetOverlayWindow? window;

    /// <summary>
    /// Show (or move) the outline onto <paramref name="card"/>. Called for every move-loop frame,
    /// so the placement work is a handful of property writes and one window move.
    /// </summary>
    /// <param name="card">Card rectangle in physical screen pixels (already deflated by the
    /// widget margin).</param>
    /// <param name="cornerRadius">Card corner radius in physical pixels.</param>
    /// <param name="scaling">Scaling of the screen the card is on.</param>
    /// <param name="blocked">Whether the release would be refused here (禁止组件重叠 and the cell is
    /// taken). Only the stroke colour changes: same geometry, same window, same click-through
    /// contract — the outline is still one card's edge, it just stops promising it.</param>
    public static void Show(PixelRect card, int cornerRadius, double scaling, bool blocked = false)
    {
        var overlay = window ??= Create();
        // Placed BEFORE the first Show: a window that appears at its old position for one frame
        // reads as the outline jumping a cell behind the cursor.
        overlay.Place(card, cornerRadius, scaling, blocked);
        if (!overlay.IsVisible) overlay.Show();
    }

    /// <summary>
    /// Build the overlay ahead of the first drag, without showing it.
    /// <para>
    /// Called when a widget opens: the first drag after startup would otherwise load the XAML and
    /// register the window's style callback from inside <c>WM_ENTERSIZEMOVE</c>, which is the one
    /// place an unexpected activation would end the drag. The object costs nothing while hidden,
    /// and its native window is still created lazily by the first <see cref="Show"/> — with the
    /// ex-style callback registered in the constructor, so it is born click-through.
    /// </para>
    /// </summary>
    public static void Prepare() => window ??= Create();

    /// <summary>Dismiss the outline. Safe to call repeatedly and when nothing is shown.</summary>
    public static void Hide()
    {
        if (window is not { IsVisible: true } overlay) return;
        overlay.Hide();
    }

    private static DropTargetOverlayWindow Create()
    {
        var overlay = new DropTargetOverlayWindow();
        // The instance is reused for the app's lifetime, so a close only has to drop the
        // reference — the next drag builds a fresh window rather than showing a dead one.
        overlay.Closed += (_, _) =>
        {
            if (ReferenceEquals(window, overlay)) window = null;
        };
        return overlay;
    }
}
