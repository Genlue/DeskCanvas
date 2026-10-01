using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DeskCanvas.Services;

/// <summary>
/// Unified mouse wheel horizontal scrolling service for the entire application.
/// Enforces the global rule:
/// - Mouse wheel UP (finger push forward, delta.Y > 0) scrolls LEFT (Offset.X decreases).
/// - Mouse wheel DOWN (finger pull back, delta.Y < 0) scrolls RIGHT (Offset.X increases).
/// - Automatically applies to all present and future horizontal ScrollViewers.
/// </summary>
public static class HorizontalScrollHelper
{
    private static bool isRegistered;

    /// <summary>
    /// Registers the global class handler on <see cref="ScrollViewer"/>.
    /// Safe to call multiple times; idempotent.
    /// </summary>
    public static void RegisterGlobal()
    {
        if (isRegistered) return;
        isRegistered = true;

        // Register on InputElement.PointerWheelChangedEvent for ScrollViewer
        InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(
            (scroller, e) => HandlePointerWheel(scroller, e),
            RoutingStrategies.Tunnel,
            handledEventsToo: false);

        InputElement.PointerWheelChangedEvent.AddClassHandler<ScrollViewer>(
            (scroller, e) => HandlePointerWheel(scroller, e),
            RoutingStrategies.Bubble,
            handledEventsToo: true);
    }

    /// <summary>
    /// Translates vertical wheel delta into horizontal scroll offset if the ScrollViewer can scroll horizontally.
    /// </summary>
    public static void HandlePointerWheel(ScrollViewer scroller, PointerWheelEventArgs e)
    {
        if (e.Handled) return;

        var delta = e.Delta.Y;
        if (delta == 0) return;

        var maxX = scroller.Extent.Width - scroller.Viewport.Width;
        if (maxX <= 0) return;

        var maxY = scroller.Extent.Height - scroller.Viewport.Height;
        var isVerticalDisabled = scroller.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled;
        var isPurelyHorizontal = isVerticalDisabled || maxY <= 0.5;
        var isShiftHeld = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        // If it can scroll vertically and vertical is enabled and Shift is not held, leave for default vertical scroll
        if (!isPurelyHorizontal && !isShiftHeld)
            return;

        var pointer = e.GetPosition(scroller);
        if (pointer.X < 0 || pointer.Y < 0 ||
            pointer.X > scroller.Bounds.Width || pointer.Y > scroller.Bounds.Height)
            return;

        const double scrollStep = 48.0;

        // Rule: Mouse wheel UP (delta > 0) -> scroll LEFT (targetX decreases).
        //       Mouse wheel DOWN (delta < 0) -> scroll RIGHT (targetX increases).
        var targetX = Math.Clamp(scroller.Offset.X - delta * scrollStep, 0, maxX);

        if (Math.Abs(targetX - scroller.Offset.X) > 0.001)
        {
            scroller.Offset = new Vector(targetX, scroller.Offset.Y);
            e.Handled = true;
        }
    }
}
