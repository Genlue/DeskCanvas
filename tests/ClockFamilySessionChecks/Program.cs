// Clock family session checks (P8 batch D — the Analog / Flip / Frameless / TickDial clocks).
//
// Run: dotnet run --project tests/ClockFamilySessionChecks
//
// Pins the batch-D slice of the P8 session migration, all through DIRECT construction (the
// host pipeline rig lives in tests/SessionHookChecks): the four remaining clock views take the
// IWidgetSession constructor parameter and forward activity pushes to their view models, whose
// shared-timer subscriptions reconcile on (loaded/running) AND (session RenderActive). Also
// pins the SharedTimeZone second-wave migration (every private FindSystemTimeZoneById copy in
// the Clock component is gone) and the per-screen theme seam (ClockPerScreenTheme).
using System.Reflection;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Clock.Models;
using Clock.ViewModels;
using Clock.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

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
object? FieldOf(object target, string name) => target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(target);
UpdateTimer TimerOf(object vm) => (UpdateTimer)FieldOf(vm, "timer")!;

// VM accessors (the views keep their view models in a private "viewModel" field, the
// SessionHookChecks convention).
AnalogClockViewModel VmOfAnalog(AnalogI view) => (AnalogClockViewModel)FieldOf(view, "viewModel")!;
AnalogClockViewModel VmOfAnalogIII(AnalogIII view) => (AnalogClockViewModel)FieldOf(view, "viewModel")!;
TickDialClockViewModel VmOfTickDial(AnalogIV view) => (TickDialClockViewModel)FieldOf(view, "viewModel")!;
FlipClockViewModel VmOfFlip(FlipClock view) => (FlipClockViewModel)FieldOf(view, "viewModel")!;

// =========================================================================================
// 1. Analog family (AnalogI / AnalogIII → AnalogClockViewModel): ticking follows RenderActive
// =========================================================================================
Console.WriteLine("--- Analog family (AnalogI / AnalogIII) gating ---");
var legacyAnalog = new AnalogI(new ClockModel(ShowSeconds: true));
var legacyVm = VmOfAnalog(legacyAnalog);
Check(legacyVm.TickSubscriptionCountForChecks == 1, "1.1 a session-less AnalogI keeps the legacy always-on subscription");
legacyAnalog.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(legacyVm.TickSubscriptionCountForChecks == 1, "1.2 a session-less AnalogI ignores activity pushes (old path)");
Check(legacyVm.SessionForChecks == null, "1.3 the legacy path carries no session");

var idA = WidgetInstanceId.New();
var sA = WidgetSessionRegistry.GetOrCreate(idA);
var analog = new AnalogI(new ClockModel(), sA);
var analogVm = VmOfAnalog(analog);
Check(analogVm.SessionForChecks == sA, "1.4 the injected session reaches the view model");
Check(analogVm.TickSubscriptionCountForChecks == 1, "1.5 an injected, visible session starts subscribed");
analog.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(analogVm.TickSubscriptionCountForChecks == 0, "1.6 RenderActive=false unsubscribes the shared timer");
analog.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(analogVm.TickSubscriptionCountForChecks == 1, "1.7 RenderActive=true re-subscribes");
analog.OnSessionActivity(ActivitySnapshot.Preview);
Check(analogVm.TickSubscriptionCountForChecks == 1, "1.8 a preview snapshot keeps the subscription alive");

// Overlapping transitions must not corrupt the subscription state machine.
analog.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
analogVm.Stop();
Check(analogVm.TickSubscriptionCountForChecks == 0, "1.9 covered + stopped stays unsubscribed");
analog.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(analogVm.TickSubscriptionCountForChecks == 0, "1.10 a resume push does not resurrect a stopped view model");
analogVm.Start();
Check(analogVm.TickSubscriptionCountForChecks == 1, "1.11 Start reconciles the subscription after both transitions");

var idB = WidgetInstanceId.New();
var sB = WidgetSessionRegistry.GetOrCreate(idB);
await sB.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredAnalog = new AnalogIII(new ClockModel(), sB);
var coveredVm = VmOfAnalogIII(coveredAnalog);
Check(coveredVm.SessionForChecks == sB && coveredVm.TickSubscriptionCountForChecks == 0, "1.12 a covered session starts AnalogIII unsubscribed");
coveredAnalog.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredVm.TickSubscriptionCountForChecks == 1, "1.13 the covered AnalogIII re-subscribes on the resume push");

// Loaded/Unloaded keep their historic Start/Stop role beside the session gate.
var idC = WidgetInstanceId.New();
var sC = WidgetSessionRegistry.GetOrCreate(idC);
var analogHost = new Window { Width = 200, Height = 200, SystemDecorations = SystemDecorations.None, ShowInTaskbar = false };
var attachedAnalog = new AnalogI(new ClockModel(), sC);
analogHost.Content = attachedAnalog;
analogHost.Show();
Flush();
Check(VmOfAnalog(attachedAnalog).TickSubscriptionCountForChecks == 1, "1.14 the loaded AnalogI ticks");
analogHost.Close();
Flush();
Check(VmOfAnalog(attachedAnalog).TickSubscriptionCountForChecks == 0, "1.15 Unloaded stops the subscription (the Stop path)");

// =========================================================================================
// 2. TickDial family (AnalogIV → TickDialClockViewModel)
// =========================================================================================
Console.WriteLine("--- TickDial (AnalogIV) gating ---");
var legacyTick = new AnalogIV(new ClockModel());
Check(VmOfTickDial(legacyTick).TickSubscriptionCountForChecks == 1, "2.1 a session-less AnalogIV keeps the legacy always-on subscription");
legacyTick.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(VmOfTickDial(legacyTick).TickSubscriptionCountForChecks == 1, "2.2 a session-less AnalogIV ignores activity pushes");

var idD = WidgetInstanceId.New();
var sD = WidgetSessionRegistry.GetOrCreate(idD);
var tick = new AnalogIV(new ClockModel(), sD);
var tickVm = VmOfTickDial(tick);
Check(tickVm.SessionForChecks == sD && tickVm.TickSubscriptionCountForChecks == 1, "2.3 an injected, visible session starts subscribed");
tick.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(tickVm.TickSubscriptionCountForChecks == 0, "2.4 RenderActive=false unsubscribes");
tick.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(tickVm.TickSubscriptionCountForChecks == 1, "2.5 RenderActive=true re-subscribes");

var idE = WidgetInstanceId.New();
var sE = WidgetSessionRegistry.GetOrCreate(idE);
await sE.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredTick = new AnalogIV(new ClockModel(), sE);
Check(VmOfTickDial(coveredTick).TickSubscriptionCountForChecks == 0, "2.6 a covered session starts AnalogIV unsubscribed");
coveredTick.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(VmOfTickDial(coveredTick).TickSubscriptionCountForChecks == 1, "2.7 the covered AnalogIV re-subscribes on the resume push");

// =========================================================================================
// 3. Flip family (FlipClock → FlipClockViewModel)
// =========================================================================================
Console.WriteLine("--- FlipClock gating ---");
var legacyFlip = new FlipClock(new FlipClockModel());
Check(VmOfFlip(legacyFlip).TickSubscriptionCountForChecks == 1, "3.1 a session-less FlipClock keeps the legacy always-on subscription");
legacyFlip.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(VmOfFlip(legacyFlip).TickSubscriptionCountForChecks == 1, "3.2 a session-less FlipClock ignores activity pushes");

var idF = WidgetInstanceId.New();
var sF = WidgetSessionRegistry.GetOrCreate(idF);
var flip = new FlipClock(new FlipClockModel(), sF);
var flipVm = VmOfFlip(flip);
Check(flipVm.SessionForChecks == sF && flipVm.TickSubscriptionCountForChecks == 1, "3.3 an injected, visible session starts subscribed");
flip.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(flipVm.TickSubscriptionCountForChecks == 0, "3.4 RenderActive=false unsubscribes");
flip.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(flipVm.TickSubscriptionCountForChecks == 1, "3.5 RenderActive=true re-subscribes");
flip.OnSessionActivity(ActivitySnapshot.Preview);
Check(flipVm.TickSubscriptionCountForChecks == 1, "3.6 a preview snapshot keeps the subscription alive");

var idG = WidgetInstanceId.New();
var sG = WidgetSessionRegistry.GetOrCreate(idG);
await sG.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredFlip = new FlipClock(new FlipClockModel(), sG);
Check(VmOfFlip(coveredFlip).TickSubscriptionCountForChecks == 0, "3.7 a covered session starts FlipClock unsubscribed");
coveredFlip.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(VmOfFlip(coveredFlip).TickSubscriptionCountForChecks == 1, "3.8 the covered FlipClock re-subscribes on the resume push");

// =========================================================================================
// 4. Frameless clock (FramelessDigital keeps its tick logic in code-behind)
// =========================================================================================
Console.WriteLine("--- FramelessDigital gating ---");
var framelessHost = new Window { Width = 312, Height = 152, SystemDecorations = SystemDecorations.None, ShowInTaskbar = false };
var legacyFrameless = new FramelessDigital(new FramelessClockModel());
framelessHost.Content = legacyFrameless;
framelessHost.Show();
Flush();
Check(legacyFrameless.IsTickingForChecks, "4.1 a session-less FramelessDigital ticks (legacy always-on)");
framelessHost.Close();
Flush();
Check(!legacyFrameless.IsTickingForChecks, "4.2 Unloaded stops the frameless subscription");
legacyFrameless.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!legacyFrameless.IsTickingForChecks, "4.3 a session-less FramelessDigital ignores activity pushes (no resurrection)");

var idH = WidgetInstanceId.New();
var sH = WidgetSessionRegistry.GetOrCreate(idH);
var frameless = new FramelessDigital(new FramelessClockModel(), null, null, sH);
Check(frameless.SessionForChecks == sH && frameless.IsTickingForChecks, "4.4 an injected, visible session starts subscribed");
frameless.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!frameless.IsTickingForChecks, "4.5 RenderActive=false unsubscribes the frameless tick");
frameless.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(frameless.IsTickingForChecks, "4.6 RenderActive=true re-subscribes");

var idI = WidgetInstanceId.New();
var sI = WidgetSessionRegistry.GetOrCreate(idI);
await sI.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredFrameless = new FramelessDigital(new FramelessClockModel(), null, null, sI);
Check(!coveredFrameless.IsTickingForChecks, "4.7 a covered session starts the frameless clock unsubscribed");
coveredFrameless.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredFrameless.IsTickingForChecks, "4.8 the covered frameless clock re-subscribes on the resume push");

// =========================================================================================
// 5. Rhythm equivalence: the historic tick tiers survive the migration unchanged
// =========================================================================================
Console.WriteLine("--- Tick tier (rhythm) equivalence ---");
Check(ReferenceEquals(TimerOf(VmOfAnalog(legacyAnalog)), TimerService.Timer100Ms), "5.1 Analog with ShowSeconds keeps the 100 ms tier");
Check(ReferenceEquals(TimerOf(new AnalogClockViewModel(new ClockModel(ShowSeconds: false))), TimerService.Timer5Seconds), "5.2 Analog without ShowSeconds keeps the 5 s tier");
Check(ReferenceEquals(TimerOf(VmOfFlip(legacyFlip)), TimerService.Timer1Second), "5.3 FlipClock keeps the 1 s tier (exact :00 flips)");
Check(ReferenceEquals(TimerOf(VmOfTickDial(legacyTick)), TimerService.Timer1Second), "5.4 TickDial with ShowSeconds keeps the 1 s tier");
Check(ReferenceEquals(TimerOf(new TickDialClockViewModel(new ClockModel(ShowSecondsTrail: false))), TimerService.Timer1Minute), "5.5 TickDial without ShowSeconds keeps the 1 min tier");
Check(ReferenceEquals(FieldOf(new FramelessDigital(new FramelessClockModel()), "currentTimer"), TimerService.Timer1Minute), "5.6 FramelessDigital outside a widget window keeps the 1 min tier");
var secondsFrameless = new FramelessDigital(new FramelessClockModel(ShowSeconds: true));
Check(ReferenceEquals(FieldOf(secondsFrameless, "currentTimer"), TimerService.Timer1Minute), "5.7 FramelessDigital outside a widget window stays on 1 min even with ShowSeconds");

// =========================================================================================
// 6. SharedTimeZone second wave: the four migrated consumers resolve through Runtime
// =========================================================================================
Console.WriteLine("--- SharedTimeZone consumption & fallback ---");
SharedTimeZone.ResetForChecks();
Check(ReferenceEquals(SharedTimeZone.Resolve("China Standard Time"), SharedTimeZone.Resolve("China Standard Time")),
    "6.1 the shared cache hands the same TimeZoneInfo back (cache-hit equivalence)");
Check(ReferenceEquals(SharedTimeZone.Resolve(null), TimeZoneInfo.Local), "6.2 a null id resolves to the local zone (unset timezone)");

// The unknown-id fallback is the SharedTimeZone contract (Utc), and every consumer keeps
// rendering a plausible time through it instead of throwing.
var utcClock = new AnalogClockViewModel(new ClockModel(Use24Hours: true, TimeZoneId: "No/Such/Zone"));
Check(Regex.IsMatch(utcClock.DigitalTimeText, @"^\d{2}:\d{2}$"), $"6.3 Analog renders a plausible time through the Utc fallback ('{utcClock.DigitalTimeText}')");
utcClock.Dispose();
var utcFlip = new FlipClockViewModel(new FlipClockModel(Use24Hours: true, TimeZoneId: "No/Such/Zone"));
Check(Regex.IsMatch(utcFlip.HourText, @"^\d{2}$") && Regex.IsMatch(utcFlip.MinuteText, @"^\d{2}$"),
    $"6.4 FlipClock renders a plausible time through the Utc fallback ('{utcFlip.HourText}:{utcFlip.MinuteText}')");
utcFlip.Dispose();
var utcTick = new TickDialClockViewModel(new ClockModel(Use24Hours: true, TimeZoneId: "No/Such/Zone"));
Check(Regex.IsMatch(utcTick.TimeText, @"^\d{1,2}:\d{2}$"), $"6.5 TickDial renders a plausible time through the Utc fallback ('{utcTick.TimeText}')");
utcTick.Dispose();
var framelessTime = (DateTime)typeof(FramelessDigital)
    .GetMethod("GetCurrentTime", BindingFlags.NonPublic | BindingFlags.Instance)!
    .Invoke(new FramelessDigital(new FramelessClockModel(TimeZoneId: "No/Such/Zone")), [])!;
Check(Math.Abs((framelessTime - DateTime.UtcNow).TotalSeconds) < 5, $"6.6 FramelessDigital resolves an unknown id to the Utc fallback ('{framelessTime:HH:mm}')");

var localClock = new AnalogClockViewModel(new ClockModel(Use24Hours: true));
Check(Regex.IsMatch(localClock.DigitalTimeText, @"^\d{2}:\d{2}$"), "6.7 the unset timezone (null) keeps the local reading");
localClock.Dispose();

// =========================================================================================
// 7. Per-screen theme seam (ClockPerScreenTheme, the A3 pattern)
// =========================================================================================
Console.WriteLine("--- Per-screen theme seam ---");
var seamType = typeof(AnalogI).Assembly.GetType("Clock.Services.ClockPerScreenTheme")!;
var seamField = seamType.GetField("WindowVariantResolverForChecks", BindingFlags.NonPublic | BindingFlags.Static)!;
var resolveMethod = seamType.GetMethod("Resolve", BindingFlags.NonPublic | BindingFlags.Static)!;

seamField.SetValue(null, new Func<Visual, ThemeVariant?>(_ => ThemeVariant.Dark));
Check(Equals(resolveMethod.Invoke(null, [legacyAnalog]), ThemeVariant.Dark),
    "7.1 the injected window resolver wins (per-screen dark)");
seamField.SetValue(null, null);

var detached = new AnalogIII(new ClockModel());
Check(Equals(resolveMethod.Invoke(null, [detached]), detached.ActualThemeVariant),
    "7.2 without a window the seam returns the control's own variant (the previous reading)");

// =========================================================================================
// 8. Wiring source scan (guards the batch-D wiring against accidental removal)
// =========================================================================================
Console.WriteLine("--- Source scan ---");
string? FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Widgets", "Clock", "Views", "Digital.axaml.cs")))
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
    string Read(params string[] parts) => File.ReadAllText(Path.Combine(repoRoot, Path.Combine(parts)));

    // No private timezone boilerplate remains anywhere in the Clock component.
    var clockDir = Path.Combine(repoRoot, "src", "Widgets", "Clock");
    var clockSources = Directory.EnumerateFiles(clockDir, "*.cs", SearchOption.AllDirectories)
        .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
        .ToList();
    var boilerplateFiles = clockSources
        .Where(f => File.ReadAllText(f).Contains("FindSystemTimeZoneById"))
        .Select(Path.GetFileName)
        .ToList();
    Check(boilerplateFiles.Count == 0, $"8.1 no FindSystemTimeZoneById copy remains in the Clock component (found in: {string.Join(", ", boilerplateFiles)})");

    var globalVariantFiles = clockSources
        .Where(f => File.ReadAllText(f).Contains("Application.Current.ActualThemeVariant"))
        .Select(Path.GetFileName)
        .ToList();
    Check(globalVariantFiles.Count == 0, $"8.2 no uniform Application.Current.ActualThemeVariant read remains in the Clock component (found in: {string.Join(", ", globalVariantFiles)})");

    foreach (var (name, file) in new[]
    {
        ("AnalogClockViewModel", Path.Combine("ViewModels", "AnalogClockViewModel.cs")),
        ("FlipClockViewModel", Path.Combine("ViewModels", "FlipClockViewModel.cs")),
        ("TickDialClockViewModel", Path.Combine("ViewModels", "TickDialClockViewModel.cs")),
        ("FramelessDigital", Path.Combine("Views", "FramelessDigital.axaml.cs"))
    })
    {
        Check(Read("src", "Widgets", "Clock", file).Contains("SharedTimeZone.Resolve"), $"8.3 {name} consumes the shared timezone");
    }

    foreach (var (name, file) in new[]
    {
        ("AnalogI", "AnalogI.axaml.cs"),
        ("AnalogIII", "AnalogIII.axaml.cs"),
        ("AnalogIV", "AnalogIV.axaml.cs"),
        ("FlipClock", "FlipClock.axaml.cs"),
        ("FramelessDigital", "FramelessDigital.axaml.cs")
    })
    {
        var source = Read("src", "Widgets", "Clock", "Views", file);
        Check(source.Contains("IWidgetActivityConsumer") && source.Contains("OnSessionActivity"),
            $"8.4 {name} declares the session activity consumer and forwards pushes");
    }
}

// The session-taking constructors stay discoverable by WidgetFactory's NeedsSession probe.
Check(typeof(AnalogI).GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetSession)))
    && typeof(AnalogIII).GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetSession)))
    && typeof(AnalogIV).GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetSession)))
    && typeof(FlipClock).GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetSession)))
    && typeof(FramelessDigital).GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IWidgetSession))),
    "8.5 all five migrated views expose an IWidgetSession constructor parameter (the factory probe)");

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Clock family session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

/// <summary>Headless Avalonia host (the SessionHookChecks rig, minus the app pipeline).</summary>
sealed class ProbeApp : Avalonia.Application
{
    public override void OnFrameworkInitializationCompleted() { }
}
