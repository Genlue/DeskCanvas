using System;
using Avalonia;
using Avalonia.Controls;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Services;

/// <summary>
/// Coordinates dragging desktop widgets into the Stack Edit window.
/// </summary>
public static class StackDropCoordinator
{
    /// <summary>The currently active Edit window (target for drop).</summary>
    public static Window? ActiveTargetWindow { get; set; }

    /// <summary>Handler that accepts a dropped widget layout and returns true if absorbed.</summary>
    public static Func<WidgetLayout, bool>? ActiveDropHandler { get; set; }

    /// <summary>Fired when a widget is successfully dropped and accepted.</summary>
    public static event Action<WidgetLayout>? WidgetDropped;

    /// <summary>Whether a stack drop session is currently active and visible.</summary>
    public static bool IsActive => ActiveTargetWindow is { IsVisible: true } && ActiveDropHandler != null;

    /// <summary>
    /// Checks if a given screen point (in physical pixels) falls within the active target window bounds.
    /// </summary>
    public static bool ContainsScreenPoint(PixelPoint pt)
    {
        if (ActiveTargetWindow is not { IsVisible: true } win) return false;
        var scaling = win.DesktopScaling <= 0 ? 1.0 : win.DesktopScaling;
        var width = win.ClientSize.Width > 0 ? win.ClientSize.Width : (double.IsNaN(win.Width) ? 0 : win.Width);
        var height = win.ClientSize.Height > 0 ? win.ClientSize.Height : (double.IsNaN(win.Height) ? 0 : win.Height);
        var rect = new PixelRect(win.Position, new PixelSize(
            (int)Math.Round(width * scaling),
            (int)Math.Round(height * scaling)));
        return rect.Contains(pt);
    }

    public static bool TryAccept(WidgetLayout layout)
    {
        if (ActiveDropHandler == null) return false;
        var accepted = ActiveDropHandler.Invoke(layout);
        if (accepted)
        {
            WidgetDropped?.Invoke(layout);
        }
        return accepted;
    }
}
