using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Interfaces;

/// <summary>
/// Layout provider for a single sidebar widget. Behaves like <see cref="IWidgetLayoutProvider"/>
/// but identifies its entry by a stable <see cref="InstanceId"/> inside the screen's
/// <see cref="SidebarLayout"/> — never by the widget's type/position, so several instances of
/// the same type coexist with independent configuration.
/// <para>
/// It is also the only live view of the instance's <b>grid span</b>. The span is read back from
/// the stored layout on every call instead of being captured once: the entry a host was built
/// from is a snapshot, so a captured copy would keep reporting the pre-resize span (and, before
/// the sidebar became a grid, the pre-resize pixel size) for the rest of the session.
/// </para>
/// </summary>
public interface ISidebarWidgetLayoutProvider : IWidgetLayoutProvider
{
    /// <summary>Stable identity of this sidebar widget instance (never derived from type or position).</summary>
    string InstanceId { get; }

    /// <summary>Always <c>true</c> — lets shared code tell a sidebar provider from a desktop one.</summary>
    bool IsSidebar { get; }

    /// <summary>
    /// The instance's stored grid span, or <c>null</c> when the entry was written before the
    /// sidebar became a grid (the caller then falls back to the widget's own idiom).
    /// </summary>
    (int Columns, int Rows)? Span { get; }

    /// <summary>
    /// Publish a new grid span for this instance.
    /// </summary>
    /// <param name="columns">Column span to store.</param>
    /// <param name="rows">Row span to store.</param>
    /// <returns><c>false</c> when the entry is no longer part of the sidebar (the layout owns the
    /// set of sidebar widgets; a save may only update one that is already there).</returns>
    bool SaveSpan(int columns, int rows);
}
