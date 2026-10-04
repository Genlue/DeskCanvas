using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Views;

/// <summary>
/// The host-side contract a widget control is created under. There are exactly two hosts:
/// the desktop window (<see cref="Widget"/>) and the 组件库 preview (Gallery).
/// <para>
/// A widget view is authored to be <i>hosted</i>, not to stand alone: it either leaves its
/// own <c>Margin</c> unset and lets the host supply the 12px content inset, or declares
/// <c>Margin="0"</c> when its artwork is meant to run full-bleed to the card edge (the Notes
/// header bar, Music, folders, weather tiles, search…). That inset comes from the
/// <c>.widget-content-host</c> styles in App.axaml, which <b>both</b> hosts put on their card
/// container — that is what keeps the two renderings from drifting apart. The preview used to
/// guess with a hard-coded <c>Margin 12</c> instead, which over-inset the 14 of 30 widgets
/// that author <c>Margin="0"</c> themselves.
/// </para>
/// <para>
/// This helper only adds the class markers those styles key off (<c>Frameless</c> /
/// <c>Flush</c>). Most widgets add their own, but a few get them from the host rather than
/// declaring them (Notes is the one that matters today), so the marking has to happen on
/// both paths or the preview would silently disagree with the desktop.
/// </para>
/// </summary>
public static class WidgetContentHost
{
    /// <summary>
    /// Mark <paramref name="control"/> with the host-owned classes. Idempotent: widgets may
    /// have declared their own already, and both hosts call this.
    /// </summary>
    public static void Prepare(UserControl control)
    {
        if (control is IFramelessWidget)
            Add(control, "Frameless");

        if (IsFlush(control))
            Add(control, "Flush");
    }

    /// <summary>
    /// Whether the host must treat this widget as flush, i.e. suppress the content inset
    /// entirely. Name-keyed rather than interface-keyed because these two views legitimately
    /// fill the card corner to corner (Notes' colored header bar, the aggregate card).
    /// </summary>
    public static bool IsFlush(UserControl control) =>
        (control is IFixedSizeWidget && control.GetType().Name == "AggregateView")
        || control.GetType().Name == "Note";

    private static void Add(UserControl control, string className)
    {
        if (!control.Classes.Contains(className))
            control.Classes.Add(className);
    }
}
