using System;
using Avalonia.Animation.Easings;

namespace DeskCanvas.UI.Animation;

/// <summary>
/// A damped spring integrator: the motion primitive behind the animation feel of the
/// "Mac Island &amp; Dock" Windhawk mod, ported to managed code.
/// <para>
/// The mod never uses fixed-duration easing for anything the eye tracks. Every moving
/// quantity is a <c>SpringTowards</c> call — a critically-damped (or deliberately
/// under-damped) oscillator integrated with a <b>fixed</b> 1/480 s substep, so the trajectory
/// does not change with the frame rate and a late frame cannot make the motion jump ahead.
/// <see cref="Step"/> reproduces that integrator exactly; the caller supplies the frame's
/// <c>dt</c> and is expected to clamp it (the mod clamps to 25 ms, or 1.5 frame intervals
/// when a frame-rate limit is configured).
/// </para>
/// <para>
/// <b>This is for motion whose target can move mid-flight</b> — hover, drag, resize, a panel
/// reopened mid-close. A transition re-starts from rest on every retarget; a spring keeps its
/// velocity, which is the whole reason the mod's island feels physical. When the target is
/// known up front and nothing can interrupt it, use <see cref="SpringMotion.CreateEasing"/>
/// instead: the same physics, pre-computed onto Avalonia's render clock.
/// </para>
/// </summary>
public struct Spring
{
    /// <summary>The integrator's fixed substep. Matches the mod's <c>kStep</c>.</summary>
    private const double Substep = 1.0 / 480.0;

    /// <summary>A frame longer than this is treated as this long, so a stall cannot teleport the value.</summary>
    private const double MaxFrame = 0.1;

    public Spring(double value = 0)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    /// <summary>The current value.</summary>
    public double Value { get; private set; }

    /// <summary>Where the value is heading.</summary>
    public double Target { get; private set; }

    /// <summary>Units per second; carried across retargets.</summary>
    public double Velocity { get; private set; }

    /// <summary>Retarget without disturbing the velocity — the move that reads as physical.</summary>
    public void SetTarget(double target) => Target = target;

    /// <summary>Jump to a value and come to rest (a new animation starting from here).</summary>
    public void SnapTo(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0;
    }

    /// <summary>
    /// Advance one frame. <paramref name="seconds"/> is the spring's time scale (ω = 8 / seconds,
    /// as in the mod); <paramref name="damping"/> 1 is critical, below 1 overshoots.
    /// Returns true while the spring is still moving.
    /// </summary>
    public bool Step(double dt, double seconds, double damping = 1.0)
    {
        if (!(seconds > 0) || double.IsNaN(dt) || dt <= 0)
        {
            return IsMoving();
        }

        if (damping <= 0) damping = 1.0;

        var omega = 8.0 / seconds;
        var frame = Math.Min(dt, MaxFrame);

        // Fixed substeps, not one big Euler step: the trajectory must be identical whether the
        // frame took 6 ms or 24 ms (the mod's integrator does exactly this).
        for (var remaining = frame; remaining > 0; remaining -= Substep)
        {
            var h = Math.Min(remaining, Substep);
            var acceleration = omega * omega * (Target - Value) - 2.0 * damping * omega * Velocity;
            Velocity += acceleration * h;
            Value += Velocity * h;
        }

        if (Math.Abs(Target - Value) < 0.001 && Math.Abs(Velocity) < 0.01)
        {
            Value = Target;
            Velocity = 0;
            return false;
        }

        return true;
    }

    /// <summary>Whether the spring would keep moving if stepped (false at rest).</summary>
    public bool IsMoving()
        => Math.Abs(Target - Value) >= 0.001 || Math.Abs(Velocity) >= 0.01;
}

/// <summary>
/// The spring vocabulary: the mod's damping presets, and the bridge from a damping ratio to an
/// Avalonia easing so a plain <c>DoubleTransition</c> can carry the same motion.
/// <para>
/// A spring and an easing curve are the same physics seen from two sides. When the target is
/// known up front and nothing can interrupt it (a panel opening), the trajectory can be
/// pre-computed — that is what <see cref="CreateEasing"/> returns, and it moves exactly like
/// the mod's island while still interpolating on Avalonia's render clock.
/// </para>
/// <para>
/// <b>Why the stiffness is fixed at 100:</b> Avalonia's <see cref="SpringEasing"/> maps a
/// transition's progress 0→1 onto one unit of spring time. ω = √(stiffness / mass) = 10 puts
/// the overshoot peak near 45% of the duration and leaves ~0.1% residual at the end — the
/// same shape the mod's ω = 8 / seconds produces, with no tail to snap away.
/// </para>
/// </summary>
public static class SpringMotion
{
    /// <summary>Critical damping: settles without overshoot. The mod's default.</summary>
    public const double Smooth = 1.0;

    /// <summary>The mod's <c>kBounceDamping</c> — a visible but restrained ~3.8% overshoot.</summary>
    public const double Bounce = 0.72;

    /// <summary>The mod's <c>kPanelWidthDamping</c> — the liveliest preset it uses (~7.5%).</summary>
    public const double Lively = 0.62;

    /// <summary>Natural frequency of the normalized spring (see the type remarks).</summary>
    private const double Omega = 10.0;

    /// <summary>Spring constant for a unit mass; with <see cref="Omega"/> = 10.</summary>
    private const double Stiffness = Omega * Omega;

    /// <summary>
    /// The mod's damping ratio as an Avalonia easing. Feed it to any transition:
    /// <c>new DoubleTransition { Property = …, Duration = …, Easing = SpringMotion.CreateEasing(SpringMotion.Bounce) }</c>.
    /// </summary>
    /// <param name="damping">Damping ratio; 1 is critical, below 1 overshoots.</param>
    public static SpringEasing CreateEasing(double damping = Bounce)
        => new(mass: 1.0, stiffness: Stiffness, damping: 2.0 * (damping > 0 ? damping : Smooth) * Omega);
}
