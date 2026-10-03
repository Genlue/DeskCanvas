using System;
using System.Collections.Generic;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// One pagination indicator of a stack widget: the child's index, its display title and
/// whether it is the active child, plus the opacity the indicator should be drawn with.
/// </summary>
/// <param name="Index">Zero-based index of the child widget the indicator points at.</param>
/// <param name="Title">Display title of that child widget.</param>
/// <param name="IsActive">Whether that child widget is the currently active one.</param>
/// <param name="Opacity">Opacity the indicator should be rendered with.</param>
public record StackWidgetIndicatorItem(int Index, string Title, bool IsActive, double Opacity);

/// <summary>
/// Implemented by container widgets (like WidgetStackView) that host a carousel/stack of
/// interchangeable child widgets and expose external pagination indicator items.
/// </summary>
public interface IStackWidget
{
    /// <summary>Indicator items for pagination.</summary>
    IReadOnlyList<StackWidgetIndicatorItem> IndicatorItems { get; }
    /// <summary>Whether a transition animation is currently running.</summary>
    bool IsTransitionActive { get; }
    /// <summary>
    /// Whether the host should hide the pagination dots while idle (常态隐藏小圆点):
    /// they are only revealed while a switch transition runs or the pointer is over
    /// the widget, and hidden again afterwards.
    /// </summary>
    /// <remarks>
    /// Default-interface implementation on purpose: a previously-deployed widget DLL
    /// (bundle not yet re-extracted) still satisfies the extended interface and simply
    /// reports the historic "always show" behavior instead of failing a TypeLoad.
    /// </remarks>
    bool AutoHideIndicators => false;
    /// <summary>Switch to a specific child widget index.</summary>
    void SwitchToIndex(int index);
    /// <summary>Fired when indicator items or active index change.</summary>
    event EventHandler? IndicatorItemsChanged;

    /// <summary>Whether the current active child widget has a configurable settings dialog.</summary>
    bool CanEditCurrentChild { get; }
    /// <summary>Display title of the current active child widget.</summary>
    string? CurrentChildTitle { get; }
    /// <summary>Open the settings dialog for the current active child widget.</summary>
    void EditCurrentChild(object? ownerWindow = null);
}
