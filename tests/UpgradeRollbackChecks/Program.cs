// UpgradeRollbackChecks: end-to-end drills for the upgrade and rollback paths on the
// real Core persistence pipeline, using the same sandbox as IdentityStorageChecks
// (DESKCANVAS_DATA_FOLDER redirected to a disposable temp folder before any Const
// access). Covered drills:
//   1. Upgrade   — a legacy v1 layout (plain WidgetLayout array, no identity) loads
//                  through the real LayoutProvider, gets stable identities backfilled
//                  and persisted as format v3; reloading is idempotent.
//   2. Save failure — real filesystem failures (uncreatable directory, locked
//                  destination) must not publish DataChanged and must leave the old
//                  document intact (P1 atomic-save contract).
//   3. Generation guard — write-backs never downgrade the persisted identity or the
//                  session generation; WidgetSession generation is monotonic.
//   4. Rollback  — a v3 document exported to the v1 field subset round-trips through
//                  the real pipeline losslessly for expressible entries; differences
//                  that v1 cannot express (ContentScale) are explicitly detected.
//   5. Widget manifest — IAssemblyProvider.GetAssemblyInfos tolerates missing/empty
//                  directories and non-widget DLLs; Activate's fallback chain keeps
//                  model types without default constructors creatable.

using System.Text.Json;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Services;
using DeskCanvas.Runtime;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok, string? detail = null)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(ok || detail == null ? "" : $" — {detail}")}");
    if (ok) passed++; else failures.Add(name);
}

// Sandbox must be pinned before any Const static initializer runs.
var root = Path.Combine(Path.GetTempPath(), "DeskCanvas-UpgradeRollbackChecks", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", root);
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", root);
var layoutPath = DeskCanvas.Core.Const.LayoutFile;
Directory.CreateDirectory(Path.GetDirectoryName(layoutPath)!);

try
{
    // ---------- Drill 1: upgrade (v1 → v3, identity backfill, idempotent reload) ----------
    const string legacyV1 = """
        [{"Type":"Clock","SubType":"Digital","X":10,"Y":20,"Width":200,"Height":120,"Settings":null}]
        """;
    File.WriteAllText(layoutPath, legacyV1);

    var first = new LayoutProvider().Get().AllWidgets.Single();
    Check("legacy v1 layout loads through the real pipeline", first.Type == "Clock" && first.SubType == "Digital");
    Check("legacy entry receives a stable identity", first.HasStableIdentity);
    Check("upgrade preserves geometry (X/Y/Width/Height)",
        first.X == 10 && first.Y == 20 && first.Width == 200 && first.Height == 120);

    var persistedRaw = File.ReadAllText(layoutPath);
    var persisted = JsonSerializer.Deserialize<ScreensLayout>(persistedRaw)!;
    Check("migration is persisted as format v3", persistedRaw.Contains("\"Version\":3") && persisted.Version >= 3);
    Check("persisted entry carries the backfilled identity",
        persisted.AllWidgets.Single().InstanceId == first.InstanceId);

    var reloaded = new LayoutProvider().Get().AllWidgets.Single();
    Check("reload keeps the same InstanceId (idempotent round-trip)", reloaded.InstanceId == first.InstanceId);
    Check("reload keeps the migrated geometry", reloaded.X == 10 && reloaded.Y == 20);
    Check("reloaded entry keeps the generation assigned at migration", reloaded.Generation == 1);

    // ---------- Drill 2: save failure (P1 contract on the real pipeline) ----------
    // 2a. Bad path injection: the parent path component is an existing file, so
    //     Directory.CreateDirectory inside JsonParser.Save fails on the real filesystem.
    var blockerPath = Path.Combine(root, "blocker");
    File.WriteAllText(blockerPath, "i am a file, not a directory");
    var badPath = Path.Combine(blockerPath, "layout.json");
    var badParser = new ScreensParser(badPath);
    var badChanged = 0;
    badParser.DataChanged += (_, _, _) => badChanged++;
    var badSaveThrew = false;
    try { badParser.Save(SeedScreens()); }
    catch (Exception) { badSaveThrew = true; }
    Check("uncreatable directory makes Save throw", badSaveThrew);
    Check("failed Save does not publish DataChanged", badChanged == 0);

    // 2b. Locked destination: the temp commit succeeds but File.Replace on a file
    //     held with FileShare.None fails, so neither cache nor file may change.
    var lockedPath = Path.Combine(root, "locked", "layout.json");
    Directory.CreateDirectory(Path.GetDirectoryName(lockedPath)!);
    File.WriteAllText(lockedPath, JsonSerializer.Serialize(SeedScreens()));
    var lockedParser = new ScreensParser(lockedPath);
    _ = lockedParser.Get(); // populate the cache with the readable old document
    var lockedChanged = 0;
    lockedParser.DataChanged += (_, _, _) => lockedChanged++;
    await using (File.Open(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
    {
        var lockedSaveThrew = false;
        try { lockedParser.Save(SeedScreens()); }
        catch (Exception) { lockedSaveThrew = true; }
        Check("locked destination makes Save throw", lockedSaveThrew);
        Check("locked-destination Save does not publish DataChanged", lockedChanged == 0);
        Check("locked-destination Save keeps the cached old value",
            lockedParser.Get().Screens.Single().Layout.Count == 1);
    }

    Check("failed Save keeps the original document intact",
        JsonSerializer.Deserialize<ScreensLayout>(File.ReadAllText(lockedPath))!.AllWidgets.Count == 1);

    // ---------- Drill 3: generation guard ----------
    // Seed a v3 document whose entry already carries identity + generation 3.
    var stableId = Guid.NewGuid();
    var identified = new WidgetLayout("Clock", "Digital", 10, 20, 200, 120, null,
        InstanceId: stableId, Generation: 3);
    File.WriteAllText(layoutPath, JsonSerializer.Serialize(new ScreensLayout(
        [new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, [identified])], 3)));
    var layoutProvider = new LayoutProvider();
    var stored = layoutProvider.Get().AllWidgets.Single();
    var widgetProvider = new WidgetLayoutProvider(layoutProvider, ScreensLayout.LegacyPrimaryId, stored);

    // A legacy-shaped write (no identity, e.g. an old settings caller) must not
    // downgrade the persisted identity.
    var legacyShaped = new WidgetLayout("Clock", "Digital", 50, 60, 200, 120, JsonSerializer.SerializeToElement(new { mode = "flip" }));
    widgetProvider.Save(legacyShaped);
    var afterWrite = JsonSerializer.Deserialize<ScreensLayout>(File.ReadAllText(layoutPath))!.AllWidgets.Single();
    Check("legacy-shaped write-back preserves the session identity", afterWrite.InstanceId == stableId);
    Check("write-back never downgrades the generation", afterWrite.Generation == 3);
    Check("write-back applies the new geometry",
        afterWrite.X == 50 && afterWrite.Y == 60 && afterWrite.Settings.HasValue);
    Check("live provider reflects the written entry", widgetProvider.Get().SameWidgetAs(afterWrite));

    // EnsureIdentity on an already identified entry is a no-op (no regeneration).
    var noop = stored.EnsureIdentity();
    Check("EnsureIdentity on an identified entry keeps the same instance",
        ReferenceEquals(noop, stored) && noop.InstanceId == stableId);

    // WidgetSession generation is monotonic across the lifecycle.
    var session = new WidgetSession(new WidgetInstanceId(Guid.NewGuid()));
    var gen0 = session.Generation;
    await session.InitializeAsync();
    var gen1 = session.Generation;
    await session.ApplySettingsAsync(new { volume = 2 });
    var gen2 = session.Generation;
    await session.SetActivityAsync(ActivitySnapshot.Preview);
    var gen3 = session.Generation;
    await session.DisposeAsync();
    var gen4 = session.Generation;
    Check("session generation starts at zero", gen0 == 0);
    Check("session generation is monotonic across lifecycle commands",
        gen0 < gen1 && gen1 < gen2 && gen2 < gen3 && gen3 < gen4);

    // ---------- Drill 4: rollback (v3 → v1 field subset, real round-trip) ----------
    // Export the migrated document to the v1 field subset (Type/SubType/X/Y/Width/
    // Height/Settings). ContentScale is NOT expressible in v1: the difference must be
    // detectable rather than silently assumed away.
    var scaled = new WidgetLayout("Notes", "Note", 5, 6, 300, 200, JsonSerializer.SerializeToElement(new { text = "hi" }),
        ContentScale: 1.25, InstanceId: Guid.NewGuid(), Generation: 2);
    var plain = stored with { X = 50, Y = 60, Settings = JsonSerializer.SerializeToElement(new { mode = "flip" }), Generation = 3 };
    File.WriteAllText(layoutPath, JsonSerializer.Serialize(new ScreensLayout(
        [new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, [plain, scaled])], 3)));
    var v3Doc = new LayoutProvider().Get();
    var v1Export = JsonSerializer.Serialize(v3Doc.AllWidgets.Select(w => new
    {
        w.Type, w.SubType, w.X, w.Y, w.Width, w.Height, w.Settings
    }));
    Check("v1 field view is expressible for v3 entries", v1Export.Contains("\"Type\"") && v1Export.Contains("\"Settings\""));

    File.WriteAllText(layoutPath, v1Export);
    var rolledBack = new LayoutProvider().Get();
    Check("v1 export reloads through the real pipeline (rollback re-upgrade)",
        rolledBack.AllWidgets.Count == 2 && rolledBack.AllWidgets.All(w => w.HasStableIdentity));
    var plainAfter = rolledBack.AllWidgets.Single(w => w.Type == "Clock");
    Check("plain entry round-trips losslessly to v1 and back",
        plainAfter.X == 50 && plainAfter.Y == 60 && plainAfter.Width == 200 && plainAfter.Height == 120 &&
        plainAfter.Settings.HasValue);
    var scaledAfter = rolledBack.AllWidgets.Single(w => w.Type == "Notes");
    var rollbackDiffDetected = scaledAfter.ContentScale == null && scaled.X == scaledAfter.X && scaledAfter.Width == 300;
    Check("rollback difference (v1 cannot express ContentScale) is explicitly detected",
        rollbackDiffDetected,
        "expected ContentScale loss to be observable, got " + scaledAfter.ContentScale?.ToString());

    // ---------- Drill 5: widget manifest contract (IAssemblyProvider) ----------
    var provider = new NullServiceProvider();
    var assemblyProvider = new AssemblyProvider(provider); // ctor must tolerate a missing Widgets folder
    Check("AssemblyProvider constructor tolerates a missing Widgets folder", true);

    var emptyDir = Path.Combine(root, "widgets-empty");
    Directory.CreateDirectory(emptyDir);
    Check("GetAssemblyInfos on an empty directory yields no entries",
        assemblyProvider.GetAssemblyInfos(emptyDir).Count == 0);

    var missingDir = Path.Combine(root, "widgets-missing");
    Check("GetAssemblyInfos on a missing directory does not throw and yields no entries",
        assemblyProvider.GetAssemblyInfos(missingDir).Count == 0);

    // A non-widget DLL (no WidgetInfoAttribute) must be ignored, not crash the scan.
    var noiseDir = Path.Combine(root, "widgets-noise");
    Directory.CreateDirectory(noiseDir);
    var contractsDll = Path.Combine(AppContext.BaseDirectory, "DeskCanvas.Contracts.dll");
    Check("scan input DLL is available next to the harness", File.Exists(contractsDll));
    if (File.Exists(contractsDll))
    {
        File.Copy(contractsDll, Path.Combine(noiseDir, "DeskCanvas.Contracts.dll"), overwrite: true);
        Check("non-widget DLL is ignored by the manifest scan",
            assemblyProvider.GetAssemblyInfos(noiseDir).Count == 0);
    }

    // Activation fallback: a model type without a default constructor (the classic
    // ClockModel case) must still be creatable through the three-level fallback.
    var plainModel = (PlainModel)assemblyProvider.Activate(typeof(PlainModel));
    Check("Activate creates a default-constructible model", plainModel != null);
    var fallbackModel = (FallbackModel)assemblyProvider.Activate(typeof(FallbackModel));
    Check("Activate fallback chain fills optional constructor values for models without a default constructor",
        fallbackModel.A == 7 && fallbackModel.B == "seed",
        $"got A={fallbackModel.A}, B={fallbackModel.B}");
}
finally
{
    Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", null);
    Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", null);
    try { Directory.Delete(root, recursive: true); } catch { }
}

Console.WriteLine(failures.Count == 0
    ? $"\nUpgradeRollbackChecks ALL PASS ({passed} 项)"
    : $"\nUpgradeRollbackChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

/// <summary>A fresh two-screen-format-compatible document with one entry.</summary>
static ScreensLayout SeedScreens() => new(
    [new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null,
        [new WidgetLayout("Clock", "Digital", 10, 20, 200, 120, null)])], 3);

/// <summary>Real-pipeline parser over an arbitrary path (JsonParser is bound to
/// Const.LayoutFile via LayoutProvider; these drills need failure paths).</summary>
sealed class ScreensParser(string path) : JsonParser<ScreensLayout>(path), ILayoutProvider;

/// <summary>Service provider stub: no services — drives the Activate fallback chain
/// into its optional-parameter branch.</summary>
sealed class NullServiceProvider : IServiceProvider
{
    public object? GetService(Type serviceType) => null;
}

sealed class PlainModel;

sealed class FallbackModel(int a = 7, string b = "seed")
{
    public int A => a;
    public string B => b;
}
