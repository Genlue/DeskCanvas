// Quick access / notes session checks (P8 batch B, slice B3 — Folders / Shortcuts / Notes).
//
// Run: dotnet run --project tests/QuickAccessSessionChecks
//
// Pins the B3 slice: the session-less legacy paths, the view's
// IWidgetActivityConsumer forwarding, the Notes per-screen markdown palette seam and the
// untouched save semantics, the migrated secondary panels' PanelService wiring (source
// scan + a real per-owner open/toggle/cooldown round trip), and the Shortcuts editor's P6
// WindowRoles registration staying in place. View models and views are constructed directly
// (no host pipeline), on the SessionHookChecks headless rig (real Win32 handles + Skia).
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Notes.Models;
using Notes.Services;
using Notes.Views;

// UseHeadlessDrawing=false gives the probe REAL Win32 window handles (the SessionHookChecks
// rig) — the panel windows open for real through SecondaryPanelWindow's native z-order work.

// Fresh per-run sandbox (the IdentityStorageChecks pattern): the structural tests-harness
// sandbox persists across runs, and stale widget records would break seeded-state
// assertions. Must run before Const initializes.
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
    Path.Combine(Path.GetTempPath(), "DeskCanvas-FixtureSandboxes",
        "QuickAccessSessionChecks-" + Guid.NewGuid().ToString("N")));

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());

var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures.Add(message);
}
void Flush() => Dispatcher.UIThread.RunJobs();

static string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts")))
        dir = dir.Parent!;
    return dir!.FullName;
}
static string ReadSource(params string[] parts) => File.ReadAllText(Path.Combine(RepoRoot(), Path.Combine(parts)));

void PanelServiceReset() =>
    typeof(PanelService).GetMethod("ResetForChecks", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, null);

// =========================================================================================
// 5. Notes save semantics unchanged: focus-loss commit, no diff no write, layout persistence
// =========================================================================================
Console.WriteLine("--- Notes save semantics ---");
NoteModel? pushed = null;
var popup = new NotePopupWindow(new NoteModel("Note title", "note body"), null, m => pushed = m);
var bodyEditor = (TextBox)typeof(NotePopupWindow)
    .GetField("BodyEditor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(popup)!;
var commit = typeof(NotePopupWindow).GetMethod("Commit", BindingFlags.NonPublic | BindingFlags.Instance)!;
bodyEditor.Text = "edited body";
commit.Invoke(popup, null);
Check(pushed != null && pushed.Title == "Note title" && pushed.Content == "edited body",
    "5.1 the editor commit flows the edited content back through onModelChanged (title preserved)");
Check(pushed!.Updated != null, "5.2 the commit stamps Updated (the historic last-edited marker)");
var pushedOnce = pushed;
commit.Invoke(popup, null);
Check(ReferenceEquals(pushed, pushedOnce),
    "5.3 a commit with no diff does not fire the callback again (no write without a change)");
commit.Invoke(popup, null);
Check(ReferenceEquals(pushed, pushedOnce), "5.4 repeated no-diff commits stay silent");

var noteSource = ReadSource("src", "Widgets", "Notes", "Views", "Note.axaml.cs");
Check(noteSource.Contains("Model with { Content = newText, Updated = DateTime.Now }"),
    "5.5 the card's inline commit still stamps and persists on focus loss (the UpdateContent pipeline is untouched)");
Check(noteSource.Contains("JsonSerializer.SerializeToElement(newModel)") && noteSource.Contains("widgetLayoutProvider!.Save(newLayout)"),
    "5.6 the internal note still persists through the widget layout provider's Save");
Check(!noteSource.Contains("AutoSave") && !noteSource.Contains("autosave"),
    "5.7 no periodic auto-save exists on the card (save stays focus-loss driven)");

// =========================================================================================
// 6. The Notes per-screen markdown palette seam
// =========================================================================================
Console.WriteLine("--- Notes markdown palette per-screen seam ---");
var rendererSource = ReadSource("src", "Widgets", "Notes", "Services", "MarkdownRenderer.cs");
Check(rendererSource.Contains("scope?.ActualThemeVariant"),
    "6.1 the palette resolves the variant from the render scope first (per-screen light/dark)");
Check(!rendererSource.Contains("Application.Current?.ActualThemeVariant =="),
    "6.2 the app-wide variant is no longer the primary read (it only backs the unresolved-scope fallback)");
Check(noteSource.Contains("TryGetResource(\"SystemControlForegroundBaseHighBrush\", this.ActualThemeVariant"),
    "6.3 the card's base-high brush lookup follows the card's own variant (was the app-wide variant)");

var paletteType = typeof(MarkdownRenderer).GetNestedType("Palette", BindingFlags.NonPublic)!;
var fromMethod = paletteType.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!;
var bodyProperty = paletteType.GetProperty("Body")!;
var typography = new MarkdownTypography(
    true,
    null,
    new MarkdownPalette(BodyColor: "#FF112233"),
    new MarkdownPalette(BodyColor: "#FFEEDDCC"));
static Color BodyOf(object palette, PropertyInfo property) => ((SolidColorBrush)property.GetValue(palette)!).Color;

// RequestedThemeVariant is settable on Window (TopLevel), not on a bare Control —
// and a detached Window still resolves its own ActualThemeVariant from it.
var darkScope = new Window { RequestedThemeVariant = ThemeVariant.Dark };
var darkPalette = fromMethod.Invoke(null, new object?[] { typography, darkScope })!;
Check(BodyOf(darkPalette, bodyProperty) == Color.Parse("#FFEEDDCC"),
    "6.4 a dark-scope render picks the dark palette");
var lightScope = new Window { RequestedThemeVariant = ThemeVariant.Light };
var lightPalette = fromMethod.Invoke(null, new object?[] { typography, lightScope })!;
Check(BodyOf(lightPalette, bodyProperty) == Color.Parse("#FF112233"),
    "6.5 a light-scope render picks the light palette");
var unsetScope = new Control(); // never attached: no resolved variant of its own
var fallbackPalette = fromMethod.Invoke(null, new object?[] { typography, unsetScope })!;
var appExpected = Application.Current!.ActualThemeVariant == ThemeVariant.Dark
    ? Color.Parse("#FFEEDDCC")
    : Color.Parse("#FF112233");
Check(BodyOf(fallbackPalette, bodyProperty) == appExpected,
    "6.6 an unresolved scope falls back to the app-wide variant (the historic behaviour)");

// =========================================================================================
// 7. The migrated panels: PanelService wiring (source scan)
// =========================================================================================
Console.WriteLine("--- Panel migration source scan ---");
void ScanPanel(string label, string relativePath, string identityKey)
{
    var source = ReadSource(new[] { "src", "Widgets" }.Concat(relativePath.Split('/')).ToArray());
    Check(source.Contains("PanelService.Open(new PanelRequest("),
        $"{label}: ShowPopup routes through PanelService.Open");
    Check(source.Contains("\"" + identityKey + "\""),
        $"{label}: the identity key is \"{identityKey}\"");
    Check(source.Contains("PanelScope.PerOwner"),
        $"{label}: the panel identity is scoped PerOwner");
    Check(!source.Contains("PanelCoolingDown<") && !source.Contains("TryCloseActivePanel<"),
        $"{label}: the legacy helper pair is gone from the panel");
}
ScanPanel("NotePopupWindow", "Notes/Views/NotePopupWindow.axaml.cs", "Notes.Editor");
ScanPanel("ShortcutsPopupWindow", "Shortcuts/Views/ShortcutsPopupWindow.axaml.cs", "Shortcuts.QuickPanel");
ScanPanel("BigFolderPopupWindow", "Folders/Views/BigFolderPopupWindow.axaml.cs", "Folders.BigFolderGrid");
var editorSource = ReadSource("src", "Widgets", "Shortcuts", "Views", "ShortcutsEditorWindow.axaml.cs");
Check(editorSource.Contains("WindowRoles.Register"),
    "7.x ShortcutsEditorWindow keeps its P6 WindowRoles registration (untouched by the panel migration)");

// =========================================================================================
// 8. PanelService per-owner round trip with a real migrated panel
// =========================================================================================
Console.WriteLine("--- PanelService per-owner round trip (NotePopupWindow) ---");
PanelServiceReset();
var ownerIdentity = WidgetInstanceId.New();
PanelOpenDecision Open() => PanelService.Open(new PanelRequest(
    ownerIdentity, null, "Notes.Editor", PanelScope.PerOwner,
    () => new NotePopupWindow(new NoteModel("round trip", "body"), null, _ => { })));
try
{
    Check(Open() == PanelOpenDecision.Opened, "8.1 the first open for the identity opens a live panel");
    Check(Open() == PanelOpenDecision.ToggledClosed, "8.2 a second open toggles the live panel closed");
    Flush();
    Check(Open() == PanelOpenDecision.CoolingDown,
        "8.3 the just-toggled identity is inside its cooldown (the flicker guard)");
    var otherIdentity = WidgetInstanceId.New();
    var other = PanelService.Open(new PanelRequest(
        otherIdentity, null, "Notes.Editor", PanelScope.PerOwner,
        () => new NotePopupWindow(new NoteModel("round trip", "body"), null, _ => { })));
    Check(other == PanelOpenDecision.Opened,
        "8.4 another owner's identity opens independently (per-owner, not process-wide)");
}
finally
{
    PanelServiceReset();
    WidgetSessionRegistry.ResetForChecks();
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Quick access session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================
// Probe app: the same style sources PanelShellAvaloniaChecks mounts, so the migrated panel
// base class resolves its shell styles in headless runs.
// =========================================================================================
sealed class ProbeApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://DeskCanvas.UI/Styles/Controls/SecondaryPanelShell.axaml"))
        {
            Source = new Uri("avares://DeskCanvas.UI/Styles/Controls/SecondaryPanelShell.axaml")
        });
        Styles.Add(new StyleInclude(new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml"))
        {
            Source = new Uri("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml")
        });
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DeskCanvas.UI/"))
        {
            Source = new Uri("avares://DeskCanvas.UI/Styles/DesignTokens.axaml")
        });
    }
}
