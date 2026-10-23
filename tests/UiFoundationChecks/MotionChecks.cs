using System;
using Avalonia.Animation.Easings;
using DeskCanvas.UI.Animation;

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
}
