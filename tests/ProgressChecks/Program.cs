using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Progress.Models;
using Progress.ViewModels;
using Progress.Views;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Runtime;

namespace ProgressChecks;

/// <summary>
/// Regression checks for the Progress widget's P8-A slice: the per-screen theme resolution
/// seam behind the DarkMode==null fallback, the session-gated five-second tick, the
/// IWidgetSelfRefreshing contract, the settings page's Host scroll ownership, and the
/// source-level migration invariants.
/// </summary>
class Program
{
    private static int failures;

    private static readonly MethodInfo ResolveVariantMethod = typeof(ProgressViewModel).GetMethod(
        "ResolveProgressThemeVariant", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly FieldInfo VariantResolverField = typeof(ProgressViewModel).GetField(
        "WindowVariantResolverForChecks", BindingFlags.NonPublic | BindingFlags.Static)!;

    [STAThread]
    static int Main(string[] args)
    {
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());
        // Pin the uniform fallback variant: every "uniform" assertion below reads it.
        Application.Current.RequestedThemeVariant = ThemeVariant.Light;

        Console.WriteLine("=== Progress widget per-screen theme / session / W9 checks ===");
        Console.WriteLine();

        CheckPerScreenThemeSeam();
        CheckBrushValueEquivalence();
        CheckLegacySessionlessBehaviour();
        CheckSessionGating();
        CheckViewWiringAndSelfRefresh();
        CheckSourceInvariants();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---- Part 1: ResolveProgressThemeVariant — the internal seam ----

    private static void CheckPerScreenThemeSeam()
    {
        Console.WriteLine("--- per-screen theme seam (ResolveProgressThemeVariant) ---");
        var uniform = Application.Current!.ActualThemeVariant;
        var anchor = new Control(); // a detached visual — no window, no host services

        Check("resolve(null) falls back to the uniform application variant",
            ResolveVariant(null) == uniform);

        Check("a detached visual without host services falls back to the uniform variant",
            ResolveVariant(anchor) == uniform);

        SetResolver(_ => ThemeVariant.Dark);
        try
        {
            Check("an injected window mapping resolving Dark wins over the uniform variant",
                ResolveVariant(anchor) == ThemeVariant.Dark);
        }
        finally
        {
            SetResolver(null);
        }

        SetResolver(_ => ThemeVariant.Light);
        try
        {
            Check("an injected window mapping resolving Light wins over the uniform variant",
                ResolveVariant(anchor) == ThemeVariant.Light);
        }
        finally
        {
            SetResolver(null);
        }

        SetResolver(_ => null);
        try
        {
            Check("a window that cannot resolve to a variant falls back to the uniform variant",
                ResolveVariant(anchor) == uniform);
        }
        finally
        {
            SetResolver(null);
        }
    }

    // ---- Part 2: UpdateBrushes stays value-equivalent ----

    private static void CheckBrushValueEquivalence()
    {
        Console.WriteLine("--- brush value equivalence (accent / IsColorful / remaining) ---");

        var explicitDark = new ProgressViewModel(
            new ProgressModel(),
            new StubSettings(BuildSettings(new Theme(
                DarkMode: true, AccentColor: "#FF3B30", OpacityLevel: 0.2,
                Monochrome: true, UseNativeFrame: false, FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Acrylic))));
        Check("explicit DarkMode=true paints the dark remaining dots (#32FFFFFF)",
            Remaining(explicitDark) == Color.Parse("#32FFFFFF"));
        explicitDark.Dispose();

        var explicitLight = new ProgressViewModel(
            new ProgressModel(),
            new StubSettings(BuildSettings(new Theme(
                DarkMode: false, AccentColor: "#FF3B30", OpacityLevel: 0.2,
                Monochrome: true, UseNativeFrame: false, FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Acrylic))));
        Check("explicit DarkMode=false paints the light remaining dots (#26000000)",
            Remaining(explicitLight) == Color.Parse("#26000000"));
        explicitLight.Dispose();

        // DarkMode == null: the seam decides. Anchor resolves Dark through the injected mapping.
        var anchor = new Control();
        SetResolver(_ => ThemeVariant.Dark);
        ProgressViewModel perScreenDark;
        try
        {
            perScreenDark = new ProgressViewModel(
                new ProgressModel(),
                new StubSettings(BuildSettings(new Theme(
                    DarkMode: null, AccentColor: "#FF3B30", OpacityLevel: 0.2,
                    Monochrome: true, UseNativeFrame: false, FontFamily: "Segoe UI",
                    Surface: SurfaceStyle.Acrylic))),
                session: null,
                themeAnchor: anchor);
        }
        finally
        {
            SetResolver(null);
        }
        Check("DarkMode=null with a dark per-screen window paints dark remaining dots",
            Remaining(perScreenDark) == Color.Parse("#32FFFFFF"));
        perScreenDark.Dispose();

        var uniform = new ProgressViewModel(
            new ProgressModel(),
            new StubSettings(BuildSettings(new Theme(
                DarkMode: null, AccentColor: "#FF3B30", OpacityLevel: 0.2,
                Monochrome: true, UseNativeFrame: false, FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Acrylic))),
            session: null,
            themeAnchor: new Control());
        Check("DarkMode=null with an unresolvable anchor keeps the uniform (light) dots",
            Remaining(uniform) == Color.Parse("#26000000"));
        uniform.Dispose();

        var colorful = new ProgressViewModel(
            new ProgressModel(),
            new StubSettings(BuildSettings(new Theme(
                DarkMode: true, AccentColor: null, OpacityLevel: 1.0,
                Monochrome: false, UseNativeFrame: false, FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Colorful))));
        Check("FollowAccent under 多彩 paints the vibrant green (#30D158)",
            Passed(colorful) == Color.Parse("#30D158"));
        Check("FollowAccent under 多彩 paints the vibrant red current dot (#FF375F)",
            Current(colorful) == Color.Parse("#FF375F"));
        colorful.Dispose();

        var accentless = new ProgressViewModel(new ProgressModel());
        Check("FollowAccent with no theme at all keeps the lavender fallback (#7B61FF)",
            Passed(accentless) == Color.Parse("#7B61FF"));
        accentless.Dispose();

        var accent = new ProgressViewModel(
            new ProgressModel(),
            new StubSettings(BuildSettings(new Theme(
                DarkMode: true, AccentColor: "#FF3B30", OpacityLevel: 0.2,
                Monochrome: true, UseNativeFrame: false, FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Acrylic))));
        Check("FollowAccent with a picked accent uses the picked colour",
            Passed(accent) == Color.Parse("#FF3B30"));
        accent.Dispose();

        var custom = new ProgressViewModel(new ProgressModel(
            FollowAccentColor: false,
            PassedColor: "#11AA22"));
        Check("a custom PassedColor wins over the accent path",
            Passed(custom) == Color.Parse("#11AA22"));
        custom.Dispose();
    }

    // ---- Part 3: session-less legacy behaviour ----

    private static void CheckLegacySessionlessBehaviour()
    {
        Console.WriteLine("--- session-less view model (always-on legacy path) ---");

        var vm = new ProgressViewModel(new ProgressModel());
        Check("a fresh session-less view model is not ticking", !vm.IsTickingForChecks);

        vm.Start();
        Check("Start subscribes the five-second tick", vm.TickSubscriptionCountForChecks == 1);

        vm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
        Check("a session-less view model ignores activity pushes entirely",
            vm.TickSubscriptionCountForChecks == 1 && vm.RenderActiveForChecks);

        vm.Stop();
        Check("Stop unsubscribes the tick", vm.TickSubscriptionCountForChecks == 0);

        vm.Start();
        Check("Start re-subscribes after Stop", vm.TickSubscriptionCountForChecks == 1);

        vm.Dispose();
        Check("Dispose releases the tick subscription", vm.TickSubscriptionCountForChecks == 0);

        vm.Start();
        Check("a disposed view model must not restart", vm.TickSubscriptionCountForChecks == 0);
    }

    // ---- Part 4: session-gated behaviour ----

    private static void CheckSessionGating()
    {
        Console.WriteLine("--- session-gated view model ---");

        var session = new StubSession();
        var vm = new ProgressViewModel(new ProgressModel(), session: session);

        Check("the session is stored on the view model", vm.SessionForChecks == session);
        Check("a visible initial activity leaves renderActive on", vm.RenderActiveForChecks);

        vm.Start();
        Check("Start subscribes while the session renders", vm.TickSubscriptionCountForChecks == 1);

        vm.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
        Check("a covered screen gates the tick off", vm.TickSubscriptionCountForChecks == 0
            && !vm.RenderActiveForChecks);

        vm.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
        Check("a visible screen gates the tick back on", vm.TickSubscriptionCountForChecks == 1
            && vm.RenderActiveForChecks);

        vm.Dispose();
        Check("Dispose unwinds the gated subscription", vm.TickSubscriptionCountForChecks == 0);

        var bornCovered = new StubSession(new ActivitySnapshot(ActivityReason.ScreenCovered));
        var vm2 = new ProgressViewModel(new ProgressModel(), session: bornCovered);
        vm2.Start();
        Check("a view model born covered never subscribes on Start",
            vm2.TickSubscriptionCountForChecks == 0);
        vm2.OnSessionActivity(ActivitySnapshot.Preview);
        Check("a preview snapshot counts as render-active and subscribes",
            vm2.TickSubscriptionCountForChecks == 1);
        vm2.Dispose();
    }

    // ---- Part 5: view wiring, IWidgetSelfRefreshing contract, teardown ----

    private static void CheckViewWiringAndSelfRefresh()
    {
        Console.WriteLine("--- view wiring & self-refresh contract ---");

        Check("ProgressView implements IWidgetActivityConsumer + IWidgetSelfRefreshing",
            typeof(IWidgetActivityConsumer).IsAssignableFrom(typeof(ProgressView))
            && typeof(IWidgetSelfRefreshing).IsAssignableFrom(typeof(ProgressView)));

        Check("ProgressView declares an IWidgetSession constructor parameter (factory probe)",
            typeof(ProgressView).GetConstructors().Any(constructor => constructor
                .GetParameters()
                .Any(parameter => parameter.ParameterType == typeof(IWidgetSession))));

        var session = new StubSession();
        var view = new ProgressView(new ProgressModel(), null, null, session);
        var vm = (ProgressViewModel)ReadField(view, "viewModel")!;

        Check("the view passes the session into its view model", vm.SessionForChecks == session);
        Check("the view model stays unsubscribed before the first load",
            vm.TickSubscriptionCountForChecks == 0);

        view.OnSessionActivity(new ActivitySnapshot(ActivityReason.ScreenCovered));
        Check("activity pushes reach the view model while unloaded (gate recorded, no tick)",
            !vm.RenderActiveForChecks && vm.TickSubscriptionCountForChecks == 0);

        view.RaiseEvent(new RoutedEventArgs(Control.LoadedEvent));
        Check("loading starts the view model but the covered gate keeps it off the timer",
            vm.TickSubscriptionCountForChecks == 0);

        view.OnSessionActivity(new ActivitySnapshot(ActivityReason.DesktopVisible));
        Check("a live visible push subscribes the tick on the loaded view model",
            vm.TickSubscriptionCountForChecks == 1);

        var refreshed = new WidgetLayout("Progress", "ProgressView", 0, 0, 300, 200, null) with
        {
            Settings = JsonSerializer.SerializeToElement(
                new ProgressModel(Mode: ProgressMode.Week, ShowHeader: false))
        };
        view.Refresh(refreshed);
        Check("Refresh applies the new model to the live view model (Week columns, no header)",
            vm.PreferredColumns == 7 && !vm.ShowHeader);

        view.RaiseEvent(new RoutedEventArgs(Control.UnloadedEvent));
        Check("unloading releases the view model and stops the tick",
            ReadField(view, "viewModel") == null && vm.TickSubscriptionCountForChecks == 0);

        view.RaiseEvent(new RoutedEventArgs(Control.LoadedEvent));
        var vm2 = (ProgressViewModel)ReadField(view, "viewModel")!;
        Check("reloading builds a fresh view model that keeps the refreshed model",
            !ReferenceEquals(vm, vm2)
            && vm2.SessionForChecks == session
            && !vm2.ShowHeader
            && vm2.PreferredColumns == 7
            && vm2.TickSubscriptionCountForChecks == 1);

        view.RaiseEvent(new RoutedEventArgs(Control.UnloadedEvent));
    }

    // ---- Part 6: source-level migration invariants ----

    private static void CheckSourceInvariants()
    {
        Console.WriteLine("--- source invariants ---");

        var root = FindRepoRoot();
        var viewPath = Path.Combine(root, "src", "Widgets", "Progress", "Views", "ProgressView.axaml.cs");
        var vmPath = Path.Combine(root, "src", "Widgets", "Progress", "ViewModels", "ProgressViewModel.cs");
        var settingsPath = Path.Combine(root, "src", "Widgets", "Progress", "Views", "Settings", "ProgressSettings.axaml");

        var viewSource = File.ReadAllText(viewPath);
        var vmSource = File.ReadAllText(vmPath);
        var settingsSource = File.ReadAllText(settingsPath);

        Check("ProgressSettings.axaml declares ScrollOwnership=\"Host\"",
            settingsSource.Contains("SettingsPage.ScrollOwnership=\"Host\""));
        Check("ProgressSettings.axaml no longer owns a page ScrollViewer",
            !settingsSource.Contains("<ScrollViewer"));
        Check("ProgressView.axaml.cs implements IWidgetActivityConsumer",
            viewSource.Contains("IWidgetActivityConsumer"));
        Check("the view model code (comments stripped) no longer reads the global application variant",
            !StripComments(vmSource).Contains("Application.Current.ActualThemeVariant"));
        Check("the view model exposes the ResolveProgressThemeVariant seam",
            vmSource.Contains("ResolveProgressThemeVariant"));
    }

    // ---- helpers ----

    private static ThemeVariant ResolveVariant(Visual? visual) =>
        (ThemeVariant)ResolveVariantMethod.Invoke(null, [visual])!;

    private static void SetResolver(Func<Visual, ThemeVariant?>? resolver) =>
        VariantResolverField.SetValue(null, resolver);

    private static Color Remaining(ProgressViewModel vm) => ((SolidColorBrush)vm.RemainingBrush!).Color;
    private static Color Passed(ProgressViewModel vm) => ((SolidColorBrush)vm.PassedBrush!).Color;
    private static Color Current(ProgressViewModel vm) => ((SolidColorBrush)vm.CurrentBrush!).Color;

    private static object? ReadField(object target, string name) => target
        .GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(target);

    private static string StripComments(string source) => string.Join(
        Environment.NewLine,
        source.Split('\n')
            .Select(line =>
            {
                var index = line.IndexOf("//", StringComparison.Ordinal);
                return index >= 0 ? line[..index] : line;
            }));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "src", "Widgets", "Progress")))
        {
            dir = dir.Parent;
        }
        return dir?.FullName
            ?? throw new InvalidOperationException("repository root not found");
    }

    private static AppSettings BuildSettings(Theme theme) => new(
        theme,
        Templates: [],
        new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(72, 8, 16),
        new Region("zh-Hans"),
        RunOnStartup: false,
        IgnoreUpdate: null);

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    private sealed class StubSession : IWidgetSession
    {
        public StubSession(ActivitySnapshot? initial = null)
        {
            Activity = initial ?? new ActivitySnapshot();
        }

        public WidgetInstanceId InstanceId { get; } = WidgetInstanceId.New();
        public WidgetLifecycleState State { get; private set; } = WidgetLifecycleState.Ready;
        public long Generation { get; private set; }
        public ActivitySnapshot Activity { get; private set; }
        public Exception? Fault { get; }

        public ValueTask InitializeAsync(CancellationToken cancellationToken = default)
        {
            State = WidgetLifecycleState.Ready;
            return ValueTask.CompletedTask;
        }

        public ValueTask ApplySettingsAsync(object? settings, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask SetActivityAsync(ActivitySnapshot activity, CancellationToken cancellationToken = default)
        {
            Activity = activity;
            Generation++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = WidgetLifecycleState.Disposed;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging;
        public event DataChangedEvent<AppSettings>? DataChanged;

        public AppSettings Get() => value;

        public void Save(AppSettings data)
        {
            var old = value;
            DataChanging?.Invoke(this, old, data);
            value = data;
            DataChanged?.Invoke(this, old, data);
        }
    }
}
