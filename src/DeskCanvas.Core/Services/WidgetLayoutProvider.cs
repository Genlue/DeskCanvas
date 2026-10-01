using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Core.Services;

/// <inheritdoc cref="IWidgetLayoutProvider" />
public class WidgetLayoutProvider(ILayoutProvider layoutProvider, string screenId, WidgetLayout? widgetLayout) : IWidgetLayoutProvider
{
    /// <inheritdoc />
    public string ScreenId { get; set; } = screenId;

    /// <inheritdoc />
    public event DataChangedEvent<WidgetLayout>? DataChanging;

    /// <inheritdoc />
    public event DataChangedEvent<WidgetLayout>? DataChanged;

    /// <inheritdoc />
    public WidgetLayout Get() => widgetLayout!;

    /// <inheritdoc />
    public void Save(WidgetLayout data)
    {
        DataChanging?.Invoke(this, widgetLayout, data);
        var screens = layoutProvider.Get();
        var screen = screens.FindById(ScreenId);
        var index = screen == null ? -1 : ResolveIndex(screen.Layout);

        if (index < 0)
        {
            // Self-heal screen-identity drift. The widget window is alive, so its entry
            // exists in SOME stored screen configuration — but this provider's ScreenId
            // can go stale (docked/undocked topology changes, regenerated screen ids),
            // and dropping every save then silently broke everything the widget writes:
            // settings edits never persisted ("add folder fails") and drag positions
            // reverted on the next activation ("widget jumps back"). Search every stored
            // screen for the entry and rebind instead — this still only ever UPDATES an
            // existing entry, never appends one (the resurrected-widget guard below).
            foreach (var candidate in screens.Screens)
            {
                var candidateIndex = WidgetLayout.IndexOfIdentity(candidate.Layout, widgetLayout!);
                if (candidateIndex < 0) continue;

                screen = candidate;
                index = candidateIndex;
                ScreenId = candidate.Id;
                break;
            }
        }

        if (screen == null || index < 0)
        {
            // The layout owns the set of widgets: a save may only UPDATE an entry that is
            // already there. Appending when the entry is missing resurrected widgets the
            // user had just left behind — most visibly during a profile switch, where the
            // still-alive widgets of the outgoing profile re-added themselves to the
            // freshly loaded layout (persisted to disk, so the duplicate sets survived a
            // restart).
            System.Diagnostics.Debug.WriteLine(
                $"[WidgetLayoutProvider] {data.Type}/{data.SubType} is no longer in screen '{ScreenId}' — save ignored");
            return;
        }

        var layout = screen.Layout;
        layout = layout.Select((item, i) => i == index ? data : item).ToList();

        layoutProvider.Save(screens.WithScreen(screen with { Layout = layout }));
        var oldData = widgetLayout;
        widgetLayout = data;
        DataChanged?.Invoke(this, oldData, data);
    }

    /// <inheritdoc />
    public void Remove()
    {
        var screens = layoutProvider.Get();
        var screen = screens.FindById(ScreenId);
        if (screen == null) return;

        var index = ResolveIndex(screen.Layout);
        var layout = index == -1
            ? screen.Layout
            : screen.Layout.Where((_, i) => i != index).ToList();

        layoutProvider.Save(screens.WithScreen(screen with { Layout = layout }));
    }

    /// <summary>
    /// Locate this provider's widget inside the screen's layout list
    /// (see <see cref="WidgetLayout.IndexOfIdentity"/>: reference first, then identity).
    /// </summary>
    private int ResolveIndex(System.Collections.Generic.List<WidgetLayout> layout)
    {
        var current = widgetLayout;
        if (current == null) return -1;

        return WidgetLayout.IndexOfIdentity(layout, current);
    }
}