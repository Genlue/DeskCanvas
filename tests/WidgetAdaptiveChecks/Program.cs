using System.Resources;
using System.Runtime.CompilerServices;
using Avalonia.Platform;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using Grid = Avalonia.Controls.Grid;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Themes.Fluent;
using DeskCanvas.Views;
using Tools.Models;
using Tools.Views;

[assembly: WidgetInfo(typeof(WidgetAdaptiveChecks.TopologyContent))]

namespace WidgetAdaptiveChecks;

/// <summary>
/// Checks the widget adaptive-sizing contract from the user-visible defect: the translator
/// widget's secondary-panel (expand) button was cut in half at a 160px free-mode size, because
/// the control bar's fixed Auto columns simply overflowed and the card clipped them.
///
/// <para>
/// Two layers pin two contracts:
/// 1. <see cref="Widget.ComputeContentMinWindowSize"/> — the host's content-driven minimum
///    window size math (content DesiredSize + card margins + outline, floored at 48).
/// 2. The translator control bar's measure-driven degradation — at ANY width the bar either
///    fits (natural width ≤ available) after hiding low-frequency buttons, or has exhausted
///    its degradation sequence; the expand button (the panel's only entry point) survives
///    everywhere except the very last level.
/// </para>
///
/// The host loop (DesiredSize → MinWidth/MinHeight → grow-to-fit) and the view's degradation
/// close each other: degradation shrinks DesiredSize, so the host's minimum never exceeds
/// what the view has already tried to fit. That closure is asserted here via the same
/// DesiredSize the host reads.
///
/// Usage: dotnet run --project tests/WidgetAdaptiveChecks -c Release
/// </summary>
internal static class Program
{
    private static int failures;

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
    private static int Main()
    {
        Console.WriteLine("=== Widget adaptive sizing checks ===");
        Console.WriteLine();

        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        CheckMinSizeMath();
        CheckControlBarDegradation();
        WindowTopologyChecks.Run(Check);

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED!");
            return 0;
        }
        Console.WriteLine($"{failures} CHECK(S) FAILED!");
        return 1;
    }

    // ---- 1. Host-side minimum-size math ----

    private static void CheckMinSizeMath()
    {
        Console.WriteLine("--- Content-driven minimum window size math ---");
        var method = typeof(Widget).GetMethod("ComputeContentMinWindowSize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(method != null, "Widget.ComputeContentMinWindowSize exists");

        if (method == null) return;

        // No margins, no outline: the window minimum is the content minimum.
        var bare = InvokeMin(method, new Size(160, 120), new Thickness(0), new Thickness(0));
        Check(bare == (160, 120), $"no margin/outline → min == desired (got {bare})");

        // The real card geometry: 12px margin on each side + a 1px outline ring.
        var carded = InvokeMin(method, new Size(160, 120), new Thickness(12), new Thickness(1));
        Check(carded == (186, 146), $"12 margin + 1 outline → min = desired + 26 (got {carded})");

        // Fractional desired sizes round up — a fractional pixel short would keep the
        // content 0.4px truncated forever.
        var rounded = InvokeMin(method, new Size(100.4, 99.6), new Thickness(0), new Thickness(0));
        Check(rounded == (101, 100), $"fractional desired rounds up (got {rounded})");

        // The 48px floor: a view that reports nearly no minimum (Viewbox clocks) keeps
        // the historical 48px free-resize floor.
        var floored = InvokeMin(method, new Size(10, 8), new Thickness(0), new Thickness(0));
        Check(floored == (48, 48), $"tiny desired keeps the 48px floor (got {floored})");
    }

    private static (double, double) InvokeMin(
        MethodInfo method, Size desired, Thickness margin, Thickness outline) =>
        ((double, double))method.Invoke(null,
            [desired, margin, outline, 48.0])!;

    // ---- 2. Translator control bar measure-driven degradation ----

    private static void CheckControlBarDegradation()
    {
        Console.WriteLine();
        Console.WriteLine("--- Translator control bar degradation (bug: expand button truncated at 160px) ---");

        // One fresh window+view per width: a shown window is only ever sized once here
        // (no message pump is running, so resize churn on a live window is not a supported
        // path), and a fresh view doubles as per-case isolation for the monotonicity read.
        (bool engine, bool swap, bool translate, bool expand)? previous = null;

        foreach (var (w, h) in new[] { (320, 170), (260, 160), (200, 160), (160, 160), (140, 140), (110, 110), (96, 96) })
        {
            var (view, bar, engine, swap, translate, expand, window) = LayoutAt(w, h);

            // The bar lives inside the view's own small-tier margin (tier-small adds
            // Margin 6,5 to the view), so the bar's budget is the view's arranged width,
            // while the view's DesiredSize — which is what the host's minimum-size loop
            // reads, own margin included — is budgeted by the window client area.
            var availableForBar = view.Bounds.Width;
            var clientWidth = window.Bounds.Width;
            var natural = NaturalBarWidth(bar);

            // Fit holds above the floor; at the floor the last degradation level may
            // legitimately still overflow in a bare window (the real host's minimum-size
            // clamp grows the window instead — tested via ComputeContentMinWindowSize).
            Check(natural <= availableForBar + 0.5 || !expand.IsVisible,
                $"{w}×{h}: control bar fits after degradation (natural {natural:F0} ≤ available {availableForBar:F0})");
            Check(view.DesiredSize.Width <= clientWidth + 0.5,
                $"{w}×{h}: view DesiredSize {view.DesiredSize.Width:F0} ≤ client {clientWidth:F0} (host min-size loop sees no overflow)");

            if (view.DesiredSize.Width > clientWidth + 0.5)
            {
                DumpOverflow(view, availableForBar);
            }

            var current = (engine: engine.IsVisible, swap: swap.IsVisible,
                translate: translate.IsVisible, expand: expand.IsVisible);
            if (previous is { } prev)
            {
                Check(NeverReappearing(prev.engine, current.engine) && NeverReappearing(prev.swap, current.swap)
                      && NeverReappearing(prev.translate, current.translate) && NeverReappearing(prev.expand, current.expand),
                    $"{w}×{h}: visibility degrades monotonically " +
                    $"(engine {current.engine} swap {current.swap} translate {current.translate} expand {current.expand})");
            }
            previous = current;

            if (w == 160)
            {
                // The reported bug, pinned literally: at the 160px size from the screenshot
                // the expand button — the secondary panel's only entry point — must be
                // intact (inside the card, covered by the fit invariant) and on screen.
                Check(expand.IsVisible, "160×160: expand button (secondary panel entry) still visible");
                Check(swap.IsVisible, "160×160: swap button still visible");
            }

            window.Close();
        }

        // The degradation bottom: at the floor, only the last resort may hide the expand
        // button — a half-drawn button (the original defect) must never be the outcome.
        var (floorView, floorBar, _, _, _, floorExpand, floorWindow) = LayoutAt(64, 64);
        Check(NaturalBarWidth(floorBar) <= floorView.Bounds.Width + 0.5 || !floorExpand.IsVisible,
            "64×64: bar either fits or has collapsed to the last degradation level (never half-drawn)");
        floorWindow.Close();
    }

    /// <summary>
    /// The bar's natural width, read (never measured) off the live tree: a manual
    /// Measure(infinity) on an already-measured control overwrites its DesiredSize and
    /// ping-pongs the running layout forever — the very defect this view once had.
    /// </summary>
    private static double NaturalBarWidth(Grid bar)
    {
        double natural = 0;
        foreach (var child in bar.Children)
        {
            if (child is not Layoutable { IsVisible: true } element) continue;
            natural += element.DesiredSize.Width + element.Margin.Left + element.Margin.Right;
        }
        return natural;
    }

    private static (
        TranslatorView View, Grid Bar, Button Engine, Button Swap, Button Translate, Button Expand, Window Window)
        LayoutAt(double width, double height)
    {
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
            Width = width,
            Height = height,
            Content = new TranslatorView(new TranslatorModel())
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var view = (TranslatorView)window.Content!;
        return (
            view,
            GetField<Grid>(view, "ControlBarGrid")!,
            GetField<Button>(view, "EngineBtn")!,
            GetField<Button>(view, "SwapBtn")!,
            GetField<Button>(view, "TranslateBtn")!,
            GetField<Button>(view, "ExpandBtn")!,
            window);
    }

    /// <summary>Visibility as width SHRINKS (the loop descends): a hidden button must never reappear.</summary>
    private static bool NeverReappearing(bool previous, bool current) => previous || !current;

    /// <summary>Print the subtree branches whose DesiredSize exceeds the constraint, widest offender first.</summary>
    private static void DumpOverflow(TranslatorView view, double available)
    {
        Console.WriteLine($"      [trace] overflow hunting: available {available:F1}, view desired {view.DesiredSize.Width:F1}");
        void Walk(Visual visual, string indent)
        {
            foreach (var child in visual.GetVisualChildren())
            {
                if (child is not Layoutable l) continue;
                if (l.DesiredSize.Width > available + 0.5)
                {
                    Console.WriteLine($"      [trace] {indent}{l.GetType().Name}{(l is TextBlock tb ? $"(\"{(tb.Text ?? "")[..Math.Min(16, tb.Text?.Length ?? 0)]}\")" : "")} desired {l.DesiredSize.Width:F1} (bounds {l.Bounds.Width:F1})");
                    Walk(child, indent + "  ");
                }
            }
        }
        Walk(view, "");
    }

    private static T? GetField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) as T;
    }
}

public sealed class TopologyContent : UserControl { }

internal static class WindowTopologyChecks
{
    public static void Run(Action<bool, string> check)
    {
        var settings = new SettingsStore(new AppSettings(
            new Theme(true, null, 1, false, false, "Segoe UI", Surface: SurfaceStyle.Solid), [],
            new Layout(GridMode.Free, false, false, false, false), new Dimensions(100, 10, 16),
            new Region("en"), false, null));
        var entry = new WidgetLayout("Topology", nameof(TopologyContent), 40, 60, 200, 180, null);
        var a = new ScreenLayout("a", "A|1920x1080", null, null, null, 100, []);
        var b = new ScreenLayout("b", "B|1920x1080", null, null, null, 1.5, [entry], Margin: 24, Radius: 36);
        var store = new LayoutStore(new ScreensLayout([a, b]));
        var monitor = new DisplayMonitorService(store);
        // A fixed anchor prevents widget.Opened from attaching the monitor service to the
        // test window. Topology is injected below, independent of the host computer.
        typeof(DisplayMonitorService).GetField("anchor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, new Window());
        var assembly = new AssemblyStore(settings, store, monitor);
        var factory = new WidgetFactory(assembly, store, monitor, () => throw new InvalidOperationException());
        var provider = new WidgetLayoutProvider(store, b.Id, entry);
        var widget = assembly.NewWidget(provider);
        SetAttached(monitor, a, b, 1);
        widget.Show();
        Dispatcher.UIThread.RunJobs();
        var live = (Dictionary<string, List<Widget>>)typeof(WidgetFactory)
            .GetField("activeWidgets", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(factory)!;
        live[b.Id] = [widget];

        foreach (var dpi in new[] { 1.0, 1.5, 2.0 })
        {
            SetAttached(monitor, a, b, dpi);
            widget.Position = new PixelPoint(10, 10); // Windows has moved it onto screen A.
            var writes = store.Writes;
            widget.RefreshDisplayMetrics();
            check(widget.Position == new PixelPoint(1960, 60), $"DPI {dpi}: restores B-relative saved position after Windows relocation (actual {widget.Position}, expected 1960,60; provider {provider.Get().X},{provider.Get().Y})");
            check(widget.EffectiveMargin == 24 && widget.EffectiveBaseRadius == 36,
                $"DPI {dpi}: uses B's margin and corner radius while physical placement was A");
            var scale = (double) typeof(Widget).GetProperty("EffectiveContentScale", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(widget)!;
            check(scale == 1.5, $"DPI {dpi}: content scale follows logical B ownership");
            check(Math.Abs(widget.Radius.TopLeft * dpi - 36) < 0.001,
                $"DPI {dpi}: radius resolves through the owning display DPI");
            check(store.Writes == writes, $"DPI {dpi}: restoring metrics does not persist transient geometry");
        }

        settings.Save(settings.Get() with { Layout = new Layout(GridMode.Manual, false, false, false, false) });
        var bGrid = new DeskCanvas.Core.Models.Settings.Grid(10, 8, 10, 5, 6);
        b = b with { Grid = bGrid };
        SetAttached(monitor, a, b, 2);
        store.Save(store.Get().WithScreen(b));
        widget.Position = new PixelPoint(10, 10);
        var gridService = new GridService(settings, monitor);
        typeof(Widget).GetMethod("SetMinMaxSize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(widget, [false]);
        gridService.SetSize(widget, 2, 3);
        check(Math.Abs(widget.Width - 115) < 0.001 && Math.Abs(widget.Height - 172.5) < 0.001,
            "Manual size uses B's 115px cell at 200% DPI despite physical A placement");
        check(widget.WidgetMargin.Left == 24, "Manual grid uses the owning display's dedicated margin");
        var resizedB = new Screen(2, new PixelRect(-1280, -720, 1280, 720), new PixelRect(-1280, -680, 1280, 680), false);
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor,
            new[] { new AttachedScreen(resizedB, new ScreenIdentity("B", "B", 1280, 720, false, 2), b) });
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.IsTopologyChanging))!.SetValue(monitor, false);
        var writesBeforeReflow = store.Writes;
        widget.RefreshDisplayMetrics();
        var manualPosition = widget.Position;
        check(manualPosition == new PixelPoint(-1152, -646) && Math.Abs(widget.Width - 154) < 0.001,
            $"manual topology restoration recalculates size and snaps into the moved smaller work area (position {manualPosition}, width {widget.Width})");
        check(store.Writes == writesBeforeReflow && provider.Get().X == 40 && provider.Get().Y == 60,
            "manual work-area reflow preserves saved coordinates without writing transient snapped placement");
        settings.Save(settings.Get() with { Layout = new Layout(GridMode.Free, false, false, false, false) });
        var tinyB = new Screen(2, new PixelRect(-500, -300, 300, 240), new PixelRect(-500, -280, 300, 220), false);
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor,
            new[] { new AttachedScreen(tinyB, new ScreenIdentity("B", "B", 300, 240, false, 2), b) });
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.IsTopologyChanging))!.SetValue(monitor, false);
        var writesBeforeClamp = store.Writes;
        widget.RefreshDisplayMetrics();
        check(widget.Position == tinyB.WorkingArea.Position && store.Writes == writesBeforeClamp && provider.Get().X == 40,
            $"free mode clamps presentation into a reduced work area while retaining saved placement (position {widget.Position}, width {widget.Width}, height {widget.Height}, writes {store.Writes}/{writesBeforeClamp})");

        factory.SuspendForDisplayChange();
        factory.SetWidgetsHidden(true);
        SetAttached(monitor, a, b, 1.5);
        factory.OnScreensChanged();
        check(!widget.IsVisible && factory.WidgetsHidden, "topology restoration preserves the tray's hidden preference");

        // Exercise production ownership indexing: a drag updates the live provider before
        // the next topology event, while the factory still has its original B bucket.
        provider.ScreenId = a.Id;
        store.Save(new ScreensLayout([a with { Layout = [entry] }, b with { Layout = [] }]));
        SetAttached(monitor, a, null, 1);
        factory.OnScreensChanged();
        check(live.ContainsKey(a.Id) && !live.ContainsKey(b.Id) && factory.HasWidgets,
            "a user transfer from B to A survives B unplugging without duplicate windows");

        var writesBeforeOffline = store.Writes;
        SetAttached(monitor, null, b, 1);
        factory.OnScreensChanged();
        check(!factory.HasWidgets && !live.ContainsKey(a.Id), "an unplugged owner closes its native widget window");
        check(store.Writes == writesBeforeOffline && store.Get().FindById(a.Id)!.Layout.Count == 1,
            "offline teardown retains the owner's saved widget entry without rewriting geometry");
        factory.CloseAll();

        // A surviving v1 window holds absolute coordinates. Its provider must be retired
        // before migration changes the same entry to working-area-relative coordinates.
        var legacyEntry = entry with { Y = 100 };
        var legacy = a with { Id = ScreensLayout.LegacyPrimaryId, Key = null, Layout = [legacyEntry] };
        store.Save(new ScreensLayout([legacy]));
        var legacyProvider = new WidgetLayoutProvider(store, legacy.Id, legacyEntry);
        var legacyWindow = assembly.NewWidget(legacyProvider);
        live[legacy.Id] = [legacyWindow];
        var legacyAttached = new[] { new AttachedScreen(
            new Screen(1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 40, 1920, 1040), true),
            new ScreenIdentity("A", "A", 1920, 1080, true, 1), legacy) };
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor, legacyAttached);
        // Freeze the OS enumeration seam during this injected-topology migration check.
        typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, true);
        try { factory.OnScreensChanged(); }
        finally { typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, false); }
        var migratedWindow = live[legacy.Id].Single();
        var migratedProvider = (IWidgetLayoutProvider)typeof(Widget).GetField("widgetLayoutProvider", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(migratedWindow)!;
        check(!ReferenceEquals(migratedWindow, legacyWindow) && migratedProvider.Get().Y == 60,
            "live legacy window is recreated with migrated relative coordinates after taskbar-origin conversion");
        check(store.Get().FindById(legacy.Id)!.Key != null && store.Get().FindById(legacy.Id)!.Layout.Count == 1,
            "legacy migration pins the screen and retains exactly one widget");
        factory.CloseAll();

        var offlineEntry = entry with { X = 2040, Y = 120 };
        legacy = legacy with { Key = null, Layout = [legacyEntry, offlineEntry] };
        b = b with { Layout = [] };
        store.Save(new ScreensLayout([legacy, b]));
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor,
            legacyAttached.Select(attached => attached with { Config = legacy }).ToList());
        typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, true);
        try
        {
            var visible = factory.Create().ToList();
            check(visible.Count == 1 && live[legacy.Id].Count == 1,
                "partial legacy startup creates only the online widget while an offline absolute entry stays hidden");
            check(store.Get().FindById(legacy.Id)!.Layout.Count == 2 && store.Get().FindById(legacy.Id)!.Key == null,
                "partial legacy startup preserves offline absolute coordinates in storage");
            var replugged = legacyAttached.Select(attached => attached with { Config = legacy }).ToList();
            replugged.Add(new AttachedScreen(new Screen(1.5, new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 0, 1920, 1040), false),
                new ScreenIdentity("B", "B", 1920, 1080, false, 1.5), b));
            typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor, replugged);
            factory.OnScreensChanged();
            check(live.Values.Sum(windows => windows.Count) == 2 && live[b.Id].Single().ScreenId == b.Id,
                "legacy offline entry is created on its own screen after replugging without duplicate windows");
            check(store.Get().FindById(b.Id)!.Layout.Single().X == 120 && store.Get().FindById(legacy.Id)!.Layout.Count == 1,
                "replug migration transfers the preserved absolute entry to B's relative coordinates exactly once");
        }
        finally { typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, false); }
        factory.CloseAll();

        foreach (var initialStartup in new[] { true, false })
        {
            legacy = legacy with { Key = null, Layout = [legacyEntry, offlineEntry] };
            store.Save(new ScreensLayout([legacy]));
            var primaryOnly = legacyAttached.Select(attached => attached with { Config = legacy }).ToList();
            typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor, primaryOnly);
            typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, true);
            try
            {
                if (!initialStartup) factory.Create().ToList();
                var unconfigured = new AttachedScreen(new Screen(1.5, new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 0, 1920, 1040), false),
                    new ScreenIdentity("B", "B", 1920, 1080, false, 1.5), null);
                primaryOnly.Add(unconfigured);
                typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor, primaryOnly);
                if (initialStartup) factory.Create().ToList();
                else factory.OnScreensChanged();
                var secondary = monitor.Attached.Single(attached => attached.Identity.DeviceName == "B").Config;
                check(secondary != null && store.Get().FindById(secondary.Id)!.Layout.Single().X == 120,
                    $"{(initialStartup ? "startup" : "replug")}: unconfigured attached secondary receives legacy widget automatically");
                check(live.Values.Sum(windows => windows.Count) == 2 && secondary != null && live[secondary.Id].Count == 1,
                    $"{(initialStartup ? "startup" : "replug")}: legacy bucket creation yields exactly one native widget per owner");
            }
            finally { typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(monitor, false); }
            factory.CloseAll();
        }
    }

    private static void SetAttached(DisplayMonitorService monitor, ScreenLayout? a, ScreenLayout? b, double dpi)
    {
        var attached = new List<AttachedScreen>();
        if (a != null) attached.Add(new AttachedScreen(new Screen(1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true), new ScreenIdentity("A", "A", 1920, 1080, true, 1), a));
        if (b != null) attached.Add(new AttachedScreen(new Screen(dpi, new PixelRect(1920, 0, 1920, 1080), new PixelRect(1920, 0, 1920, 1040), false), new ScreenIdentity("B", "B", 1920, 1080, false, dpi), b));
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor, attached);
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.IsTopologyChanging))!.SetValue(monitor, false);
    }

    private static void MakePositionAware(Widget widget)
    {
        // Avalonia.Headless has no real HWND and its Move implementation does not retain
        // coordinates. Forward every platform operation except that missing move seam;
        // production ApplyPosition, sizing, styles and lifecycle continue to execute.
        var impl = widget.PlatformImpl!;
        var proxy = DispatchProxy.Create<IWindowImpl, PositionAwareWindow>();
        ((PositionAwareWindow)(object)proxy).Target = impl;
        for (var type = typeof(Widget); type != null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (ReferenceEquals(field.GetValue(widget), impl)) field.SetValue(widget, proxy);

        var activated = typeof(Widget).GetMethod("OnActivated", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        widget.Activated -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), widget, activated);
        var opened = typeof(Widget).GetMethod("OnOpened", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        widget.Opened -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), widget, opened);
    }

    public class PositionAwareWindow : DispatchProxy
    {
        public IWindowImpl Target = null!;
        private PixelPoint position;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Position") return position;
            if (method.Name == "Move")
            {
                position = (PixelPoint)args![0]!;
                var changed = (Action<PixelPoint>?)typeof(IWindowBaseImpl)
                    .GetProperty("PositionChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Target);
                changed?.Invoke(position);
                return null;
            }
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }
    }

    private sealed class LayoutStore(ScreensLayout initial) : ILayoutProvider
    {
        private ScreensLayout value = initial;
        public int Writes { get; private set; }
        public event DataChangedEvent<ScreensLayout>? DataChanging;
        public event DataChangedEvent<ScreensLayout>? DataChanged;
        public ScreensLayout Get() => value;
        public void Save(ScreensLayout next) { var old = value; DataChanging?.Invoke(this, old, next); value = next; Writes++; DataChanged?.Invoke(this, old, next); }
    }

    private sealed class SettingsStore(AppSettings initial) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging;
        public event DataChangedEvent<AppSettings>? DataChanged;
        public AppSettings Get() => initial;
        public void Save(AppSettings value) { var old = initial; DataChanging?.Invoke(this, old, value); initial = value; DataChanged?.Invoke(this, old, value); }
    }

    private sealed class AssemblyStore(SettingsStore settings, LayoutStore layout, DisplayMonitorService monitor) : IAssemblyProvider
    {
        // The real constructor seeds profiles on disk; this fixture only needs event backing
        // fields and must never read or write the user's profile files.
        private readonly ProfileService profiles = (ProfileService)RuntimeHelpers.GetUninitializedObject(typeof(ProfileService));
        public Widget NewWidget(IWidgetLayoutProvider provider)
        {
            var widget = new Widget(settings, provider, new GridService(settings, monitor), layout, monitor, profiles, () => new TopologyContent(), () => throw new InvalidOperationException());
            MakePositionAware(widget);
            return widget;
        }
        public object Activate(Type type, params object[] args) => type == typeof(Widget) ? NewWidget((IWidgetLayoutProvider)args[0]) : Activator.CreateInstance(type)!;
        public Assembly LoadAssembly(string name) => typeof(TopologyContent).Assembly;
        public ILookup<string, AssemblyInfo> GetAssemblyInfos(string path) => Array.Empty<AssemblyInfo>().ToLookup(_ => "");
        public void UnloadAssembly(string name) { }
        public ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
    }
}
