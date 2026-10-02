using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="ISidebarWidgetLayoutProvider" />
public class SidebarWidgetLayoutProvider(ILayoutProvider layoutProvider, string screenId,
    string instanceId, WidgetLayout? widgetLayout) : ISidebarWidgetLayoutProvider
{
    /// <inheritdoc />
    /// <remarks>
    /// Only locates the owning display. Unlike a desktop widget this never moves the entry
    /// across screens — sidebar instances belong to exactly one screen's sidebar.
    /// </remarks>
    public string ScreenId { get; set; } = screenId;

    /// <inheritdoc />
    public string InstanceId { get; } = instanceId;

    /// <inheritdoc />
    public bool IsSidebar => true;

    /// <inheritdoc />
    public event DataChangedEvent<WidgetLayout>? DataChanging;

    /// <inheritdoc />
    public event DataChangedEvent<WidgetLayout>? DataChanged;

    /// <inheritdoc />
    public WidgetLayout Get() => widgetLayout!;

    /// <inheritdoc />
    public (int Columns, int Rows)? Span
    {
        get
        {
            var entry = FindEntry();
            if (entry is not { HasSpan: true }) return null;
            return (entry.Columns, entry.Rows);
        }
    }

    /// <inheritdoc />
    public bool SaveSpan(int columns, int rows) => MutateEntry(entry => entry with
    {
        Columns = Math.Max(1, columns),
        Rows = Math.Max(1, rows)
    });

    /// <summary>
    /// Locate this instance's live entry, or <c>null</c> when the screen or the entry is gone
    /// (profile switch, replug, removal).
    /// </summary>
    private SidebarWidgetEntry? FindEntry()
    {
        var screen = layoutProvider.Get().FindById(ScreenId);
        var items = screen?.Sidebar?.Items;
        if (items is null) return null;

        foreach (var entry in items)
            if (entry.InstanceId == InstanceId)
                return entry;
        return null;
    }

    /// <summary>
    /// Apply <paramref name="edit"/> to this instance's stored entry. Returns <c>false</c> without
    /// writing anything when the entry is not in the sidebar — the layout owns the set of sidebar
    /// widgets, so a save may only update one that is already there (appending is exactly how a
    /// widget of the outgoing profile would resurrect itself inside a freshly loaded layout).
    /// </summary>
    private bool MutateEntry(Func<SidebarWidgetEntry, SidebarWidgetEntry> edit)
    {
        var screens = layoutProvider.Get();
        var screen = screens.FindById(ScreenId);
        var sidebar = screen?.Sidebar;
        if (screen is null || sidebar is null) return false;

        var entries = new List<SidebarWidgetEntry>(sidebar.Items);
        var index = entries.FindIndex(entry => entry.InstanceId == InstanceId);
        if (index < 0) return false;

        var updated = edit(entries[index]);
        if (updated == entries[index]) return true;

        entries[index] = updated;
        layoutProvider.Save(screens.WithScreen(screen with { Sidebar = sidebar with { Widgets = entries } }));
        return true;
    }

    /// <inheritdoc />
    public void Save(WidgetLayout data)
    {
        DataChanging?.Invoke(this, widgetLayout, data);

        var screens = layoutProvider.Get();
        var screen = screens.FindById(ScreenId);
        if (screen?.Sidebar is null)
        {
            // The screen vanished (profile switch, replug) or never had a sidebar. Never append:
            // appending is exactly how a widget of the outgoing profile would resurrect itself
            // inside the freshly loaded layout.
            System.Diagnostics.Debug.WriteLine(
                $"[SidebarWidgetLayoutProvider] screen '{ScreenId}' has no sidebar — save ignored ({InstanceId})");
            return;
        }

        var index = screen.Sidebar.Items.FindIndex(entry => entry.InstanceId == InstanceId);
        if (index < 0)
        {
            // The entry is gone: the layout owns the set of sidebar widgets, a save may only
            // update one that is already there.
            System.Diagnostics.Debug.WriteLine(
                $"[SidebarWidgetLayoutProvider] {data.Type}/{data.SubType} ({InstanceId}) is no longer in the sidebar — save ignored");
            return;
        }

        var entries = new List<SidebarWidgetEntry>(screen.Sidebar.Items);
        entries[index] = entries[index] with { Layout = data };
        var updatedSidebar = screen.Sidebar with { Widgets = entries };

        layoutProvider.Save(screens.WithScreen(screen with { Sidebar = updatedSidebar }));

        var oldData = widgetLayout;
        widgetLayout = data;
        DataChanged?.Invoke(this, oldData, data);
    }

    /// <inheritdoc />
    public void Remove()
    {
        var screens = layoutProvider.Get();
        var screen = screens.FindById(ScreenId);
        if (screen?.Sidebar is null) return;

        var entries = screen.Sidebar.Items.Where(entry => entry.InstanceId != InstanceId).ToList();
        if (entries.Count == screen.Sidebar.Items.Count) return;

        var updatedSidebar = screen.Sidebar with { Widgets = entries };
        layoutProvider.Save(screens.WithScreen(screen with { Sidebar = updatedSidebar }));
    }
}
