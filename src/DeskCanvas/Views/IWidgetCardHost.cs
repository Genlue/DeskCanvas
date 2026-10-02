using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Views;

/// <summary>
/// A host that embeds a <see cref="WidgetCard"/>. Both the desktop widget window
/// (<see cref="Widget"/>) and the sidebar host implement it, so the card can forward its
/// interactive bits (stack pagination, the corner resize handle) to whoever owns the widget —
/// without the card knowing whether it lives on the desktop or in the sidebar.
/// <para>
/// The card's data bindings resolve against the same object by reflection, so every property
/// the card template references (material, radius, margin, background, outline, indicators,
/// resize visibility, …) must exist on the host as well.
/// </para>
/// </summary>
public interface IWidgetCardHost
{
    /// <summary>Whether this host is the sidebar (used by shared code that must branch).</summary>
    bool IsSidebarHost { get; }

    /// <summary>A stack pagination dot was clicked (see <c>StackWidgetIndicatorItem.Index</c>).</summary>
    void OnStackIndicatorClicked(object? sender, RoutedEventArgs e);

    /// <summary>The mouse wheel scrolled over the stack pagination dots.</summary>
    void OnStackIndicatorsWheelChanged(object? sender, PointerWheelEventArgs e);

    /// <summary>The bottom-right resize handle was pressed.</summary>
    void OnResizeHandlePressed(object? sender, PointerPressedEventArgs e);
}
