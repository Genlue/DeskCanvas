// Calendar session checks (P8 batch A, slice A2 — the calendar lifecycle migration).
//
// Run: dotnet run --project tests/CalendarSessionChecks
//
// Pins the A2 slice: the Month/Date calendar views accept an optional IWidgetSession (injected
// by WidgetFactory's constructor probe) and forward the host's activity push to their view
// models, whose five-minute tick subscription is reconciled against Start/Stop and the
// session's RenderActive (the Digital clock pilot pattern from A1). The host pipeline itself
// (WidgetFactory → Widget window → registry → content) is SessionHookChecks' job — here the
// views are constructed directly on the headless platform, following the rig of
// tests/SessionHookChecks (UseHeadlessDrawing=false + UseSkia).
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Calendar.Models;
using Calendar.ViewModels;
using Calendar.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

// UseHeadlessDrawing=false + UseSkia: the same platform shape SessionHookChecks pins, so the
// widgets' code paths (themed brushes, visual tree lookups) behave like on the real desktop.
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
MonthCalendarViewModel VmOf(Month month) =>
    (MonthCalendarViewModel)typeof(Month).GetField("viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(month)!;
DateCalendarViewModel DateVmOf(DateOne date) =>
    (DateCalendarViewModel)typeof(DateOne).GetField("viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(date)!;

// =========================================================================================
// 1. Legacy path: no session — the historic always-on behaviour is untouched
// =========================================================================================
Console.WriteLine("--- Legacy (session-less) construction ---");
var legacyMonth = new Month(new MonthCalendarModel(DayOfWeek.Monday));
Check(VmOf(legacyMonth).TickSubscriptionCountForChecks == 1, "1.1 a session-less Month keeps the legacy always-on subscription");
Check(VmOf(legacyMonth).SessionForChecks == null, "1.2 a session-less Month carries no session");
legacyMonth.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(VmOf(legacyMonth).TickSubscriptionCountForChecks == 1, "1.3 a session-less Month ignores activity pushes (old path)");

var legacyDate = new DateOne();
Check(DateVmOf(legacyDate).TickSubscriptionCountForChecks == 1, "1.4 a session-less Date keeps the legacy always-on subscription");
legacyDate.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(DateVmOf(legacyDate).TickSubscriptionCountForChecks == 1, "1.5 a session-less Date ignores activity pushes (old path)");

// =========================================================================================
// 2. Month view model: ticking follows RenderActive (direct construction)
// =========================================================================================
Console.WriteLine("--- Month view model gating ---");
var idA = WidgetInstanceId.New();
var sA = WidgetSessionRegistry.GetOrCreate(idA);
var monthVm = new MonthCalendarViewModel(new MonthCalendarModel(DayOfWeek.Monday), sA);
Check(monthVm.TickSubscriptionCountForChecks == 1, "2.1 an injected, visible session starts subscribed");
monthVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(monthVm.TickSubscriptionCountForChecks == 0, "2.2 RenderActive=false unsubscribes the shared timer");
monthVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(monthVm.TickSubscriptionCountForChecks == 1, "2.3 RenderActive=true re-subscribes");
monthVm.OnSessionActivity(ActivitySnapshot.Preview);
Check(monthVm.TickSubscriptionCountForChecks == 1, "2.4 a preview snapshot keeps the subscription alive");

// Overlapping transitions must not corrupt the subscription state machine: the settings
// window caches pages and the Gallery keeps a live preview, so Start/Stop (load/unload)
// interleave with activity pushes while the view model is never disposed.
monthVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
monthVm.Stop();
Check(monthVm.TickSubscriptionCountForChecks == 0, "2.5 covered + stopped stays unsubscribed");
monthVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(monthVm.TickSubscriptionCountForChecks == 0, "2.6 a resume push does not resurrect a stopped view model");
monthVm.Start();
Check(monthVm.TickSubscriptionCountForChecks == 1, "2.7 Start reconciles the subscription after both transitions");

// A session already covered at construction time starts unsubscribed.
var idB = WidgetInstanceId.New();
var sB = WidgetSessionRegistry.GetOrCreate(idB);
await sB.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredVm = new MonthCalendarViewModel(new MonthCalendarModel(DayOfWeek.Monday), sB);
Check(coveredVm.TickSubscriptionCountForChecks == 0, "2.8 a covered session starts unsubscribed");
coveredVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredVm.TickSubscriptionCountForChecks == 1, "2.9 the covered view model re-subscribes on the resume push");

// =========================================================================================
// 3. Date view model: the same gate on the 1x1 calendar
// =========================================================================================
Console.WriteLine("--- Date view model gating ---");
var idC = WidgetInstanceId.New();
var sC = WidgetSessionRegistry.GetOrCreate(idC);
var dateVm = new DateCalendarViewModel(sC);
Check(dateVm.TickSubscriptionCountForChecks == 1, "3.1 an injected, visible session starts subscribed");
dateVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(dateVm.TickSubscriptionCountForChecks == 0, "3.2 RenderActive=false unsubscribes the shared timer");
dateVm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(dateVm.TickSubscriptionCountForChecks == 1, "3.3 RenderActive=true re-subscribes");

// =========================================================================================
// 4. View-level forwarding: IWidgetActivityConsumer reaches the view model
// =========================================================================================
Console.WriteLine("--- View-level IWidgetActivityConsumer forwarding ---");
var idD = WidgetInstanceId.New();
var sD = WidgetSessionRegistry.GetOrCreate(idD);
var month = new Month(new MonthCalendarModel(DayOfWeek.Monday), sD);
Check(VmOf(month).SessionForChecks == sD, "4.1 the Month view hands the injected session to its view model");
month.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(VmOf(month).TickSubscriptionCountForChecks == 0, "4.2 a push on the Month view unsubscribes the view model's timer");

var idE = WidgetInstanceId.New();
var sE = WidgetSessionRegistry.GetOrCreate(idE);
var date = new DateOne(sE);
Check(DateVmOf(date).SessionForChecks == sE, "4.3 the Date view hands the injected session to its view model");
date.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(DateVmOf(date).TickSubscriptionCountForChecks == 0, "4.4 a push on the Date view unsubscribes the view model's timer");

// =========================================================================================
// 5. Teardown: the host's final activity stops rendering, the release is silent
// =========================================================================================
Console.WriteLine("--- Teardown activity ---");
var idF = WidgetInstanceId.New();
var sF = WidgetSessionRegistry.GetOrCreate(idF);
var tornDown = new DateOne(sF);
Check(DateVmOf(tornDown).TickSubscriptionCountForChecks == 1, "5.1 the pre-teardown view model ticks");
// The host's exact teardown order (Widget.axaml.cs PrepareForTeardown): push the
// TeardownActivity (DisplayDetached) into the content, then release the registry entry.
tornDown.OnSessionActivity(new ActivitySnapshot(ActivityReason.DisplayDetached));
Check(DateVmOf(tornDown).TickSubscriptionCountForChecks == 0, "5.2 the teardown activity stops the subscription");
Check(WidgetSessionRegistry.Remove(idF, new ActivitySnapshot(ActivityReason.DisplayDetached)), "5.3 the registry releases the torn-down instance");
Check(sF.State == WidgetLifecycleState.Disposed, "5.4 the removal disposed the session after the final report");
// The widget window closes right after the push, so Unloaded stops the view model; a racing
// activity push arriving after all of that must neither throw nor resurrect the tick.
DateVmOf(tornDown).Stop();
var latePushThrown = false;
try { tornDown.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible)); }
catch (Exception) { latePushThrown = true; }
Check(!latePushThrown && DateVmOf(tornDown).TickSubscriptionCountForChecks == 0, "5.5 a late push after the teardown neither throws nor re-subscribes");

// =========================================================================================
// 6. Source scan: the wiring and the HollowChecks assertion object survived the migration
// =========================================================================================
Console.WriteLine("--- Source scan ---");
string? FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Widgets", "Calendar", "Views", "Month.axaml.cs")))
            return dir.FullName;
        dir = dir.Parent;
    }
    return null;
}
var repoRoot = FindRepoRoot();
if (repoRoot == null)
{
    Console.WriteLine("[NOTE] repository root not found — the source scan was skipped");
}
else
{
    var monthSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Calendar", "Views", "Month.axaml.cs"));
    var dateSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Calendar", "Views", "Date.axaml.cs"));
    var monthVmSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Calendar", "ViewModels", "MonthCalendarViewModel.cs"));
    var dateVmSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Calendar", "ViewModels", "DateCalendarViewModel.cs"));

    Check(monthSource.Contains("IWidgetSession? session = null"), "6.1 Month.axaml.cs declares the optional session constructor parameter");
    Check(monthSource.Contains("IWidgetActivityConsumer"), "6.2 Month.axaml.cs implements IWidgetActivityConsumer");
    Check(dateSource.Contains("IWidgetSession? session = null"), "6.3 Date.axaml.cs declares the optional session constructor parameter");
    Check(dateSource.Contains("IWidgetActivityConsumer"), "6.4 Date.axaml.cs implements IWidgetActivityConsumer");
    Check(monthVmSource.Contains("UpdateTickSubscription"), "6.5 MonthCalendarViewModel reconciles its subscription");
    Check(dateVmSource.Contains("UpdateTickSubscription"), "6.6 DateCalendarViewModel reconciles its subscription");

    // tests/CalendarHollowChecks renders the Month view and analyses the today marker pixels;
    // its assertion object is the brush resolution chain — it must have survived untouched.
    Check(monthSource.Contains("ResolveAccentBrush") && monthSource.Contains("CalendarTodayBrush"),
        "6.7 the Month today-brush resolution (CalendarHollowChecks' assertion object) is intact");
    Check(monthSource.Contains("SetTodayDotBrush"), "6.8 the Month marker brush swap guard is intact");
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Calendar session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted() { }
}
