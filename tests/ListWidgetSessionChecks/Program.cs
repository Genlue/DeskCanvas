// List widget session checks (P8 batch B, slice B2 — the list/reminder family:
// Countdown, Habits, Reminders, Timetable).
//
// Run: dotnet run --project tests/ListWidgetSessionChecks
//
// Direct construction only (no host pipeline): pins the session gate on the two timed
// members of the family — the Countdown view model (its private DispatcherTimer collapsed
// into the shared TimerService tiers, reconciled from isRunning AND RenderActive) and the
// Timetable card view (its minute-timer subscription reconciled from attached AND
// RenderActive) — plus the legacy session-less path, the view-level IWidgetActivityConsumer
// forwarding, the per-screen theme pins, and the four popup migrations onto PanelService
// (source scans: identity keys, PerOwner scope, owner-instance forwarding).
// Headless rig copied from tests/SessionHookChecks: UseHeadlessDrawing=false gives real
// Win32 window handles without running the host pipeline.
using System.Reflection;
using TimetableView = Timetable.Views.Timetable;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using Countdown.Models;
using Countdown.Services;
using Countdown.ViewModels;
using Countdown.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using Timetable.Models;
using Timetable.Views;

// Sandbox every component store (Countdown/Timetable JSON) into a throwaway folder —
// same isolation as tests/CountdownChecks.
Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
    Path.Combine(Path.GetTempPath(), "DeskCanvas-ListWidget-Checks-" + Guid.NewGuid().ToString("N")));

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

CountdownViewModel VmOf(CountdownView view) =>
    (CountdownViewModel)typeof(CountdownView).GetField("viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view)!;

void InvokeLifecycle(TimetableView view, string methodName)
{
    // The view declares OnLoaded/OnUnloaded twice across its partial classes — take the
    // one TimetableView itself declares instead of GetMethod's ambiguous match.
    var method = typeof(TimetableView)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(m => m.Name == methodName && m.DeclaringType == typeof(TimetableView));
    method.Invoke(view, [null, null]);
}

CountdownItem EventWithin(TimeSpan offset, string id) => new(
    Id: id,
    Title: "probe event",
    TargetDate: DateTime.Now + offset,
    StartDate: DateTime.Now);

// =========================================================================================
// 1. Countdown view model — the legacy (session-less) path
// =========================================================================================
Console.WriteLine("--- Countdown view model: legacy path ---");
var legacyVm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.DaysOnly });
legacyVm.Start();
Check(legacyVm.TickSubscriptionCountForChecks == 1, "1.1 a session-less countdown starts with exactly one tick subscription");
Check(!legacyVm.IsHighPrecisionTickForChecks, "1.2 the DaysOnly granularity rides the 1-minute tier");
legacyVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(legacyVm.TickSubscriptionCountForChecks == 1, "1.3 a session-less countdown ignores activity pushes (old always-on path)");
legacyVm.Stop();
legacyVm.UpdateSettings(new CountdownModel { Granularity = CountdownGranularity.AlwaysDetailed });
legacyVm.Start();
Check(legacyVm.IsHighPrecisionTickForChecks, "1.4 AlwaysDetailed switches the subscription to the 1-second tier");
legacyVm.Dispose();
Check(legacyVm.TickSubscriptionCountForChecks == 0, "1.5 Dispose unsubscribes the shared timer");
Check(legacyVm.IsDisposed, "1.6 Dispose marks the view model disposed");

// =========================================================================================
// 2. Countdown view model — the session-gated path
// =========================================================================================
Console.WriteLine("--- Countdown view model: session gate ---");
var idA = WidgetInstanceId.New();
var sA = WidgetSessionRegistry.GetOrCreate(idA);
var gatedVm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.DaysOnly }, sA);
gatedVm.Start();
Check(gatedVm.TickSubscriptionCountForChecks == 1, "2.1 an injected, visible session starts subscribed");
Check(gatedVm.SessionForChecks == sA, "2.2 the session seam carries the injected session");
gatedVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(gatedVm.TickSubscriptionCountForChecks == 0, "2.3 RenderActive=false unsubscribes the shared timer");
gatedVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(gatedVm.TickSubscriptionCountForChecks == 1, "2.4 RenderActive=true re-subscribes");
gatedVm.OnSessionActivity(ActivitySnapshot.Preview);
Check(gatedVm.TickSubscriptionCountForChecks == 1, "2.5 a preview snapshot keeps the subscription alive (RenderActive)");

// A session already covered at construction time starts unsubscribed.
var idB = WidgetInstanceId.New();
var sB = WidgetSessionRegistry.GetOrCreate(idB);
await sB.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredVm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.DaysOnly }, sB);
coveredVm.Start();
Check(coveredVm.TickSubscriptionCountForChecks == 0, "2.6 a covered session starts unsubscribed");
coveredVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredVm.TickSubscriptionCountForChecks == 1, "2.7 the covered countdown re-subscribes on the resume push");

// Overlapping transitions reconcile without corrupting the state machine.
var idC = WidgetInstanceId.New();
var sC = WidgetSessionRegistry.GetOrCreate(idC);
var mixedVm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.DaysOnly }, sC);
mixedVm.Start();
mixedVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
mixedVm.Stop();
Check(mixedVm.TickSubscriptionCountForChecks == 0, "2.8 covered + stopped stays unsubscribed");
mixedVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(mixedVm.TickSubscriptionCountForChecks == 0, "2.9 a resume push does not resurrect a stopped view model");
mixedVm.Start();
Check(mixedVm.TickSubscriptionCountForChecks == 1, "2.10 Start reconciles the subscription after both transitions");

// The 24-hour window drives the tier through a real store change (the ModelChanged push).
CountdownStore.Save([]);
var idD = WidgetInstanceId.New();
var sD = WidgetSessionRegistry.GetOrCreate(idD);
var autoVm = new CountdownViewModel(new CountdownModel { Granularity = CountdownGranularity.Auto24Hours }, sD);
autoVm.Start();
Check(!autoVm.IsHighPrecisionTickForChecks, "2.11 Auto24Hours with no near event rides the 1-minute tier");
CountdownStore.AddOrUpdate(EventWithin(TimeSpan.FromHours(2), "probe-near"), null);
Flush();
Check(autoVm.IsHighPrecisionTickForChecks, "2.12 an event entering the 24h window switches the subscription to the 1-second tier");
autoVm.UpdateSettings(new CountdownModel { Granularity = CountdownGranularity.DaysOnly });
Check(!autoVm.IsHighPrecisionTickForChecks, "2.13 a DaysOnly settings update drops back to the 1-minute tier");
CountdownStore.Delete("probe-near", null);
Flush();

// =========================================================================================
// 3. Timetable card view — the legacy (session-less) path
// =========================================================================================
Console.WriteLine("--- Timetable view: legacy path ---");
var legacyTimetable = new TimetableView();
Check(legacyTimetable.SessionForChecks == null, "3.1 the parameterless constructor runs the legacy session-less path");
Check(legacyTimetable.TickSubscriptionCountForChecks == 0, "3.2 an unloaded legacy card holds no timer subscription");
InvokeLifecycle(legacyTimetable, "OnLoaded");
Check(legacyTimetable.TickSubscriptionCountForChecks == 1, "3.3 the loaded legacy card subscribes to the shared minute timer");
InvokeLifecycle(legacyTimetable, "OnLoaded");
Check(legacyTimetable.TickSubscriptionCountForChecks == 1, "3.4 a repeated Loaded cannot double-subscribe (reconciliation)");
legacyTimetable.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(legacyTimetable.TickSubscriptionCountForChecks == 1, "3.5 a session-less card ignores activity pushes (old always-on path)");
InvokeLifecycle(legacyTimetable, "OnUnloaded");
Check(legacyTimetable.TickSubscriptionCountForChecks == 0, "3.6 Unloaded unsubscribes the shared timer");

// =========================================================================================
// 4. Timetable card view — the session-gated path
// =========================================================================================
Console.WriteLine("--- Timetable view: session gate ---");
var idE = WidgetInstanceId.New();
var sE = WidgetSessionRegistry.GetOrCreate(idE);
await sE.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredTimetable = new TimetableView(null, sE);
Check(coveredTimetable.SessionForChecks == sE, "4.1 the session seam carries the injected session");
Check(!coveredTimetable.RenderActiveForChecks, "4.2 the covered session's render flag is read at construction");
InvokeLifecycle(coveredTimetable, "OnLoaded");
Check(coveredTimetable.TickSubscriptionCountForChecks == 0, "4.3 a covered session stays unsubscribed through Loaded");
((IWidgetActivityConsumer)coveredTimetable).OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredTimetable.TickSubscriptionCountForChecks == 1, "4.4 the IWidgetActivityConsumer push re-subscribes after resume");
((IWidgetActivityConsumer)coveredTimetable).OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(coveredTimetable.TickSubscriptionCountForChecks == 0, "4.5 the consumer push gates the timer back off");
InvokeLifecycle(coveredTimetable, "OnUnloaded");
((IWidgetActivityConsumer)coveredTimetable).OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredTimetable.TickSubscriptionCountForChecks == 0, "4.6 a resume push cannot resurrect a detached card (attached AND renderActive)");

var idF = WidgetInstanceId.New();
var sF = WidgetSessionRegistry.GetOrCreate(idF);
var liveTimetable = new TimetableView(null, sF);
InvokeLifecycle(liveTimetable, "OnLoaded");
Check(liveTimetable.TickSubscriptionCountForChecks == 1, "4.7 a visible session subscribes through Loaded");
InvokeLifecycle(liveTimetable, "OnUnloaded");
liveTimetable.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
InvokeLifecycle(liveTimetable, "OnLoaded");
Check(liveTimetable.TickSubscriptionCountForChecks == 0, "4.8 a session covered while detached starts unsubscribed on re-attach");

// =========================================================================================
// 5. CountdownView: the view-level consumer forwarding (host contract)
// =========================================================================================
Console.WriteLine("--- CountdownView consumer forwarding ---");
var idG = WidgetInstanceId.New();
var sG = WidgetSessionRegistry.GetOrCreate(idG);
var sessionView = new CountdownView(new CountdownModel(), null, sG);
var sessionViewVm = VmOf(sessionView);
Check(sessionViewVm.SessionForChecks == sG, "5.1 the view forwards the injected session into its view model");
sessionViewVm.Start();
Check(sessionViewVm.TickSubscriptionCountForChecks == 1, "5.2 the view model starts subscribed before the push");
((IWidgetActivityConsumer)sessionView).OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(sessionViewVm.TickSubscriptionCountForChecks == 0, "5.3 the view's IWidgetActivityConsumer push gates the view model off");
var bareView = new CountdownView();
Check(VmOf(bareView).SessionForChecks == null, "5.4 the legacy CountdownView constructor stays session-less");

// =========================================================================================
// 6. Theme: per-screen N/A pins (source scans)
// =========================================================================================
Console.WriteLine("--- Theme pins ---");
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
    static IEnumerable<string> WidgetSources(string repoRoot, string widget)
    {
        var dir = Path.Combine(repoRoot, "src", "Widgets", widget);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;
            yield return file;
        }
    }

    var globalThemeReads = 0;
    foreach (var widget in new[] { "Countdown", "Habits", "Reminders", "Timetable" })
        foreach (var file in WidgetSources(repoRoot, widget))
            if (File.ReadAllText(file).Contains("Application.Current.ActualThemeVariant"))
                globalThemeReads++;
    Check(globalThemeReads == 0, "6.1 no list-family widget reads the global Application.Current.ActualThemeVariant (per-screen N/A holds)");

    var timetableSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Timetable", "Views", "Timetable.axaml.cs"));
    Check(timetableSource.Contains("ThemeScopeResolver.ResolveColor(this, \"SystemAccentColor\", ActualThemeVariant"),
        "6.2 Timetable resolves its accent against the visual's own ActualThemeVariant (per-screen)");
    var panelSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Timetable", "Views", "TimetablePanelWindow.axaml.cs"));
    Check(panelSource.Contains("ThemeScopeResolver.ResolveBrush(this, \"SystemAccentColor\", ActualThemeVariant"),
        "6.3 the timetable panel resolves its accent against the visual's own ActualThemeVariant (per-screen)");

    // =====================================================================================
    // 7. Popup migrations onto PanelService (source scans)
    // =====================================================================================
    Console.WriteLine("--- Popup migrations onto PanelService ---");
    var popupFiles = new (string Widget, string File, string Key)[]
    {
        ("Countdown", "CountdownPopupWindow.axaml.cs", "countdown.popup"),
        ("Habits", "HabitsPopupWindow.axaml.cs", "habits.popup"),
        ("Reminders", "RemindersPopupWindow.axaml.cs", "reminders.popup"),
        ("Timetable", "TimetablePanelWindow.axaml.cs", "timetable.panel"),
    };

    var index = 1;
    var seenKeys = new HashSet<string>();
    foreach (var (widget, fileName, key) in popupFiles)
    {
        var source = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", widget, "Views", fileName));
        Check(source.Contains("PanelService.Open(new PanelRequest("), $"7.{index} {fileName} opens through PanelService.Open");
        index++;
        Check(source.Contains($"\"{key}\""), $"7.{index} {fileName} declares the identity key \"{key}\"");
        index++;
        Check(source.Contains("PanelScope.PerOwner"), $"7.{index} {fileName} scopes its identity PerOwner");
        index++;
        Check(source.Contains("widget.InstanceId"), $"7.{index} {fileName} forwards the owner widget's instance id");
        index++;
        Check(!source.Contains("PanelCoolingDown<") && !source.Contains("TryCloseActivePanel<"),
            $"7.{index} {fileName} no longer drives the legacy per-type toggle helpers");
        index++;
        Check(!source.Contains(".ShowAsSecondaryPanel("), $"7.{index} {fileName} no longer shows itself (the service shows the panel)");
        index++;
        seenKeys.Add(key);
    }
    Check(seenKeys.Count == 4, "7.25 the four identity keys are distinct");
    Check(seenKeys.SetEquals(new HashSet<string> { "countdown.popup", "habits.popup", "reminders.popup", "timetable.panel" }),
        "7.26 the identity keys follow the <widget>.<purpose> contract");
}

// =========================================================================================
Flush();
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"List widget session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

sealed class ProbeApp : DeskCanvas.App
{
    public override void OnFrameworkInitializationCompleted() { }
}
