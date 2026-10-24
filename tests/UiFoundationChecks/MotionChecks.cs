using System;
using System.IO;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.UI.Animation;
using DeskCanvas.ViewModels;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using LiquidGlassSurface = DeskCanvas.Views.Controls.LiquidGlassSurface;

namespace UiFoundationChecks;

/// <summary>
/// Motion checks — the spring core ported from the Windhawk "Mac Island &amp; Dock" mod.
/// <list type="bullet">
/// <item><b>Curve shape</b>: the shipped easing starts at 0, lands on 1, and the bounce preset
/// peaks ~3.8% above the target near 45% of the duration — the mod's <c>kBounceDamping</c>
/// budget. Critical damping never overshoots (an overshooting alpha would clip and dip).</item>
/// <item><b>Integrator</b>: settles exactly on the target, reproduces the same overshoot, and
/// produces the same trajectory at 60 Hz and 240 Hz (the fixed 1/480 s substep).</item>
/// <item><b>Retarget</b>: changing the target mid-flight keeps the velocity — the property that
/// makes an interrupted animation read as physical instead of restarting.</item>
/// </list>
/// </summary>
internal static partial class Program
{
    private static void CheckMotion()
    {
        Console.WriteLine("--- motion: spring curve + integrator (Mac Island & Dock port) ---");

        var bounce = SpringMotion.CreateEasing(SpringMotion.Bounce);
        var smooth = SpringMotion.CreateEasing(SpringMotion.Smooth);

        Check(Math.Abs(bounce.Ease(0)) < 1e-9, "bounce curve starts at 0");
        Check(Math.Abs(smooth.Ease(0)) < 1e-9, "smooth curve starts at 0");
        Check(Math.Abs(bounce.Ease(1) - 1.0) < 0.005,
            $"bounce curve is within 0.5% of the target at the end ({bounce.Ease(1):0.0000})");
        Check(Math.Abs(smooth.Ease(1) - 1.0) < 0.005,
            $"smooth curve is within 0.5% of the target at the end ({smooth.Ease(1):0.0000})");

        var bouncePeak = 0.0;
        var bouncePeakAt = 0.0;
        var smoothPeak = 0.0;
        for (var i = 0; i <= 1000; i++)
        {
            var t = i / 1000.0;
            var b = bounce.Ease(t);
            if (b > bouncePeak) { bouncePeak = b; bouncePeakAt = t; }
            smoothPeak = Math.Max(smoothPeak, smooth.Ease(t));
        }
        Check(bouncePeak > 1.02 && bouncePeak < 1.06,
            $"bounce preset overshoots by ~3.8% (peak {bouncePeak:0.0000})");
        Check(bouncePeakAt > 0.35 && bouncePeakAt < 0.6,
            $"the overshoot peaks near the middle of the duration ({bouncePeakAt:0.000})");
        Check(smoothPeak <= 1.0 + 1e-9,
            $"critical damping never overshoots (peak {smoothPeak:0.000000})");

        // The integrator must land exactly on the target and stop reporting movement.
        var spring = new Spring(0);
        spring.SetTarget(1);
        var moving = true;
        var integratorPeak = 0.0;
        for (var i = 0; i < 600 && moving; i++)
        {
            moving = spring.Step(1.0 / 60.0, 0.32, SpringMotion.Bounce);
            integratorPeak = Math.Max(integratorPeak, spring.Value);
        }
        Check(!moving, "the spring reports rest once settled");
        Check(Math.Abs(spring.Value - 1.0) < 1e-12,
            $"the spring lands exactly on its target ({spring.Value:0.000000})");
        Check(integratorPeak > 1.01 && integratorPeak < 1.08,
            $"the integrator reproduces the curve's overshoot (peak {integratorPeak:0.0000})");

        // Frame-rate independence: 1 s of motion at 60 Hz and at 240 Hz must agree.
        static double RunFor(double dt, int steps)
        {
            var s = new Spring(0);
            s.SetTarget(1);
            for (var i = 0; i < steps; i++) s.Step(dt, 0.32, SpringMotion.Bounce);
            return s.Value;
        }
        var at60 = RunFor(1.0 / 60.0, 60);
        var at240 = RunFor(1.0 / 240.0, 240);
        Check(Math.Abs(at60 - at240) < 1e-6,
            $"60 Hz and 240 Hz produce the same trajectory ({at60:0.000000} vs {at240:0.000000})");

        // Retargeting mid-flight keeps the velocity (a transition would restart from rest).
        var retarget = new Spring(0);
        retarget.SetTarget(1);
        for (var i = 0; i < 10; i++) retarget.Step(1.0 / 60.0, 0.32, SpringMotion.Bounce);
        var carried = retarget.Velocity;
        retarget.SetTarget(0.4);
        Check(carried > 0.1, $"the spring is moving before the retarget (v = {carried:0.000})");
        Check(Math.Abs(retarget.Velocity - carried) < 1e-12,
            "retargeting keeps the velocity (no restart from rest)");
    }

    /// <summary>
    /// Probe for <c>TopLevel.RequestAnimationFrame</c> under the headless platform (动效交接
    /// §4.2/§8.2: the spring integrator's driver must be verified BEFORE anything is built on it,
    /// or the checks would hang). The callback must fire from the dispatcher jobs or from a forced
    /// render tick — whichever it is, the integrator's self-recursive frame loop is safe to drive
    /// in this harness.
    /// </summary>
    private static void CheckAnimationFrameProbe()
    {
        Console.WriteLine("--- motion: RequestAnimationFrame probe (headless) ---");

        var window = new Window { Width = 120, Height = 90, WindowDecorations = WindowDecorations.None };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var fired = 0;
        window.RequestAnimationFrame(_ => fired++);
        Dispatcher.UIThread.RunJobs();
        var afterJobs = fired;

        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Check(fired > 0 && (afterJobs > 0 || fired > afterJobs),
            $"RequestAnimationFrame fires headlessly (jobs {afterJobs}, after a forced render tick {fired})");

        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The highlight bar's full flight, on a REALLY shown headless window with the RAF loop
    /// driven the way production drives it. This is the reproduction harness for the reported
    /// “选中框锁在中间偏下 / 每次只滑半个行距”: the bar must converge on the selected row's
    /// rect after the first result set AND after every arrow step — a spring that dies
    /// mid-flight shows up here as a bar that never reaches its own target.
    /// </summary>
    private static void CheckResultHighlightFlight()
    {
        Console.WriteLine("--- motion: result highlight flight (shown window, driven RAF) ---");

        var sandbox = Path.Combine(Path.GetTempPath(), "uifoundation-highlight-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(sandbox);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        var folder = Path.Combine(sandbox, "Launcher");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "Aurora Notes", "Blue Horizon", "Canvas Studio", "Delta Files", "Ember Music" })
        {
            File.WriteAllText(Path.Combine(folder, name + ".url"),
                "[InternetShortcut]\r\nURL=https://example.com/" + name.Replace(" ", "-").ToLowerInvariant() + "\r\n");
        }

        var catalog = new LauncherCatalog();
        catalog.Start(folder);

        var settings = new AppSettings(
            Theme: new Theme(null, null, 0.2, false, false, "Segoe UI", SurfaceStyle.FullColor),
            Templates: [],
            Layout: new Layout(GridMode.Manual, SnapSize: true, LockSize: false, SnapPosition: true, LockPosition: false),
            Dimensions: new Dimensions(160, 12, 24),
            Region: new Region("en-US"),
            RunOnStartup: false, IgnoreUpdate: null,
            Launcher: new LauncherSettings(Enabled: true, WebSearch: false, WebResultCount: 0));
        var provider = new FlightSettingsProvider(settings);
        var icons = new ShellIconService();
        var usage = new SpotlightUsageStore(Path.Combine(sandbox, "usage.json"));
        var clipboard = new SpotlightClipboardService(provider);
        var files = new FileSearchService(provider);
        var router = new SpotlightQueryRouter(
            new SpotlightAppProvider(catalog, usage),
            new SpotlightFileProvider(files),
            new SpotlightClipboardProvider(clipboard),
            new SpotlightExternalResults(),
            provider,
            new SpotlightWebProvider(provider));
        var viewModel = new SpotlightViewModel(router, catalog, icons, usage, clipboard, files, provider,
            new SpotlightPreviewService(icons), new SpotlightWebResults(provider));
        viewModel.Reload();

        var window = new SpotlightWindow(viewModel, provider);
        window.ApplyAppearanceForChecks();
        window.ApplySummonModeForChecks(SpotlightSummonMode.Search);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Drive layout + RAF the way a shown window's render clock would.
        static void Tick(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
            }
        }

        double BarY() => window.ResultHighlightRectForChecks.Y;
        double BarH() => window.ResultHighlightRectForChecks.Height;

        bool Converges(string what)
        {
            // The spring settles in well under a second of driven frames; each failing snapshot
            // is reported so a half-flight is visible in the log, not just a boolean.
            var target = window.ResultHighlightTargetForChecks;
            if (target is not { } rect)
            {
                Check(false, $"{what}: a target exists to converge on");
                return false;
            }

            for (var i = 0; i < 120; i++)
            {
                Tick(1);
                if (Math.Abs(BarY() - rect.Y) < 0.75 && Math.Abs(BarH() - rect.Height) < 0.75) break;
            }

            var ok = Math.Abs(BarY() - rect.Y) < 0.75 && Math.Abs(BarH() - rect.Height) < 0.75;
            Check(ok, $"{what}: the bar converges on the selected row " +
                      $"(bar {BarY():F1}+{BarH():F1}, target {rect.Y:F1}+{rect.Height:F1})");
            return ok;
        }

        window.SetQueryForChecks("notes");
        PumpHighlightUntil(() => viewModel.Results.Count > 0 && !viewModel.IsSearching);
        Tick(6);
        Converges("first result set");

        for (var step = 1; step <= 3; step++)
        {
            Check(window.HandleKeyForChecks(Key.Down), $"↓ #{step} is claimed");
            Tick(6);
            Converges($"after ↓ #{step}");
        }

        // A rebuilt result set must re-snap, not glide from the old rows (the reported lockup).
        window.SetQueryForChecks("blue");
        PumpHighlightUntil(() => !viewModel.IsSearching);
        Tick(6);
        Converges("after the query rebuild");

        window.Close();
        Dispatcher.UIThread.RunJobs();
        catalog.Dispose();
        viewModel.Dispose();
        try { Directory.Delete(sandbox, true); } catch { }
    }

    /// <summary>Pump the dispatcher (search routing is async) without a render tick loop.</summary>
    private static void PumpHighlightUntil(Func<bool> done)
    {
        for (var i = 0; i < 400 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            System.Threading.Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Minimal settings provider for the flight harness (same shape as the visual one).</summary>
    private sealed class FlightSettingsProvider(AppSettings settings) : IAppSettingsProvider
    {
        private AppSettings current = settings;
        public AppSettings Get() => current;
        public void Save(AppSettings data) { current = data; }
        public void Reload() { }
        public event DataChangedEvent<AppSettings>? DataChanging;
        public event DataChangedEvent<AppSettings>? DataChanged;
    }

    /// <summary>
    /// Probe: how a Panel arranges a child that has an explicit Height + Margin.Top but keeps
    /// the default (Stretch) vertical alignment — the exact shape ResultHighlight was in when
    /// the bar "stuck at the panel's middle and moved half a row per step". If Stretch behaves
    /// like Center here, Margin.Top alone is NOT a position and Top alignment is mandatory.
    /// </summary>
    private static void CheckStretchArrangementProbe()
    {
        Console.WriteLine("--- motion: panel arrangement of an explicit-height child ---");

        var panel = new Panel { Width = 400, Height = 430 };
        var bar = new Border { Width = 300, Height = 48, Margin = new Thickness(14, 37, 0, 0) };
        panel.Children.Add(bar);
        var host = new Window { Width = 420, Height = 470, WindowDecorations = WindowDecorations.None };
        host.Content = panel;
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Console.WriteLine($"  probe: margin.top=37, height=48 -> Bounds.Y={bar.Bounds.Y:F1} " +
                          "(Top alignment would be 37; the default Stretch centres the remaining space)");
        Check(Math.Abs(bar.Bounds.Y - 37) > 1,
            "stretch arrangement: without Top, an explicit-height child is NOT at its margin.top " +
            $"({bar.Bounds.Y:F1}) — ResultHighlight's Top alignment is load-bearing");

        // The pinned twin: identical bar, Top-aligned — lands exactly where the margin says.
        var pinned = new Border
        {
            Width = 300, Height = 48, Margin = new Thickness(14, 37, 0, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        panel.Children.Add(pinned);
        panel.InvalidateMeasure();
        panel.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(pinned.Bounds.Y - 37) < 0.5,
            $"stretch arrangement: Top alignment lands the bar at its margin.top ({pinned.Bounds.Y:F1})");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The glass shape-morph port (聚焦搜索 bar → card): the rect feed, the at-rest judgement,
    /// the mutual exclusion with the uniform-scale feed, and — the one mapping the whole morph
    /// stands or falls on — the animated frame's <b>desktop</b> sampling origin. A rect-shaped
    /// frame must translate the covered rect into render pixels on top of the surface's own
    /// desktop origin, or the refracted content would swim while the card grows
    /// (动效交接 §8.3, the highest-risk line of the port).
    /// </summary>
    private static void CheckGlassShapePort()
    {
        Console.WriteLine("--- motion: glass shape-morph port (SetAnimationFrameShape) ---");

        var surface = new LiquidGlassSurface
        {
            Material = new Theme(null, null, 0.18, true, false, "Microsoft YaHei UI", SurfaceStyle.LiquidGlass),
            CornerRadius = new CornerRadius(18),
        };
        var host = new Window { Width = 460, Height = 580, WindowDecorations = WindowDecorations.None };
        host.Content = surface;
        host.Show();
        Dispatcher.UIThread.RunJobs();
        Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        static T? Read<T>(object instance, string field) where T : struct
            => instance.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(instance) is T value ? value : null;

        // ---- the rect feed ----
        surface.SetAnimationFrameShape(new Rect(0, 0, 460, 100), 34);
        Check(Read<bool>(surface, "animationShapeActive") == true
              && Read<Rect>(surface, "animationShape") is { } fed && Math.Abs(fed.Height - 100) < 0.5,
            "a partial rect is remembered as the morphing shape");
        Check(Math.Abs((Read<double>(surface, "animationShapeRadius") ?? 0) - 34) < 0.01,
            "the shape's radius is remembered (DIP, un-clamped by the port)");

        // ---- the frame mapping: desktop sampling origin preserved (§8.3) ----
        var frame = new LiquidGlassRenderer.Frame(
            Width: 920, Height: 1160, Scale: 2f, Radius: 36f,
            DesktopX: 120f, DesktopY: 240f, DesktopWidth: 2560f, DesktopHeight: 1440f,
            ScreenX: 0f, ScreenY: 0f, ScreenWidth: 2560f, ScreenHeight: 1440f,
            new Theme(null, null, 0.18, true, false, "Microsoft YaHei UI", SurfaceStyle.LiquidGlass),
            Dark: false);
        var preparedType = typeof(LiquidGlassSurface).GetNestedType("Prepared", BindingFlags.NonPublic)!;
        var prepared = preparedType.GetConstructors()[0].Invoke([null, frame, null, 0L]);
        var build = typeof(LiquidGlassSurface).GetMethod(
            "BuildAnimationFrame", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var animated = (LiquidGlassRenderer.Frame)build.Invoke(surface, [prepared])!;

        Check(Math.Abs(animated.Width - 920) < 1 && Math.Abs(animated.Height - 200) < 1,
            $"the animated frame carries the covered rect in render pixels ({animated.Width}×{animated.Height})");
        Check(Math.Abs(animated.Radius - 34) < 0.01,
            $"the animated frame keeps the radius in DIP ({animated.Radius})");
        Check(Math.Abs(animated.DesktopX - 120f) < 0.5 && Math.Abs(animated.DesktopY - 240f) < 0.5,
            $"the sampling origin stays at the surface's desktop origin for a top-anchored rect " +
            $"({animated.DesktopX:F0},{animated.DesktopY:F0})");

        // An offset rect shifts the origin by the offset in render pixels — desktop coordinates
        // both sides, which is exactly what keeps the refraction from swimming mid-morph.
        surface.SetAnimationFrameShape(new Rect(20, 40, 400, 90), 18);
        var shifted = (LiquidGlassRenderer.Frame)build.Invoke(surface, [prepared])!;
        Check(Math.Abs(shifted.DesktopX - (120f + 40f)) < 0.5 && Math.Abs(shifted.DesktopY - (240f + 80f)) < 0.5,
            $"an offset rect moves the sampling origin with it, in render pixels " +
            $"({shifted.DesktopX:F0},{shifted.DesktopY:F0})");
        Check(Math.Abs(shifted.DesktopWidth - 2560f) < 0.5 && Math.Abs(shifted.ScreenWidth - 2560f) < 0.5,
            "the shape morph never touches the desktop/screen geometry the backdrop samples");

        // ---- at rest: the whole bounds with the styled radius returns the material to rest ----
        surface.SetAnimationFrameShape(
            new Rect(0, 0, surface.Bounds.Width, surface.Bounds.Height), 18);
        Check(Read<bool>(surface, "animationShapeActive") != true,
            "the surface's own bounds + styled radius return the glass to rest");

        // ---- mutual exclusion with the uniform-scale feed (the panels' port) ----
        surface.SetAnimationFrameShape(new Rect(0, 0, 460, 100), 34);
        surface.SetAnimationFrameScale(0.8, new Point(0.5, 0.5));
        Check(Read<bool>(surface, "animationShapeActive") != true
              && Math.Abs((Read<double>(surface, "animationScale") ?? 1) - 0.8) < 0.001,
            "the uniform-scale feed takes over from the shape feed");
        surface.SetAnimationFrameShape(new Rect(0, 0, 460, 100), 34);
        Check(Read<bool>(surface, "animationShapeActive") == true
              && Math.Abs((Read<double>(surface, "animationScale") ?? 0) - 1.0) < 0.001,
            "the shape feed takes over from the uniform-scale feed");

        host.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
