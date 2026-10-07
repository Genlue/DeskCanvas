using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Grid = DeskCanvas.Core.Models.Settings.Grid;

namespace DragCommitChecks;

/// <summary>
/// Checks the drag-commit contract from the user-visible defect: in manual grid mode,
/// dragging a widget and releasing the mouse left it floating at the raw drop position —
/// the grid snap only ran after the next click.
///
/// <para>
/// Avalonia runs the native move loop inside a posted Send-priority callback
/// (WindowImpl.BeginMoveDrag), which swallows the physical mouse-up: the synthesized
/// PointerReleased fires at client point (0,0) and only reaches the widget when nothing in
/// the tree handles it and the legacy mouse pipeline delivers it. The commit therefore
/// rides the loop's own WM_EXITSIZEMOVE, with the Background-priority fallback committed
/// to drags that never enter the loop.
/// </para>
///
/// <para>
/// Two pins two contracts:
/// 1. The WM_EXITSIZEMOVE hook — the deterministic end-of-drag signal, sent by the loop
///    itself with the window already at the final drop position — consumes the pending
///    commit exactly once (snap + save), and must never mark the message handled.
/// 2. The Background fallback — posted at drag start for drags that never enter the loop
///    — stays inert while the loop IS active: Background work runs whenever Win32 input
///    goes quiet, i.e. exactly when the user pauses mid-drag to aim at a grid cell;
///    committing there consumed the pending flag and the release silently skipped the
///    grid snap (the v3.4.0 regression — the widget stayed where it was dropped until the
///    next click re-committed it).
/// </para>
///
/// Usage: dotnet run --project tests/DragCommitChecks -c Release
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
    private static void Main()
    {
        Console.WriteLine("=== Drag commit checks ===");
        Console.WriteLine();

        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        CheckHookSignature();
        CheckFallbackInertWhileLoopActive();
        CheckLoopExitCommitsExactlyOnce();
        CheckFallbackCommitsWhenLoopNeverEntered();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        if (failures > 0) Environment.Exit(1);
    }

    // ---- 1. Hook signature + WM_ENTERSIZEMOVE bracket ----

    private static void CheckHookSignature()
    {
        Console.WriteLine("--- Native move-loop bracket hook ---");
        var (widget, provider, layout, monitor) = Build();
        var hook = BindHook(widget);
        if (hook == null) return;

        var handled = false;
        hook(IntPtr.Zero, 0x0231, IntPtr.Zero, IntPtr.Zero, ref handled);
        Check(GetField(widget, "moveLoopActive"), "WM_ENTERSIZEMOVE marks the native move loop active");
        Check(!handled, "the hook never claims the message (Avalonia's bracket handling stays intact)");

        hook(IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);
        Check(!GetField(widget, "moveLoopActive"), "WM_EXITSIZEMOVE clears the loop flag");
        Check(!GetField(widget, "moveCommitPending"), "an exit without a pending commit does not commit");
        Check(layout.Writes == 0, "no pending commit, no save");
    }

    // ---- 2. THE REGRESSION: Background fallback must stay inert while the loop is active ----

    private static void CheckFallbackInertWhileLoopActive()
    {
        Console.WriteLine("--- Background fallback stays inert while the move loop is active ---");
        var (widget, provider, layout, monitor) = Build();

        // The drag exactly as Win32 reports it: the commit was marked pending at press,
        // the native move loop is active, and the user paused mid-drag (Win32 input went
        // quiet) — which is exactly when Background-priority dispatcher work runs. This is
        // the v3.4.0 regression: the fallback consumed the flag there and the release
        // silently skipped the grid snap.
        SetField(widget, "moveCommitPending", true);
        SetField(widget, "moveLoopActive", true);
        widget.Position = new PixelPoint(500, 333); // mid-drag, unsnapped

        InvokeInternal(widget, "CommitPendingMoveAfterLoop");

        Check(GetField(widget, "moveCommitPending"),
            "the pending commit survives a Background-priority dispatch mid-drag");
        Check(layout.Writes == 0, "a mid-drag pause commits nothing (the widget does not snap while aiming)");
        Check(widget.Position == new PixelPoint(500, 333),
            $"the widget stays at the raw mid-drag position (actual {widget.Position})");
    }

    // ---- 3. WM_EXITSIZEMOVE owns the commit ----

    private static void CheckLoopExitCommitsExactlyOnce()
    {
        Console.WriteLine("--- WM_EXITSIZEMOVE owns the commit ---");
        var (widget, provider, layout, monitor) = Build();
        var hook = BindHook(widget);
        if (hook == null) return;

        SetField(widget, "moveCommitPending", true);
        widget.Position = new PixelPoint(500, 333); // raw drop position

        var handled = false;
        hook(IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);

        Check(!GetField(widget, "moveCommitPending"), "the loop exit consumed the pending commit");
        Check(widget.Position == new PixelPoint(576, 384),
            $"the drop at (500,333) snaps into the grid cell (actual {widget.Position}, expected 576,384)");
        Check(layout.Writes == 1, "the commit saved the position exactly once");
        Check(provider.Get().X == 576 && provider.Get().Y == 384,
            $"the snapped position persisted to storage (actual {provider.Get().X},{provider.Get().Y})");

        // A second exit (or the fallback dispatching late) must not save again.
        hook(IntPtr.Zero, 0x0232, IntPtr.Zero, IntPtr.Zero, ref handled);
        InvokeInternal(widget, "CommitPendingMoveAfterLoop");
        Check(layout.Writes == 1, "a double commit (hook + late fallback) saves exactly once");
    }

    // ---- 4. Fallback commits a drag that never entered the loop ----

    private static void CheckFallbackCommitsWhenLoopNeverEntered()
    {
        Console.WriteLine("--- Fallback commits a drag that never entered the move loop ---");
        var (widget, provider, layout, monitor) = Build();

        // BeginMoveDrag failing / a host without WM_ENTERSIZEMOVE: the flag is pending and
        // the loop was never entered — the fallback must own the commit.
        SetField(widget, "moveCommitPending", true);
        SetField(widget, "moveLoopActive", false);
        widget.Position = new PixelPoint(500, 333);

        InvokeInternal(widget, "CommitPendingMoveAfterLoop");

        Check(!GetField(widget, "moveCommitPending"), "the fallback consumed the pending commit");
        Check(widget.Position == new PixelPoint(576, 384),
            $"the drop position snapped into the grid (actual {widget.Position}, expected 576,384)");
        Check(layout.Writes == 1, "the fallback saved the position exactly once");
    }

    // ---- rig ----

    private static (Widget, WidgetLayoutProvider, LayoutStore, DisplayMonitorService) Build()
    {
        var settings = new SettingsStore(new AppSettings(
            new Theme(true, null, 1, false, false, "Segoe UI", Surface: SurfaceStyle.Solid), [],
            new Layout(GridMode.Manual, false, false, false, false), new Dimensions(100, 10, 16),
            new Region("en"), false, null));
        var entry = new WidgetLayout("Test", nameof(StubContent), 500, 333, 384, 384, null);
        var screenConfig = new ScreenLayout("screen-a", "A|1920x1080", null, null, new Grid(8, 6, 0, 0, 10), null, [entry]);
        var layout = new LayoutStore(new ScreensLayout([screenConfig]));
        var monitor = new DisplayMonitorService(layout);
        // Seed the seams the real service reads; Refresh() stays frozen so the headless
        // screen stub can never flip the topology-observation state mid-check.
        typeof(DisplayMonitorService).GetField("anchor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, new Window());
        typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, true);
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor,
            new List<AttachedScreen>
            {
                new(new Screen(1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true),
                    new ScreenIdentity("A", "A", 1920, 1080, true, 1), screenConfig)
            });
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.IsTopologyChanging))!
            .SetValue(monitor, false);

        var provider = new WidgetLayoutProvider(layout, screenConfig.Id, entry);
        var profiles = (ProfileService)RuntimeHelpers.GetUninitializedObject(typeof(ProfileService));
        var widget = new Widget(settings, provider, new GridService(settings, monitor), layout, monitor, profiles,
            () => new StubContent(), () => throw new InvalidOperationException());
        MakePositionAware(widget);
        return (widget, provider, layout, monitor);
    }

    /// <summary>
    /// Bind the exact callback the constructor registers for real drags: the private
    /// WndProc hook, as a <see cref="Win32Properties.CustomWndProcHookCallback"/>, so the
    /// check drives the production hook body headlessly (headless has no real HWND, and
    /// Win32Properties only attaches to the Win32 backend).
    /// </summary>
    private static Win32Properties.CustomWndProcHookCallback? BindHook(Widget widget)
    {
        var method = typeof(Widget).GetMethod("OnWidgetWndProc",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
        Check(method != null, "Widget declares the native move-loop wndproc hook");
        if (method == null) return null;

        try
        {
            return (Win32Properties.CustomWndProcHookCallback)Delegate.CreateDelegate(
                typeof(Win32Properties.CustomWndProcHookCallback), widget, method);
        }
        catch (Exception ex)
        {
            Check(false, $"hook binds to CustomWndProcHookCallback ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    private static void SetField(Widget widget, string name, bool value) =>
        typeof(Widget).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(widget, value);

    private static bool GetField(Widget widget, string name) =>
        (bool)typeof(Widget).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(widget)!;

    private static void InvokeInternal(Widget widget, string name) =>
        typeof(Widget).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
            .Invoke(widget, null);

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

    private sealed class StubContent : UserControl { }
}
