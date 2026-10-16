// ResourcePressureChecks — resource pressure evaluation (P7 slice B) check fixture.
//
// Run: dotnet run --project tests/ResourcePressureChecks
//
// Pure .NET: references only DeskCanvas.Contracts + DeskCanvas.Runtime. The consumer wiring
// checks are source scans (ResourceLedgerChecks' FindRepoRoot pattern) so no host object is
// instantiated; the consumer decision logic itself is covered by evaluator assertions plus
// those scans (the consumers keep their decision helpers private inside the host project,
// which this fixture deliberately does not reference).
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

var failures = new List<string>();
var passed = 0;
void Check(string name, bool ok)
{
    if (ok) { passed++; Console.WriteLine($"[PASS] {name}"); }
    else { failures.Add(name); Console.WriteLine($"[FAIL] {name}"); }
}

const long Cap = 1000;

// ---------------------------------------------------------------- Evaluate: threshold matrix

Check("evaluate: zero total is None",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(0, Cap, Rising: false)) == ResourcePressureLevel.None);
Check("evaluate: ratio 0.49 is None",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(499, Cap, Rising: false)) == ResourcePressureLevel.None);
Check("evaluate: ratio 0.5 (boundary) is Watch",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(500, Cap, Rising: false)) == ResourcePressureLevel.Watch);
Check("evaluate: ratio 0.79 is Watch",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(799, Cap, Rising: false)) == ResourcePressureLevel.Watch);
Check("evaluate: ratio 0.8 (boundary) is High",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(800, Cap, Rising: false)) == ResourcePressureLevel.High);
Check("evaluate: ratio 1.0 is High",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(1000, Cap, Rising: false)) == ResourcePressureLevel.High);
Check("evaluate: cap of zero or less is None (no cap agreed, no invented pressure)",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(Cap, 0, Rising: false)) == ResourcePressureLevel.None &&
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(Cap, -5, Rising: false)) == ResourcePressureLevel.None);
Check("evaluate: negative total defends to None",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(-100, Cap, Rising: false)) == ResourcePressureLevel.None);
Check("evaluate: middle budget tier scale (384 MiB cap, 384 MiB total → High)",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(
        ResourceBudget.Tiers[1].SoftCapBytes, ResourceBudget.Tiers[1].SoftCapBytes, Rising: false)) == ResourcePressureLevel.High);

// ---------------------------------------------------------------- ApplyHysteresis

ResourcePressureEvaluator.ResetForChecks();

Check("hysteresis: candidate equal to previous is maintained",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.Watch, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.Watch &&
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.None, 2)
        == ResourcePressureLevel.None);
Check("hysteresis: differing candidate is not adopted before stableRounds rounds (round 1)",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.None);
Check("hysteresis: differing candidate is adopted on the stableRounds-th consecutive round (round 2)",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.Watch);
Check("hysteresis: after adoption the new level is maintained",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.Watch, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.Watch);

ResourcePressureEvaluator.ResetForChecks();
Check("hysteresis: stableRounds=1 adopts a differing candidate on the first round",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.High, 1)
        == ResourcePressureLevel.High);

ResourcePressureEvaluator.ResetForChecks();
Check("hysteresis: stableRounds=0 switches immediately",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.High, 0)
        == ResourcePressureLevel.High);

ResourcePressureEvaluator.ResetForChecks();
var jitterStable = true;
for (var i = 0; i < 6; i++)
{
    var c = i % 2 == 0 ? ResourcePressureLevel.Watch : ResourcePressureLevel.High;
    if (ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, c, 3) != ResourcePressureLevel.None)
        jitterStable = false;
}
Check("hysteresis: back-and-forth jitter never switches away from the previous level", jitterStable);

ResourcePressureEvaluator.ResetForChecks();
var divergenceResets = // Watch starts a streak, High diverges from the streak → nothing adopted
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.Watch, 3)
        == ResourcePressureLevel.None &&
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.High, 3)
        == ResourcePressureLevel.None &&
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.High, 3)
        == ResourcePressureLevel.None &&
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.High, 3)
        == ResourcePressureLevel.High;
Check("hysteresis: a diverging candidate restarts the streak (only truly consecutive rounds count)", divergenceResets);

ResourcePressureEvaluator.ResetForChecks();
// Adoption rule (pinned by the divergence test above): the Nth consecutive differing
// round adopts once N reaches stableRounds. After the reset the streak restarts at zero,
// so Watch round 1 stays None and round 2 adopts — proving the accumulator was cleared.
Check("hysteresis: ResetForChecks clears the accumulator (next differing round is round 1 again)",
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.None &&
    ResourcePressureEvaluator.ApplyHysteresis(ResourcePressureLevel.None, ResourcePressureLevel.Watch, 2)
        == ResourcePressureLevel.Watch);

// ---------------------------------------------------------------- TotalReclaimableBytes

Check("total: empty snapshot sums to 0",
    ResourcePressureEvaluator.TotalReclaimableBytes(new List<ResourceUsage>()) == 0);
Check("total: null snapshot defends to 0", ResourcePressureEvaluator.TotalReclaimableBytes(null!) == 0);

var rows = new List<ResourceUsage>
{
    new("glass.wallpaper", ResourceKind.GlassCapture, 1, 33_177_600, "t"),
    new("glass.source-cache", ResourceKind.GlassBackdrop, 3, 4_000_000, "t"),
    new("picture:1", ResourceKind.PictureFrame, 2, 8_000_000, "t"),
    new("clock.frame-cache:1", ResourceKind.GlyphMask, 1, 500_000, "t"),
};
Check("total: rows across kinds and owners are summed",
    ResourcePressureEvaluator.TotalReclaimableBytes(rows) == 33_177_600L + 4_000_000 + 8_000_000 + 500_000);

rows.Add(new("picture:2", ResourceKind.PicturePendingDecode, 1, 12_000_000, "t"));
Check("total: PicturePendingDecode counts as reclaimable",
    ResourcePressureEvaluator.TotalReclaimableBytes(rows) == 33_177_600L + 4_000_000 + 8_000_000 + 500_000 + 12_000_000);

rows.Add(new("future.owner", (ResourceKind)999, 1, 1_000, "t"));
Check("total: unknown kind values are tolerated and summed",
    ResourcePressureEvaluator.TotalReclaimableBytes(rows) == 33_177_600L + 4_000_000 + 8_000_000 + 500_000 + 12_000_000 + 1_000);

rows.Add(new("neg", ResourceKind.Other, 1, -5, "t"));
Check("total: negative byte rows do not reduce the sum",
    ResourcePressureEvaluator.TotalReclaimableBytes(rows) == 33_177_600L + 4_000_000 + 8_000_000 + 500_000 + 12_000_000 + 1_000);

// ---------------------------------------------------------------- Real singleton smoke (Runtime ledger)

ResourceLedger.ResetForChecks();
var ledger = ResourceLedger.Instance;
ledger.Report("glass.wallpaper", ResourceKind.GlassCapture, 1, 33_177_600, "t");
ledger.Report("glass.source-cache", ResourceKind.GlassBackdrop, 2, 6_000_000, "t");
Check("smoke: singleton snapshot total matches the reported sum",
    ResourcePressureEvaluator.TotalReclaimableBytes(ledger.Snapshot()) == 33_177_600L + 6_000_000);
Check("smoke: singleton total evaluates to None against a far cap",
    ResourcePressureEvaluator.Evaluate(new ResourcePressureState(
        ResourcePressureEvaluator.TotalReclaimableBytes(ledger.Snapshot()),
        ResourceBudget.Tiers[2].SoftCapBytes, Rising: false)) == ResourcePressureLevel.None);
ledger.Report("glass.wallpaper", ResourceKind.GlassCapture, 0, 0, "t");
Check("smoke: a zero report clears its row from the total",
    ResourcePressureEvaluator.TotalReclaimableBytes(ledger.Snapshot()) == 6_000_000);
ResourceLedger.ResetForChecks();
Check("smoke: ResetForChecks empties the singleton total",
    ResourcePressureEvaluator.TotalReclaimableBytes(ledger.Snapshot()) == 0);

// ---------------------------------------------------------------- Consumer wiring source scans
// The consumers keep their pressure decision helpers private in the host project, which this
// fixture deliberately does not reference (host TFM differs); the decision logic itself is
// covered by the evaluator checks above, the wiring by these scans.

var repoRoot = FindRepoRoot();
Check("repo root located (Directory.Build.props found above the probe binary)", repoRoot is not null);
if (repoRoot is null) return Finish(failures, passed);

string Read(string relativePath) => File.ReadAllText(Path.Combine(repoRoot!, relativePath));

var trimmer = Read("src/DeskCanvas/Services/MemoryTrimmerService.cs");
var wallpaper = Read("src/DeskCanvas/Services/LiquidGlassWallpaper.cs");

Check("MemoryTrimmerService observes pressure through ResourcePressureEvaluator.Evaluate",
    trimmer.Contains("ResourcePressureEvaluator.Evaluate"));
Check("MemoryTrimmerService escalates the requested priority under pressure (EscalateForPressure at RequestTrim entry)",
    trimmer.Contains("EscalateForPressure") && trimmer.Contains("priority = EscalateForPressure(priority, ObservePressure())"));
Check("MemoryTrimmerService degrades observation failure to no pressure",
    trimmer.Contains("return ResourcePressureLevel.None;"));
Check("LiquidGlassWallpaper observes pressure through ResourcePressureEvaluator.Evaluate",
    wallpaper.Contains("ResourcePressureEvaluator.Evaluate"));
Check("LiquidGlassWallpaper keeps DESKCANVAS_GLASS_IDLE_RELEASE_MS as an explicit override",
    wallpaper.Contains("DESKCANVAS_GLASS_IDLE_RELEASE_MS") && wallpaper.Contains("IdleReleaseGraceUserLocked"));
Check("LiquidGlassWallpaper shortens the idle grace under High pressure (grace / 4)",
    wallpaper.Contains("IdleReleaseGraceMs / 4"));
Check("LiquidGlassWallpaper applies consumer-local hysteresis to the pressure level",
    wallpaper.Contains("AdvanceIdlePressureHysteresis") && wallpaper.Contains("idlePressureStreak"));
Check("contracts evaluator exists with the full API surface",
    File.Exists(Path.Combine(repoRoot!, "src/DeskCanvas.Contracts/ResourcePressure.cs")) &&
    Read("src/DeskCanvas.Contracts/ResourcePressure.cs").Contains("static class ResourcePressureEvaluator") &&
    Read("src/DeskCanvas.Contracts/ResourcePressure.cs").Contains("ApplyHysteresis") &&
    Read("src/DeskCanvas.Contracts/ResourcePressure.cs").Contains("TotalReclaimableBytes"));

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
