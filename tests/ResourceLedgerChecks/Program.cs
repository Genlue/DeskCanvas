// ResourceLedgerChecks — read-only resource ledger (P7 slice A) check fixture.
//
// Run: dotnet run --project tests/ResourceLedgerChecks
//
// Pure .NET: references only DeskCanvas.Contracts + DeskCanvas.Runtime. The wiring checks are
// source scans (WindowRoleChecks' FindRepoRoot pattern) so no rendering object is instantiated.
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok)
{
    if (ok) { passed++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add(name); Console.WriteLine($"[FAIL] {name}"); }
}

var ledger = ResourceLedger.Instance;
ResourceLedger.ResetForChecks();

// ---------------------------------------------------------------- Report / Snapshot aggregation

ledger.Report("a", ResourceKind.GlassBackdrop, 2, 100, "t1");
ledger.Report("a", ResourceKind.GlassBackdrop, 3, 150, "t2"); // absolute report overwrites the row
var snap = ledger.Snapshot();
Check("absolute report overwrites the same owner+kind row",
    snap.Count == 1 && snap[0].Owner == "a" && snap[0].Kind == ResourceKind.GlassBackdrop &&
    snap[0].Count == 3 && snap[0].EstimatedBytes == 150 && snap[0].ReleaseThread == "t2");

ledger.Report("a", ResourceKind.GlassCapture, 1, 64, "t");
snap = ledger.Snapshot();
Check("same owner with different kinds gets separate rows", snap.Count == 2);
Check("snapshot sorts by kind then owner",
    snap.SequenceEqual(snap.OrderBy(x => x.Kind).ThenBy(x => x.Owner, StringComparer.Ordinal)));

ledger.Report("b", ResourceKind.GlassBackdrop, 1, 10, "t");
snap = ledger.Snapshot();
Check("owners of one kind are ordered by name",
    snap.Where(x => x.Kind == ResourceKind.GlassBackdrop).Select(x => x.Owner).SequenceEqual(new[] { "a", "b" }));

ledger.Report("a", ResourceKind.GlassCapture, 0, 0, "t");
Check("zero report clears the row",
    ledger.Snapshot().Count(x => x.Owner == "a" && x.Kind == ResourceKind.GlassCapture) == 0);

// ---------------------------------------------------------------- Bounded table

ResourceLedger.ResetForChecks();
for (var i = 0; i < 260; i++)
    ledger.Report($"owner-{i:D3}", ResourceKind.Other, 1, i + 1, "t");
snap = ledger.Snapshot();
var otherRows = snap.Where(x => x.Kind == ResourceKind.Other).ToList();
Check($"bounded table keeps at most {ResourceLedger.MaxOwnersPerKind} rows per kind",
    otherRows.Count <= ResourceLedger.MaxOwnersPerKind);
Check("overflow row exists beyond the cap", otherRows.Any(x => x.Owner == ResourceLedger.OverflowOwner));
Check("overflow merging preserves total count", otherRows.Sum(x => x.Count) == 260);
Check("overflow merging preserves total bytes", otherRows.Sum(x => x.EstimatedBytes) == 260 * 261 / 2);

// ---------------------------------------------------------------- ResetForChecks

ResourceLedger.ResetForChecks();
Check("ResetForChecks wipes every row", ledger.Snapshot().Count == 0);
ledger.Report("x", ResourceKind.Other, 1, 1, "t");
Check("ResetForChecks then report works again", ledger.Snapshot().Count == 1);

// ---------------------------------------------------------------- GlassMemoryEstimator

Check("estimator: 1920x1080 BGRA", GlassMemoryEstimator.EstimateBgraBytes(1920, 1080) == 8_294_400L);
Check("estimator: 3840x2160 BGRA", GlassMemoryEstimator.EstimateBgraBytes(3840, 2160) == 33_177_600L);
Check("estimator: zero/negative dimensions defend to 0",
    GlassMemoryEstimator.EstimateBgraBytes(0, 1080) == 0 &&
    GlassMemoryEstimator.EstimateBgraBytes(1920, -4) == 0);

// ---------------------------------------------------------------- ResourceBudget.Tiers

Check("budget tiers: exactly three candidates", ResourceBudget.Tiers.Count == 3);
Check("budget tiers: 192/384/768 MiB values",
    ResourceBudget.Tiers[0].SoftCapBytes == 192L * 1024 * 1024 &&
    ResourceBudget.Tiers[1].SoftCapBytes == 384L * 1024 * 1024 &&
    ResourceBudget.Tiers[2].SoftCapBytes == 768L * 1024 * 1024);
Check("budget tiers: names are non-empty", ResourceBudget.Tiers.All(x => !string.IsNullOrEmpty(x.TierName)));

// ---------------------------------------------------------------- Defensive reporting

ResourceLedger.ResetForChecks();
var threw = false;
try
{
    ledger.Report("", ResourceKind.Other, 1, 1, "t");          // empty owner ignored
    ledger.Report("  ", ResourceKind.Other, 1, 1, "t");        // whitespace owner ignored
    ledger.Report(null!, ResourceKind.Other, 1, 1, "t");       // null owner ignored
    ledger.Report("neg", ResourceKind.Other, -5, -100, "t");   // negatives clamped, no throw
    ledger.Report("neg", ResourceKind.Other, 2, 50, "t");
}
catch { threw = true; }
Check("defensive inputs never throw", !threw);
var negRow = ledger.Snapshot().SingleOrDefault(x => x.Owner == "neg");
Check("empty/whitespace owners are not recorded", ledger.Snapshot().Count == 1);
Check("negative count/bytes are clamped", negRow is { Count: 2, EstimatedBytes: 50 });

ledger.Report("threadless", ResourceKind.Other, 1, 1, null!);
Check("null release thread is tolerated",
    ledger.Snapshot().Single(x => x.Owner == "threadless").ReleaseThread == "");

// ---------------------------------------------------------------- Wiring source scans
// The six wired counters must report through ResourceLedger; the P8 placeholders must NOT.
// (WallpaperSnapshotService does not exist in the tree — the wallpaper snapshot cache is
// LiquidGlassWallpaper's cached/stale pair, wired here as glass.wallpaper / GlassCapture.)

var repoRoot = FindRepoRoot();
Check("repo root located (Directory.Build.props found above the probe binary)", repoRoot is not null);
if (repoRoot is null) return Finish(failures, passed);

string Read(string relativePath) => File.ReadAllText(Path.Combine(repoRoot!, relativePath));

string[] wiredFiles =
[
    "src/DeskCanvas/Services/LiquidGlassSource.cs",            // glass.source-cache / GlassBackdrop
    "src/DeskCanvas/Services/LiquidGlassWallpaper.cs",         // glass.wallpaper / GlassCapture
    "src/DeskCanvas/Services/LiquidGlassRenderer.cs",          // glass.wallpaper-lease / GlassLease
    "src/Widgets/Picture/ViewModels/PictureViewModel.cs",      // picture:{n} / PictureFrame
    "src/Widgets/Clock/Services/FramelessGlassFrameCache.cs",  // clock.frame-cache:{n} / GlyphMask
];
foreach (var file in wiredFiles)
    Check($"{file} reports to the ledger", Read(file).Contains("ResourceLedger.Instance.Report"));

string[] notWiredFiles =
[
    "src/Widgets/Map/Services/MapTileService.cs",      // MapTile wiring is the P8 batch
    "src/Widgets/Folders/Services/FolderIconService.cs", // FileIcon wiring is the P8 batch
];
foreach (var file in notWiredFiles)
    Check($"{file} is NOT wired in this slice (P8 placeholder)", !Read(file).Contains("ResourceLedger"));

// The Picture decode path is synchronous (no pending-decode queue exists), so PicturePendingDecode
// must have no reporting site anywhere in src.
var pendingDecodeWired = Directory.EnumerateFiles(Path.Combine(repoRoot!, "src"), "*.cs", SearchOption.AllDirectories)
    .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
    .Any(p => File.ReadAllText(p).Contains("ResourceKind.PicturePendingDecode,"));
Check("PicturePendingDecode stays unwired (synchronous decode path)", !pendingDecodeWired);

return Finish(failures, passed);

static string? FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
        dir = dir.Parent;
    return dir?.FullName;
}

static int Finish(List<string> failures, int checks)
{
    Console.WriteLine();
    Console.WriteLine(failures.Count == 0
        ? $"ALL {checks} CHECKS PASSED"
        : $"{failures.Count} of {checks} checks FAILED:{Environment.NewLine}  - {string.Join($"{Environment.NewLine}  - ", failures)}");
    return failures.Count == 0 ? 0 : 1;
}
