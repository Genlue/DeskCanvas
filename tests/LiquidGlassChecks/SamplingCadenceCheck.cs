using DeskCanvas.Services.GlassCapture;

/// <summary>
/// The live sampler's <b>cadence</b> — the three numbers that decide how often the desktop is
/// actually looked at. Pure arithmetic, so it is pinned here rather than by watching the glass.
/// <para>
/// Each assertion maps to a defect that shipped:
/// </para>
/// <list type="number">
///   <item><description><b>Capture cadence.</b> The wallpaper session was pinned to
///   <see cref="GlassSamplingPolicy.StaticCaptureCadenceMs"/> because nothing ever called
///   <c>WallpaperBackdropSource.SetCadence</c>: a 10 ms request produced at 4 fps and the glass
///   looked frozen on every animated wallpaper.</description></item>
///   <item><description><b>Idle stretch.</b> "No new frame" was read as "still desktop", so a
///   source producing slower than the poll walked the interval to the 500 ms ceiling — while the
///   wallpaper was moving.</description></item>
///   <item><description><b>Stall guard.</b> A 150 ms floor capped a 10 ms request at ~6.7 fps
///   whenever a card was mid-prepare. The drag path skipped the guard, which is exactly why a
///   drag looked right and a still desktop did not.</description></item>
/// </list>
/// </summary>
internal static class SamplingCadenceCheck
{
    private static int failures;

    public static int Run()
    {
        failures = 0;
        Console.WriteLine("=== 采样节奏 (capture cadence / idle stretch / stall guard) ===");

        CaptureCadence();
        IdleStretch();
        StallGuard();
        IdleEvidence();
        StaticRefresh();

        Console.WriteLine(failures == 0 ? "glass sampling cadence check passed" : $"{failures} SAMPLING CADENCE CHECK(S) FAILED");
        return failures;
    }

    /// <summary>
    /// The session produces frames at this rate, so it is the ceiling on everything downstream: a
    /// value larger than the probe interval makes the probe find nothing by construction.
    /// </summary>
    private static void CaptureCadence()
    {
        Check(GlassSamplingPolicy.CaptureCadence(liveSampling: true, 10) == 10,
            "live sampling at 10 ms hands the capture session a 10 ms cadence (the reported bug)");
        Check(GlassSamplingPolicy.CaptureCadence(liveSampling: true, 250) == 250,
            "a 250 ms request reaches the session unchanged");
        Check(GlassSamplingPolicy.CaptureCadence(liveSampling: true, 1000) == 1000,
            "the slowest request is passed through (1 fps)");
        Check(GlassSamplingPolicy.CaptureCadence(liveSampling: true, 1) == 1,
            "a degenerate interval still leaves the session producing (never 0 = unthrottled)");
        Check(GlassSamplingPolicy.CaptureCadence(liveSampling: false, 10) ==
              GlassSamplingPolicy.StaticCaptureCadenceMs,
            "live sampling off keeps the settle cadence (250 ms), whatever interval is stored");
    }

    /// <summary>
    /// The period the pacer waits for: the user's interval, doubled per idle round up to the
    /// 500 ms ceiling, pinned for a drag, and replaced by the settle cadence when only the settle
    /// window wants frames.
    /// </summary>
    private static void IdleStretch()
    {
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 0, dragging: false, settleWithoutLive: false) == 10,
            "a fresh interval is the user's own");
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 1, dragging: false, settleWithoutLive: false) == 20,
            "one idle round doubles the interval");
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 3, dragging: false, settleWithoutLive: false) == 80,
            "three idle rounds are 8x");
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 6, dragging: false, settleWithoutLive: false) ==
              GlassSamplingPolicy.MaxIdleIntervalMs,
            "the stretch stops at the 500 ms ceiling");
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 99, dragging: false, settleWithoutLive: false) ==
              GlassSamplingPolicy.MaxIdleIntervalMs,
            "an absurd idle count cannot overflow past the ceiling");
        Check(GlassSamplingPolicy.EffectiveInterval(1000, idleRounds: 0, dragging: false, settleWithoutLive: false) == 1000,
            "a 1 fps request is not sped up by the ceiling (cap = max(interval, 500))");

        // A drag moves the sampling REGION while the wallpaper stays still: the idle stretch has to
        // get out of the way or the capture rectangle trails the card by up to half a second.
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 6, dragging: true, settleWithoutLive: false) == 10,
            "a drag ignores the idle stretch entirely");
        Check(GlassSamplingPolicy.EffectiveInterval(10, idleRounds: 6, dragging: true, settleWithoutLive: true) ==
              GlassSamplingPolicy.SettleIntervalMs,
            "the settle cadence wins over the drag override (the window owns the probe while it runs)");
        Check(GlassSamplingPolicy.EffectiveInterval(250, idleRounds: 0, dragging: false, settleWithoutLive: true) ==
              GlassSamplingPolicy.SettleIntervalMs,
            "settle-only sampling runs at the settle cadence, not the stored live interval");
    }

    /// <summary>
    /// The guard lets the surfaces digest a publish; it must scale with the requested period, not
    /// sit on a floor that silently outlaws the small intervals the settings offer.
    /// </summary>
    private static void StallGuard()
    {
        Check(GlassSamplingPolicy.StallGuard(10, settleWithoutLive: false) == 40,
            "a 10 ms request gets a 40 ms guard, not the old 150 ms (which capped it at ~6.7 fps)");
        Check(GlassSamplingPolicy.StallGuard(10, settleWithoutLive: false) < 150,
            "the guard no longer exceeds the floor that made a small interval unreachable");
        Check(GlassSamplingPolicy.StallGuard(1, settleWithoutLive: false) == GlassSamplingPolicy.MinStallGuardMs,
            "the guard floors at one 60 Hz frame");
        Check(GlassSamplingPolicy.StallGuard(250, settleWithoutLive: false) == 1000,
            "the guard keeps its 1 s ceiling for slow intervals");
        Check(GlassSamplingPolicy.StallGuard(1000, settleWithoutLive: false) == GlassSamplingPolicy.MaxStallGuardMs,
            "a 1 fps request cannot push the guard past its ceiling");
        Check(GlassSamplingPolicy.StallGuard(10, settleWithoutLive: true) == 2 * GlassSamplingPolicy.SettleIntervalMs,
            "the settle window tightens the guard to two of its own rounds");
    }

    /// <summary>
    /// Whether an empty probe is evidence of a still desktop. This is the distinction the reported
    /// bug was missing: a live source outrun by the poll is not an idle desktop.
    /// </summary>
    private static void IdleEvidence()
    {
        Check(GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: -1, effectiveIntervalMs: 10),
            "no live source at all (the static wallpaper image) is idle by definition");
        Check(!GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 0, effectiveIntervalMs: 10),
            "a frame produced this very round is not evidence of stillness");
        Check(!GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 33, effectiveIntervalMs: 10),
            "a 30 fps source keeps the interval at the user's setting");
        Check(!GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 249, effectiveIntervalMs: 10),
            "a source silent for less than the quiet window is merely slower than the poll");
        Check(GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 250, effectiveIntervalMs: 10),
            "past the quiet window an empty round counts as idle (a still desktop still gets cheap)");
        Check(GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 300, effectiveIntervalMs: 100),
            "the quiet window is never shorter than 250 ms, whatever the interval");
        Check(!GlassSamplingPolicy.CountsAsIdle(sourceSilenceMs: 300, effectiveIntervalMs: 500),
            "a slow request widens the quiet window instead of thrashing between stretch and reset");
    }

    /// <summary>
    /// The static material's slow refresh (实时采样 off). The bug: the "every 3 s" rule only ran
    /// inside a render, and a frozen desktop never renders, so the first capture — taken while a
    /// single surface had reported its rectangle — was the only one of the session and every card
    /// sampled a patch that did not cover it.
    /// </summary>
    private static void StaticRefresh()
    {
        var screen = new CaptureRect(0, 0, 2560, 1440);
        var firstPatch = new CaptureRect(2048, 448, 2560, 800);

        Check(!GlassSamplingPolicy.StaticRefreshDue(2999, 0, firstPatch, firstPatch),
            "the static material does not refresh before its 3 s clock");
        Check(GlassSamplingPolicy.StaticRefreshDue(3000, 0, firstPatch, firstPatch),
            "the static material refreshes on its 3 s clock even when nothing rendered");
        Check(GlassSamplingPolicy.StaticRefreshDue(10, 0, firstPatch, screen),
            "a layout that outgrew the frozen patch is refreshed immediately, not in 3 s");
        Check(!GlassSamplingPolicy.StaticRefreshDue(10, 5, screen, new CaptureRect(8, 8, 2552, 1432)),
            "a request still inside the last captured interest needs no capture");

        Check(!GlassSamplingPolicy.StaticRetryAllowed(249, 0),
            "a static capture is not retried in a tighter loop than the cooldown");
        Check(GlassSamplingPolicy.StaticRetryAllowed(250, 0),
            "a failed static capture is retried after the cooldown");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition)
        {
            failures++;
            Console.WriteLine($"  [FAIL] {label}");
            return;
        }

        Console.WriteLine($"  PASS: {label}");
    }
}
