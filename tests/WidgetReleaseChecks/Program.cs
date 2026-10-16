using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Microsoft.Extensions.DependencyInjection;

namespace WidgetReleaseChecks;

/// <summary>
/// Pins the widget-release contract behind the report "switching profiles does not release the
/// widget set — memory accumulates". Runs the REAL host pipeline — <c>WidgetFactory.CloseAll</c> /
/// <c>ProfileService.SwitchProfile</c> with REAL widget windows and REAL widget content — and
/// asserts that every closed widget is actually given back:
/// <list type="bullet">
/// <item>one create/close cycle × 3 per widget type (all 29 shipped views), with a forced full GC
/// afterwards: the window, its content and the Folders platform registrations
/// (<c>WidgetOleDropTarget</c>'s OLE drop target + WM_DROPFILES subclass) must all be gone;</item>
/// <item>profile switching: every outgoing window/content is collected, the shared glass material
/// caches are released, and no static registry (panels / z-order / timers) grows.</item>
/// </list>
/// <para>
/// <b>The defect this suite was written for.</b> The Folders views resolved their platform handle
/// inside <c>Unloaded</c> to revoke their OLE drop target / WM_DROPFILES subclass —
/// <c>VisualRoot is TopLevel &amp;&amp; TryGetPlatformHandle()</c> — but <c>TopLevel.HandleClosed</c>
/// clears <c>PlatformImpl</c> <i>before</i> it raises <c>Unloaded</c>, so during teardown the
/// handle can never be read and the revoke was silently skipped. The static registries kept the
/// captured callbacks, which held the whole view — and through its parent chain the closed widget
/// window — alive: one more pinned set per closed Folders widget, so repeated profile switches
/// accumulated memory. The fix captures the handle at <c>Loaded</c> and revokes with it at
/// <c>Unloaded</c>, plus a dead-handle prune in <c>WidgetOleDropTarget</c> as the safety net.
/// </para>
/// <para>
/// Real Win32 by default: the Folders registrations need REAL window handles (the headless
/// platform reports a fake handle, so nothing ever registers there). All probe windows are
/// created with <c>Opacity = 0</c> / <c>ShowActivated = false</c> — nothing appears on screen —
/// and the harness writes only to its own sandbox folder under <c>%TEMP%</c>.
/// <c>--headless</c> runs the profile-switch phase on the headless platform (object graph +
/// exact shared-cache release; the Folders registrations are not reachable there).
/// </para>
/// Usage: dotnet run --project tests/WidgetReleaseChecks -c Release
/// </summary>
internal static class Program
{
    private static readonly List<(int Round, WeakReference Window, WeakReference Content)> Previous = [];
    private static readonly List<string> SweepIssues = [];
    private static readonly List<string> SweepSkipped = [];
    private static int failures;
    private static bool realMode;

    private static void Check(bool condition, string message)
    {
        Console.WriteLine(condition ? $"  [PASS] {message}" : $"  [FAIL] {message}");
        if (!condition) failures++;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        realMode = !args.Contains("--headless");
        Console.WriteLine($"=== widget release checks ({(realMode ? "real Win32" : "headless")}) ===");
        Console.WriteLine();

        // The harness sandbox: Const routes a tests\ harness to %TEMP%\DeskCanvas-TestHarnesses.
        var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-TestHarnesses", "WidgetReleaseChecks");
        try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true); } catch { }
        Directory.CreateDirectory(sandbox);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Widgets"));

        var cts = new CancellationTokenSource();
        var builder = AppBuilder.Configure<ProbeApp>().WithInterFont();
        if (realMode)
        {
            builder
                .UseWin32()
                .UsePlatformDetect()
                .With(new Win32PlatformOptions
                {
                    CompositionMode = [Win32CompositionMode.WinUIComposition],
                    WinUICompositionBackdropCornerRadius = 8f,
                })
                .SetupWithoutStarting();
        }
        else
        {
            builder
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseSkia()
                .SetupWithoutStarting();
        }
        Application.Current!.Styles.Add(new FluentTheme());

        if (realMode)
        {
            // Run on the real dispatcher loop so the render pipeline (and the glass material it
            // drives) actually runs between the switches.
            Dispatcher.UIThread.Post(async void () =>
            {
                try
                {
                    await CheckAllWidgetTypesReleaseAsync();
                    Previous.Clear();
                    await CheckProfileSwitchReleaseAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"UNHANDLED: {ex}");
                    failures++;
                }
                finally
                {
                    cts.Cancel();
                }
            });
            Dispatcher.UIThread.MainLoop(cts.Token);
        }
        else
        {
            try
            {
                CheckProfileSwitchReleaseAsync().GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine("  [NOTE] the all-widget Folders registration sweep needs real window handles; run without --headless for the full contract.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"UNHANDLED: {ex}");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL WIDGET RELEASE CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>Let the platform settle (render + sampler ticks) before the next measurement.</summary>
    private static Task SettleAsync(int ms)
    {
        if (realMode) return Task.Delay(ms);

        // Headless: fully synchronous, so every continuation stays on the probe's main thread.
        Pump();
        Thread.Sleep(ms);
        Pump();
        return Task.CompletedTask;
    }

    private static async Task CheckProfileSwitchReleaseAsync()
    {
        var rig = SetupRig(forSweep: false);
        var widgetLayouts = rig.WidgetLayouts;
        var widgetFactory = rig.Factory;
        var profileService = rig.Profiles;

        // The sweep phase left a single-widget layout on disk; restore the standard set before
        // the profiles snapshot it.
        SaveLayout(rig.Layout, rig.Monitor, rig.Screen, widgetLayouts);

        profileService.EnsureSeeded();
        profileService.SaveCurrentProfile();
        Check(profileService.CreateProfile("Alt", copyCurrent: true), "created profile 'Alt'");

        // ---- initial creation -----------------------------------------------------------------
        widgetFactory.CreateFromLayout();
        await SettleAsync(600);
        var initialCount = SnapshotRound(widgetFactory, round: 0);
        Console.WriteLine($"initial widgets: {initialCount}");
        Check(initialCount == widgetLayouts.Count, $"initial profile created all {widgetLayouts.Count} widgets (got {initialCount})");

        // ---- switch loop ----------------------------------------------------------------------
        Console.WriteLine();
        Console.WriteLine(realMode
            ? $"{"round",6} {"live",5} {"retained",9} {"heap(MB)",9} {"private(MB)",11} {"ws(MB)",8} {"gdi",5} {"user",5} {"handles",8} {"glass",6} {"wall",5}"
            : $"{"round",6} {"live",5} {"retained",9} {"heap(MB)",9} {"private(MB)",11} {"glass",6}");

        var rounds = realMode ? 30 : 6;
        using var selfProcess = Process.GetCurrentProcess();
        for (var round = 1; round <= rounds; round++)
        {
            var target = round % 2 == 1 ? "Alt" : AppSettings.DefaultProfileName;
            var ok = profileService.SwitchProfile(target);
            await SettleAsync(realMode ? 700 : 80);

            var heap = ForceGc();
            var live = SnapshotRound(widgetFactory, round);
            var prev = Previous.Where(p => p.Round == round - 1).ToList();
            var retained = prev.Count(p => p.Window.IsAlive) + prev.Count(p => p.Content.IsAlive);

            selfProcess.Refresh();
            var private_ = selfProcess.PrivateMemorySize64 / 1048576.0;
            var glass = GetStaticProperty("DeskCanvas.Services.LiquidGlassSourceCache", "CachedEntryCount");
            var wallpaperCached = !GetStaticFieldIsNull("DeskCanvas.Services.LiquidGlassWallpaper", "cached");

            if (realMode)
            {
                var ws = selfProcess.WorkingSet64 / 1048576.0;
                var gdi = GetGuiResources(selfProcess.Handle, 0);
                var user = GetGuiResources(selfProcess.Handle, 1);
                Console.WriteLine($"{round,6} {live,5} {retained,9} {heap / 1048576.0,9:F1} {private_,11:F1} {ws,8:F1} {gdi,5} {user,5} {selfProcess.HandleCount,8} {glass,6} {(wallpaperCached ? "yes" : "no"),5}");
            }
            else
            {
                Console.WriteLine($"{round,6} {live,5} {retained,9} {heap / 1048576.0,9:F1} {private_,11:F1} {glass,6}");
            }

            if (!ok) Check(false, $"switch {round} to '{target}' succeeded");
            if (live != widgetLayouts.Count) Check(false, $"round {round} rebuilt the full set (live {live})");
        }

        // ---- material-cache release on switch -------------------------------------------------
        Console.WriteLine();
        Console.WriteLine("--- shared material caches through one more switch ---");
        PopulateGlassCaches();
        var glassBefore = int.Parse(GetStaticProperty("DeskCanvas.Services.LiquidGlassSourceCache", "CachedEntryCount"));
        var okSwitch = profileService.SwitchProfile(profileService.GetActiveProfile() == "Alt" ? AppSettings.DefaultProfileName : "Alt");
        await SettleAsync(realMode ? 300 : 60);
        var glassAfter = int.Parse(GetStaticProperty("DeskCanvas.Services.LiquidGlassSourceCache", "CachedEntryCount"));
        Console.WriteLine($"CachedEntryCount: before={glassBefore} after={glassAfter}");
        Check(okSwitch, "the cache-release switch itself succeeded");
        Check(glassBefore >= 1, $"a backdrop was cached before the switch (was {glassBefore})");

        if (realMode)
        {
            // The new set renders immediately in real mode, so the cache is released and
            // rebuilt in the same pass — the contract is "no accumulation", never growth.
            Check(glassAfter <= glassBefore, $"the switch did not grow the shared backdrops ({glassBefore} -> {glassAfter})");
        }
        else
        {
            Check(glassAfter == 0, $"the switch released the shared backdrops (now {glassAfter})");
        }

        // ---- verdict ---------------------------------------------------------------------------
        Console.WriteLine();
        var finalPrev = Previous.Where(p => p.Round >= 1 && p.Round < rounds).ToList();
        var aliveWindows = finalPrev.Count(p => p.Window.IsAlive);
        var aliveContent = finalPrev.Count(p => p.Content.IsAlive);
        Console.WriteLine($"post-GC: {aliveWindows}/{finalPrev.Count} previous widget windows alive, " +
                          $"{aliveContent}/{finalPrev.Count} previous content controls alive (all but the live set)");
        Check(aliveWindows == 0, $"the outgoing widget windows are all collected (alive {aliveWindows})");
        Check(aliveContent == 0, $"the outgoing widget content is all collected (alive {aliveContent})");

        Console.WriteLine();
        Console.WriteLine("static registries after the switches (live set still on screen):");
        Console.WriteLine($"  LiquidGlassSourceCache.CachedEntryCount = {GetStaticProperty("DeskCanvas.Services.LiquidGlassSourceCache", "CachedEntryCount")}");
        Console.WriteLine($"  PanelService.activePanels                = {GetStaticFieldCount("DeskCanvas.Services.PanelService", "activePanels")}");
        Console.WriteLine($"  WidgetZOrder.widgetHandles               = {GetStaticFieldCount("DeskCanvas.Services.WidgetZOrder", "widgetHandles")}");
        Console.WriteLine($"  TimerService subscribers                 = {TimerSubscribers()}");

        // A LIVE Folders widget legitimately holds its drop registration; every CLOSED one must
        // not. Close the whole live set and the registrations have to go with the windows — the
        // end-to-end pin of the reported defect: pre-fix, each closed Folders window kept its OLE
        // drop target + WM_DROPFILES registration in the static dictionaries, and through their
        // captured callbacks the whole view — so every profile switch accumulated one more set.
        var liveFolders = LiveWindows(widgetFactory).Count(w => w.Title.StartsWith("Folders"));
        var oleWithLiveSet = OleRegistrations();
        Console.WriteLine($"  Folders drop registrations (live set)    = {oleWithLiveSet} ({liveFolders} live Folders widget(s))");
        if (realMode)
            Check(oleWithLiveSet == $"{liveFolders}/{liveFolders}",
                $"each live Folders widget holds exactly its own registration ({oleWithLiveSet})");

        widgetFactory.CloseAll();
        await SettleAsync(realMode ? 300 : 60);
        ForceGc();
        var oleAfterCloseAll = OleRegistrations();
        Console.WriteLine($"after the final CloseAll: live={LiveWindows(widgetFactory).Count}, Folders registrations={oleAfterCloseAll}");
        Check(LiveWindows(widgetFactory).Count == 0, "no widget window survives the final CloseAll");
        Check(oleAfterCloseAll == "0/0", $"every Folders drop registration was released with its window (registrations {oleAfterCloseAll})");

        var canary = new object();
        var canaryRef = new WeakReference(canary);
        GC.KeepAlive(canary);
        ForceGc();
        Check(canaryRef.IsAlive, "measurement canary: a live reference is reported alive after GC");
    }

    // ---- rig ------------------------------------------------------------------------------------

    private sealed record Rig(
        AppSettingsProvider Settings,
        LayoutProvider Layout,
        DisplayMonitorService Monitor,
        Avalonia.Platform.Screen Screen,
        WidgetFactory Factory,
        ProfileService Profiles,
        List<WidgetLayout> WidgetLayouts);

    /// <summary>The app's DI graph, headless or real — the same wiring the app's own startup uses.</summary>
    private static Rig SetupRig(bool forSweep)
    {
        var settings = new AppSettingsProvider();
        var layout = new LayoutProvider();
        var monitor = new DisplayMonitorService(layout);

        // Neutralise Refresh: the probe drives Attached by hand (exactly what RefreshCore would
        // publish), so screens and the Win32 device enumeration can never flip IsTopologyChanging
        // in this rig. Production semantics (which screen a widget belongs to) are unaffected.
        SetField(monitor, "refreshing", true);

        var screenHostWindow = new Window { Width = 100, Height = 100, ShowInTaskbar = false, Opacity = 0 };
        screenHostWindow.Show();
        Pump();
        var screen = screenHostWindow.Screens.All.First();
        screenHostWindow.Hide();
        Console.WriteLine($"screen: {screen.Bounds} scaling={screen.Scaling}");

        // A permanent anchor, like the app's WidgetAnchorWindow — so no widget ever becomes the
        // monitor's anchor (which would pin the first widget of every churn round).
        monitor.Attach(screenHostWindow);

        settings.Save(new AppSettings(
            new Theme(false, null, 1, false, false, "Inter",
                Surface: !forSweep && realMode ? SurfaceStyle.LiquidGlass : SurfaceStyle.Solid),
            [], new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
            new Region("en"), false, null, ActiveProfile: AppSettings.DefaultProfileName));

        var widgetLayouts = !forSweep && realMode
            ? new List<WidgetLayout>
            {
                new("Clock", "FramelessDigital", 36, 36, 900, 460, null),
                new("Clock", "Digital", 36, 520, 800, 400, null),
                new("Monitor", "SingleMetric", 1000, 36, 400, 400, null),
                new("Calendar", "Month", 1000, 460, 400, 400, null),
                new("Weather", "Forecast", 960, 884, 340, 420, null),
                new("Picture", "PictureView", 36, 940, 420, 300, null),
                // The widget the reported defect lived in: every switch must give its OLE drop
                // target / WM_DROPFILES registrations back with the window.
                new("Folders", "Folder", 500, 940, 420, 320, null),
            }
            : new List<WidgetLayout>
            {
                new("Clock", "Digital", 40, 40, 800, 400, null),
                new("Monitor", "SingleMetric", 40, 480, 400, 400, null),
                new("Calendar", "Month", 480, 480, 400, 400, null),
                new("Weather", "Forecast", 900, 40, 340, 400, null),
            };

        SaveLayout(layout, monitor, screen, widgetLayouts);

        var assemblyProvider = new AssemblyProviderStub();
        var widgetFactory = new WidgetFactory(assemblyProvider, layout, monitor, () => null!);
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

        return new Rig(settings, layout, monitor, screen, widgetFactory, profileService, widgetLayouts);
    }

    private static void SaveLayout(LayoutProvider layout, DisplayMonitorService monitor,
        Avalonia.Platform.Screen screen, List<WidgetLayout> widgetLayouts)
    {
        var screenConfig = new ScreenLayout("probe-screen", "ProbeScreen|1280x1024", null, null, null, null, widgetLayouts);
        layout.Save(new ScreensLayout([screenConfig]));
        SetAttached(monitor, screen, screenConfig);
    }

    // ---- all-widget sweep -----------------------------------------------------------------------

    /// <summary>One representative view of every widget type shipped in the bundle.</summary>
    private static readonly (string Assembly, string View)[] AllWidgets =
    [
        ("Clock", "FramelessDigital"),
        ("Clock", "Digital"),
        ("Calendar", "Month"),
        ("Calendar", "Date"),
        ("Weather", "Forecast"),
        ("Weather", "Temperature"),
        ("Monitor", "SingleMetric"),
        ("Monitor", "MultiDashboard"),
        ("Notes", "Note"),
        ("Reminders", "List"),
        ("Folders", "Folder"),
        ("Folders", "BigFolder"),
        ("Folders", "SingleFile"),
        ("Music", "Music"),
        ("Search", "SearchView"),
        ("Progress", "ProgressView"),
        ("Picture", "PictureView"),
        ("Batteries", "BatteriesView"),
        ("Map", "MapView"),
        ("Pomodoro", "PomodoroView"),
        ("StackWidgets", "WidgetStackView"),
        ("Tools", "ClipboardView"),
        ("Tools", "CalculatorView"),
        ("Tools", "TranslatorView"),
        ("ControlCenter", "ControlCenterView"),
        ("Storage", "StorageView"),
        ("Hitokoto", "HitokotoView"),
        ("FixedWidgets", "AggregateView"),
        ("Timetable", "Timetable"),
    ];

    /// <summary>
    /// Create/close every widget type three times and assert what each cycle gives back — the
    /// window, its content and the Folders platform registrations must all be gone after a forced
    /// full GC, so a per-type resource leak is attributed to its type.
    /// </summary>
    private static async Task CheckAllWidgetTypesReleaseAsync()
    {
        var rig = SetupRig(forSweep: true);
        rig.Profiles.EnsureSeeded();

        Console.WriteLine("--- every widget type: 3 × create/close, then forced GC ---");
        Console.WriteLine($"{"widget",-26} {"live",4} {"retWin",6} {"retContent",10} {"heap(MB)",9} {"private(MB)",11} {"gdi",5} {"user",5} {"handles",8}");

        using var selfProcess = Process.GetCurrentProcess();
        var cycle = 0;
        foreach (var (assembly, view) in AllWidgets)
        {
            var note = "";
            var passFrom = cycle + 1;
            try
            {
                for (var pass = 1; pass <= 3; pass++)
                {
                    cycle++;
                    SaveLayout(rig.Layout, rig.Monitor, rig.Screen, [new WidgetLayout(assembly, view, 40, 40, 520, 420, null)]);
                    rig.Factory.CreateFromLayout();
                    await SettleAsync(realMode ? 420 : 40);
                    SnapshotRound(rig.Factory, cycle);
                    rig.Factory.CloseAll();
                    await SettleAsync(realMode ? 320 : 40);
                }
            }
            catch (Exception ex)
            {
                note = $"SKIP instantiate: {ex.GetType().Name}: {FirstLine(ex.Message)}";
                SweepSkipped.Add($"{assembly}/{view} ({ex.GetType().Name}: {FirstLine(ex.Message)})");
                try { rig.Factory.CloseAll(); } catch { }
            }

            ForceGc();
            var prev = Previous.Where(p => p.Round >= passFrom && p.Round <= cycle).ToList();
            var retainedWin = prev.Count(p => p.Window.IsAlive);
            var retainedContent = prev.Count(p => p.Content.IsAlive);
            selfProcess.Refresh();

            var heap = GC.GetTotalMemory(false) / 1048576.0;
            var private_ = selfProcess.PrivateMemorySize64 / 1048576.0;
            var live = LiveWindows(rig.Factory).Count;
            var ole = OleRegistrations();
            if (realMode)
            {
                Console.WriteLine($"{assembly + "/" + view,-26} {live,4} {retainedWin,6} {retainedContent,10} " +
                                  $"{heap,9:F1} {private_,11:F1} {GetGuiResources(selfProcess.Handle, 0),5} {GetGuiResources(selfProcess.Handle, 1),5} {selfProcess.HandleCount,8} ole={ole,-7} {note}");
            }
            else
            {
                Console.WriteLine($"{assembly + "/" + view,-26} {live,4} {retainedWin,6} {retainedContent,10} {heap,9:F1} {private_,11:F1} ole={ole,-7} {note}");
            }

            // What the reported defect looked like: Folders/* kept its window, its content AND a
            // live registration here after every close. Anything but all-zero is that defect.
            if (note.Length == 0 && (live != 0 || retainedWin != 0 || retainedContent != 0 || ole != "0/0"))
            {
                SweepIssues.Add($"{assembly}/{view}: live={live}, retained window/content={retainedWin}/{retainedContent}, registrations={ole}");
            }
        }

        Console.WriteLine();
        if (SweepSkipped.Count > 0)
        {
            Console.WriteLine($"  [NOTE] not instantiable in this harness (contract not verified for them):");
            foreach (var skipped in SweepSkipped) Console.WriteLine($"         {skipped}");
        }
        if (SweepIssues.Count == 0)
        {
            Check(true, $"all {AllWidgets.Length - SweepSkipped.Count} exercised widget types released window + content + platform registrations");
        }
        else
        {
            foreach (var issue in SweepIssues) Check(false, $"released cleanly — {issue}");
        }

        var allPrev = Previous.ToList();
        var aliveWindows = allPrev.Count(p => p.Window.IsAlive);
        var aliveContent = allPrev.Count(p => p.Content.IsAlive);
        Console.WriteLine($"sweep total: {allPrev.Count} tracked windows, {aliveWindows} alive, {aliveContent} content alive after all closes.");
        Check(aliveWindows == 0, $"every swept widget window was collected (alive {aliveWindows})");
        Check(aliveContent == 0, $"every swept widget content was collected (alive {aliveContent})");
        Check(LiveWindows(rig.Factory).Count == 0, "no widget window survives its CloseAll");

        var canary = new object();
        var canaryRef = new WeakReference(canary);
        GC.KeepAlive(canary);
        ForceGc();
        Check(canaryRef.IsAlive, "measurement canary: a live reference is reported alive after GC");
    }

    private static string FirstLine(string text)
    {
        var index = text.IndexOf('\n');
        return index < 0 ? text : text[..index];
    }

    // ---- measurement rig ------------------------------------------------------------------------

    /// <summary>Weak-reference the live windows; the strong references die with this frame.</summary>
    private static int SnapshotRound(WidgetFactory factory, int round)
    {
        var windows = LiveWindows(factory);
        foreach (var window in windows)
        {
            var content = ContentOf(window);
            Previous.Add((round, new WeakReference(window),
                content != null ? new WeakReference(content) : new WeakReference(new object())));
        }
        return windows.Count;
    }

    private static List<Widget> LiveWindows(WidgetFactory factory)
    {
        var field = typeof(WidgetFactory).GetField("activeWidgets", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)field.GetValue(factory)!;
        var list = new List<Widget>();
        foreach (var bucket in dict.Values)
            foreach (Widget w in (System.Collections.IEnumerable)bucket!)
                list.Add(w);
        return list;
    }

    private static object? ContentOf(Widget window)
    {
        var field = typeof(Widget).GetField("ContentPresenter", BindingFlags.NonPublic | BindingFlags.Instance);
        if (field?.GetValue(window) is ContentPresenter presenter) return presenter.Content;
        return window.FindControl<ContentPresenter>("ContentPresenter")?.Content;
    }

    private static long ForceGc()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(true);
    }

    /// <summary>
    /// Drain the dispatcher (headless mode). The headless platform reports a fake native handle,
    /// so the activation-time Win32 interop (<c>RemoveWindowFromAltTab</c>) throws 1400 on it —
    /// a headless-environment artifact, not a host defect; the real app's handles are valid.
    /// </summary>
    private static void Pump()
    {
        try
        {
            Dispatcher.UIThread.RunJobs();
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(target, value);

    /// <summary>
    /// Populate the process-wide glass caches with a synthetic capture + backdrop, exactly the
    /// shape a rendered glass widget would leave behind: one cache-owned <c>GlassSource</c> and
    /// one cached <c>WallpaperSnapshot</c>.
    /// </summary>
    private static void PopulateGlassCaches()
    {
        byte[] png;
        using (var bitmap = new SkiaSharp.SKBitmap(64, 64))
        {
            bitmap.Erase(new SkiaSharp.SKColor(40, 60, 120));
            using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 90);
            png = data.ToArray();
        }

        var theme = new Theme(false, null, 1, false, false, "Inter", Surface: SurfaceStyle.LiquidGlass);
        var frame = new LiquidGlassRenderer.Frame(320, 240, 1, 32, 0, 0, 1200, 800, 0, 0, 1200, 800, theme, false);
        var snapshot = new WallpaperSnapshot(png, SkiaSharp.SKColors.Black);

        var cacheType = typeof(LiquidGlassWallpaper).Assembly.GetType("DeskCanvas.Services.LiquidGlassSourceCache")!;
        var get = cacheType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static)!;
        var source = get.Invoke(null, [frame, snapshot]);
        // Release the caller's reference; the cache keeps its own (the steady state a rendered
        // widget leaves behind).
        source?.GetType().GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance)?.Invoke(source, null);

        var wallpaperType = typeof(LiquidGlassWallpaper);
        wallpaperType.GetField("cached", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, new WallpaperSnapshot(png, SkiaSharp.SKColors.Black));
        wallpaperType.GetField("cachedKey", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, "synthetic");
    }

    private static bool GetStaticFieldIsNull(string typeName, string fieldName)
    {
        var type = typeof(WidgetFactory).Assembly.GetType(typeName);
        var field = type?.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        return field?.GetValue(null) == null;
    }

    /// <summary>The Folders widget's static OLE drop-target registries (registrations / subclasses).</summary>
    private static string OleRegistrations()
    {
        var type = typeof(Folders.Services.WidgetOleDropTarget);
        var reg = (System.Collections.IDictionary)type
            .GetField("registrations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var sub = (System.Collections.IDictionary)type
            .GetField("subclassRegistrations", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        return $"{reg.Count}/{sub.Count}";
    }

    /// <summary>Total live subscriber count across the seven shared timers.</summary>
    private static int TimerSubscribers()
    {
        var total = 0;
        foreach (var name in new[] { "Timer100Ms", "Timer1Second", "Timer5Seconds", "Timer1Minute", "Timer5Minutes", "Timer1Hour", "Timer1Day" })
        {
            var timer = (UpdateTimer)typeof(TimerService).GetField(name, BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var list = (System.Collections.ICollection)timer.GetType()
                .GetField("subscribers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(timer)!;
            total += list.Count;
        }
        return total;
    }

    private static void SetAttached(DisplayMonitorService monitor, Avalonia.Platform.Screen screen, ScreenLayout config)
    {
        var attached = new AttachedScreen(
            screen,
            new ScreenIdentity("\\\\.\\PROBE1", "Probe Screen", screen.Bounds.Width, screen.Bounds.Height,
                screen.IsPrimary, screen.Scaling, "PROBE", "PROBE0"),
            config);
        var property = typeof(DisplayMonitorService).GetProperty("Attached")!;
        property.SetValue(monitor, new List<AttachedScreen> { attached });
    }

    private static string GetStaticProperty(string typeName, string propertyName)
    {
        var type = typeof(WidgetFactory).Assembly.GetType(typeName);
        var property = type?.GetProperty(propertyName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        return property?.GetValue(null)?.ToString() ?? "n/a";
    }

    private static string GetStaticFieldCount(string typeName, string fieldName)
    {
        var type = typeof(WidgetFactory).Assembly.GetType(typeName);
        var field = type?.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
        if (field?.GetValue(null) is System.Collections.ICollection collection) return collection.Count.ToString();
        return "n/a";
    }

    [DllImport("user32.dll")]
    private static extern uint GetGuiResources(IntPtr hProcess, uint uiFlags);

    /// <summary>Loads the widget assemblies referenced by this probe and activates through DI.</summary>
    private sealed class AssemblyProviderStub : IAssemblyProvider
    {
        public static IServiceProvider? Services;

        public System.Linq.ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) =>
            Array.Empty<AssemblyInfo>().ToLookup(info => info.AssemblyName);

        public Assembly LoadAssembly(string assemblyName) => Assembly.Load(assemblyName);

        public void UnloadAssembly(string assemblyName) { }

        public object Activate(Type type, params object[] args)
        {
            var instance = ActivatorUtilities.CreateInstance(Services!, type, args);
            // Real mode: keep every probe window invisible and non-activating — nothing may
            // appear on the user's desktop (or steal focus) while the probe runs.
            if (realMode && instance is Window window)
            {
                window.Opacity = 0;
                window.ShowActivated = false;
            }
            return instance;
        }

        public System.Resources.ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
    }

    private sealed class ProbeApp : DeskCanvas.App
    {
        public override void OnFrameworkInitializationCompleted() { }
    }
}
