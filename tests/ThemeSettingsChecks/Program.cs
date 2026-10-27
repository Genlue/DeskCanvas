using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

var failures = new List<string>();
void Check(string n, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {n}"); if (!ok) failures.Add(n); }
var clock = new ClockThemeSettingsAdapter();
var events = 0; clock.Store.Committed += (_, _) => events++;
var first = clock.CommitPalette("blue", 0);
Check("commit advances revision", first.Succeeded && first.Revision == 1);
Check("snapshot embeds committed revision", first.Snapshot.Revision == first.Revision);
var conflict = clock.CommitPalette("red", 0);
Check("stale expected revision conflicts", !conflict.Succeeded && conflict.Conflict == "revision-conflict");
var same = clock.CommitPalette("blue", 1);
Check("same value does not notify", same.Succeeded && events == 1);
Check("palette impact is explicit", (first.Snapshot.Impact & ThemeImpact.Palette) != 0);
var countdown = new CountdownThemeSettingsAdapter();
var material = countdown.CommitMaterial("acrylic", 0);
Check("second consumer uses same settings protocol", material.Succeeded && (material.Snapshot.Impact & ThemeImpact.Material) != 0);
var persisted = "original";
var saveFails = true;
var saves = 0;
var published = 0;
var session = new PersistentSettingsSession<string>(() => persisted, next =>
{
    saves++;
    if (saveFails) throw new IOException("test-only private filesystem diagnostic");
    persisted = next;
});
session.Committed += (_, _) => published++;
session.Stage("draft");
Check("staging does not persist or publish", saves == 0 && session.Revision == 0 && published == 0);
var failed = session.Commit(0);
Check("durable failure preserves draft and revision", !failed.Succeeded && failed.Error == "save-failed"
    && session.Draft == "draft" && session.Revision == 0 && published == 0 && persisted == "original");
saveFails = false;
var retry = session.Commit(0);
Check("retry publishes once after durable save", retry.Succeeded && retry.Snapshot == "draft"
    && session.Revision == 1 && published == 1 && saves == 2);
session.Commit(1);
Check("unchanged persistent draft skips duplicate save", saves == 2 && published == 1);
session.Stage("later-draft");
persisted = "other-editor";
var stale = session.Commit(1);
Check("external change conflicts and retains editor draft", !stale.Succeeded && stale.Error == "revision-conflict"
    && stale.Snapshot == "other-editor" && session.Draft == "later-draft" && saves == 2 && published == 1);
session.Reload();
Check("explicit reload resolves conflict without persistence", session.Draft == "other-editor"
    && session.Revision == 1 && saves == 2);
session.Stage("new-draft");
Check("wrong local expected revision conflicts without saving", !session.Commit(0).Succeeded && saves == 2);
Check("successful commit after reload advances revision", session.Commit(1).Succeeded
    && session.Revision == 2 && persisted == "new-draft" && published == 2);

// ---- 强调色偏移 / 跟随强调色: the stored record's own contract ---------------------------
// The two new parameters are appended with defaults so a settings file written before the
// feature still deserializes; NormalizeForSurface is the one place a hand-edited value is
// made honest before any resolver reads it.
var baseline = new DeskCanvas.Core.Models.Settings.Theme(
    DarkMode: null, AccentColor: null, OpacityLevel: 0.4, Monochrome: false,
    UseNativeFrame: false, FontFamily: "Inter");
Check("accent shift defaults to 0 and follow-accent to off",
    baseline.AccentShift == 0 && !baseline.FollowAccentBackground);
Check("out-of-range accent shift clamps to 100",
    (baseline with { AccentShift = 500 }).NormalizeForSurface().AccentShift == 100);
Check("negative accent shift clamps to 0",
    (baseline with { AccentShift = -20 }).NormalizeForSurface().AccentShift == 0);
Check("non-finite accent shift resolves to 0",
    (baseline with { AccentShift = double.NaN }).NormalizeForSurface().AccentShift == 0);
Check("follow-accent survives normalization",
    (baseline with { FollowAccentBackground = true }).NormalizeForSurface().FollowAccentBackground);
Check("normalization keeps the stored background pair",
    (baseline with { SolidBackgroundDark = "#112233", SolidBackgroundLight = "#FEDCBA" })
        .NormalizeForSurface() is { SolidBackgroundDark: "#112233", SolidBackgroundLight: "#FEDCBA" });

Console.WriteLine(failures.Count == 0 ? "ThemeSettingsChecks ALL PASS" : $"ThemeSettingsChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
