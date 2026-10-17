// Batteries checks (P8 batch A, slice A4 — the Batteries widget).
//
// Run: dotnet run --project tests/BatteriesChecks
//
// Pins the A4 refactor: the retired private 3 s DispatcherTimer is now a 3-tick divider over
// the SHARED TimerService.Timer1Second (fullscreen PauseAll/ResumeAll stops the ticks for
// free), the poll reconciles over two gates (session RenderActive + explicit Suspend/Resume),
// the readout brushes resolve their light/dark variant per WINDOW (per-screen policy) instead
// of the uniform Application variant, and the third-tier `new AppSettingsProvider()` fallback
// is gone (two-tier resolution: injected → App.Services → safe defaults).
// PowerService stays a component-internal implementation on purpose: it is the single
// consumer of its own P/Invoke surface, so no shared abstraction is warranted.
using Microsoft.Extensions.DependencyInjection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Batteries.Models;
using Batteries.Services;
using Batteries.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

AppBuilder.Configure<ProbeApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
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

// =========================================================================================
// 1. The 3-tick divider (pure seam): 1 s shared ticks → an effective 3 s poll cadence
// =========================================================================================
Console.WriteLine("--- 3-tick divider (pure seam) ---");
Check(BatteriesViewModel.ShouldPollOnTick(0), "1.1 tick 0 (the FIRST tick after subscribing) polls — subscribe means refresh");
Check(!BatteriesViewModel.ShouldPollOnTick(1) && !BatteriesViewModel.ShouldPollOnTick(2), "1.2 ticks 1 and 2 do not poll");
Check(BatteriesViewModel.ShouldPollOnTick(3), "1.3 tick 3 polls — every third shared tick");
var periodic = Enumerable.Range(0, 24).All(t => BatteriesViewModel.ShouldPollOnTick(t) == (t % 3 == 0));
Check(periodic, "1.4 the divider is exactly t % 3 == 0 over 24 ticks (boundary pinned)");
var pollsIn12 = Enumerable.Range(0, 12).Count(BatteriesViewModel.ShouldPollOnTick);
Check(pollsIn12 == 4, $"1.5 twelve 1 s ticks produce exactly 4 polls (the old 3 s DispatcherTimer cadence), got {pollsIn12}");

// =========================================================================================
// 2. Legacy session-less view model: always-on poll, the explicit suspend gate
// =========================================================================================
Console.WriteLine("--- Session-less view model (legacy path) ---");
var legacy = new BatteriesViewModel();
Check(legacy.IsPollingForChecks && legacy.SessionForChecks == null, "2.1 a session-less view model starts subscribed (always-on, no session)");
Check(legacy.PollCountForChecks == 1, "2.2 construction polls once immediately");
legacy.SimulateTickForChecks();
Check(legacy.PollCountForChecks == 2 && legacy.TickCounterForChecks == 1, "2.3 the first shared tick polls (subscribe-means-refresh)");
legacy.SimulateTickForChecks();
legacy.SimulateTickForChecks();
Check(legacy.PollCountForChecks == 2, "2.4 ticks 1 and 2 do not poll (divider holds at runtime, not only in the pure function)");
legacy.SimulateTickForChecks();
Check(legacy.PollCountForChecks == 3, "2.5 tick 3 polls — the 3 s cadence over the shared timer");
legacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(legacy.IsPollingForChecks && legacy.RenderActiveForChecks, "2.6 a session-less view model ignores activity pushes (old path)");
legacy.Suspend();
Check(!legacy.IsPollingForChecks && legacy.SuspendedForChecks, "2.7 Suspend unsubscribes from the shared timer");
legacy.SimulateTickForChecks();
Check(legacy.PollCountForChecks == 3, "2.8 a tick while suspended polls nothing");
legacy.Suspend();
Check(!legacy.IsPollingForChecks, "2.9 Suspend is idempotent");
legacy.Resume();
Check(legacy.IsPollingForChecks && legacy.PollCountForChecks == 4, "2.10 Resume re-subscribes and polls once immediately (fresh readout)");
legacy.Resume();
Check(legacy.PollCountForChecks == 4, "2.11 Resume is idempotent (no extra poll)");
legacy.Dispose();
Check(!legacy.IsPollingForChecks, "2.12 Dispose unsubscribes from the shared timer");

// =========================================================================================
// 3. Session gating: the poll follows RenderActive pushes
// =========================================================================================
Console.WriteLine("--- Session gating ---");
var idA = WidgetInstanceId.New();
var sessionA = WidgetSessionRegistry.GetOrCreate(idA);
var gated = new BatteriesViewModel(session: sessionA);
Check(gated.IsPollingForChecks && gated.RenderActiveForChecks, "3.1 an injected, visible session starts subscribed");
gated.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!gated.IsPollingForChecks && !gated.RenderActiveForChecks, "3.2 a covered screen unsubscribes the poll");
gated.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(gated.IsPollingForChecks, "3.3 the visible push re-subscribes");
gated.OnSessionActivity(ActivitySnapshot.Preview);
Check(gated.IsPollingForChecks, "3.4 a preview snapshot keeps the poll alive (RenderActive)");
gated.Dispose();

// A session already covered at construction time starts unsubscribed.
var idB = WidgetInstanceId.New();
var sessionB = WidgetSessionRegistry.GetOrCreate(idB);
await sessionB.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var coveredAtBirth = new BatteriesViewModel(session: sessionB);
Check(!coveredAtBirth.IsPollingForChecks, "3.5 a covered session starts unsubscribed");
coveredAtBirth.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(coveredAtBirth.IsPollingForChecks, "3.6 the covered view model re-subscribes on the resume push");
coveredAtBirth.Dispose();

// =========================================================================================
// 4. Overlapping gates: session activity AND explicit suspension must BOTH be open
// =========================================================================================
Console.WriteLine("--- Overlapping gates (reconciliation) ---");
var idC = WidgetInstanceId.New();
var sessionC = WidgetSessionRegistry.GetOrCreate(idC);
var dual = new BatteriesViewModel(session: sessionC);
Check(dual.IsPollingForChecks, "4.1 both gates open → subscribed");
dual.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
dual.Suspend();
Check(!dual.IsPollingForChecks, "4.2 covered + suspended stays unsubscribed");
dual.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(!dual.IsPollingForChecks, "4.3 a session resume alone does not resurrect a suspended poll (suspend gate still closed)");
dual.Resume();
Check(dual.IsPollingForChecks && dual.PollCountForChecks == 2, "4.4 Resume with the session visible re-subscribes and polls immediately (both gates open)");
// Redundant transitions must not corrupt the reconciled state.
dual.Suspend();
dual.Suspend();
dual.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!dual.IsPollingForChecks, "4.5 redundant close transitions stay cleanly unsubscribed");
dual.Resume();
dual.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
dual.Resume();
Check(dual.IsPollingForChecks, "4.6 redundant open transitions reconcile to exactly one subscription");
dual.Dispose();
dual.Dispose();
Check(!dual.IsPollingForChecks, "4.7 double Dispose stays unsubscribed (no dangling shared-timer callback)");

// =========================================================================================
// 5. Fullscreen coupling: the poll rides the shared timer the host pauses
// =========================================================================================
Console.WriteLine("--- TimerService fullscreen pause ---");
var rider = new BatteriesViewModel();
bool TimerPaused() =>
    (bool)(typeof(UpdateTimer).GetField("paused", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .GetValue(TimerService.Timer1Second) ?? false);
Check(rider.IsPollingForChecks && !TimerPaused(), "5.1 the poll holds a live subscription on Timer1Second and the timer runs");
TimerService.PauseAll();
Check(TimerPaused(), "5.2 PauseAll (fullscreen) stops tick delivery on the shared timer");
Check(rider.IsPollingForChecks, "5.3 the subscription itself survives the pause (resume restores the cadence)");
TimerService.ResumeAll();
Check(!TimerPaused() && rider.IsPollingForChecks, "5.4 ResumeAll restores tick delivery — the poll continues automatically");
rider.Dispose();

// =========================================================================================
// 6. Per-window variant resolution + pinned palette
// =========================================================================================
Console.WriteLine("--- Per-window variant + pinned palette ---");
Check(BatteryDeviceItem.ResolveVariant(null, ThemeVariant.Dark) == ThemeVariant.Dark, "6.1 ResolveVariant(null, Dark) = Dark — the uniform fallback applies");
Check(BatteryDeviceItem.ResolveVariant(null, null) == ThemeVariant.Default, "6.2 ResolveVariant(null, null) = Default — nothing to resolve from");
Check(BatteryDeviceItem.ResolveVariant(ThemeVariant.Light, ThemeVariant.Dark) == ThemeVariant.Light, "6.3 the host window's variant wins over the uniform one");
Check(BatteryDeviceItem.StatusColor(true, true, 50) == Color.Parse("#30D158")
    && BatteryDeviceItem.StatusColor(false, true, 50) == Color.Parse("#34C759"), "6.4 charging pair pinned: #30D158 (dark) / #34C759 (light)");
Check(BatteryDeviceItem.StatusColor(true, false, 50) == Color.Parse("#30D158")
    && BatteryDeviceItem.StatusColor(false, false, 50) == Color.Parse("#34C759"), "6.5 >20% pair pinned: the green pair");
Check(BatteryDeviceItem.StatusColor(true, false, 15) == Color.Parse("#FF9F0A")
    && BatteryDeviceItem.StatusColor(false, false, 15) == Color.Parse("#FF9500"), "6.6 11–20% pair pinned: #FF9F0A (dark) / #FF9500 (light)");
Check(BatteryDeviceItem.StatusColor(true, false, 5) == Color.Parse("#FF453A")
    && BatteryDeviceItem.StatusColor(false, false, 5) == Color.Parse("#FF3B30"), "6.7 <=10% pair pinned: #FF453A (dark) / #FF3B30 (light)");

var chargingItem = new BatteryDeviceItem("main", DeviceKind.Computer, 80, isCharging: true);
chargingItem.VariantProvider = () => ThemeVariant.Dark;
Check(((SolidColorBrush)chargingItem.StatusBrush).Color == Color.Parse("#30D158"),
    "6.8 a window-dark variant provider flips the brush to the dark palette (per-window source wired)");
chargingItem.VariantProvider = () => ThemeVariant.Light;
Check(((SolidColorBrush)chargingItem.StatusBrush).Color == Color.Parse("#34C759"),
    "6.9 the same item under a window-light variant uses the light palette");
chargingItem.VariantProvider = null;
Check(((SolidColorBrush)chargingItem.StatusBrush).Color == BatteryDeviceItem.StatusColor(
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark, true, 80),
    "6.10 no provider falls back to the uniform Application variant");
var empty = new BatteryDeviceItem(string.Empty, DeviceKind.Mouse, 0, isCharging: false, hasDevice: false);
Check(empty.StatusBrush == Brushes.Transparent, "6.11 an empty slot stays Transparent");
Check(empty.TrackBrush != null, "6.12 the empty slot's track brush still resolves (neutral resource or Gray)");

// =========================================================================================
// 7. Monochrome three-branch equivalence (values carried over verbatim)
// =========================================================================================
Console.WriteLine("--- Monochrome branches ---");
Func<ThemeVariant?> dark = () => ThemeVariant.Dark;
IAppSettingsProvider ProviderWith(Theme theme) => new StubSettingsProvider(
    new AppSettings(theme, [], new Layout(GridMode.Manual, false, false, false, false),
        new Dimensions(100, 10, 16), new Region("en"), false, null));
var blackWhite = new Theme(false, null, 1, true, false, "Inter", Surface: SurfaceStyle.Acrylic, MonochromeVariant: MonochromeStyle.BlackWhite);
var bwDark = new BatteryDeviceItem("d", DeviceKind.Computer, 60, false, true, ProviderWith(blackWhite)) { VariantProvider = dark };
Check(bwDark.StatusBrush == Brushes.White, "7.1 BlackWhite + dark window: the ring is White (accent itself)");
var bwLight = new BatteryDeviceItem("l", DeviceKind.Computer, 60, false, true, ProviderWith(blackWhite)) { VariantProvider = () => ThemeVariant.Light };
Check(bwLight.StatusBrush == Brushes.Black, "7.2 BlackWhite + light window: the ring is Black");
Check(bwDark.ChargingBadgeForeground == Brushes.Black, "7.3 BlackWhite + dark window: the badge inverts to Black");
var accentPicked = new Theme(false, "#A1B2C3", 1, true, false, "Inter", Surface: SurfaceStyle.Acrylic, MonochromeVariant: MonochromeStyle.Accent);
var accentItem = new BatteryDeviceItem("a", DeviceKind.Computer, 60, false, true, ProviderWith(accentPicked)) { VariantProvider = dark };
Check(((SolidColorBrush)accentItem.StatusBrush).Color == Color.Parse("#A1B2C3"), "7.4 the hand-picked monochrome accent is honored");
var accentDefaultTheme = new Theme(false, null, 1, true, false, "Inter", Surface: SurfaceStyle.Acrylic, MonochromeVariant: MonochromeStyle.Accent);
var accentResourceItem = new BatteryDeviceItem("r", DeviceKind.Computer, 60, false, true, ProviderWith(accentDefaultTheme)) { VariantProvider = dark };
var accentLightItem = new BatteryDeviceItem("l2", DeviceKind.Computer, 60, false, true, ProviderWith(accentDefaultTheme)) { VariantProvider = () => ThemeVariant.Light };
object? resourceAccent = null;
var accentFromResource = Application.Current != null
    && Application.Current.TryGetResource("SystemControlForegroundAccentBrush", ThemeVariant.Dark, out resourceAccent)
    && resourceAccent is IBrush;
if (accentFromResource)
    Check(((SolidColorBrush)accentResourceItem.StatusBrush).Color == ((SolidColorBrush)(IBrush)resourceAccent!).Color,
        "7.5 without a picked accent the themed accent resource wins (same lookup the view does)");
else
    Check(((SolidColorBrush)accentResourceItem.StatusBrush).Color == Color.Parse("#70A5FF"),
        "7.5 no accent resource in the rig: the pinned dark fallback #70A5FF applies");
Check(((SolidColorBrush)accentLightItem.StatusBrush).Color == Color.Parse("#0078D4") || accentFromResource,
    "7.6 the light fallback #0078D4 is still pinned (or the accent resource covers the branch)");

// =========================================================================================
// 8. Settings provider resolution: injected → App.Services → safe defaults (no third tier)
// =========================================================================================
Console.WriteLine("--- Settings provider resolution ---");
var injected = ProviderWith(new Theme(false, null, 1, false, false, "Inter", Surface: SurfaceStyle.Acrylic));
var appLevel = ProviderWith(new Theme(false, null, 1, false, false, "Inter", Surface: SurfaceStyle.Acrylic));
// App.Services is an IServiceProvider — publish the probe provider through a minimal
// service container so the widget's second resolution tier can find it.
typeof(DeskCanvas.App).GetProperty("Services")!.GetSetMethod(true)!.Invoke(null,
    [new Microsoft.Extensions.DependencyInjection.ServiceCollection()
        .AddSingleton(appLevel)
        .BuildServiceProvider()]);
var injectedVm = new BatteriesViewModel(settingsProvider: injected);
Check(ReferenceEquals(injectedVm.ProviderForChecks, injected), "8.1 the injected provider wins over the app-level service");
injectedVm.Dispose();
var appLevelVm = new BatteriesViewModel();
Check(ReferenceEquals(appLevelVm.ProviderForChecks, appLevel), "8.2 without injection the App.Services tier resolves");
var brushNotified = false;
appLevelVm.Items[0].PropertyChanged += (_, e) =>
{
    if (e.PropertyName == nameof(BatteryDeviceItem.StatusBrush)) brushNotified = true;
};
((StubSettingsProvider)appLevel).RaiseDataChanged();
Flush();
Check(brushNotified, "8.3 a live DataChanged push refreshes the readout brushes (posted to the UI thread)");
appLevelVm.Dispose();
brushNotified = false;
((StubSettingsProvider)appLevel).RaiseDataChanged();
Flush();
Check(!brushNotified, "8.4 a push after Dispose is a silent no-op (handler detached)");
typeof(DeskCanvas.App).GetProperty("Services")!.GetSetMethod(true)!.Invoke(null, [null]);
var orphanVm = new BatteriesViewModel();
Check(orphanVm.ProviderForChecks == null, "8.5 both tiers empty: no provider is created inside the component (safe defaults, third tier removed)");
Check(orphanVm.Items[0].StatusBrush != null, "8.6 the safe-default path still brushes (colourful branch, no theme info)");
orphanVm.Dispose();

// =========================================================================================
// 9. Source scan: guards the A4 refactor against accidental removal
// =========================================================================================
Console.WriteLine("--- Source scan ---");
string? FindRepoRoot()
{
    DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "src", "Widgets", "Batteries", "Batteries.csproj")))
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
    var vmSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Batteries", "ViewModels", "BatteriesViewModel.cs"));
    Check(!vmSource.Contains("DispatcherTimer"), "9.1 the private DispatcherTimer is gone from the view model");
    Check(vmSource.Contains("TimerService.Timer1Second") && vmSource.Contains("ShouldPollOnTick"),
        "9.2 the poll rides the shared 1 s timer through the 3-tick divider");
    Check(!vmSource.Contains("new AppSettingsProvider()"), "9.3 the third-tier standalone provider fallback is removed");
    var viewSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Batteries", "Views", "BatteriesView.axaml.cs"));
    Check(viewSource.Contains("IWidgetActivityConsumer") && viewSource.Contains("IFocusThemedContent"),
        "9.4 the view consumes session activity and the focus-theme host entry");
    Check(viewSource.Contains("IWidgetSession"), "9.5 the view declares the session constructor parameter");
    var powerService = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "Batteries", "Services", "PowerService.cs"));
    Check(powerService.Contains("GetCurrentPowerInfo"), "9.6 PowerService is still the component-internal power source (single consumer, not abstracted)");
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Batteries checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

sealed class ProbeApp : DeskCanvas.App
{
    public override void OnFrameworkInitializationCompleted() { }
}

/// <summary>Read-only settings stub: hands back one fixed snapshot, never touches disk.</summary>
sealed class StubSettingsProvider(AppSettings settings) : IAppSettingsProvider
{
    public AppSettings Get() => settings;

    public void Save(AppSettings data) { }

    public event DataChangedEvent<AppSettings> DataChanging
    {
        add { }
        remove { }
    }

    private DataChangedEvent<AppSettings>? dataChanged;
    public event DataChangedEvent<AppSettings> DataChanged
    {
        add => dataChanged += value;
        remove => dataChanged -= value;
    }

    public void RaiseDataChanged() => dataChanged?.Invoke(this, null, settings);
}
