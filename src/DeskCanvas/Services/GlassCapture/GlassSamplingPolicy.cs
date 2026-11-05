using System;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// The live sampler's cadence arithmetic: how often the desktop is <b>looked at</b>, how far the
/// idle stretch is allowed to move that number, and how long the surfaces may digest a publish
/// before the next capture may start.
/// <para>
/// Extracted from <c>LiquidGlassWallpaper</c> because three separate bugs lived in these three
/// numbers and none of them was assertable — they sat behind a dispatcher, a waitable timer and a
/// D3D11 session, so the only way to catch a regression was to watch the glass and guess. Nothing
/// here touches a device, a clock or a window; every value is an int in, an int out.
/// </para>
/// </summary>
internal static class GlassSamplingPolicy
{
    /// <summary>
    /// Frame-production cadence handed to a capture session that the live sampler is <b>not</b>
    /// driving — the wallpaper-change settle window, which probes at
    /// <see cref="SettleIntervalMs"/> and does not need the system to produce anything faster.
    /// <para>
    /// This is the value the wallpaper session used to be pinned to <i>always</i>, because
    /// <c>WallpaperBackdropSource.SetCadence</c> had no caller: a 10 ms request then produced one
    /// frame per 250 ms and every faster probe found nothing.
    /// </para>
    /// </summary>
    internal const int StaticCaptureCadenceMs = 250;

    /// <summary>Probe cadence inside a wallpaper-change settle window.</summary>
    internal const int SettleIntervalMs = 250;

    /// <summary>
    /// Longest the sampler stretches between probes while the desktop stays unchanged. The stretch
    /// exists so an idle desktop costs a few captures per second instead of the pipeline's maximum
    /// rate; it does not throttle a changing one — the first probe that finds new content snaps the
    /// interval back to the user's setting.
    /// </summary>
    internal const int MaxIdleIntervalMs = 500;

    /// <summary>
    /// How long the <b>capture source</b> must have been silent before "no new frame this round" is
    /// allowed to count as "the desktop is still".
    /// <para>
    /// Without this distinction a source that merely produces slower than the poll — a 25 fps video
    /// wallpaper, a host that coalesces updates, a session whose <c>MinUpdateInterval</c> the OS
    /// refuses to honour — reads as a still desktop on every empty round, and the idle stretch
    /// drives the sampler to the 500 ms ceiling while the wallpaper is very much moving. That was
    /// the reported "对动态壁纸的采样率极低（哪怕设置了 10ms）".
    /// </para>
    /// </summary>
    internal const int QuietBeforeIdleMs = 250;

    /// <summary>Floor of the stall guard; below one 60 Hz frame it stops meaning anything.</summary>
    internal const int MinStallGuardMs = 16;

    /// <summary>Ceiling of the stall guard.</summary>
    internal const int MaxStallGuardMs = 1000;

    /// <summary>Cap on the idle shift; 2^6 × 10 ms already exceeds <see cref="MaxIdleIntervalMs"/>.</summary>
    private const int MaxIdleShift = 6;

    /// <summary>
    /// The frame-production cadence to hand a capture session. <paramref name="intervalMs"/> is
    /// expected already clamped by the caller (<c>LiquidGlassSettings.Normalize</c>).
    /// <para>
    /// Live sampling on → exactly the user's interval: the system producing faster than the
    /// sampler polls would only burn GPU, and slower is what made a 10 ms setting unreachable.
    /// </para>
    /// </summary>
    internal static int CaptureCadence(bool liveSampling, int intervalMs) =>
        liveSampling ? Math.Max(1, intervalMs) : StaticCaptureCadenceMs;

    /// <summary>
    /// The period the pacer actually waits for: the user's interval, stretched while the desktop
    /// stays unchanged, and pinned to the user's interval for the whole of a drag — a drag moves the
    /// sampling <b>region</b> while the wallpaper itself stays still, so the idle probe would read
    /// "unchanged" and stretch the interval, leaving the capture rectangle up to half a second
    /// behind the card.
    /// </summary>
    /// <param name="settleWithoutLive">
    /// True while a wallpaper-change settle window is the only thing asking for frames: it runs at
    /// <see cref="SettleIntervalMs"/>, and the live sampler's own cadence and backoff are untouched.
    /// </param>
    internal static int EffectiveInterval(int intervalMs, int idleRounds, bool dragging,
        bool settleWithoutLive)
    {
        if (settleWithoutLive) return SettleIntervalMs;
        if (dragging) return intervalMs;

        var idle = Math.Clamp(idleRounds, 0, MaxIdleShift);
        var stretched = (long)intervalMs << idle;
        var cap = Math.Max(intervalMs, MaxIdleIntervalMs);
        return (int)Math.Min(Math.Max(stretched, intervalMs), cap);
    }

    /// <summary>
    /// Longest a new round may be held off by busy surfaces before the tick forces through anyway,
    /// measured from the last capture.
    /// <para>
    /// Proportional to the requested period (four of them, so a surface gets a full publish +
    /// prepare window), with a 16 ms floor and the historic 1 s ceiling. The floor used to be
    /// 150 ms, which silently capped a 10 ms request at ~6.7 fps whenever a card was mid-prepare —
    /// a ceiling the drag path skipped, which is why a drag looked correct and a still desktop
    /// did not.
    /// </para>
    /// </summary>
    internal static int StallGuard(int effectiveIntervalMs, bool settleWithoutLive) =>
        settleWithoutLive
            // Settle rounds must not be held off for a full second while the fade is still moving.
            ? 2 * SettleIntervalMs
            : Math.Clamp(4 * effectiveIntervalMs, MinStallGuardMs, MaxStallGuardMs);

    /// <summary>
    /// Whether an empty round is evidence that the desktop is still.
    /// <para>
    /// A source that has produced nothing for a while is idle (or absent): the stretch is correct.
    /// A source that produced a frame within the quiet window is alive and merely slower than the
    /// poll, and stretching for it would drop the achieved rate below the wallpaper's own.
    /// </para>
    /// </summary>
    /// <param name="sourceSilenceMs">
    /// Milliseconds since the capture session last delivered a frame; negative when there is no live
    /// source at all (the static wallpaper image), which is idle by definition.
    /// </param>
    internal static bool CountsAsIdle(long sourceSilenceMs, int effectiveIntervalMs)
    {
        if (sourceSilenceMs < 0) return true;
        var quiet = Math.Max(QuietBeforeIdleMs, 2 * Math.Max(1, effectiveIntervalMs));
        return sourceSilenceMs >= quiet;
    }
}
