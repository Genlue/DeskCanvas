using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.UI.Animation;
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
