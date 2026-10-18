using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using DeskCanvas.Services;

// ============================================================================================
// WindowRoleChecks — P6 slice 2 (window roles & platform capability boundary) check fixture.
//
// Run:  dotnet run --project tests/WindowRoleChecks
//
// Verifies, without touching any window's live behaviour (this slice is a behaviour-preserving
// declarative registration):
//   1. the six WindowRole profiles — completeness and the exact field values documented in
//      WindowRoles' class comment (hard-coded here so a silent profile change fails loudly);
//   2. the registry semantics — idempotent same-role re-registration, last-wins overwrite on a
//      different role, null for unregistered windows (GetRole / ProfileOf), ResetForChecks;
//   3. the wiring — every window-creation point this slice touched still calls
//      WindowRoles.Register (source scan, so the calls cannot be dropped silently);
//   4. the widget platform boundary — [DllImport] occurrences under src/Widgets must stay
//      exactly on the pinned whitelist (file + count): widgets keep Win32 interop in their own
//      vetted services/views and must not grow new P/Invoke surfaces.
//
// NOTE: SecondaryPanelWindow's own registration line (WindowRole.SecondaryPanel) is added by the
// integration agent — SecondaryPanelWindow.cs is outside this slice's whitelist. The registry
// API itself is covered below by registering a plain window with SecondaryPanel and querying it.
// ============================================================================================

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();

var failures = new List<string>();
var checks = 0;
void Check(bool condition, string message)
{
    checks++;
    Console.WriteLine($"[{(condition ? "PASS" : "FAIL")}] {message}");
    if (!condition) failures.Add(message);
}

// -------------------------------------------------------------------------------------------
// 1. Role profiles: every field, hard-coded to the values verified against the current code
//    (the evidence for each value lives in the WindowRoles class comment).
// -------------------------------------------------------------------------------------------
Console.WriteLine("--- role profiles ---");

var widget = WindowRoles.GetProfile(WindowRole.DesktopWidget);
Check(widget is { ShowInTaskbar: false, ZBand: WindowZBand.BottomBand, ActivatesOnShow: false,
        TracksFocusTheme: true, CloseOnDisplayChange: false },
    "DesktopWidget profile: no taskbar, bottom band, no activation, theme-tracked, not closed on display change");

var panel = WindowRoles.GetProfile(WindowRole.SecondaryPanel);
Check(panel is { ShowInTaskbar: false, ZBand: WindowZBand.AboveWidgets, ActivatesOnShow: true,
        TracksFocusTheme: true, CloseOnDisplayChange: true },
    "SecondaryPanel profile: no taskbar, above-widgets band, activates, theme-tracked, closed on display change");

var settings = WindowRoles.GetProfile(WindowRole.Settings);
Check(settings is { ShowInTaskbar: true, ZBand: WindowZBand.None, ActivatesOnShow: true,
        TracksFocusTheme: false, CloseOnDisplayChange: false },
    "Settings profile: taskbar window, no band, activates on show, not theme-tracked, hides instead of closing");

var dialog = WindowRoles.GetProfile(WindowRole.Dialog);
Check(dialog is { ShowInTaskbar: false, ZBand: WindowZBand.None, ActivatesOnShow: true,
        TracksFocusTheme: false, CloseOnDisplayChange: false },
    "Dialog profile: modal baseline (no taskbar, no band, activates, not theme-tracked, never closed by display change)");

var preview = WindowRoles.GetProfile(WindowRole.Preview);
Check(preview is { ShowInTaskbar: false, ZBand: WindowZBand.None, ActivatesOnShow: false,
        TracksFocusTheme: false, CloseOnDisplayChange: false },
    "Preview profile: reserved all-inert placeholder");

var anchor = WindowRoles.GetProfile(WindowRole.Anchor);
Check(anchor is { ShowInTaskbar: false, ZBand: WindowZBand.BottomBand, ActivatesOnShow: false,
        TracksFocusTheme: false, CloseOnDisplayChange: false },
    "Anchor profile: no taskbar, bottom band (pinned with the widgets), never activates or tracks");

Check(Enum.GetValues<WindowRole>().All(role => WindowRoles.GetProfile(role) is not null),
    "GetProfile resolves without throwing for all six roles (table completeness)");

// -------------------------------------------------------------------------------------------
// 2. Registry semantics (headless windows, never shown — registration is instance-keyed).
// -------------------------------------------------------------------------------------------
Console.WriteLine("--- registry semantics ---");

var unregistered = new Window();
Check(WindowRoles.GetRole(unregistered) is null,
    "GetRole on an unregistered window returns null");
Check(WindowRoles.ProfileOf(unregistered) is null,
    "ProfileOf on an unregistered window returns null");

var probed = new Window();
WindowRoles.Register(probed, WindowRole.Dialog);
Check(WindowRoles.GetRole(probed) == WindowRole.Dialog,
    "GetRole returns the registered role");
Check(WindowRoles.ProfileOf(probed) == WindowRoles.GetProfile(WindowRole.Dialog),
    "ProfileOf returns the profile of the registered role");

WindowRoles.Register(probed, WindowRole.Dialog);
Check(WindowRoles.GetRole(probed) == WindowRole.Dialog,
    "re-registering the same role is idempotent");

WindowRoles.Register(probed, WindowRole.Settings);
Check(WindowRoles.GetRole(probed) == WindowRole.Settings
      && WindowRoles.ProfileOf(probed) == WindowRoles.GetProfile(WindowRole.Settings),
    "re-registering with a different role overwrites (last registration wins)");

// The registry API is role-agnostic; SecondaryPanelWindow.cs gets its own Register line from
// the integration agent — the role itself must resolve exactly like any other.
var panelProbe = new Window();
WindowRoles.Register(panelProbe, WindowRole.SecondaryPanel);
Check(WindowRoles.GetRole(panelProbe) == WindowRole.SecondaryPanel
      && WindowRoles.ProfileOf(panelProbe) == panel,
    "SecondaryPanel registrations resolve through the same registry (window wiring owned by the integration slice)");

var roles = Enum.GetValues<WindowRole>();
var roundtrip = roles.Select(_ => new Window()).Zip(roles.Cast<WindowRole?>(),
        (window, role) => (window, role))
    .All(pair =>
    {
        WindowRoles.Register(pair.window, pair.role!.Value);
        return WindowRoles.GetRole(pair.window) == pair.role;
    });
Check(roundtrip, "every role round-trips through Register/GetRole on a fresh window");

typeof(WindowRoles).GetMethod("ResetForChecks", BindingFlags.NonPublic | BindingFlags.Static)!
    .Invoke(null, null);
Check(WindowRoles.GetRole(probed) is null && WindowRoles.GetRole(panelProbe) is null,
    "ResetForChecks clears the whole registration table");

bool throwsOnNull = false;
try { WindowRoles.Register(null!, WindowRole.Dialog); }
catch (ArgumentNullException) { throwsOnNull = true; }
Check(throwsOnNull, "Register(null, ...) throws ArgumentNullException");

// -------------------------------------------------------------------------------------------
// 3. Wiring source scan: each creation point this slice registered must still carry its
//    Register call. Paths are repo-relative, resolved from the repo root located by walking
//    up from the probe binary until the solution file appears.
// -------------------------------------------------------------------------------------------
Console.WriteLine("--- wiring source scan ---");

var repoRoot = FindRepoRoot();
Check(repoRoot is not null, "repo root located (Directory.Build.props found above the probe binary)");
if (repoRoot is null) return Finish(failures, checks);

string Read(string relativePath) => File.ReadAllText(Path.Combine(repoRoot!, relativePath));

Check(Read("src/DeskCanvas/Views/Widget.axaml.cs").Contains("WindowRoles.Register(this, WindowRole.DesktopWidget)"),
    "Widget.axaml.cs registers its window as DesktopWidget");
Check(Read("src/DeskCanvas/Views/Settings.axaml.cs").Contains("WindowRoles.Register(this, WindowRole.Settings)"),
    "Settings.axaml.cs registers the shared settings window as Settings");
Check(Read("src/DeskCanvas/App.axaml.cs").Contains("WindowRoles.Register(anchor, WindowRole.Anchor)"),
    "App.axaml.cs registers the anchor window as Anchor");

// Dialog roles, per file — one check each so a dropped registration names its window.
string[] dialogFiles =
[
    "src/DeskCanvas/Views/ConfirmDialog.axaml.cs",
    "src/DeskCanvas/Views/InputDialog.axaml.cs",
    "src/DeskCanvas/Views/ScaleDialog.axaml.cs",
    "src/DeskCanvas/Views/ProcessPickerDialog.axaml.cs",
    "src/DeskCanvas/Views/WallpaperAlignDialog.axaml.cs",
    "src/DeskCanvas/Views/UpdatePopup.axaml.cs",
    "src/DeskCanvas/Views/EditWidget.axaml.cs",
    "src/DeskCanvas/Views/GridEditor.axaml.cs",
    "src/Widgets/Shortcuts/Views/ShortcutsEditorWindow.axaml.cs",
    "src/Widgets/Timetable/Views/GuideWindow.axaml.cs",
    "src/Widgets/Timetable/Views/ImportReviewWindow.axaml.cs",
];
foreach (var file in dialogFiles)
    Check(Read(file).Contains("WindowRoles.Register(this, WindowRole.Dialog)"),
        $"{file} registers its window as Dialog");

// -------------------------------------------------------------------------------------------
// 4. Widget platform boundary: [DllImport] under src/Widgets stays on the pinned whitelist.
//    Key = repo-relative file, value = exact occurrence count. A new P/Invoke site — new file
//    or an added declaration — fails the check; this is the "widgets do not re-grow Win32
//    interop outside their vetted services" guard for the P6 capability boundary.
// -------------------------------------------------------------------------------------------
Console.WriteLine("--- widget interop boundary scan ---");

var widgetImportWhitelist = new Dictionary<string, int>
{
    ["src/Widgets/Batteries/Services/PowerService.cs"] = 5,
    ["src/Widgets/ControlCenter/Services/SystemControlService.cs"] = 13,
    ["src/Widgets/Folders/Services/WidgetOleDropTarget.cs"] = 12,
    ["src/Widgets/Folders/Services/FolderIconService.cs"] = 12,
    ["src/Widgets/Folders/Views/BigFolder.axaml.cs"] = 1,
    ["src/Widgets/Folders/Views/Folder.axaml.cs"] = 1,
    ["src/Widgets/Folders/Views/SingleFile.axaml.cs"] = 1,
    ["src/Widgets/Picture/Views/PictureView.axaml.cs"] = 1,
    ["src/Widgets/Shortcuts/Services/Executors/SystemExecutors.cs"] = 4,
    ["src/Widgets/Shortcuts/Services/KeyboardInput.cs"] = 1,
    ["src/Widgets/Shortcuts/Services/ShortcutHotkeyService.cs"] = 8,
    ["src/Widgets/Tools/Services/ClipboardNative.cs"] = 12,
};

var widgetsRoot = Path.Combine(repoRoot, "src", "Widgets");
var actualImports = Directory.EnumerateFiles(widgetsRoot, "*.cs", SearchOption.AllDirectories)
    .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                   && !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
    .Select(path => (Relative: Path.GetRelativePath(repoRoot, path).Replace('\\', '/'),
            Count: CountOccurrences(File.ReadAllText(path), "[DllImport")))
    .Where(entry => entry.Count > 0)
    .ToDictionary(entry => entry.Relative, entry => entry.Count);

Check(actualImports.Keys.All(file => widgetImportWhitelist.ContainsKey(file)),
    $"no [DllImport] appears outside the pinned widget whitelist (found in: {string.Join(", ", actualImports.Keys.Except(widgetImportWhitelist.Keys).DefaultIfEmpty("—"))})");

var drift = widgetImportWhitelist
    .Where(kvp => !actualImports.TryGetValue(kvp.Key, out var count) || count != kvp.Value)
    .Select(kvp => $"{kvp.Key} (expected {kvp.Value}, found {(actualImports.TryGetValue(kvp.Key, out var c) ? c : 0)})")
    .ToList();
Check(drift.Count == 0,
    $"every whitelisted widget file keeps its exact [DllImport] count{(drift.Count > 0 ? " — drift: " + string.Join("; ", drift) : "")}");

static int CountOccurrences(string text, string needle)
{
    var count = 0;
    var index = 0;
    while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
    {
        count++;
        index += needle.Length;
    }
    return count;
}

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

return Finish(failures, checks);

/// <summary>Minimal application shell for the headless probe (styles are irrelevant — the
/// windows here are never shown, only used as registry keys).</summary>
file sealed class ProbeApp : Application
{
    public override void Initialize() =>
        Styles.Add(new FluentTheme());
}
