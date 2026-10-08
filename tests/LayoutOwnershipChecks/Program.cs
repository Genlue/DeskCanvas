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
        StableMonitorIdentity();
        IndependentTwinConfigurations();
        OfflineScreenOwnership();
        LegacyMigrationAccumulates();
        ClonedDesktopTopology();
        PhysicalBindingAndDisambiguation();

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

    private static void StableMonitorIdentity()
    {
        var first = new ScreenLayout("first", "Twin|1920x1080", null, null, null, null, [],
            HardwareId: "MODEL", MonitorId: "DISPLAY#MODEL#ONE");
        var second = first with { Id = "second", MonitorId = "DISPLAY#MODEL#TWO" };
        var screen = new ScreenIdentity("DISPLAY2", "Twin", 1920, 1080, false, 1,
            "MODEL", "display#model#two");
        Check("monitor instance selects the right twin independent of enumeration order",
            ScreenMatcher.Match([first, second], screen)?.Id == "second");
        Check("unknown twin cannot steal an existing twin's configuration",
            ScreenMatcher.Match([first, second], screen with { MonitorId = "THREE" }) == null);
        Check("single physical monitor survives a port/instance change",
            ScreenMatcher.Match([first], screen with { MonitorId = "NEW_PORT" })?.Id == "first");
        Check("ambiguous attached twins disable the model fallback",
            ScreenMatcher.Match([first], screen with { MonitorId = "NEW_PORT" }, null, false) == null);
        Check("pinned hardware never falls back to a same-name stand-in",
            ScreenMatcher.Match([first], screen with { HardwareId = "OTHER", MonitorId = "OTHER" }) == null);
        Check("a bound legacy primary does not migrate to the replacement primary",
            ScreenMatcher.Match([first with { Id = ScreensLayout.LegacyPrimaryId, Key = null }],
                screen with { IsPrimary = true, HardwareId = "OTHER", MonitorId = "OTHER" }) == null);
        var old = first with { MonitorId = null };
        Check("older model-only configuration upgrades compatibly",
            ScreenMatcher.Match([old], screen)?.Id == "first");
        Check("an interface-only monitor still has a stable identity",
            !(screen with { HardwareId = "", FriendlyName = ScreenIdentity.FallbackName }).IsAnonymous);
        var oldTwins = new[] { old, second with { MonitorId = null } };
        var upgrading = new HashSet<string>();
        Check("older model-only twins upgrade one bucket per physical screen",
            ScreenMatcher.Match(oldTwins, screen, upgrading, false, true)?.Id == "first"
            && ScreenMatcher.Match(oldTwins, screen with { MonitorId = "ONE" }, upgrading, false, true)?.Id == "second");
        var consumed = new HashSet<string>();
        Check("consuming matching prevents two screens sharing one layout",
            ScreenMatcher.Match([first], screen with { MonitorId = first.MonitorId! }, consumed) != null
            && ScreenMatcher.Match([first], screen with { MonitorId = first.MonitorId! }, consumed) == null);
    }

    private static void IndependentTwinConfigurations()
    {
        var first = new ScreenLayout("first", "Twin|1920x1080", null, null, null, null, [], Margin: 5, Radius: 12);
        var second = first with { Id = "second", Margin = 20, Radius = 30 };
        var store = new ScreensLayout([first, second]);
        Check("empty twin screens retain independent margins and radii", ReferenceEquals(store.Deduplicate(), store));
        var roundTrip = JsonSerializer.Deserialize<ScreensLayout>(JsonSerializer.Serialize(store))!;
        Check("screen appearance survives layout persistence", roundTrip.Screens.Count == 2
            && roundTrip.FindById("second")?.Margin == 20 && roundTrip.FindById("second")?.Radius == 30);
    }

    private static void OfflineScreenOwnership()
    {
        var widget = Widget("Clock", "Digital", 100, 200);
        var offline = new ScreenLayout("offline", "Offline|1920x1080", null, null, null, null, [widget]);
        var online = offline with { Id = "online", Key = "Online|2560x1440", Layout = [] };
        var store = new StubLayoutProvider(_ => new ScreensLayout([online, offline]));
        var provider = new WidgetLayoutProvider(store, offline.Id, widget);
        provider.Save(widget with { Width = 300 });
        Check("an offline screen keeps its widget coordinates and owner",
            store.Get().FindById(online.Id)!.Layout.Count == 0
            && store.Get().FindById(offline.Id)!.Layout[0].X == 100
            && provider.ScreenId == offline.Id);
    }

    private static void LegacyMigrationAccumulates()
    {
        var first = Widget("Clock", "Digital", -1900, 10);
        var second = Widget("Notes", "Note", -1800, 100);
        var stays = Widget("Monitor", "Metric", 100, 200);
        var existing = Widget("Calendar", "Calendar", 200, 300);
        var target = new ScreenLayout("left", "Left|1920x1080", null, null, null, null, [existing]);
        var store = ScreensLayout.FromLegacy([first, second, stays]).AddScreen(target);
        (string, int, int)? Owner(WidgetLayout widget) => widget.X < 0 ? (target.Id, -1920, 0) : null;
        var migrated = store.MigrateLegacyWidgets(Owner);
        Check("legacy migration accumulates all widgets on the destination",
            migrated.FindById(target.Id)!.Layout.Count == 3
            && migrated.FindById(target.Id)!.Layout[1].X == 20
            && migrated.FindById(target.Id)!.Layout[2].X == 120);
        Check("legacy migration removes every transferred source widget exactly once",
            migrated.FindById(ScreensLayout.LegacyPrimaryId)!.Layout.Count == 1
            && migrated.FindById(ScreensLayout.LegacyPrimaryId)!.Layout[0].Type == "Monitor");
        Check("legacy migration is idempotent", ReferenceEquals(migrated.MigrateLegacyWidgets(Owner), migrated));
    }

    private static void ClonedDesktopTopology()
    {
        var primary = (0, 0, 1920, 1080);
        var left = (-1920, 0, 1920, 1080);
        Check("cloned outputs are a coherent single logical desktop",
            DisplayTopology.HasSameGeometry([primary], [primary, primary]));
        Check("extended-to-single transitional rectangles are incoherent",
            !DisplayTopology.HasSameGeometry([primary], [primary, left]));
        Check("same-resolution geometry mismatch is never coherent",
            !DisplayTopology.HasSameGeometry([primary], [left]));
        Check("mirror mode preserves the surviving owner independent of output order",
            DisplayTopology.SelectDevice(["DISPLAY2", "DISPLAY1"], "DISPLAY2") == "DISPLAY2");
        Check("initial mirror owner is deterministic",
            DisplayTopology.SelectDevice(["DISPLAY2", "DISPLAY1"], null) == "DISPLAY1");
        Check("missing output selects only an exact geometry candidate",
            DisplayTopology.SelectDevice([], "DISPLAY1") == null);
    }

    private static void PhysicalBindingAndDisambiguation()
    {
        // 1. External screen taking DISPLAY1 must NEVER steal internal screen's configuration
        var internalConfig = new ScreenLayout("internal-id", "0C8E|2560x1600", "Laptop Screen", @"\\.\DISPLAY1", null, null, [],
            HardwareId: "BOE0C8E", MonitorId: @"\\?\DISPLAY#BOE0C8E#5&1551B5BC&0&UID4355#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
            Binding: new ScreenBinding(@"DISPLAY\BOE0C8E\5&1551B5BC&0&UID4355", HardwareId: "BOE0C8E"));

        var externalScreen = new ScreenIdentity(@"\\.\DISPLAY1", "G52 Max", 2560, 1440, true, 1.0,
            HardwareId: "SAC2463",
            MonitorId: @"\\?\DISPLAY#SAC2463#5&1551B5BC&0&UID4352#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
            PhysicalIdentity: new PhysicalMonitorIdentity(@"DISPLAY\SAC2463\5&1551B5BC&0&UID4352",
                @"\\?\DISPLAY#SAC2463#5&1551B5BC&0&UID4352#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
                "SAC", "2463", "SAC2463", null, "G52 Max", false));

        Check("external screen on DISPLAY1 rejects internal config despite matching DeviceName",
            ScreenMatcher.Match([internalConfig], externalScreen) == null);

        // 2. Exact PnP binding matches even when port/GDI shifts
        var externalConfig = new ScreenLayout("external-id", "G52 Max|2560x1440", "Desk Monitor", @"\\.\DISPLAY2", null, null, [],
            HardwareId: "SAC2463", MonitorId: @"\\?\DISPLAY#SAC2463#5&1551B5BC&0&UID4352#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
            Binding: new ScreenBinding(@"DISPLAY\SAC2463\5&1551B5BC&0&UID4352", HardwareId: "SAC2463"));

        Check("exact PnP binding matches external monitor when it becomes DISPLAY1",
            ScreenMatcher.Match([internalConfig, externalConfig], externalScreen)?.Id == "external-id");

        // 3. Batch matching accurately separates Bound, Unconfigured and Unresolved
        var anonScreen = new ScreenIdentity(@"\\.\DISPLAY3", ScreenIdentity.FallbackName, 1920, 1080, false, 1.0);
        var unconfScreen = new ScreenIdentity(@"\\.\DISPLAY4", "New Screen", 1920, 1080, false, 1.0,
            HardwareId: "NEW1234", MonitorId: @"DISPLAY\NEW1234\123");

        var batch = ScreenMatcher.MatchBatch([internalConfig, externalConfig], [externalScreen, anonScreen, unconfScreen]);
        Check("batch match: external screen is Bound",
            batch.Matches.Any(m => m.Screen == externalScreen && m.Status == BindingStatus.Bound && m.Config?.Id == "external-id"));
        Check("batch match: anonymous virtual screen is Unresolved",
            batch.Matches.Any(m => m.Screen == anonScreen && m.Status == BindingStatus.Unresolved));
        Check("batch match: new monitor is Unconfigured",
            batch.Matches.Any(m => m.Screen == unconfScreen && m.Status == BindingStatus.Unconfigured));
        Check("batch match: offline internal config remains unmatched",
            batch.UnmatchedConfigs.Count == 1 && batch.UnmatchedConfigs[0].Id == "internal-id");

        // 4. Layout v3 JSON round-trip preserves ScreenBinding and aliases
        var storeV3 = new ScreensLayout([externalConfig with
        {
            Binding = externalConfig.Binding! with { InstanceAliases = [@"DISPLAY\SAC2463\OLD_PORT"] }
        }], Version: 3);
        var json = JsonSerializer.Serialize(storeV3);
        var reloaded = JsonSerializer.Deserialize<ScreensLayout>(json)!;
        Check("v3 persistence preserves ScreenBinding and instance aliases",
            reloaded.Version == 3
            && reloaded.Screens[0].Binding?.MatchesInstance(@"DISPLAY\SAC2463\OLD_PORT") == true);

        // 5. User original scenario: Extend -> Single External (as primary) -> Extend
        // External screen: SAC2463, Internal screen: BOE0C8E
        var screenExt = externalScreen with { DeviceName = @"\\.\DISPLAY1", IsPrimary = true };
        var screenInt = new ScreenIdentity(@"\\.\DISPLAY2", "0C8E", 2560, 1600, false, 1.0,
            HardwareId: "BOE0C8E",
            MonitorId: @"\\?\DISPLAY#BOE0C8E#5&1551B5BC&0&UID4355#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
            PhysicalIdentity: new PhysicalMonitorIdentity(@"DISPLAY\BOE0C8E\5&1551B5BC&0&UID4355",
                @"\\?\DISPLAY#BOE0C8E#5&1551B5BC&0&UID4355#{E6F07B5F-EE97-4A90-B076-33F57BF4EAA7}",
                "BOE", "0C8E", "BOE0C8E", null, "0C8E", true));

        // Extended mode:
        var matchExt = ScreenMatcher.Match([internalConfig, externalConfig], screenExt);
        var matchInt = ScreenMatcher.Match([internalConfig, externalConfig], screenInt);
        Check("extended mode: external matches external config", matchExt?.Id == "external-id");
        Check("extended mode: internal matches internal config", matchInt?.Id == "internal-id");

        // Switch to single external screen (internal is offline, external is primary DISPLAY1):
        var singleExt = screenExt with { DeviceName = @"\\.\DISPLAY1", IsPrimary = true };
        var matchSingle = ScreenMatcher.Match([internalConfig, externalConfig], singleExt);
        Check("single external: external still uses external config, never steals internal", matchSingle?.Id == "external-id");

        // Back to extended:
        var matchBackExt = ScreenMatcher.Match([internalConfig, externalConfig], screenExt);
        var matchBackInt = ScreenMatcher.Match([internalConfig, externalConfig], screenInt);
        Check("back to extended: external keeps external config", matchBackExt?.Id == "external-id");
        Check("back to extended: internal restores internal config", matchBackInt?.Id == "internal-id");
    }

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
