using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Implemented by widget views that manage their own state and therefore refresh
/// in place when their layout/settings change, instead of being recreated — the
/// old recreation path destroys the editing session (caret, focus, IME state)
/// and can leave focused controls detached from the visual tree.
/// </summary>
public interface IWidgetSelfRefreshing
{
    /// <summary>
    /// Refresh the view from the new widget layout data.
    /// </summary>
    /// <param name="layout">The new layout of this widget.</param>
    void Refresh(WidgetLayout layout);
}
