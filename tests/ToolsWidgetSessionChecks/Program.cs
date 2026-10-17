// Tools/Stack/Search/ControlCenter session checks (P8 batch F).
//
// Run: dotnet run --project tests/ToolsWidgetSessionChecks
//
// Pins the batch-F slices, direct-constructed on the headless platform (no host pipeline):
//   - the clipboard monitor's polling gate (the 400 ms singleton timer reconciles against the
//     attached views: gated views count while they render, session-less views stay always-on),
//   - the clipboard view's session relay (Loaded/Unloaded attach/detach + activity pushes),
//   - the translator view's debounce gate (a covered card stops the pending auto-translate,
//     a resume push never resurrects it),
//   - the control-center view's 1.5 s system-state poll gate (loaded + not suspended +
//     render-active), including the Suspend/Resume interplay,
//   - the three Tools popups' PanelService migration (identity keys, PerOwner scope, the
//     legacy cooldown/toggle helpers gone), guarded by source scans,
//   - the N/A decisions (Search and Stack have no session-mappable cadence; no component
//     reads a global theme variant) as regression-guarding source scans.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ControlCenter.Models;
using ControlCenter.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using Tools.Models;
using Tools.Services;
using Tools.Views;

// Fresh per-run sandbox (the IdentityStorageChecks pattern): the structural tests-harness
// sandbox persists across runs, and the clipboard monitor's store would leak state into
// the covered-at-construction and debounce assertions. Must run before Const initializes.
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
    Path.Combine(Path.GetTempPath(), "DeskCanvas-FixtureSandboxes",
        "ToolsWidgetSessionChecks-" + Guid.NewGuid().ToString("N")));

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

WidgetSessionRegistry.ResetForChecks();

// =========================================================================================
// 1. ClipboardMonitorService polling gate (direct construction; the capture logic itself is
//    untouched — only the run state of the shared 400 ms timer is reconciled)
// =========================================================================================
Console.WriteLine("--- Clipboard monitor polling gate ---");
var monitor = new ClipboardMonitorService();
Check(!monitor.IsPollingForChecks, "1.1 a fresh monitor instance does not poll (the constructor no longer self-starts the timer)");
var viewA = new object();
monitor.AttachView(viewA, sessionGated: false, initialRenderActive: false);
Check(monitor.IsPollingForChecks, "1.2 an attached session-less view keeps the historic always-on polling");
monitor.DetachView(viewA);
Check(!monitor.IsPollingForChecks, "1.3 detaching the always-on view stops the poll");
var viewB = new object();
monitor.AttachView(viewB, sessionGated: true, initialRenderActive: true);
Check(monitor.IsPollingForChecks, "1.4 an attached gated view that renders keeps the poll running");
monitor.OnViewRenderActivity(viewB, false);
Check(!monitor.IsPollingForChecks, "1.5 RenderActive=false on the only gated view stops the poll");
monitor.OnViewRenderActivity(viewB, true);
Check(monitor.IsPollingForChecks, "1.6 RenderActive=true re-starts the poll");
monitor.AttachView(viewA, sessionGated: false, initialRenderActive: false);
monitor.OnViewRenderActivity(viewB, false);
Check(monitor.IsPollingForChecks, "1.7 a covered gated view does not stop the poll while an always-on view is attached");
monitor.DetachView(viewA);
Check(!monitor.IsPollingForChecks, "1.8 the poll stops once the last wanting view is gone");
var viewC = new object();
monitor.AttachView(viewC, sessionGated: true, initialRenderActive: false);
Check(!monitor.IsPollingForChecks, "1.9 a gated view covered at attach time starts without polling");
monitor.DetachView(viewB);
monitor.DetachView(viewC);
monitor.DetachView(viewC);
Check(!monitor.IsPollingForChecks, "1.10 double-detach is idempotent and leaves the poll stopped");

// =========================================================================================
// 2. ClipboardView session relay (hosted, the singleton monitor the views really use)
// =========================================================================================
Console.WriteLine("--- Clipboard view session relay ---");
var sharedMonitor = ClipboardMonitorService.Instance;
var legacyHost = new Window { Width = 240, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var legacyView = new ClipboardView();
legacyHost.Content = legacyView;
legacyHost.Show();
Flush();
Check(legacyView.SessionForChecks == null, "2.1 a preview-style ClipboardView carries no session (legacy path)");
Check(sharedMonitor.IsPollingForChecks, "2.2 the loaded legacy view keeps the monitor polling");
legacyView.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(sharedMonitor.IsPollingForChecks, "2.3 a session-less view ignores the covered push (still polling)");
legacyHost.Close();
Flush();
Check(!sharedMonitor.IsPollingForChecks, "2.4 the unloaded legacy view leaves the gate (poll stops)");

var idA = WidgetInstanceId.New();
var sessionA = WidgetSessionRegistry.GetOrCreate(idA);
var gatedHost = new Window { Width = 240, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var gatedView = new ClipboardView(new ClipboardModel(), sessionA);
gatedHost.Content = gatedView;
gatedHost.Show();
Flush();
Check(gatedView.SessionForChecks == sessionA, "2.5 the injected view carries its session");
Check(sharedMonitor.IsPollingForChecks, "2.6 a visible gated view keeps the monitor polling");
gatedView.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!sharedMonitor.IsPollingForChecks, "2.7 the covered push stops the poll for the only gated view");
gatedView.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(sharedMonitor.IsPollingForChecks, "2.8 the resume push re-starts the poll");

// The visible gated view from 2.5-2.8 must leave before the covered-at-construction case:
// 2.9 pins "the ONLY attached view is covered" — with the visible view still attached the
// monitor legitimately keeps polling.
gatedHost.Close();
Flush();

var idB = WidgetInstanceId.New();
var sessionB = WidgetSessionRegistry.GetOrCreate(idB);
await sessionB.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredHost = new Window { Width = 240, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var coveredView = new ClipboardView(new ClipboardModel(), sessionB);
coveredHost.Content = coveredView;
coveredHost.Show();
Flush();
Check(!sharedMonitor.IsPollingForChecks, "2.9 a gated view covered at construction loads without polling");
coveredView.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(sharedMonitor.IsPollingForChecks, "2.10 the covered view polls again after the resume push");
coveredHost.Close();
Flush();
Check(!sharedMonitor.IsPollingForChecks, "2.11 closing every hosted view stops the poll (no gate set leaks)");

// =========================================================================================
// 3. TranslatorView debounce gate (direct construction; no network call is ever reached —
//    the debounce is stopped before its 600 ms tick)
// =========================================================================================
Console.WriteLine("--- Translator view debounce gate ---");
TextBox InputBoxOf(TranslatorView view) =>
    (TextBox)typeof(TranslatorView).GetField("InputTextBox", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

var legacyTranslator = new TranslatorView();
Check(legacyTranslator.SessionForChecks == null && legacyTranslator.RenderActiveForChecks, "3.1 a session-less TranslatorView starts legacy always-on");
// The text-changed XAML hookup doesn't fire for programmatic sets in this headless rig,
// so the fixture invokes OnInputTextChanged directly — it IS the typing logic the session
// gate wraps (the AXAML wiring itself is untouched pre-existing plumbing).
void TypeInto(TranslatorView view, string text)
{
    var box = InputBoxOf(view);
    box.Text = text;
    typeof(TranslatorView)
        .GetMethod("OnInputTextChanged", BindingFlags.NonPublic | BindingFlags.Instance)!
        .Invoke(view, [null, null!]);
}

TypeInto(legacyTranslator, "hello");
Check(legacyTranslator.IsDebouncePendingForChecks, "3.2 typing arms the auto-translate debounce");
legacyTranslator.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(legacyTranslator.IsDebouncePendingForChecks, "3.3 a session-less view ignores the covered push (debounce survives)");

var idC = WidgetInstanceId.New();
var sessionC = WidgetSessionRegistry.GetOrCreate(idC);
var gatedTranslator = new TranslatorView(new TranslatorModel(), sessionC);
Check(gatedTranslator.RenderActiveForChecks, "3.4 an injected visible session starts render-active");
TypeInto(gatedTranslator, "hello");
Check(gatedTranslator.IsDebouncePendingForChecks, "3.5 typing arms the debounce on the gated view too");
gatedTranslator.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!gatedTranslator.IsDebouncePendingForChecks, "3.6 the covered push stops the pending debounce (no request on an unseen card)");
gatedTranslator.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(!gatedTranslator.IsDebouncePendingForChecks, "3.7 the resume push never resurrects the debounce");

var idD = WidgetInstanceId.New();
var sessionD = WidgetSessionRegistry.GetOrCreate(idD);
await sessionD.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredTranslator = new TranslatorView(new TranslatorModel(), sessionD);
Check(!coveredTranslator.RenderActiveForChecks, "3.8 a covered session starts the view render-inactive");
InputBoxOf(coveredTranslator).Text = "hello";
coveredTranslator.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!coveredTranslator.IsDebouncePendingForChecks, "3.9 the covered push keeps the covered view's debounce stopped");

// =========================================================================================
// 4. ControlCenterView poll gate (hosted; the 1.5 s system-state poll)
// =========================================================================================
Console.WriteLine("--- Control center poll gate ---");
var ccLegacyHost = new Window { Width = 300, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var ccLegacy = new ControlCenterView(new ControlCenterModel());
ccLegacyHost.Content = ccLegacy;
ccLegacyHost.Show();
Flush();
Check(ccLegacy.SessionForChecks == null, "4.1 a session-less ControlCenterView carries no session (legacy path)");
Check(ccLegacy.IsPollingForChecks, "4.2 the loaded legacy view polls");
ccLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(ccLegacy.IsPollingForChecks, "4.3 a session-less view ignores the covered push (still polling)");

var idE = WidgetInstanceId.New();
var sessionE = WidgetSessionRegistry.GetOrCreate(idE);
var ccGatedHost = new Window { Width = 300, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var ccGated = new ControlCenterView(new ControlCenterModel(), sessionE);
ccGatedHost.Content = ccGated;
ccGatedHost.Show();
Flush();
Check(ccLegacy.IsPollingForChecks, "4.4 the legacy view still polls alongside the gated one");
Check(ccGated.IsPollingForChecks, "4.5 a visible gated view polls");
ccGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!ccGated.IsPollingForChecks, "4.6 the covered push stops the gated view's poll");
ccGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(ccGated.IsPollingForChecks, "4.7 the resume push re-starts the poll");
ccGated.Suspend();
Check(!ccGated.IsPollingForChecks, "4.8 fullscreen suspension stops the poll (Suspend wins)");
ccGated.Resume();
Check(ccGated.IsPollingForChecks, "4.9 Resume restores the poll while the session renders");
ccGated.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
ccGated.Resume();
Check(!ccGated.IsPollingForChecks, "4.10 a resume while the session is covered stays stopped");

var idF = WidgetInstanceId.New();
var sessionF = WidgetSessionRegistry.GetOrCreate(idF);
await sessionF.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var ccCoveredHost = new Window { Width = 300, Height = 200, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var ccCovered = new ControlCenterView(new ControlCenterModel(), sessionF);
ccCoveredHost.Content = ccCovered;
ccCoveredHost.Show();
Flush();
Check(!ccCovered.IsPollingForChecks, "4.11 a gated view covered at construction loads without polling");
ccCovered.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(ccCovered.IsPollingForChecks, "4.12 the covered view polls again after the resume push");
ccLegacyHost.Close();
Flush();
ccGatedHost.Close();
Flush();
ccCoveredHost.Close();
Flush();

// =========================================================================================
// 5. Popup migration: the three Tools popups route through PanelService (source scan)
// =========================================================================================
Console.WriteLine("--- Popup migration source scan ---");
string? FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "DeskCanvas", "Views", "Widget.axaml.cs")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
var repoRoot = FindRepoRoot();
if (repoRoot == null)
{
    Console.WriteLine("[NOTE] repository root not found — the source scans were skipped");
}
else
{
    string Source(params string[] parts) => File.ReadAllText(Path.Combine([repoRoot, .. parts]));

    var calculatorPopup = Source("src", "Widgets", "Tools", "Views", "CalculatorPopupWindow.axaml.cs");
    Check(calculatorPopup.Contains("PanelService.Open(new PanelRequest(")
          && calculatorPopup.Contains("\"tools.calculator\"")
          && calculatorPopup.Contains("PanelScope.PerOwner"),
        "5.1 CalculatorPopupWindow opens through PanelService under the tools.calculator PerOwner identity");
    Check(!calculatorPopup.Contains("PanelCoolingDown<") && !calculatorPopup.Contains("TryCloseActivePanel<"),
        "5.2 CalculatorPopupWindow no longer drives the legacy cooldown/toggle helpers itself");

    var clipboardPopup = Source("src", "Widgets", "Tools", "Views", "ClipboardPopupWindow.axaml.cs");
    Check(clipboardPopup.Contains("PanelService.Open(new PanelRequest(")
          && clipboardPopup.Contains("\"tools.clipboard\"")
          && clipboardPopup.Contains("PanelScope.PerOwner"),
        "5.3 ClipboardPopupWindow opens through PanelService under the tools.clipboard PerOwner identity");
    Check(!clipboardPopup.Contains("PanelCoolingDown<") && !clipboardPopup.Contains("TryCloseActivePanel<"),
        "5.4 ClipboardPopupWindow no longer drives the legacy cooldown/toggle helpers itself");

    var translatorPopup = Source("src", "Widgets", "Tools", "Views", "TranslatorPopupWindow.axaml.cs");
    Check(translatorPopup.Contains("PanelService.Open(new PanelRequest(")
          && translatorPopup.Contains("\"tools.translator\"")
          && translatorPopup.Contains("PanelScope.PerOwner"),
        "5.5 TranslatorPopupWindow opens through PanelService under the tools.translator PerOwner identity");
    Check(!translatorPopup.Contains("PanelCoolingDown<") && !translatorPopup.Contains("TryCloseActivePanel<"),
        "5.6 TranslatorPopupWindow no longer drives the legacy cooldown/toggle helpers itself");

    Check(calculatorPopup.Contains("widget.InstanceId : default")
          && clipboardPopup.Contains("widget.InstanceId : default")
          && translatorPopup.Contains("widget.InstanceId : default"),
        "5.7 all three ShowPopup entries resolve the owner widget's instance id (PerOwner scope)");

    // Cooldown equivalence: the PanelRequest default equals the legacy reopen guard.
    var cooldownDefault = typeof(PanelRequest).GetConstructors()
        .Select(c => c.GetParameters().FirstOrDefault(p => p.Name == "CooldownMs"))
        .FirstOrDefault(p => p != null)?
        .DefaultValue;
    var legacyCooldown = typeof(DeskCanvas.Views.SecondaryPanelWindow)
        .GetField("ReopenCooldownMs", BindingFlags.NonPublic | BindingFlags.Static)?
        .GetValue(null);
    Check(Equals(cooldownDefault, 250) && Equals(legacyCooldown, 250),
        "5.8 the PanelService default cooldown matches the legacy 250 ms reopen guard");
}

// =========================================================================================
// 6. N/A decisions as regression guards (source scans)
// =========================================================================================
Console.WriteLine("--- N/A decisions ---");
if (repoRoot != null)
{
    string Source(params string[] parts) => File.ReadAllText(Path.Combine([repoRoot, .. parts]));

    var searchVm = Source("src", "Widgets", "Search", "ViewModels", "SearchViewModel.cs");
    var searchView = Source("src", "Widgets", "Search", "Views", "SearchView.axaml.cs");
    Check(!searchVm.Contains("DispatcherTimer") && !searchVm.Contains("TimerService")
          && !searchView.Contains("DispatcherTimer") && !searchView.Contains("TimerService"),
        "6.1 Search has no timed cadence at all — session gating is N/A by construction");

    var stackView = Source("src", "Widgets", "Stack", "Views", "WidgetStackView.axaml.cs");
    Check(!stackView.Contains("TimerService."),
        "6.2 Stack subscribes no shared timer — its debounce/transition timers are interaction-driven (N/A)");
    Check(stackView.Contains("IWidgetSuspendable") && stackView.Contains("ForwardSuspend"),
        "6.3 Stack already forwards fullscreen suspension to every child card (the hidden-work contract)");

    // Theme per-screen: no component may read the global application variant.
    string[] scanned =
    [
        Path.Combine(repoRoot, "src", "Widgets", "Tools"),
        Path.Combine(repoRoot, "src", "Widgets", "Search"),
        Path.Combine(repoRoot, "src", "Widgets", "Stack"),
        Path.Combine(repoRoot, "src", "Widgets", "ControlCenter"),
    ];
    var globalReads = new List<string>();
    foreach (var dir in scanned)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            if (File.ReadAllText(file).Contains("Application.Current"))
            {
                globalReads.Add(Path.GetFileName(file));
            }
        }
    }
    Check(globalReads.Count == 0,
        globalReads.Count == 0
            ? "6.4 no component reads the global Application.Current theme (per-window variants only) — per-screen theme N/A holds"
            : $"6.4 global Application.Current reads crept back in: {string.Join(", ", globalReads)}");
    Check(stackView.Contains("FocusThemeCoordinator.ResolveVisualTheme"),
        "6.5 Stack's card backgrounds derive darkness from the visual's own effective theme (per-screen)");
}

// =========================================================================================
// 7. Session wiring guards (the P8 batch F injection points)
// =========================================================================================
Console.WriteLine("--- Session wiring guards ---");
if (repoRoot != null)
{
    string Source(params string[] parts) => File.ReadAllText(Path.Combine([repoRoot, .. parts]));

    var clipboardViewSource = Source("src", "Widgets", "Tools", "Views", "ClipboardView.axaml.cs");
    Check(clipboardViewSource.Contains("IWidgetActivityConsumer") && clipboardViewSource.Contains("IWidgetSession? session = null"),
        "7.1 ClipboardView keeps the session constructor parameter and the activity-consumer relay");
    var monitorSource = Source("src", "Widgets", "Tools", "Services", "ClipboardMonitorService.cs");
    Check(monitorSource.Contains("ReconcilePolling") && monitorSource.Contains("private void ReconcilePolling()"),
        "7.2 the clipboard monitor reconciles its poll instead of self-starting it");
    Check(monitorSource.Contains("legacyViews.Count > 0 || renderActiveViews.Count > 0"),
        "7.3 the poll's wanted-rule keeps the session-less always-on contract");
    var translatorViewSource = Source("src", "Widgets", "Tools", "Views", "TranslatorView.axaml.cs");
    Check(translatorViewSource.Contains("IWidgetActivityConsumer") && translatorViewSource.Contains("IWidgetSession? session = null"),
        "7.4 TranslatorView keeps the session constructor parameter and the debounce gate");
    var controlCenterSource = Source("src", "Widgets", "ControlCenter", "Views", "ControlCenterView.axaml.cs");
    Check(controlCenterSource.Contains("IWidgetActivityConsumer") && controlCenterSource.Contains("IWidgetSession? session = null")
          && controlCenterSource.Contains("IsLoaded && !isSuspended && renderActive"),
        "7.5 ControlCenterView keeps the session constructor parameter and the three-input poll gate");
}

// =========================================================================================
WidgetSessionRegistry.ResetForChecks();
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Tools widget session checks passed: {checks}/{checks}.");
return 0;

sealed class ProbeApp : Application
{
    public override void Initialize() { }
}
