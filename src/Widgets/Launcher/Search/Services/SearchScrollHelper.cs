using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Search.Services;

public static class SearchScrollHelper
{
    /// <summary>
    /// Attaches horizontal wheel scrolling to a ScrollViewer:
    /// - Wheel UP (delta.Y > 0) -> scroll LEFT
    /// - Wheel DOWN (delta.Y < 0) -> scroll RIGHT
    /// </summary>
    public static void Attach(ScrollViewer scroller)
    {
        scroller.AddHandler(InputElement.PointerWheelChangedEvent, (sender, e) =>
        {
            var delta = e.Delta.Y;
            if (delta == 0) return;

            var maxX = scroller.Extent.Width - scroller.Viewport.Width;
            if (maxX <= 0) return;

            const double scrollStep = 48.0;
            var targetX = Math.Clamp(scroller.Offset.X - delta * scrollStep, 0, maxX);
            if (Math.Abs(targetX - scroller.Offset.X) > 0.001)
            {
                scroller.Offset = new Vector(targetX, scroller.Offset.Y);
                e.Handled = true;
            }
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, true);
    }
}
