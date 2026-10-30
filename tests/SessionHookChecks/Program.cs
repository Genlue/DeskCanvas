// Session hook checks (P8 batch A, slices A0+A1 — the session registry, the host wiring and
// the digital clock pilot).
//
// Run: dotnet run --project tests/SessionHookChecks
//
// Pins the A0 session plumbing (registry identity, activity recording, teardown release, the
// SharedTimeZone migration) and the A1 pilot: the Digital widget gates its shared-timer
// subscription on the injected session's RenderActive. The host pipeline (WidgetFactory →
// Widget window → registry → content) runs for real on the headless platform, following the
// rig of tests/WidgetReleaseChecks.
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Skia;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Clock.Models;
using Clock.ViewModels;
using Clock.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Microsoft.Extensions.DependencyInjection;

// UseHeadlessDrawing=false gives the probe REAL Win32 window handles (the WidgetReleaseChecks
// precedent) — the host pipeline's native interop (z-order pinning, regions, Alt-Tab removal)
// needs valid HWNDs and would throw Win32Exception on the fake headless ones.
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

// =========================================================================================
// 1. Registry identity: one session per instance, released exactly once
// =========================================================================================
Console.WriteLine("--- WidgetSessionRegistry identity ---");
WidgetSessionRegistry.ResetForChecks();
var idA = WidgetInstanceId.New();
var sA1 = WidgetSessionRegistry.GetOrCreate(idA);
var sA2 = WidgetSessionRegistry.GetOrCreate(idA);
Check(ReferenceEquals(sA1, sA2), "1.1 GetOrCreate returns the same session for the same instance id");
Check(sA1.State == WidgetLifecycleState.Ready, "1.2 GetOrCreate hands out a Ready session");
Check(WidgetSessionRegistry.TryGet(idA) == sA1, "1.3 TryGet finds the registered session");
Check(sA1.InstanceId == idA, "1.4 the session carries the instance id");
Check(WidgetSessionRegistry.Remove(idA), "1.5 Remove reports a registered instance");
Check(!WidgetSessionRegistry.Remove(idA), "1.6 Remove is idempotent (the second call reports false)");
Check(WidgetSessionRegistry.TryGet(idA) == null, "1.7 TryGet misses after the remove");
Check(sA1.State == WidgetLifecycleState.Disposed, "1.8 Remove disposed the session");
var sA3 = WidgetSessionRegistry.GetOrCreate(idA);
Check(!ReferenceEquals(sA1, sA3), "1.9 GetOrCreate creates a fresh session after the remove");
Check(sA3.State == WidgetLifecycleState.Ready, "1.10 the fresh session is Ready again");
Check(!WidgetSessionRegistry.Remove(default), "1.11 Remove on an unknown (empty) id reports false");
var emptyRejected = false;
try { WidgetSessionRegistry.GetOrCreate(default); }
catch (ArgumentException) { emptyRejected = true; }
Check(emptyRejected, "1.12 GetOrCreate rejects an empty instance id");

// =========================================================================================
// 2. Activity semantics through the registry (the host's only activity write path)
// =========================================================================================
Console.WriteLine("--- Session activity through the registry ---");
var idB = WidgetInstanceId.New();
var sB = WidgetSessionRegistry.GetOrCreate(idB);
Check(sB.Activity.RenderActive && sB.Activity.PollingActive, "2.1 a fresh session is visible on the desktop (RenderActive + PollingActive)");
WidgetSessionRegistry.ReportActivity(idB, new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(sB.Activity.Reasons == ActivityReason.ScreenCovered, "2.2 the ScreenCovered report reached the session");
Check(!sB.Activity.RenderActive && !sB.Activity.PollingActive, "2.3 a covered screen neither renders nor polls");
WidgetSessionRegistry.ReportActivity(idB, new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(sB.Activity.RenderActive && sB.Activity.PollingActive, "2.4 the DesktopVisible report restores activity");
WidgetSessionRegistry.ReportActivity(WidgetInstanceId.New(), new ActivitySnapshot(ActivityReason.SessionLocked));
Check(sB.Activity.Reasons == ActivityReason.DesktopVisible, "2.5 a report for an unknown instance is a silent no-op");
WidgetSessionRegistry.ReportActivity(idB, ActivitySnapshot.Preview);
Check(sB.Activity.RenderActive && !sB.Activity.PollingActive, "2.6 the preview snapshot renders without polling");
WidgetSessionRegistry.Remove(idB, new ActivitySnapshot(ActivityReason.DisplayDetached));
Check(sB.Activity.Reasons == ActivityReason.DisplayDetached, "2.7 the final activity was recorded before the dispose");
Check(sB.State == WidgetLifecycleState.Disposed, "2.8 the removal disposed the session after the final report");
var disposedRejected = false;
try { await sB.SetActivityAsync(new ActivitySnapshot()); }
catch (InvalidOperationException) { disposedRejected = true; }
Check(disposedRejected, "2.9 the disposed session rejects further commands");

// =========================================================================================
// 3. SharedTimeZone (Runtime): the single copy behind the digital clock pilot
// =========================================================================================
Console.WriteLine("--- SharedTimeZone ---");
SharedTimeZone.ResetForChecks();
var cst = SharedTimeZone.Resolve("China Standard Time");
Check(cst.Id == "China Standard Time" && cst.BaseUtcOffset == TimeSpan.FromHours(8), "3.1 a known Windows id resolves");
Check(ReferenceEquals(cst, SharedTimeZone.Resolve("China Standard Time")), "3.2 the cache hands back the same TimeZoneInfo instance");
Check(ReferenceEquals(SharedTimeZone.Resolve(null), TimeZoneInfo.Local), "3.3 a null id resolves to the local zone (the widgets' unset timezone)");
Check(ReferenceEquals(SharedTimeZone.Resolve("No/Such/Zone"), TimeZoneInfo.Utc), "3.4 an unknown id falls back to Utc instead of throwing");
if (OperatingSystem.IsWindows())
    Check(SharedTimeZone.Resolve("Asia/Shanghai").BaseUtcOffset == TimeSpan.FromHours(8), "3.5 an IANA id resolves on Windows");
var zones = TimeZoneInfo.GetSystemTimeZones().Select(z => z.Id).Take(60).ToList();
foreach (var zone in zones) SharedTimeZone.Resolve(zone);
var cacheField = (System.Collections.IDictionary)typeof(SharedTimeZone)
    .GetField("cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
Check(cacheField.Count <= 32, $"3.6 the cache stays bounded under {zones.Count} lookups (holds {cacheField.Count})");
Check(SharedTimeZone.Resolve(zones[0]).Id == zones[0], "3.7 resolution stays correct after the cache overflow");

// =========================================================================================
// 4. The digital clock pilot (direct construction): ticking follows RenderActive
// =========================================================================================
Console.WriteLine("--- Digital clock pilot (direct construction) ---");
DigitalClockViewModel VmOf(Digital digital) =>
    (DigitalClockViewModel)typeof(Digital).GetField("viewModel", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(digital)!;

var legacy = new Digital(new ClockModel());
Check(VmOf(legacy).TickSubscriptionCountForChecks == 1, "4.1 a session-less Digital keeps the legacy always-on subscription");
legacy.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(VmOf(legacy).TickSubscriptionCountForChecks == 1, "4.2 a session-less Digital ignores activity pushes (old path)");

var idC = WidgetInstanceId.New();
var sC = WidgetSessionRegistry.GetOrCreate(idC);
var pilot = new Digital(new ClockModel(), sC);
var pilotVm = VmOf(pilot);
Check(pilotVm.TickSubscriptionCountForChecks == 1, "4.3 an injected, visible session starts subscribed");
pilot.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
Check(pilotVm.TickSubscriptionCountForChecks == 0, "4.4 RenderActive=false unsubscribes the shared timer");
pilot.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(pilotVm.TickSubscriptionCountForChecks == 1, "4.5 RenderActive=true re-subscribes");
pilot.OnSessionActivity(ActivitySnapshot.Preview);
Check(pilotVm.TickSubscriptionCountForChecks == 1, "4.6 a preview snapshot keeps the subscription alive");

// Overlapping transitions must not corrupt the subscription state machine.
pilot.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
pilotVm.Stop();
Check(pilotVm.TickSubscriptionCountForChecks == 0, "4.7 covered + stopped stays unsubscribed");
pilot.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(pilotVm.TickSubscriptionCountForChecks == 0, "4.8 a resume push does not resurrect a stopped view model");
pilotVm.Start();
Check(pilotVm.TickSubscriptionCountForChecks == 1, "4.9 Start reconciles the subscription after both transitions");

// A session already covered at construction time starts unsubscribed.
var idD = WidgetInstanceId.New();
var sD = WidgetSessionRegistry.GetOrCreate(idD);
await sD.SetActivityAsync(new ActivitySnapshot(ActivityReason.ScreenCovered));
var covered = new Digital(new ClockModel(), sD);
Check(VmOf(covered).TickSubscriptionCountForChecks == 0, "4.10 a covered session starts unsubscribed");
covered.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
Check(VmOf(covered).TickSubscriptionCountForChecks == 1, "4.11 the covered pilot re-subscribes on the resume push");

// Loaded/Unloaded keep their historic Start/Stop role beside the session gate.
var idE = WidgetInstanceId.New();
var sE = WidgetSessionRegistry.GetOrCreate(idE);
var host = new Window { Width = 200, Height = 120, WindowDecorations = WindowDecorations.None, ShowInTaskbar = false };
var attached = new Digital(new ClockModel(), sE);
host.Content = attached;
host.Show();
Flush();
Check(VmOf(attached).TickSubscriptionCountForChecks == 1, "4.12 the loaded control ticks");
host.Close();
Flush();
Check(VmOf(attached).TickSubscriptionCountForChecks == 0, "4.13 Unloaded stops the subscription (the Stop path)");

// =========================================================================================
// 5. Host pipeline: WidgetFactory creation, activity wiring and teardown (real rig)
// =========================================================================================
Console.WriteLine("--- Host pipeline: WidgetFactory creation & teardown ---");
Widget LiveWindow(WidgetFactory factory)
{
    var field = typeof(WidgetFactory).GetField("activeWidgets", BindingFlags.NonPublic | BindingFlags.Instance)!;
    var dict = (System.Collections.IDictionary)field.GetValue(factory)!;
    foreach (var bucket in dict.Values)
        foreach (Widget widget in (System.Collections.IEnumerable)bucket!)
            return widget;
    throw new InvalidOperationException("no live widget");
}
object? ContentOf(Widget window)
{
    var field = typeof(Widget).GetField("ContentPresenter", BindingFlags.NonPublic | BindingFlags.Instance);
    return field?.GetValue(window) is Avalonia.Controls.Presenters.ContentPresenter presenter
        ? presenter.Content
        : null;
}

var rig = SetupRig();
rig.Factory.CreateFromLayout();
Flush();
var widget = LiveWindow(rig.Factory);
var content = ContentOf(widget);
Check(content is Digital, "5.1 the created content is the Digital pilot");
var session = WidgetSessionRegistry.TryGet(rig.InstanceId)!;
Check(session != null, "5.2 the creation path registered the widget's session");
Check(session!.State == WidgetLifecycleState.Ready, "5.3 the registered session is Ready");
var hostVm = VmOf((Digital)content!);
Check(hostVm.SessionForChecks != null, "5.4a the factory injected the session into the pilot VM");
Check(hostVm.TickSubscriptionCountForChecks == 1, "5.4 the injected pilot is subscribed through the factory path");
widget.SuspendContent();
Flush();
Check(session.Activity.Reasons == ActivityReason.ScreenCovered, "5.5 SuspendContent reported ScreenCovered to the session");
Check(hostVm.TickSubscriptionCountForChecks == 0, "5.6 the pilot stopped ticking while covered (session → content push)");
widget.ResumeContent();
Flush();
Check(session.Activity.Reasons == ActivityReason.DesktopVisible, "5.7 ResumeContent restored DesktopVisible");
Check(hostVm.TickSubscriptionCountForChecks == 1, "5.8 the pilot re-subscribed on resume");
rig.Factory.CloseAll();
Flush();
Check(WidgetSessionRegistry.TryGet(rig.InstanceId) == null, "5.9 the teardown removed the registry entry");
Check(session.State == WidgetLifecycleState.Disposed, "5.10 the teardown disposed the session");
Check(hostVm.TickSubscriptionCountForChecks == 0, "5.11 the pilot is unsubscribed after the teardown");
rig.Factory.CloseAll();
Check(WidgetSessionRegistry.TryGet(rig.InstanceId) == null, "5.12 CloseAll is idempotent for the session registry");

// =========================================================================================
// 6. Wiring source scan (guards the A0 wiring against accidental removal)
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
    var widgetSource = File.ReadAllText(Path.Combine(repoRoot, "src", "DeskCanvas", "Views", "Widget.axaml.cs"));
    Check(widgetSource.Contains("WidgetSessionRegistry.ReportActivity"), "6.1 Widget.axaml.cs forwards lifecycle signals to the session registry");
    Check(widgetSource.Contains("WidgetSessionRegistry.Remove"), "6.2 Widget.axaml.cs releases the registry entry on teardown");
    var factorySource = File.ReadAllText(Path.Combine(repoRoot, "src", "DeskCanvas", "Services", "WidgetFactory.cs"));
    Check(factorySource.Contains("WidgetSessionRegistry.GetOrCreate"), "6.3 WidgetFactory.cs creates the session on the creation path");
    Check(factorySource.Contains("NeedsSession"), "6.4 WidgetFactory.cs probes the session constructor parameter");
    var vmSource = File.ReadAllText(Path.Combine(repoRoot, "src", "Widgets", "ClockCalendar", "Clock", "ViewModels", "DigitalClockViewModel.cs"));
    Check(vmSource.Contains("SharedTimeZone.Resolve"), "6.5 DigitalClockViewModel consumes the shared timezone");
}

// =========================================================================================
Console.WriteLine();
if (failures.Count > 0)
{
    Console.WriteLine($"FAILED {failures.Count} of {checks} checks:");
    foreach (var failure in failures) Console.WriteLine(" - " + failure);
    return 1;
}
Console.WriteLine($"Session hook checks passed: {checks}/{checks}.");
return 0;

// =========================================================================================

Rig SetupRig()
{
    var settings = new AppSettingsProvider();
    var layout = new LayoutProvider();
    var monitor = new DisplayMonitorService(layout);

    // Neutralise Refresh: the probe drives Attached by hand (exactly what RefreshCore would
    // publish), so screens and the Win32 device enumeration can never flip IsTopologyChanging
    // in this rig (same neutralisation as tests/WidgetReleaseChecks).
    typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!
        .SetValue(monitor, true);

    var screenHostWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
    screenHostWindow.Show();
    Flush();
    var screen = screenHostWindow.Screens.All.First();

    // A permanent anchor, like the app's WidgetAnchorWindow.
    monitor.Attach(screenHostWindow);

    settings.Save(new AppSettings(
        new Theme(false, null, 1, false, false, "Inter", Surface: SurfaceStyle.Acrylic),
        [], new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
        new Region("en"), false, null, ActiveProfile: AppSettings.DefaultProfileName));

    var instanceId = WidgetInstanceId.New();
    // A restored widget always carries its stored model settings (production realism): with
    // them, GetModel hands the factory a real ClockModel and the full session-taking
    // constructor is reachable. Without them the ClockModel record (no parameterless
    // constructor) defeats TryCreateDefaultModel and activation falls back to the legacy
    // session-less Digital() constructor — which would make the pipeline checks vacuous.
    var widgetLayouts = new List<WidgetLayout>
    {
        new("Clock", "Digital", 40, 40, 800, 400,
            JsonSerializer.SerializeToElement(new ClockModel()),
            InstanceId: instanceId.Value, Generation: 1),
    };
    var screenConfig = new ScreenLayout("probe-screen", "ProbeScreen|1280x1024", null, null, null, null, widgetLayouts);
    layout.Save(new ScreensLayout([screenConfig]));
    var attached = new AttachedScreen(
        screen,
        new ScreenIdentity("\\\\.\\PROBE1", "Probe Screen", screen.Bounds.Width, screen.Bounds.Height,
            screen.IsPrimary, screen.Scaling, "PROBE", "PROBE0"),
        screenConfig);
    typeof(DisplayMonitorService).GetProperty("Attached")!
        .SetValue(monitor, new List<AttachedScreen> { attached });

    var assemblyProvider = new AssemblyProviderStub();
    var widgetFactory = new WidgetFactory(assemblyProvider, layout, monitor, () => null!, settings);
    var themeService = new ThemeService(settings, new WallpaperThemeService());
    var localeService = new LocaleService(settings);
    var profileService = new ProfileService(settings, layout, themeService, localeService, widgetFactory, monitor);

    var services = new ServiceCollection()
        .AddSingleton<IAppSettingsProvider>(settings)
        .AddSingleton<ILayoutProvider>(layout)
        .AddSingleton<IAssemblyProvider>(assemblyProvider)
        .AddSingleton<WallpaperThemeService>()
        .AddSingleton<WallpaperWatcherService>()
        .AddSingleton<IThemeService>(themeService)
        .AddSingleton<ILocaleService>(localeService)
        .AddSingleton<IGridService<Widget>, GridService>()
        .AddSingleton(monitor)
        .AddSingleton(profileService)
        .AddSingleton(widgetFactory)
        .AddSingleton<IWidgetFactory<Window, UserControl>>(widgetFactory)
        .AddSingleton<Func<Settings>>(() => null!)
        .BuildServiceProvider();
    AssemblyProviderStub.Services = services;

    return new Rig(widgetFactory, instanceId);
}

sealed record Rig(WidgetFactory Factory, WidgetInstanceId InstanceId);

/// <summary>Loads the Clock assembly by name and activates through the rig's DI provider.</summary>
sealed class AssemblyProviderStub : IAssemblyProvider
{
    public static IServiceProvider? Services;

    public System.Linq.ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) =>
        Array.Empty<AssemblyInfo>().ToLookup(info => info.AssemblyName);

    public Assembly LoadAssembly(string assemblyName) => Assembly.Load(assemblyName);

    public void UnloadAssembly(string assemblyName) { }

    public object Activate(Type type, params object[] args)
    {
        // Same three-tier fallback as the real DeskCanvas.Core AssemblyProvider.Activate:
        // DI-aware activation first, then plain reflection, then best-effort constructor
        // matching (args by assignability, then DI, then parameter defaults). Without the
        // later tiers a fresh Digital (whose ClockModel has only a defaulted primary
        // constructor, so TryCreateDefaultModel's Activator.CreateInstance path yields no
        // model) cannot be activated through ActivatorUtilities alone.
        try
        {
            return ActivatorUtilities.CreateInstance(Services!, type, args);
        }
        catch
        {
            try
            {
                return args.Length == 0
                    ? Activator.CreateInstance(type)!
                    : Activator.CreateInstance(type, args)!;
            }
            catch
            {
                foreach (var ctor in type.GetConstructors().OrderByDescending(c => c.GetParameters().Length))
                {
                    var pars = ctor.GetParameters();
                    var values = new object?[pars.Length];
                    var ok = true;
                    for (var i = 0; i < pars.Length; i++)
                    {
                        var pt = pars[i].ParameterType;
                        var fromArgs = args.FirstOrDefault(a => a != null && pt.IsAssignableFrom(a.GetType()));
                        if (fromArgs != null)
                        {
                            values[i] = fromArgs;
                            continue;
                        }
                        var fromDi = Services?.GetService(pt);
                        if (fromDi != null)
                            values[i] = fromDi;
                        else if (pars[i].HasDefaultValue)
                            values[i] = pars[i].DefaultValue;
                        else
                        {
                            ok = false;
                            break;
                        }
                    }
                    if (ok)
                        return ctor.Invoke(values);
                }
                throw;
            }
        }
    }

    public System.Resources.ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
}

sealed class ProbeApp : DeskCanvas.App
{
    public override void OnFrameworkInitializationCompleted() { }
}
