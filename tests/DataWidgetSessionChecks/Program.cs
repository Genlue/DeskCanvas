// Data widget session checks (P8 batch B, slice B1 — the data-display widgets:
// Monitor, Weather, Fixed, Hitokoto, Storage).
//
// Run: dotnet run --project tests/DataWidgetSessionChecks
//
// Pins the B1 slice: every timer/refresh loop in the five data-display widgets is gated by
// the injected session's RenderActive (direct construction, the SessionHookChecks pilot rig —
// no host pipeline), the per-screen theme seams (host window variant wins, the uniform
// Application variant is only the fallback), and the Hitokoto panel's PanelService migration.
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using FixedWidgets.Models;
using FixedWidgets.ViewModels;
using FixedWidgets.Views;
using Hitokoto.Models;
using Hitokoto.Views;
using Monitor.Models;
using Monitor.ViewModels;
using Monitor.Views;
using Storage.Models;
using Storage.Services;
using Storage.ViewModels;
using Storage.Views;
using Weather.Models;
using Weather.ViewModels;
using Weather.Views;

// UseHeadlessDrawing=false gives the probe REAL window handles (the SessionHookChecks
// precedent) — the Hitokoto checks attach views to a live window so Loaded/Unloaded run.
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

// Covered/visible sessions for direct construction. The session is only the state store:
// activity reaches the view model through the view's IWidgetActivityConsumer push, exactly
// as the host delivers it on the UI thread.
IWidgetSession NewSession() => WidgetSessionRegistry.GetOrCreate(WidgetInstanceId.New());
async Task<IWidgetSession> NewCoveredSessionAsync()
{
    var session = NewSession();
    await session.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
    return session;
}

// =========================================================================================
// 1. Monitor SingleMetricViewModel: the one-second WMI poll follows RenderActive
// =========================================================================================
Console.WriteLine("--- Monitor SingleMetric gating ---");
var monitorLegacy = new SingleMetricViewModel(new SingleMetricModel(MetricType.CpuUsage));
Check(monitorLegacy.TickSubscriptionCountForChecks == 1, "1.1 a session-less SingleMetric keeps the legacy always-on subscription");
monitorLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(monitorLegacy.TickSubscriptionCountForChecks == 1, "1.2 a session-less SingleMetric ignores activity pushes (old path)");
monitorLegacy.Dispose();
Check(monitorLegacy.TickSubscriptionCountForChecks == 0, "1.3 the legacy teardown still unsubscribes (Dispose)");

var monitorSession = NewSession();
var monitor = new SingleMetricViewModel(new SingleMetricModel(MetricType.CpuUsage), monitorSession);
Check(monitor.SessionForChecks == monitorSession, "1.4 the injected session is reachable through the check seam");
Check(monitor.TickSubscriptionCountForChecks == 1, "1.5 an injected, visible session starts subscribed");
monitor.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(monitor.TickSubscriptionCountForChecks == 0, "1.6 RenderActive=false unsubscribes the shared timer");
monitor.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(monitor.TickSubscriptionCountForChecks == 1, "1.7 RenderActive=true re-subscribes");
monitor.OnSessionActivity(ActivitySnapshot.Preview);
Check(monitor.TickSubscriptionCountForChecks == 1, "1.8 a preview snapshot keeps the subscription alive");
monitor.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
monitor.Dispose();
Check(monitor.TickSubscriptionCountForChecks == 0, "1.9 Dispose unsubscribes a covered view model (teardown while gated)");

var monitorCoveredSession = await NewCoveredSessionAsync();
var monitorCovered = new SingleMetricViewModel(new SingleMetricModel(MetricType.RamUsage), monitorCoveredSession);
Check(monitorCovered.TickSubscriptionCountForChecks == 0, "1.10 a session already covered at construction starts unsubscribed");
monitorCovered.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(monitorCovered.TickSubscriptionCountForChecks == 1, "1.11 the covered pilot re-subscribes on the resume push");

// =========================================================================================
// 2. Monitor MultiDashboardViewModel: same gate over the four-ring dashboard poll
// =========================================================================================
Console.WriteLine("--- Monitor MultiDashboard gating ---");
var dashboardLegacy = new MultiDashboardViewModel(new MultiDashboardModel(MultiDashboardModel.DefaultMetrics));
Check(dashboardLegacy.TickSubscriptionCountForChecks == 1, "2.1 a session-less MultiDashboard keeps the legacy always-on subscription");
dashboardLegacy.Dispose();
Check(dashboardLegacy.TickSubscriptionCountForChecks == 0, "2.2 the legacy teardown still unsubscribes");

var dashboardSession = NewSession();
var dashboard = new MultiDashboardViewModel(new MultiDashboardModel(MultiDashboardModel.DefaultMetrics), dashboardSession);
Check(dashboard.TickSubscriptionCountForChecks == 1, "2.3 an injected, visible session starts subscribed");
dashboard.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(dashboard.TickSubscriptionCountForChecks == 0, "2.4 a covered screen unsubscribes the dashboard poll");
dashboard.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(dashboard.TickSubscriptionCountForChecks == 1, "2.5 the resume push re-subscribes");
dashboard.Dispose();
Check(dashboard.TickSubscriptionCountForChecks == 0, "2.6 Dispose unsubscribes");

// =========================================================================================
// 3. Weather Forecast/AirQuality: the hourly refresh follows RenderActive
// =========================================================================================
Console.WriteLine("--- Weather hourly refresh gating ---");
var forecastLegacy = new ForecastViewModel(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius"));
Check(forecastLegacy.TickSubscriptionCountForChecks == 1, "3.1 a session-less Forecast keeps the legacy hourly subscription");
forecastLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(forecastLegacy.TickSubscriptionCountForChecks == 1, "3.2 a session-less Forecast ignores activity pushes (old path)");
forecastLegacy.Dispose();
Check(forecastLegacy.TickSubscriptionCountForChecks == 0, "3.3 the legacy teardown still unsubscribes");

var forecastSession = NewSession();
var forecast = new ForecastViewModel(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius"), forecastSession);
Check(forecast.SessionForChecks == forecastSession, "3.4 the injected session is reachable through the check seam");
Check(forecast.TickSubscriptionCountForChecks == 1, "3.5 an injected, visible session starts subscribed");
forecast.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(forecast.TickSubscriptionCountForChecks == 0, "3.6 a covered screen unsubscribes the hourly refresh");
forecast.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(forecast.TickSubscriptionCountForChecks == 1, "3.7 the resume push re-subscribes");
forecast.OnSessionActivity(ActivitySnapshot.Preview);
Check(forecast.TickSubscriptionCountForChecks == 1, "3.8 a preview snapshot keeps the subscription alive");
forecast.Dispose();
Check(forecast.TickSubscriptionCountForChecks == 0, "3.9 Dispose unsubscribes");

var airLegacy = new AirQualityViewModel(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius"));
Check(airLegacy.TickSubscriptionCountForChecks == 1, "3.10 a session-less AirQuality keeps the legacy hourly subscription");
airLegacy.Dispose();
Check(airLegacy.TickSubscriptionCountForChecks == 0, "3.11 the legacy teardown still unsubscribes");

var airSession = NewSession();
var air = new AirQualityViewModel(new ForecastModel("Beijing", 39.9042, 116.4074, "celsius"), airSession);
air.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(air.TickSubscriptionCountForChecks == 0, "3.12 a covered screen unsubscribes the air-quality refresh");
air.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(air.TickSubscriptionCountForChecks == 1, "3.13 the resume push re-subscribes");

// =========================================================================================
// 4. Fixed AggregateViewModel: clock (1 s) + weather/calendar (1 h) subscribe and gate together
// =========================================================================================
Console.WriteLine("--- Fixed aggregate gating + per-screen marker ---");
var aggregateLegacy = new AggregateViewModel(new AggregateModel());
Check(aggregateLegacy.TickSubscriptionCountForChecks == 2, "4.1 a session-less Aggregate keeps the legacy always-on subscriptions (both timers)");
aggregateLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(aggregateLegacy.TickSubscriptionCountForChecks == 2, "4.2 a session-less Aggregate ignores activity pushes (old path)");
aggregateLegacy.Dispose();
Check(aggregateLegacy.TickSubscriptionCountForChecks == 0, "4.3 the legacy teardown still unsubscribes both timers");

var aggregateSession = NewSession();
var aggregate = new AggregateViewModel(new AggregateModel(), null, aggregateSession);
Check(aggregate.SessionForChecks == aggregateSession, "4.4 the injected session is reachable through the check seam");
Check(aggregate.TickSubscriptionCountForChecks == 2, "4.5 an injected, visible session starts subscribed on both timers");
aggregate.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(aggregate.TickSubscriptionCountForChecks == 0, "4.6 a covered screen unsubscribes both timers");
aggregate.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(aggregate.TickSubscriptionCountForChecks == 2, "4.7 the resume push re-subscribes both timers");
aggregate.Dispose();
Check(aggregate.TickSubscriptionCountForChecks == 0, "4.8 Dispose unsubscribes both timers");

Check(AggregateViewModel.ResolveEffectiveVariant(ThemeVariant.Dark, ThemeVariant.Light) == ThemeVariant.Dark,
    "4.9 ResolveEffectiveVariant: the host window's variant wins over the uniform variant");
Check(AggregateViewModel.ResolveEffectiveVariant(null, ThemeVariant.Light) == ThemeVariant.Light,
    "4.10 ResolveEffectiveVariant: a host-less readout falls back to the uniform Application variant");
Check(AggregateViewModel.ResolveEffectiveVariant(null, null) == ThemeVariant.Default,
    "4.11 ResolveEffectiveVariant: both absent falls back to the platform default");

// =========================================================================================
// 5. Storage: the ten-second drive poll reconciles session + suspend + disposed
// =========================================================================================
Console.WriteLine("--- Storage poll gating + per-screen readout variant ---");
var storageLegacy = new StorageViewModel(new StorageModel());
Check(storageLegacy.IsPollingForChecks, "5.1 a session-less Storage keeps the legacy always-running poll");
storageLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(storageLegacy.IsPollingForChecks, "5.2 a session-less Storage ignores activity pushes (old path)");
storageLegacy.Suspend();
Check(!storageLegacy.IsPollingForChecks, "5.3 the host Suspend still stops the legacy poll");
storageLegacy.Resume();
Check(storageLegacy.IsPollingForChecks, "5.4 the host Resume still restarts the legacy poll");
storageLegacy.Dispose();
Check(!storageLegacy.IsPollingForChecks, "5.5 the legacy teardown still stops the poll");

var storageSession = NewSession();
var storage = new StorageViewModel(new StorageModel(), session: storageSession);
Check(storage.SessionForChecks == storageSession, "5.6 the injected session is reachable through the check seam");
Check(storage.IsPollingForChecks, "5.7 an injected, visible session starts the poll");
storage.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(!storage.IsPollingForChecks && storage.PollStartsForChecks == 1, "5.8 a covered screen stops the poll (no double start)");
storage.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(storage.IsPollingForChecks && storage.PollStartsForChecks == 2, "5.9 the resume push restarts the poll exactly once");

storage.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
storage.Suspend();
Check(!storage.IsPollingForChecks && storage.SuspendedForChecks, "5.10 covered + suspended stays stopped");
storage.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(!storage.IsPollingForChecks, "5.11 a resume push does not override an explicit host suspension");
storage.Resume();
Check(storage.IsPollingForChecks, "5.12 Resume (both gates open) restarts the poll");
storage.Dispose();
Check(!storage.IsPollingForChecks, "5.13 Dispose stops the poll");

Check(DriveItemViewModel.ResolveVariant(ThemeVariant.Dark, ThemeVariant.Light) == ThemeVariant.Dark,
    "5.14 ResolveVariant: the host window's variant wins over the uniform variant");
Check(DriveItemViewModel.ResolveVariant(null, ThemeVariant.Light) == ThemeVariant.Light,
    "5.15 ResolveVariant: a host-less readout falls back to the uniform Application variant");
Check(DriveItemViewModel.ResolveVariant(null, null) == ThemeVariant.Default,
    "5.16 ResolveVariant: both absent falls back to the platform default");

// The warning palette is variant-pinned: a covered-screen widget whose window renders dark
// must show the dark pair, and vice versa — per screen, not per application.
var driveInfo = new StorageService.DriveStorageInfo(
    "C:\\", "C", "System", "System", "System", DriveType.Fixed, "NTFS", true,
    TotalBytes: 100, FreeBytes: 20, UsedBytes: 80, UsageFraction: 0.8, UsagePercent: 80);
var warningDrive = new DriveItemViewModel(null);
warningDrive.UpdateFrom(driveInfo, new StorageModel(UseWarningColors: true));
warningDrive.VariantProvider = () => ThemeVariant.Dark;
var darkWarning = (warningDrive.StatusBrush as ISolidColorBrush)?.Color;
warningDrive.VariantProvider = () => ThemeVariant.Light;
var lightWarning = (warningDrive.StatusBrush as ISolidColorBrush)?.Color;
Check(darkWarning == Color.Parse("#FF9F0A") && lightWarning == Color.Parse("#FF9500"),
    "5.17 the warning readout resolves the dark/light pair through the wired window variant");

// =========================================================================================
// 6. Hitokoto: the auto-refresh DispatcherTimer follows RenderActive beside attach/suspend
// =========================================================================================
Console.WriteLine("--- Hitokoto auto-refresh gating ---");
DispatcherTimer? TimerOf(HitokotoView view) =>
    (DispatcherTimer?)typeof(HitokotoView).GetField("autoRefreshTimer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(view);
Window HostOf(HitokotoView view) => new()
{
    Width = 320, Height = 120, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false,
    Content = view
};
HitokotoModel LiveModel() => new HitokotoModel(RefreshIntervalMinutes: 1).Normalize();

var hitokotoLegacy = new HitokotoView(null, null, LiveModel());
var legacyHost = HostOf(hitokotoLegacy);
legacyHost.Show();
Flush();
Check(TimerOf(hitokotoLegacy)?.IsEnabled == true, "6.1 a session-less Hitokoto keeps the legacy auto-refresh timer running while loaded");
hitokotoLegacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(TimerOf(hitokotoLegacy)?.IsEnabled == true, "6.2 a session-less Hitokoto ignores activity pushes (old path)");
legacyHost.Close();
Flush();
Check(TimerOf(hitokotoLegacy)?.IsEnabled != true, "6.3 Unloaded still stops the legacy timer");

var hitokotoSession = NewSession();
var hitokoto = new HitokotoView(null, null, LiveModel(), hitokotoSession);
Check(hitokoto.SessionForChecks == hitokotoSession, "6.4 the injected session is reachable through the check seam");
var hitokotoHost = HostOf(hitokoto);
hitokotoHost.Show();
Flush();
Check(TimerOf(hitokoto)?.IsEnabled == true, "6.5 an injected, visible session runs the auto-refresh timer while loaded");
hitokoto.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(TimerOf(hitokoto)?.IsEnabled != true && !hitokoto.RenderActiveForChecks, "6.6 a covered screen stops the auto-refresh timer");
hitokoto.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(TimerOf(hitokoto)?.IsEnabled == true && hitokoto.RenderActiveForChecks, "6.7 the resume push restarts the auto-refresh timer");
hitokoto.OnSessionActivity(ActivitySnapshot.Preview);
Check(TimerOf(hitokoto)?.IsEnabled == true, "6.8 a preview snapshot keeps the auto-refresh timer running");
hitokotoHost.Close();
Flush();

// =========================================================================================
// 7. Wiring source scan (guards the B1 wiring against accidental removal)
// =========================================================================================
Console.WriteLine("--- Wiring source scan ---");
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
    Console.WriteLine("[NOTE] repository root not found — the source scan was skipped");
}
else
{
    string Read(string relative) => File.ReadAllText(Path.Combine(repoRoot, relative));

    var hitokotoPopup = Read("src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml.cs");
    Check(hitokotoPopup.Contains("PanelService.Open(new PanelRequest("),
        "7.1 the Hitokoto popup opens through PanelService");
    Check(hitokotoPopup.Contains("PanelScope.PerOwner") && hitokotoPopup.Contains("\"hitokoto\""),
        "7.2 the Hitokoto panel identity is the per-owner \"hitokoto\" key");
    Check(!hitokotoPopup.Contains("PanelCoolingDown<HitokotoPopupWindow>") &&
          !hitokotoPopup.Contains("ShowAsSecondaryPanel(owner)"),
        "7.3 the bare legacy open path (cooldown probe + toggle + Show) is gone from the Hitokoto popup");

    var weatherPopup = Read("src/Widgets/Weather/Views/WeatherPopupWindow.axaml.cs");
    Check(weatherPopup.Contains("PanelService.Open(new PanelRequest(") &&
          weatherPopup.Contains("PanelScope.PerOwner"),
        "7.4 the Weather popup keeps its migrated PanelService wiring (regression pin)");

    var viewFiles = new[]
    {
        "src/Widgets/Monitor/Views/SingleMetric.axaml.cs",
        "src/Widgets/Monitor/Views/MultiDashboard.axaml.cs",
        "src/Widgets/Weather/Views/Forecast.axaml.cs",
        "src/Widgets/Weather/Views/FourDayForecast.axaml.cs",
        "src/Widgets/Weather/Views/Temperature.axaml.cs",
        "src/Widgets/Weather/Views/UVIndex.axaml.cs",
        "src/Widgets/Weather/Views/Pressure.axaml.cs",
        "src/Widgets/Weather/Views/SunriseSunset.axaml.cs",
        "src/Widgets/Weather/Views/AirQuality.axaml.cs",
        "src/Widgets/Fixed/Views/AggregateView.axaml.cs",
        "src/Widgets/Hitokoto/Views/HitokotoView.axaml.cs",
        "src/Widgets/Storage/Views/StorageView.axaml.cs",
    };
    Check(viewFiles.All(view => Read(view).Contains("IWidgetSession? session = null")),
        "7.5 all twelve data-widget views declare the optional session constructor parameter");
    Check(viewFiles.All(view => Read(view).Contains("IWidgetActivityConsumer") &&
                                Read(view).Contains("OnSessionActivity")),
        "7.6 all twelve data-widget views implement the activity-consumer forwarding");

    var vmFiles = new[]
    {
        "src/Widgets/Monitor/ViewModels/SingleMetricViewModel.cs",
        "src/Widgets/Monitor/ViewModels/MultiDashboardViewModel.cs",
        "src/Widgets/Weather/ViewModels/ForecastViewModel.cs",
        "src/Widgets/Weather/ViewModels/AirQualityViewModel.cs",
        "src/Widgets/Fixed/ViewModels/AggregateViewModel.cs",
        "src/Widgets/Storage/ViewModels/StorageViewModel.cs",
    };
    Check(vmFiles.All(vm => Read(vm).Contains("IWidgetSession? session = null")),
        "7.7 all six data-widget view models declare the optional session constructor parameter");
    Check(vmFiles.All(vm => Read(vm).Contains("if (session == null) return;")),
        "7.8 all six view models ignore activity pushes on the session-less (legacy) path");

    // Theme residue: no readout may decide darkness straight from the uniform Application
    // variant any more — the host window's variant wins, the Application variant only falls
    // through as the documented uniform fallback inside the resolve seams.
    var widgetDirs = new[] { "Monitor", "Weather", "Fixed", "Hitokoto", "Storage" };
    var residue = new List<string>();
    foreach (var widget in widgetDirs)
    {
        var dir = Path.Combine(repoRoot, "src", "Widgets", widget);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            var source = File.ReadAllText(file);
            if (source.Contains("Application.Current?.ActualThemeVariant == ThemeVariant.Dark"))
                residue.Add(Path.GetRelativePath(repoRoot, file));
        }
    }
    Check(residue.Count == 0,
        residue.Count == 0
            ? "7.9 no readout compares the uniform Application variant to decide darkness any more"
            : $"7.9 uniform-variant dark decisions remain in: {string.Join(", ", residue)}");

    var storageView = Read("src/Widgets/Storage/Views/StorageView.axaml.cs");
    Check(storageView.Contains("VariantProvider = () => ActualThemeVariant") &&
          !storageView.Contains("Application.Current.ActualThemeVariant"),
        "7.10 the Storage view wires the per-screen variant provider (no uniform fallback read left)");
    var fixedVm = Read("src/Widgets/Fixed/ViewModels/AggregateViewModel.cs");
    Check(!fixedVm.Contains("|| Application.Current?.ActualThemeVariant"),
        "7.11 the Fixed today-marker no longer ORs the uniform Application variant into the dark decision");
    var storageVm = Read("src/Widgets/Storage/ViewModels/StorageViewModel.cs");
    Check(storageVm.Contains("ResolveVariant(VariantProvider?.Invoke(), Application.Current?.ActualThemeVariant)"),
        "7.12 the Storage readout resolves through the host-window-first variant seam");
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Data widget session checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

/// <summary>Headless probe application (no host shell — direct construction only).</summary>
sealed class ProbeApp : DeskCanvas.App
{
    public override void OnFrameworkInitializationCompleted() { }
}
