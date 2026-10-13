using Avalonia;
using Avalonia.Controls;

namespace DeskCanvas.Views.Controls;

/// <summary>Who owns the page's single outer vertical scroll.</summary>
public enum SettingsPageScrollOwnership
{
    /// <summary>
    /// Default for untransformed/third-party pages: the page keeps its own outer ScrollViewer;
    /// the host only gives it a finite box (no viewport scrolling, no double scrollbar).
    /// </summary>
    LegacySelf,

    /// <summary>
    /// Migrated pages: the page has NO outer ScrollViewer of its own — the host viewport
    /// (EditWidget content area) is the single vertical scroll owner, with the horizontal
    /// scrollbar disabled.
    /// </summary>
    Host,
}

/// <summary>
/// Marks a widget settings page's scroll contract (W4). The page declares the ownership on its
/// root control; the EditWidget content viewport consumes it when the settings page is opened
/// (see <c>EditWidget.ApplyScrollOwnership</c>). A page must never be in "host scrolls AND page
/// root scrolls" state — migration changes the marker and removes the page's outer ScrollViewer
/// in the same commit.
/// </summary>
public static class SettingsPage
{
    public static readonly AttachedProperty<SettingsPageScrollOwnership> ScrollOwnershipProperty =
        AvaloniaProperty.RegisterAttached<Control, SettingsPageScrollOwnership>(
            "ScrollOwnership", typeof(SettingsPage));

    public static SettingsPageScrollOwnership GetScrollOwnership(Control element)
        => element.GetValue(ScrollOwnershipProperty);

    public static void SetScrollOwnership(Control element, SettingsPageScrollOwnership value)
        => element.SetValue(ScrollOwnershipProperty, value);
}
