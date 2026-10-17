using System.Diagnostics;
using System.Reflection;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;
using DeskCanvas.Views;
using DeskCanvas.Views.Pages;

// One live "widget" for the gallery to preview: the page builds one real preview control per
// registered type, so this makes PreviewListOf/ProbePreview.Disposals carry real evidence.
[assembly: WidgetInfo(typeof(SettingsReleaseChecks.ProbePreview), title: "ProbePreview")]

namespace SettingsReleaseChecks;

/// <summary>
/// Checks the settings window's release contract from the user-visible defect: the settings page
/// consumes memory (above all the gallery, which instantiates one <b>real</b> widget control per
/// widget type — timers, subscriptions, decoded bitmaps), but closing the window "never gives it
/// back" — memory stays high for as long as the window stays closed.
///
/// <para>
/// Root cause pinned here: hiding a window does <b>not</b> unload its content. The shared
/// settings window only ever hides (it is a DI singleton that can never really close), so the
/// page tree — and with it the gallery's live previews — stayed attached behind the closed
/// window, and the maintenance trim requested on close had nothing to collect. The close path
/// now detaches the current page (running the pages' regular Unloaded contract) and asks for a
/// <see cref="TrimPriority.Immediate"/> trim; ShowAndActivate re-attaches the cached page.
/// </para>
///
/// <para>
/// Two layers:
/// 1. The trimmer policy — a coalesced request inside the cooldown must <b>not</b> trim, an
///    immediate one (what the close path requests) must.
/// 2. The window lifecycle — close detaches the page (subscriptions released, gallery previews
///    disposed, trim requested and actually run); reopen re-attaches the <b>same cached
///    instance</b>, re-subscribes, and rebuilds the previews.
/// </para>
///
/// Usage: dotnet run --project tests/SettingsReleaseChecks -c Release
/// </summary>
internal static class Program
{
    private static int failures;

    /// <summary>Referenced for the whole run: the trimmer's 256 MiB floor needs a realistic process.</summary>
    private static byte[]? ballast;

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {message}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {message}");
            failures++;
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Keep every path the app derives from Const inside the build output: a Widgets folder
        // next to this exe puts Const into portable mode (Const.DataFolder = the exe folder), so
        // this check can never read or write <c>%LocalAppData%\DeskCanvas</c>.
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Widgets"));

        Console.WriteLine("=== Settings release checks ===");
        Console.WriteLine();

        // Headless windowing, but with the real Skia backend and the embedded Inter font the
        // settings XAML lays text out in: the appearance page's theme previews request
        // "avares://Avalonia.Fonts.Inter#Inter", which the pure-headless drawing stub cannot
        // shape. (No window is ever painted on screen — the checks only need the lifecycle.)
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        if (args.Contains("--live-update"))
        {
            CheckLiveUpdate();
            return failures == 0 ? 0 : 1;
        }

        if (args.Contains("--update-only"))
        {
            CheckSuccessfulUpdate();
            return failures == 0 ? 0 : 1;
        }

        CheckTrimmerPriority();
        CheckSettingsWindowRelease();
        CheckProxyAndUpdateDiagnostics();
        CheckSuccessfulUpdate();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL SETTINGS RELEASE CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---- 1. Trimmer policy: closing the window must not wait out the cooldown ----

    private static void CheckTrimmerPriority()
    {
        Console.WriteLine("--- trimmer: coalesced waits out the cooldown, immediate (settings close) runs through it ---");

        // The trimmer only acts above its 256 MiB floor; a bare check process sits below it.
        // Hold a ballast so the guard reads a realistic process — it stays alive through the
        // window checks below, which need the floor for the close-triggered trims.
        ballast = new byte[320 * 1024 * 1024];
        for (var i = 0; i < ballast.Length; i += 4096) ballast[i] = 1;

        var baseline = MemoryTrimmerService.CompletedTrims;
        MemoryTrimmerService.RequestTrim();
        Check(WaitForTrims(baseline + 1, 10_000), "a baseline request trims (process above the floor)");

        // The cooldown is armed now. A coalesced request must NOT start a second trim inside it...
        MemoryTrimmerService.RequestTrim();
        Pump(1200);
        Check(MemoryTrimmerService.CompletedTrims == baseline + 1,
            "a coalesced request inside the cooldown does not trim immediately");

        // ...while the settings window's close request (immediate) must.
        MemoryTrimmerService.RequestTrim(TrimPriority.Immediate);
        Check(WaitForTrims(baseline + 2, 10_000), "an immediate request trims inside the cooldown");
    }

    // ---- 2. Window lifecycle: close releases the page, reopen restores it ----

    private static void CheckSettingsWindowRelease()
    {
        Console.WriteLine("--- settings window: close releases the page, reopen restores it ---");

        var settingsStore = new SettingsStore(DefaultSettings());
        var layoutStore = new LayoutStore();
        var monitor = new DisplayMonitorService(layoutStore);
        var assemblies = new StubAssemblyProvider();
        // The real ProfileService constructor seeds profiles on disk; this check only needs an
        // event pair it never raises.
        var profileService = (ProfileService)RuntimeHelpers.GetUninitializedObject(typeof(ProfileService));
        var updateService = new UpdateService(settingsStore);

        Settings? window = null;
        var widgetFactory = new WidgetFactory(assemblies, layoutStore, monitor, () => window!);
        window = new Settings(settingsStore, assemblies, layoutStore, monitor, widgetFactory, profileService, updateService);
        var viewModel = (SettingsViewModel)window.DataContext!;
        var listBox = window.FindControl<ListBox>("ListBox")!;

        window.Show();
        Dispatcher.UIThread.RunJobs();
        Check(window.IsVisible, "the shared window shows");

        var defaultPage = viewModel.CurrentPage;
        Check(defaultPage != null && defaultPage.IsLoaded, "the window opens on the default page, attached");

        // Establish the no-page-attached floor: one close/reopen cycle with only the default page
        // up. Page-scoped subscribers must all be gone while the window is closed, and the same
        // cached instance must come back.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        var floorSettings = settingsStore.DataChangedSubscribers;
        var floorLayout = layoutStore.DataChangedSubscribers;
        Check(!defaultPage!.IsLoaded, "closing also releases the default page");
        Check(floorLayout == 0, $"no layout subscribers while the window is closed (floor {floorLayout})");

        window.ShowAndActivate();
        Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(viewModel.CurrentPage, defaultPage), "reopen restores the default page instance");

        // --- select the Advanced page: it subscribes to the stores while attached -----------
        var advancedItem = viewModel.AllItems.First(i => i.Type == typeof(Advanced));
        listBox.SelectedItem = advancedItem;
        Dispatcher.UIThread.RunJobs();

        var advancedPage = viewModel.CurrentPage;
        Check(advancedPage is Advanced, "selecting Advanced shows its page");
        Check(advancedPage!.IsLoaded, "the page is attached while the window is open");
        Check(layoutStore.DataChangedSubscribers == floorLayout + 1 && settingsStore.DataChangedSubscribers > floorSettings,
            $"the attached page subscribed to the stores (settings floor {floorSettings} → {settingsStore.DataChangedSubscribers}, layout {floorLayout} → {layoutStore.DataChangedSubscribers})");

        // --- close: hide + release (this used to keep the whole page tree alive) -------------
        var trimsBeforeClose = MemoryTrimmerService.CompletedTrims;
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Check(!window.IsVisible, "close hides the shared window instead of destroying it");
        Check(viewModel.CurrentPage == null, "close detached the current page");
        Check(!advancedPage.IsLoaded, "the detached page's Unloaded contract ran");
        Check(AdvancedViewModelAttached(advancedPage) == false, "the Advanced page view model released its subscriptions");
        Check(settingsStore.DataChangedSubscribers == floorSettings && layoutStore.DataChangedSubscribers == floorLayout,
            $"every page subscriber is gone while the window is closed (settings {settingsStore.DataChangedSubscribers}/{floorSettings}, layout {layoutStore.DataChangedSubscribers}/{floorLayout})");
        Check(viewModel.CurrentPageTitle == advancedItem.Text, "the window remembers the page the user left");
        Check(WaitForTrims(trimsBeforeClose + 1, 8000),
            "closing requested a trim and it ran despite the cooldown armed above");

        // --- reopen: the same cached instance, re-attached and re-subscribed -----------------
        window.ShowAndActivate();
        Dispatcher.UIThread.RunJobs();

        Check(window.IsVisible, "ShowAndActivate shows the shared window again");
        Check(ReferenceEquals(viewModel.CurrentPage, advancedPage), "reopen re-attaches the same cached page instance");
        Check(advancedPage.IsLoaded, "the page is attached again");
        Check(AdvancedViewModelAttached(advancedPage) == true, "the re-attached page re-subscribed (Loaded → Attach)");
        Check(layoutStore.DataChangedSubscribers == floorLayout + 1,
            $"the re-attach subscribed exactly once, no double-subscribe (layout {layoutStore.DataChangedSubscribers})");

        // --- the gallery: the heavy page whose previews are the memory in question -----------
        var galleryItem = viewModel.AllItems.First(i => i.Type == typeof(Gallery));
        listBox.SelectedItem = galleryItem;
        Dispatcher.UIThread.RunJobs();

        var galleryPage = (Gallery)viewModel.CurrentPage!;
        Check(PreviewListOf(galleryPage) is { Count: 1 }, "the gallery built one live preview");
        Check(ProbePreview.Disposals == 0, "no preview is disposed while the page is open");

        trimsBeforeClose = MemoryTrimmerService.CompletedTrims;
        window.Close();
        Dispatcher.UIThread.RunJobs();

        Check(PreviewListOf(galleryPage) == null, "close released the gallery preview list");
        Check(ProbePreview.Disposals == 1, "the live preview was disposed by the release");
        Check(settingsStore.DataChangedSubscribers == floorSettings && layoutStore.DataChangedSubscribers == floorLayout,
            $"the close left no page subscribers (settings {settingsStore.DataChangedSubscribers}/{floorSettings}, layout {layoutStore.DataChangedSubscribers}/{floorLayout})");
        Check(WaitForTrims(trimsBeforeClose + 1, 8000), "each close trims again — the reopened page minted fresh previews");

        window.ShowAndActivate();
        Dispatcher.UIThread.RunJobs();

        Check(ReferenceEquals(viewModel.CurrentPage, galleryPage), "reopen returns to the gallery page");
        Check(PreviewListOf(galleryPage) is { Count: 1 }, "the gallery rebuilt its live previews");
        Check(ProbePreview.Disposals == 1, "the rebuilt preview is a fresh control, not the disposed one");
        Check(settingsStore.DataChangedSubscribers == floorSettings && layoutStore.DataChangedSubscribers == floorLayout,
            $"the reopened gallery adds no subscriptions (settings {settingsStore.DataChangedSubscribers}/{floorSettings}, layout {layoutStore.DataChangedSubscribers}/{floorLayout})");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // ---- 3. Proxy auto-detection, General/Advanced sync, and update status persistence ----

    private static void CheckProxyAndUpdateDiagnostics()
    {
        Console.WriteLine("--- proxy auto-detect, General/Advanced sync & update status persistence ---");

        Check(DeskCanvas.Core.Services.ProxySettings.GetMode(null) == DeskCanvas.Core.Services.ProxyModeKind.Auto &&
              DeskCanvas.Core.Services.ProxySettings.GetMode("") == DeskCanvas.Core.Services.ProxyModeKind.Auto &&
              DeskCanvas.Core.Services.ProxySettings.GetMode("auto") == DeskCanvas.Core.Services.ProxyModeKind.Auto,
            "null, empty and 'auto' all resolve to ProxyModeKind.Auto");
        Check(DeskCanvas.Core.Services.ProxySettings.GetMode("direct") == DeskCanvas.Core.Services.ProxyModeKind.Direct,
            "'direct' resolves to ProxyModeKind.Direct");
        Check(DeskCanvas.Core.Services.ProxySettings.GetMode("system") == DeskCanvas.Core.Services.ProxyModeKind.System,
            "'system' resolves to ProxyModeKind.System");
        Check(DeskCanvas.Core.Services.ProxySettings.GetMode("http://127.0.0.1:7890") == DeskCanvas.Core.Services.ProxyModeKind.Custom,
            "custom URL resolves to ProxyModeKind.Custom");

        // --- proxy persistence mapping: the file must stay parseable by pre-3.7.0 builds ----------
        // ("auto"/"direct" are 3.7.0+ tokens; older builds sharing the file treat any unknown
        // token as a custom proxy URL — WebProxy("auto") — and their update check dies silently)
        var (autoFlag, autoFile) = DeskCanvas.Core.Services.ProxySettings.ToPersistedProxy("auto");
        Check(autoFlag == true && autoFile == "system" &&
              DeskCanvas.Core.Services.ProxySettings.FromPersistedProxy(autoFlag, autoFile) == "auto",
            "auto persists as (true, 'system') and reconstructs to 'auto'");
        Check(DeskCanvas.Core.Services.ProxySettings.ToPersistedProxy("direct") == (false, null) &&
              DeskCanvas.Core.Services.ProxySettings.FromPersistedProxy(false, null) == "direct",
            "direct persists as (false, null) and reconstructs to 'direct' (not auto)");
        Check(DeskCanvas.Core.Services.ProxySettings.ToPersistedProxy("system") == (false, "system") &&
              DeskCanvas.Core.Services.ProxySettings.ToPersistedProxy("http://127.0.0.1:7890") == (false, "http://127.0.0.1:7890"),
            "system and custom URLs persist unchanged");
        Check(DeskCanvas.Core.Services.ProxySettings.FromPersistedProxy(null, "auto") == "auto" &&
              DeskCanvas.Core.Services.ProxySettings.FromPersistedProxy(null, "system") == "system" &&
              DeskCanvas.Core.Services.ProxySettings.FromPersistedProxy(null, "http://127.0.0.1:7890") == "http://127.0.0.1:7890",
            "legacy files (null flag) interpret raw values as-is");

        Check(DeskCanvas.Core.Services.ProxySettings.ParseWindowsProxyServer("127.0.0.1:7890") == "http://127.0.0.1:7890",
            "ParseWindowsProxyServer normalizes bare host:port");
        Check(DeskCanvas.Core.Services.ProxySettings.ParseWindowsProxyServer("http=127.0.0.1:7890;https=127.0.0.1:7897") == "http://127.0.0.1:7897",
            "ParseWindowsProxyServer prefers https= entry in protocol list");

        var settingsStore = new SettingsStore(DefaultSettings());
        var layoutStore = new LayoutStore();
        var monitor = new DisplayMonitorService(layoutStore);
        var updateService = new UpdateService(settingsStore);

        // Update checks persist their result through IAppSettingsProvider.Save. In the real
        // application that raises Avalonia theme/locale/resource callbacks, which must run on
        // the UI thread. Keep this regression probe independent of those production services.
        var updateSaveStayedOnUiThread = true;
        settingsStore.DataChanging += (_, _, _) =>
            updateSaveStayedOnUiThread &= Dispatcher.UIThread.CheckAccess();

        using var generalVm = new GeneralViewModel(settingsStore, updateService);
        using var advancedVm = new AdvancedViewModel(settingsStore, layoutStore, monitor);

        Check(generalVm.AutoDetectProxy, "GeneralViewModel.AutoDetectProxy is enabled by default");
        Check(advancedVm.ProxyMode.Value == "auto", "AdvancedViewModel.ProxyMode defaults to 'auto'");

        generalVm.AutoDetectProxy = false;
        Dispatcher.UIThread.RunJobs();
        Check(settingsStore.Get().HttpProxy == "direct", "toggling AutoDetectProxy off saves 'direct'");
        Check(advancedVm.ProxyMode.Value == "direct", "AdvancedViewModel.ProxyMode reflects 'direct'");

        advancedVm.ProxyMode = advancedVm.ProxyOptions.First(o => o.Value == "auto");
        Dispatcher.UIThread.RunJobs();
        Check(generalVm.AutoDetectProxy, "switching AdvancedViewModel.ProxyMode back to 'auto' updates GeneralViewModel.AutoDetectProxy");

        // Test failed update check persistence (unreachable local port in direct mode)
        settingsStore.Save(settingsStore.Get() with
        {
            HttpProxy = "direct",
            UpdateUrl = "http://127.0.0.1:1/repos/test/test/releases/latest"
        });
        var (failInfo, failResult) = RunAsyncOnUiThread(
            () => updateService.CheckForUpdatesDetailedAsync(isManual: false));
        Check(failInfo == null && failResult == UpdateCheckResult.Failed, "unreachable endpoint returns UpdateCheckResult.Failed");
        Check(settingsStore.Get().LastUpdateCheckTime != null &&
              !string.IsNullOrWhiteSpace(settingsStore.Get().LastUpdateCheckStatus),
            $"failed automatic check records timestamp and failure reason ({settingsStore.Get().LastUpdateCheckStatus})");
        Check(updateSaveStayedOnUiThread,
            "update check persists its outcome on the UI thread");

        var connFail = RunAsyncOnUiThread(() => updateService.TestGitHubConnectivityAsync());
        Check(!connFail.Success && !string.IsNullOrWhiteSpace(connFail.Summary),
            $"connectivity test against unreachable endpoint reports failure ({connFail.Summary})");
    }

    // ---- rig ----

    private static void CheckSuccessfulUpdate()
    {
        Console.WriteLine("--- HTTP 200 update: release parsing, UI-thread save and version result ---");
        using var endpoint = new ReleaseEndpoint();
        var settings = new SettingsStore(DefaultSettings() with
        {
            HttpProxy = "direct",
            UpdateUrl = endpoint.Url
        });
        // ThemeService's DataChanging subscriber writes Avalonia properties synchronously.
        // Reproduce that contract so a successful response cannot hide an off-thread save.
        settings.DataChanging += (_, _, _) => Dispatcher.UIThread.VerifyAccess();
        var service = new UpdateService(settings);

        try
        {
            var connectivity = RunAsyncOnUiThread(() => service.TestGitHubConnectivityAsync());
            Check(connectivity.Success && connectivity.StatusCode == 200, "HTTP 200 connectivity probe succeeds");
            var (info, status) = RunAsyncOnUiThread(
                () => Task.Run(() => service.CheckForUpdatesDetailedAsync(isManual: true)));
            Check(status == UpdateCheckResult.UpdateAvailable && info?.Version == new Version(99, 0, 0),
                "the same endpoint returns UpdateAvailable after the asynchronous HTTP response");
            Check(info?.AssetFileName == "DeskCanvas-win-x64.msi", "release metadata and installer asset are parsed");
            Check(settings.Get().LastUpdateCheckTime != null &&
                  settings.Get().LastUpdateCheckStatus?.Contains("99.0.0") == true,
                "the successful update result is saved through UI-thread settings callbacks");
        }
        catch (Exception ex)
        {
            Check(false, $"successful update must not throw during the settings save: {ex.Message}");
        }
    }

    private static void CheckLiveUpdate()
    {
        var settings = new SettingsStore(DefaultSettings());
        settings.DataChanging += (_, _, _) => Dispatcher.UIThread.VerifyAccess();
        var service = new UpdateService(settings);
        var connectivity = RunAsyncOnUiThread(() => service.TestGitHubConnectivityAsync());
        Console.WriteLine($"Live GitHub connectivity: {connectivity.Summary}");
        var (info, status) = RunAsyncOnUiThread(() => service.CheckForUpdatesDetailedAsync(isManual: true));
        Console.WriteLine($"Live update: {status}, tag={info?.TagName}, asset={info?.AssetFileName}");
        Console.WriteLine(settings.Get().LastUpdateCheckStatus);
        Check(info != null && status is UpdateCheckResult.UpToDate or UpdateCheckResult.UpdateAvailable,
            "live GitHub release check completes with UI-thread settings callbacks");
    }

    /// <summary>A loopback release endpoint; no external network, fixed ports or user settings.</summary>
    private sealed class ReleaseEndpoint : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource cancellation = new();
        private readonly Task server;
        public string Url { get; }

        public ReleaseEndpoint()
        {
            listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/releases/latest";
            server = Task.Run(ServeAsync);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, leaveOpen: true);
                    while (await reader.ReadLineAsync(cancellation.Token) is { Length: > 0 }) { }
                    // Guarantee an asynchronous continuation even on a fast loopback connection.
                    await Task.Delay(30, cancellation.Token);
                    var body = Encoding.UTF8.GetBytes("""
                        {"tag_name":"v99.0.0","name":"DeskCanvas 99.0.0","body":"Update check fixture",
                         "html_url":"https://github.com/test/repo/releases/tag/v99.0.0",
                         "assets":[{"name":"DeskCanvas-win-x64.msi","size":123,
                         "browser_download_url":"https://github.com/test/repo/releases/download/v99.0.0/DeskCanvas-win-x64.msi"}]}
                        """);
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cancellation.Token);
                    await stream.WriteAsync(body, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }

        public void Dispose()
        {
            cancellation.Cancel();
            listener.Stop();
            server.GetAwaiter().GetResult();
            cancellation.Dispose();
        }
    }

    /// <summary>The Advanced view model's <c>attached</c> flag (true while it holds its subscriptions).</summary>
    private static bool? AdvancedViewModelAttached(Control page) =>
        (bool?)typeof(AdvancedViewModel)
            .GetField("attached", BindingFlags.NonPublic | BindingFlags.Instance)?
            .GetValue(page.DataContext);

    /// <summary>The gallery's live preview list (null once the page released it).</summary>
    private static List<WidgetPreviewViewModel>? PreviewListOf(Gallery gallery) =>
        (List<WidgetPreviewViewModel>?)typeof(Gallery)
            .GetField("widgets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(gallery);

    /// <summary>Run the dispatcher queue over a wall-clock span without blocking it.</summary>
    private static void Pump(int milliseconds)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < milliseconds)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    private static bool WaitForTrims(int target, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (MemoryTrimmerService.CompletedTrims >= target) return true;
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(25);
        }
        return MemoryTrimmerService.CompletedTrims >= target;
    }

    /// <summary>
    /// Await a UI-context operation while keeping the headless dispatcher pumping. Production
    /// callers await normally; this adapter is only needed because this check suite's entry
    /// point is intentionally synchronous and STA-bound.
    /// </summary>
    private static T RunAsyncOnUiThread<T>(Func<Task<T>> operation)
    {
        var task = operation();
        var timeout = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(75))
                throw new TimeoutException("The UI-context update check did not complete within 75 seconds.");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        return task.GetAwaiter().GetResult();
    }

    private static AppSettings DefaultSettings() => new(
        new Theme(true, null, 1, false, false, "Segoe UI", Surface: SurfaceStyle.Acrylic), [],
        new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
        new Region("en"), false, null);

    /// <summary>In-memory settings store counting its DataChanged subscribers — the evidence that
    /// a closed page released its subscriptions. Never touches appSettings.json.</summary>
    private sealed class SettingsStore(AppSettings initial) : IAppSettingsProvider
    {
        private DataChangedEvent<AppSettings>? dataChangingHandlers;
        private DataChangedEvent<AppSettings>? dataChangedHandlers;

        public int DataChangedSubscribers => dataChangedHandlers?.GetInvocationList().Length ?? 0;

        public event DataChangedEvent<AppSettings> DataChanging
        {
            add => dataChangingHandlers += value;
            remove => dataChangingHandlers -= value;
        }

        public event DataChangedEvent<AppSettings> DataChanged
        {
            add => dataChangedHandlers += value;
            remove => dataChangedHandlers -= value;
        }

        public AppSettings Get() => initial;

        public void Save(AppSettings value)
        {
            var old = initial;
            dataChangingHandlers?.Invoke(this, old, value);
            initial = value;
            dataChangedHandlers?.Invoke(this, old, value);
        }
    }

    /// <summary>In-memory layout store counting its DataChanged subscribers.</summary>
    private sealed class LayoutStore : ILayoutProvider
    {
        private ScreensLayout value = new([]);
        private DataChangedEvent<ScreensLayout>? dataChangingHandlers;
        private DataChangedEvent<ScreensLayout>? dataChangedHandlers;

        public int DataChangedSubscribers => dataChangedHandlers?.GetInvocationList().Length ?? 0;

        public event DataChangedEvent<ScreensLayout> DataChanging
        {
            add => dataChangingHandlers += value;
            remove => dataChangingHandlers -= value;
        }

        public event DataChangedEvent<ScreensLayout> DataChanged
        {
            add => dataChangedHandlers += value;
            remove => dataChangedHandlers -= value;
        }

        public ScreensLayout Get() => value;

        public void Save(ScreensLayout next)
        {
            var old = value;
            dataChangingHandlers?.Invoke(this, old, next);
            value = next;
            dataChangedHandlers?.Invoke(this, old, next);
        }
    }

    /// <summary>
    /// Loads exactly one widget: this check assembly itself, which carries a
    /// <see cref="WidgetInfoAttribute"/> for <see cref="ProbePreview"/>. The gallery therefore
    /// builds one real preview control whose disposal it can observe.
    /// </summary>
    private sealed class StubAssemblyProvider : IAssemblyProvider
    {
        private static readonly string AssemblyName = typeof(Program).Assembly.GetName().Name!;

        public ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) =>
            new[] { new AssemblyInfo("probe.dll", AssemblyName, "Probe widgets", "check", new Version(1, 0, 0, 0), "") }
                .ToLookup(info => info.AssemblyName);

        public Assembly LoadAssembly(string assemblyName) => typeof(ProbePreview).Assembly;

        public void UnloadAssembly(string assemblyName) { }

        public object Activate(Type type, params object[] args) =>
            args.Length == 0 ? Activator.CreateInstance(type)! : Activator.CreateInstance(type, args)!;

        public ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
    }
}

/// <summary>
/// A stand-in for a live widget preview. The gallery owns one per registered widget type while
/// the page is attached; the count of disposed instances is this check's evidence that closing
/// the window ran the release contract rather than merely hiding the tree.
/// </summary>
public sealed class ProbePreview : UserControl, IDisposable
{
    /// <summary>Disposals across every instance (the gallery rebuilds them per visit).</summary>
    public static int Disposals;

    public void Dispose() => Interlocked.Increment(ref Disposals);
}
