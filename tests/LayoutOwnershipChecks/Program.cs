using System.Text.Json;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Services;

namespace LayoutOwnershipChecks;

/// <summary>
/// Checks the ownership rule of the stored layout: the layout owns the set of widgets,
/// and a widget's provider may only <b>update</b> an entry that still exists.
///
/// This is the invariant whose violation showed the previous profile's widgets on top of
/// the new ones after a profile switch (and persisted to disk, so the duplicate sets
/// survived a restart): the outgoing widgets stayed alive long enough to observe the
/// freshly loaded layout, could not find their entry, and the provider's old
/// "append when missing" path re-created them inside the incoming configuration.
/// </summary>
class Program
{
    private static int failures;

    static int Main()
    {
        Console.WriteLine("=== Layout ownership checks ===");
        Console.WriteLine();

        UpdateExistingEntry();
        NoResurrectionAfterLayoutReplacement();
        IdentityMatchAcrossParsedDocuments();
        RemoveByIdentityAcrossParsedDocuments();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A normal save updates the widget's own entry in place — never duplicates it.</summary>
    private static void UpdateExistingEntry()
    {
        var widget = Widget("Clock", "FramelessDigital", 100, 100);
        var store = new StubLayoutProvider(store => store.WithScreen(store.Screens[0] with { Layout = [widget] }));

        var provider = new WidgetLayoutProvider(store, ScreensLayout.LegacyPrimaryId, widget);
        provider.Save(widget with { X = 250 });

        var layout = store.Get().Screens[0].Layout;
        Check("save updates the existing entry in place", layout.Count == 1 && layout[0].X == 250);

        // A second save must not add a copy either (the historic ghost-entry failure).
        provider.Save(widget with { X = 300 });
        layout = store.Get().Screens[0].Layout;
        Check("repeated saves keep exactly one entry", layout.Count == 1 && layout[0].X == 300);
    }

    /// <summary>
    /// The profile-switch scenario: the layout is replaced by another configuration while
    /// the old widget is still alive. Its save must be ignored, not appended.
    /// </summary>
    private static void NoResurrectionAfterLayoutReplacement()
    {
        var oldWidget = Widget("Clock", "FramelessDigital", 100, 100);
        var newWidget = Widget("Monitor", "SingleMetric", 400, 400);
        var store = new StubLayoutProvider(_ => Screens([oldWidget]));

        var provider = new WidgetLayoutProvider(store, ScreensLayout.LegacyPrimaryId, oldWidget);
        provider.Save(oldWidget with { X = 120 });
        Check("provider starts out owning an entry", store.Get().Screens[0].Layout[0].X == 120);

        // Profile switch: the stored layout now belongs to the incoming profile.
        store.Replace(Screens([newWidget]));

        // The outgoing widget ticks (activation, grid update, resize) and tries to save.
        provider.Save(oldWidget with { X = 999, Y = 999 });

        var layout = store.Get().Screens[0].Layout;
        Check("a widget missing from the layout is NOT re-appended", layout.Count == 1);
        Check("the incoming profile's widget is untouched",
            layout.Count == 1 && layout[0].Type == "Monitor" && layout[0].X == 400);
    }

    /// <summary>
    /// Entries re-read from disk carry a different <see cref="JsonElement"/> document, so
    /// record value equality fails on them; identity matching must still find the entry.
    /// </summary>
    private static void IdentityMatchAcrossParsedDocuments()
    {
        var first = Parse("""[{"Type":"Notes","SubType":"Note","X":0,"Y":940,"Width":152,"Height":152,"Settings":{"Content":"a"}}]""");
        var second = Parse("""[{"Type":"Notes","SubType":"Note","X":0,"Y":940,"Width":152,"Height":152,"Settings":{"Content":"b"}}]""");

        var stored = first[0];
        var reloaded = second[0];

        Check("record equality fails across documents (the trap)", !stored.Equals(reloaded));
        Check("identity match finds the reloaded entry", WidgetLayout.IndexOfIdentity([stored], reloaded) == 0);
        Check("reference match wins", WidgetLayout.IndexOfIdentity([stored, reloaded], stored) == 0);
        Check("a genuinely absent widget reports -1",
            WidgetLayout.IndexOfIdentity([stored], stored with { X = 12345 }) == -1);
    }

    /// <summary>Cross-screen transfer removes the old entry instead of leaving a stale copy.</summary>
    private static void RemoveByIdentityAcrossParsedDocuments()
    {
        var entries = Parse("""
        [{"Type":"Notes","SubType":"Note","X":0,"Y":940,"Width":152,"Height":152,"Settings":{"Content":"a"}},
         {"Type":"Clock","SubType":"Digital","X":0,"Y":0,"Width":152,"Height":152,"Settings":null}]
        """);

        // The widget object held by the running window is NOT one of the parsed instances.
        var live = Parse("""[{"Type":"Notes","SubType":"Note","X":0,"Y":940,"Width":152,"Height":152,"Settings":null}]""")[0];

        var index = WidgetLayout.IndexOfIdentity(entries, live);
        Check("transfer finds its entry among parsed siblings", index == 0);

        var remaining = entries.Where((_, i) => i != index).ToList();
        Check("transfer leaves exactly the other widget behind", remaining.Count == 1 && remaining[0].Type == "Clock");
    }

    private static List<WidgetLayout> Parse(string json) =>
        JsonSerializer.Deserialize<List<WidgetLayout>>(json)!;

    private static WidgetLayout Widget(string type, string subType, int x, int y) =>
        new(type, subType, x, y, 152, 152, null);

    private static ScreensLayout Screens(List<WidgetLayout> layout) =>
        new([new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, layout)]);

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    /// <summary>In-memory <see cref="ILayoutProvider"/> (no disk, no Avalonia).</summary>
    private sealed class StubLayoutProvider : ILayoutProvider
    {
        private ScreensLayout current;

        public StubLayoutProvider(Func<ScreensLayout, ScreensLayout> seed) =>
            current = seed(Screens([]));

        public event DataChangedEvent<ScreensLayout>? DataChanging;
        public event DataChangedEvent<ScreensLayout>? DataChanged;

        public void Replace(ScreensLayout layout) => current = layout;

        public ScreensLayout Get() => current;

        public void Save(ScreensLayout data)
        {
            var old = current;
            DataChanging?.Invoke(this, old, data);
            current = data;
            DataChanged?.Invoke(this, old, data);
        }
    }
}
