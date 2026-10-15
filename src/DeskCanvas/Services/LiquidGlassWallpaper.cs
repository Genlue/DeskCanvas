using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Win32;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// Resolves the wallpaper the glass samples. Primary source: a 1:1 capture of the
/// real desktop wallpaper — including animated wallpapers, which do not live in
/// Progman (see <see cref="DesktopCapturer"/>) — so the sampled background is
/// pixel-identical to what is actually behind the widget. The static-file path
/// (Windows wallpaper image + registry placement) remains as a fallback when no
/// capture is available.
/// </summary>
public static class LiquidGlassWallpaper
{
    /// <summary>
    /// Lower bound of the live sampling rate — effectively "as fast as possible".
    /// </summary>
    public const int MinIntervalMs = LiquidGlassSettings.MinLiveSamplingInterval;

    /// <summary>Upper bound of the live sampling rate (1 fps).</summary>
    public const int MaxIntervalMs = LiquidGlassSettings.MaxLiveSamplingInterval;

    /// <summary>Default live sampling interval (10 fps).</summary>
    public const int DefaultIntervalMs = LiquidGlassSettings.DefaultLiveSamplingInterval;

    private static readonly object Gate = new();
    private static WallpaperSnapshot? cached;
    private static string? cachedKey;

    // The last good live frame, kept alive so a failed or flat capture can be masked with it
    // instead of flashing every glass card to one flat colour. Reference counted: handing it to
    // a render only lends it, so dropping the field later cannot pull pixels from under a draw.
    private static WallpaperSnapshot? stale;

    // When the current run of unusable captures (failed or flat) began; reset by the next good one.
    private static long firstUnusableAt;

    /// <summary>How long a run of unusable captures is masked with the previous frame before the normal fallback chain takes over.</summary>
    private const int UnusableGraceMs = 250;

    /// <summary>
    /// Resolution of the identity grid the probe compares captures on. Two cells per 80 desktop
    /// pixels: any change a viewer could see through the glass spans many cells, while a change
    /// strictly inside one cell is under 0.3% of the desktop and dissolves in the backdrop blur.
    /// </summary>
    private const int IdentityColumns = 32;

    /// <summary>The row count of the identity grid; see <see cref="IdentityColumns"/>.</summary>
    private const int IdentityRows = 18;

    /// <summary>
    /// Per-channel slack of the identity comparison, in 255ths. Absorbs the sub-perceptual
    /// re-encode noise of a "paused" video wallpaper; any real animation moves cells further
    /// than this and publishes.
    /// </summary>
    private const int IdentityTolerance = 2;

    /// <summary>
    /// Longest the sampler stretches between probes while the desktop stays unchanged. The
    /// stretch exists so an idle desktop costs a few captures per second instead of the
    /// pipeline's maximum rate; it does not throttle a changing one — the first probe that
    /// finds new content snaps the interval back to the user's setting.
    /// </summary>
    private const int MaxIdleIntervalMs = 500;

    // Coarse grid of the last accepted capture, and the host it came from. The grid is the
    // reference for the temporal check (a frame that replaced nearly everything at once is an
    // MPO overlay transition, not wallpaper content); the host handle lets a genuinely NEW host
    // (a wallpaper engine restarting with different content) skip the check entirely.
    private static int[]? lastAcceptedSamples;
    private static IntPtr lastAcceptedHost;

    // The finer identity grid of the frame that is actually published, under its own gate: the
    // probe runs on a worker outside the wallpaper Gate while CaptureOnce (under the Gate)
    // touches the same fields. The reference the identity check compares against must move
    // exactly when a new frame is published, never in between — comparing against a grid that
    // was updated without its frame being published would silently freeze the glass.
    private static readonly object SampleGate = new();
    private static int[]? lastPublishedSamples;

    // The probe's reusable capture buffer. A probe that finds the desktop unchanged hands the
    // bitmap straight back: the steady state — a static desktop probed over and over — then
    // allocates nothing, instead of feeding the large-object-heap a full desktop bitmap per
    // round. Only a published frame consumes the buffer (it becomes the snapshot's pixels), so
    // a buffer is never both pooled and reachable by a render.
    private static SKBitmap? probeBuffer;

    // Single-flight flag for the probe: the sampler tick is async and the next pulse can arrive
    // while a capture is still running.
    private static int probing;

    // Consecutive probes that found the desktop unchanged; drives the idle backoff.
    private static int idleRounds;

    private static bool liveSamplingEnabled = LiquidGlassSettings.DefaultLiveSampling;
    private static int liveIntervalMs = DefaultIntervalMs;
    private static bool suspended;

    // ---- live sampler pacing --------------------------------------------------------------------
    // The sampling cadence used to be a DispatcherTimer, which on Windows is a USER timer
    // (SetTimer): ~15.6 ms granularity and lowest-priority delivery, so any requested interval
    // below one system tick was silently rounded up to it and a busy UI thread stretched it
    // further. A requested 3–10 ms never reached the pipeline — the ceiling half of the "5 ms is
    // set but the frame rate is nowhere near it" report. The pacer is a dedicated background
    // thread waiting on a high-resolution waitable timer (CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
    // Windows 10 1803+; a plain waitable timer or Thread.Sleep where that is unavailable). It
    // only SCHEDULES: every pulse posts the ordinary tick to the UI thread, where all the gates
    // (active surfaces, backoff, render state) already live and must stay. Posts are coalesced
    // through a latch so a busy UI thread never accumulates a backlog — one pending tick is the
    // most that can queue, the old timer's at-most-one-pending semantics without its granularity.
    private static Thread? pacerThread;

    /// <summary>Whether ticks are currently wanted; the pacer parks itself when they are not.</summary>
    private static bool pacerWanted;

    /// <summary>Wakes the parked pacer when the wanted state flips back on.</summary>
    private static readonly AutoResetEvent pacerKick = new(false);

    /// <summary>0/1 latch: at most one tick may be queued to the UI thread at a time.</summary>
    private static int tickQueued;

    /// <summary>
    /// Waitable timer handle backing the pacer's waits; IntPtr.Zero once (or always, on
    /// non-Windows) pacing degrades to Thread.Sleep.
    /// </summary>
    private static IntPtr waitableTimer;

    /// <summary>
    /// Turn continuous desktop sampling on or off. Switching it off is the static-wallpaper
    /// mode: the last captured frame is frozen (a single capture still runs if nothing has
    /// been sampled yet, so the glass never falls back to a stale wallpaper file).
    /// </summary>
    public static void ConfigureLiveSampling(bool enabled, int intervalMs)
    {
        liveSamplingEnabled = enabled;
        liveIntervalMs = Math.Clamp(intervalMs, MinIntervalMs, MaxIntervalMs);
        Volatile.Write(ref idleRounds, 0);
        if (!enabled)
        {
            Interlocked.Exchange(ref probeBuffer, null)?.Dispose();
        }

        if (!OperatingSystem.IsWindows()) return;
        try
        {
            UpdateTimerState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live glass sampling unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop asking for new frames — every screen is covered by a fullscreen application and
    /// nobody can see the glass. Unrelated to the user's 实时采样 switch.
    /// </summary>
    public static void SuspendLiveSampling()
    {
        suspended = true;
        UpdateTimerState();
    }

    /// <summary>Resume asking for frames after a fullscreen application went away.</summary>
    public static void ResumeLiveSampling()
    {
        suspended = false;
        UpdateTimerState();
    }

    /// <summary>True while the desktop is covered by a fullscreen/maximized application (SuspendLiveSampling).</summary>
    public static bool IsSuspended => suspended;

    /// <summary>True when the user's 实时采样 switch is on.</summary>
    public static bool LiveSamplingEnabled => liveSamplingEnabled;

    /// <summary>True when the sampler is actually ticking (switch on, glass on screen, not covered).</summary>
    public static bool LiveSamplingActive =>
        liveSamplingEnabled && !suspended && pacerWanted;

    /// <summary>The effective sampling interval in milliseconds.</summary>
    public static int LiveSamplingIntervalMs => liveIntervalMs;

    /// <summary>
    /// Re-evaluate whether the sampler should be ticking. Called when glass surfaces attach or
    /// detach — the sampler is pointless while no widget is on screen, and starting it lazily
    /// also means a theme switch made before any widget existed still begins sampling later.
    /// </summary>
    internal static void RefreshSamplerState() => UpdateTimerState();

    private static void UpdateTimerState()
    {
        // Live sampling samples the Windows desktop; elsewhere there is nothing to capture.
        if (!OperatingSystem.IsWindows()) return;
        // Active-surface state is UI-thread-bound and must be read there.
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(UpdateTimerState);
            return;
        }
        // Two demand sources: the user's live-sampling switch — gated on glass actually being on
        // screen, because the sampler is pointless while no widget is on screen — and the
        // wallpaper-change settle window, which must also keep probing for consumers that draw
        // their own material from the published frame (the frameless clock) even with live
        // sampling off.
        var shouldRun = !suspended && (liveSamplingEnabled
            ? LiquidGlassSurface.HasActiveSurfaces
            : SettleActive && HasFrameConsumers);
        if (shouldRun)
        {
            EnsurePacer();
            Volatile.Write(ref pacerWanted, true);
            pacerKick.Set();
        }
        else
        {
            Volatile.Write(ref pacerWanted, false);
            Interlocked.Exchange(ref tickQueued, 0);
            Interlocked.Exchange(ref probeBuffer, null)?.Dispose();
        }
    }

    private static void EnsurePacer()
    {
        if (pacerThread != null) return;
        waitableTimer = TryCreateWaitableTimer();
        pacerThread = new Thread(PacerLoop)
        {
            IsBackground = true,
            Name = "DeskCanvas.GlassSamplingPacer",
        };
        pacerThread.Start();
    }

    private static IntPtr TryCreateWaitableTimer()
    {
        // CREATE_WAITABLE_TIMER_HIGH_RESOLUTION (Windows 10 1803+): wakes are not rounded to the
        // system tick, which is the whole point — the sampler's 3–10 ms requests must be real.
        // A plain waitable timer keeps the old ~15.6 ms behaviour rather than failing outright.
        var timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0x00000002, TimerAllAccess);
        if (timer == IntPtr.Zero)
            timer = CreateWaitableTimerEx(IntPtr.Zero, null, 0, TimerAllAccess);
        return timer;
    }

    private static void PacerLoop()
    {
        while (true)
        {
            if (!Volatile.Read(ref pacerWanted))
            {
                pacerKick.WaitOne();
                continue;
            }

            var intervalMs = EffectiveIntervalMs;
            if (waitableTimer != IntPtr.Zero)
            {
                // One-shot relative wait, re-armed per pulse: the effective interval moves with
                // the idle backoff, and re-reading it every round keeps the pacer in step without
                // cancelling and re-programming a periodic timer.
                var due = -(intervalMs * 10_000L); // 100 ns units; negative = relative to now
                if (!SetWaitableTimer(waitableTimer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false)
                    || WaitForSingleObject(waitableTimer, WaitInfinite) != WaitObject0)
                {
                    // The timer died (handle invalid, session teardown): degrade quietly.
                    waitableTimer = IntPtr.Zero;
                    Thread.Sleep(intervalMs);
                }
            }
            else
            {
                Thread.Sleep(intervalMs);
            }

            if (Volatile.Read(ref pacerWanted)) QueueTick();
        }
    }

    private static void QueueTick()
    {
        // Coalesce: while the UI thread is busy, pulses keep arriving but the pending one has not
        // run yet — posting more of them would only queue identical ticks behind it.
        if (Interlocked.CompareExchange(ref tickQueued, 1, 0) != 0) return;
        try
        {
            Dispatcher.UIThread.Post(OnLiveSamplingTick);
        }
        catch
        {
            // The dispatcher is gone (shutdown): park the pacer instead of spinning on failures.
            Volatile.Write(ref pacerWanted, false);
            Interlocked.Exchange(ref tickQueued, 0);
        }
    }

    private const uint TimerAllAccess = 0x1F0003;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitInfinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(IntPtr lpTimerAttributes, string? lpTimerName, uint dwFlags, uint dwDesiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetWaitableTimer(IntPtr hTimer, ref long liDueTime, int lPeriod, IntPtr pfnCompletionRoutine, IntPtr lpArgToCompletionRoutine, bool fResume);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    /// <summary>
    /// One sampling tick: capture one probe frame off the UI thread and refresh the glass surfaces
    /// only when its pixels actually differ from what they are already showing.
    /// <para>
    /// The glass is a pure function of the captured frame, the widget geometry and the optics —
    /// republishing an identical frame re-runs the whole pipeline (a full desktop capture, a
    /// full-desktop blur, one prepare per card, a GPU draw each) for zero visual change, and at a
    /// 5 ms interval that is what the tick used to demand unconditionally: ~20 captures and ~180
    /// card publishes per second on a desktop that had not moved a pixel. The probe inverts the
    /// cost: an unchanged frame is the cheapest outcome (the capture buffer is reused, nothing is
    /// republished, and consecutive idle rounds stretch the period out to
    /// <see cref="MaxIdleIntervalMs"/>), while the first changed frame publishes exactly as before
    /// and snaps the interval back. A static wallpaper therefore idles at a few captures per
    /// second and an animated one runs at the full pipeline rate, identical visuals either way.
    /// </para>
    /// <para>
    /// The capture does not run on the UI thread — the tick awaits it on a worker, single-flighted
    /// through <see cref="probing"/> so a slow capture cannot stack ticks.
    /// </para>
    /// <para>
    /// The staleness guard is the safety net: if nothing has been captured for a long time while
    /// widgets are still asking, the tick forces a round anyway, so a surface that somehow never
    /// reports itself idle cannot stall live sampling permanently.
    /// </para>
    /// </summary>
    private static async void OnLiveSamplingTick()
    {
        // The pacer coalesces pulses through this latch; consuming it here lets the next pulse
        // queue a fresh tick even while this one is still probing on a worker.
        Interlocked.Exchange(ref tickQueued, 0);

        // Settle deadline expiry (RC1): a window that ran to its deadline must never just die.
        // Detection reads the raw deadline instead of SettleActive — the window is expired
        // exactly when settleUntil is in the past and not yet cleaned, a state SettleActive
        // reports as "inactive" and the early-outs below would silently park the pacer on
        // (the zero-publish death this exists to prevent). Zero publications → force the
        // fallback publish; anything else → close (with the close-out sweep) and move on.
        var now0 = Environment.TickCount64;
        var until0 = Volatile.Read(ref settleUntil);
        if (until0 != 0 && now0 >= until0)
        {
            if (Volatile.Read(ref settlePublished) == 0 && !suspended)
                RunSettleDeadlineFallback();
            else
                CloseSettle();
            return;
        }

        var settle = SettleActive;
        if ((!liveSamplingEnabled && !settle) || suspended) { UpdateTimerState(); return; }
        if (!LiquidGlassSurface.HasActiveSurfaces && !settle) { UpdateTimerState(); return; }

        var now = Environment.TickCount64;
        // The period is measured from the start of the previous round, not from its capture. A
        // capture lands a few milliseconds after the round begins, so measuring from it made every
        // other tick arrive "too early" and halved the rate — 30 publishes/s instead of 60 at a
        // 100 ms interval. While the desktop sits still the period is the backoff-stretched one.
        if (now - Interlocked.Read(ref lastRoundAt) < EffectiveIntervalMs) return;
        if (LiquidGlassSurface.AnySurfaceRendering && now - Interlocked.Read(ref lastCaptureAt) < StallGuardMs)
            return;

        Interlocked.Exchange(ref lastRoundAt, now);
        if (Interlocked.Exchange(ref probing, 1) == 1) return;
        try
        {
            var outcome = await Task.Run(ProbeCapture);
            if (outcome == ProbeOutcome.Published)
            {
                Volatile.Write(ref idleRounds, 0);
                // A published frame restarts the settle window's stability count and marks the
                // window as having delivered a new frame: the desktop just moved (another step of
                // the fade), so "settled" must be re-established by fresh unchanged rounds rather
                // than inherited from before the move.
                if (SettleActive)
                {
                    Volatile.Write(ref settlePublished, 1);
                    Volatile.Write(ref settleStable, 0);
                    GlassDiagnostics.Event("settle window published a transition frame");
                }
                // Immediate: the interval can be shorter than the surface's move/resize debounce,
                // and a debounce restarted on every tick would never elapse — freezing the glass.
                LiquidGlassSurface.RefreshAllImmediate();
            }
            else if (outcome == ProbeOutcome.Unchanged)
            {
                // Only a verified-identical frame may stretch the interval. A failed, flat or
                // wild-jumped capture says nothing about whether the desktop moved; counting it
                // as idle walked the interval toward its 500 ms ceiling on hosts with transient
                // capture hiccups (MPO overlay transitions, a busy shell), so the glass
                // stuttered even though the wallpaper never stopped animating.
                Interlocked.Increment(ref idleRounds);
                // The wallpaper has settled once the desktop verifiably shows what the surfaces
                // show for consecutive rounds — but only after the window has published at least
                // one new frame. Without that, an early "unchanged" reading (the capture still
                // seeing the old scene while the host re-composites) would close the window before
                // the transition was ever observed and freeze every surface on the old wallpaper;
                // such a window runs to its deadline instead.
                if (SettleActive && Volatile.Read(ref settlePublished) == 1
                    && Interlocked.Increment(ref settleStable) >= SettleStableRounds)
                    CloseSettle();
            }
            else if (SettleActive)
            {
                // An unusable capture says nothing about stability — a run of failures must not
                // accumulate towards closing the window.
                Volatile.Write(ref settleStable, 0);
            }
        }
        catch (Exception ex)
        {
            // A failing capture must not kill the sampler's async tick; the next one retries.
            GlassDiagnostics.Failure(ex);
        }
        finally
        {
            Volatile.Write(ref probing, 0);
        }
    }

    /// <summary>The period the sampler actually waits for: the configured interval, stretched while the desktop stays unchanged.</summary>
    private static int EffectiveIntervalMs
    {
        get
        {
            // The settle window runs at its own cadence — close enough behind the fade to read as
            // live, slow enough that a whole transition costs a few captures; the live sampler's
            // cadence and its idle backoff are untouched.
            if (SettleActive && !liveSamplingEnabled) return SettleIntervalMs;
            var idle = Math.Min(Volatile.Read(ref idleRounds), 6);
            var stretched = liveIntervalMs << idle;
            return Math.Min(Math.Max(stretched, liveIntervalMs), Math.Max(liveIntervalMs, MaxIdleIntervalMs));
        }
    }

    /// <summary>When the desktop was last captured; the stall guard is measured from here.</summary>
    private static long lastCaptureAt;

    /// <summary>When the current sampling round began; the interval is measured from here.</summary>
    private static long lastRoundAt;

    /// <summary>Captures taken since the process started, for the rate diagnostic.</summary>
    private static int captureCount;

    /// <summary>Total desktop captures; the achieved sharing is the publish rate divided by this.</summary>
    internal static int CaptureCount => Volatile.Read(ref captureCount);

    /// <summary>
    /// Longest a new round may be held off by busy surfaces before the tick forces through anyway,
    /// measured from the last capture. Scaled to the requested interval: the old flat 1000 ms let
    /// one mid-prepare card hold the whole sampler hostage for a full second — an invisible
    /// ceiling on the achieved frame rate that grew with every widget on screen, so a 5 ms
    /// request landed far below one publish per 150 ms even on an animated wallpaper. The 150 ms
    /// floor keeps tiny intervals from forcing captures faster than the pipeline digests them
    /// (the pre-probe 3 ms pathology: more captures than the surfaces can consume, each reaching
    /// whoever happens to be free and latching the rest); the 1000 ms cap keeps slow intervals'
    /// behaviour unchanged.
    /// </summary>
    private static int StallGuardMs => SettleActive && !liveSamplingEnabled
        // Settle rounds must not be held off for a full second while the fade is still moving —
        // the guard exists to let the surfaces digest a publish, and a settle publish digests in
        // well under 500 ms even with several cards. Only the window is tightened; the live
        // sampler's own guard is untouched.
        ? 2 * SettleIntervalMs
        : Math.Min(1000, Math.Max(150, 4 * EffectiveIntervalMs));

    // ---- wallpaper-change settle window ---------------------------------------------------------
    // A wallpaper switch is not an instant: Windows cross-fades the desktop over up to a couple of
    // seconds, and a capture taken mid-fade is a blend of the old and the new wallpaper. The
    // event-driven watcher knows only that a switch STARTED; with live sampling off (the default
    // static material) the refresh used to end at two fixed delays and whatever the fade happened
    // to be at those instants became the frozen frame — glass could sit on the old wallpaper or on
    // a half-faded blend until the next unrelated event re-rendered it.
    //
    // The settle window is that missing confirmation. After a wallpaper-change event the probe
    // keeps running even with live sampling off: every fade step that differs from what the
    // surfaces show is published (glass trails the desktop through the transition exactly like it
    // does for an animated wallpaper), and the window closes only once it has published at least
    // one new frame AND two consecutive probes find the desktop identical to it — the settled
    // wallpaper. A window that has published nothing no longer merely runs to its deadline: the
    // real trace showed the whole 6 s being spent without a publish (the switch reads as a wild
    // jump against the replaced scene, so early probes are rejected, while slow captures and busy
    // surfaces eat the budget) and then dying with every surface still on the old wallpaper. The
    // deadline now triggers a bounded forced-publish fallback (see
    // RunSettleDeadlineFallback), and Invalidate/BeginSettle retire the temporal acceptance grid
    // at the source so the misclassification cannot happen in the first place. Two more guards
    // make the transition uniform, not just convergent: per-surface captures are disabled for the
    // window (see GetShared — the probe owns every capture and each publish sweeps all surfaces
    // together, so no widget can freeze on its own private fade step), and the published frame is,
    // by construction, the settled one. Closing the window runs the close-out sweep
    // (SweepSettleCloseOut) so a surface that dropped the final publish is dragged onto it even on
    // a desktop that never changes again. Cost: a handful of captures over ~1–2 s on the probe's
    // pooled buffer (no allocation once it is warm); when the window closes the pacer parks and the
    // pooled buffer is given back with the sampler's idle state, so a desktop that merely changed
    // its wallpaper pays nothing afterwards.

    /// <summary>How long a settle window may run before giving up (a host that never stabilises).</summary>
    private const int SettleDeadlineMs = 6000;

    /// <summary>Probe cadence inside a settle window: close enough behind the fade to read as live, slow enough that the whole transition costs a few captures.</summary>
    private const int SettleIntervalMs = 250;

    /// <summary>Consecutive unchanged probes that close a settle window (see the section comment).</summary>
    private const int SettleStableRounds = 2;

    /// <summary>Absolute end of the current settle window; 0 = none. See <see cref="BeginSettle"/>.</summary>
    private static long settleUntil;

    /// <summary>Consecutive probes that found the desktop identical to the published frame while the settle window runs.</summary>
    private static int settleStable;

    /// <summary>1 once the current settle window has published a new frame. A window that never publishes may not close early (see the section comment).</summary>
    private static int settlePublished;

    /// <summary>True while the wallpaper-change settle window is running (see the section comment).</summary>
    internal static bool SettleActive
    {
        get
        {
            var until = Volatile.Read(ref settleUntil);
            return until != 0 && Environment.TickCount64 < until;
        }
    }

    /// <summary>
    /// Whether anything renders from the published frame: the glass surfaces, or through
    /// <see cref="WallpaperInvalidated"/> the widgets that draw their own material from it (the
    /// frameless clock). The settle window probes for these consumers, so a layout with only
    /// those on screen still converges after a wallpaper switch.
    /// </summary>
    internal static bool HasFrameConsumers => LiquidGlassSurface.HasActiveSurfaces || WallpaperInvalidated != null;

    /// <summary>
    /// A wallpaper change was announced (the watcher's debounce elapsed): keep probing until the
    /// desktop settles, so the frame the glass freezes on is the settled wallpaper rather than
    /// wherever a fixed delay happened to land mid-fade. Idempotent — consecutive switches simply
    /// restart the window. With live sampling on the probe already runs at the user's cadence and
    /// publishes each fade step, so the window adds no cadence of its own there; it still re-arms
    /// the demand in case the user switches live sampling off while the fade is running.
    /// </summary>
    public static void BeginSettle()
    {
        if (!OperatingSystem.IsWindows()) return;
        Volatile.Write(ref settleUntil, Environment.TickCount64 + SettleDeadlineMs);
        Volatile.Write(ref settleStable, 0);
        Volatile.Write(ref settlePublished, 0);
        // Fresh budget for the zero-publish deadline fallback (see RunSettleDeadlineFallback) and
        // a retired acceptance grid — the frame arriving after this event replaces the scene the
        // grid describes, exactly as in Invalidate.
        Volatile.Write(ref settleFallbacks, 0);
        ResetTemporalAcceptance();
        GlassDiagnostics.Event($"settle window opened (deadline {SettleDeadlineMs}ms, live sampling {(liveSamplingEnabled ? "on" : "off")})");
        UpdateTimerState();
    }

    /// <summary>Close the settle window and re-evaluate whether the pacer still has a reason to tick.</summary>
    private static void CloseSettle()
    {
        Volatile.Write(ref settleUntil, 0);
        Volatile.Write(ref settleStable, 0);
        UpdateTimerState();
        SweepSettleCloseOut();
    }

    /// <summary>
    /// Settle close-out verification (RC3): when the window closes, every frame-demanding surface
    /// must be rendering the frame the window ended on. A surface that was mid-prepare during the
    /// final publish dropped that frame by design, and a static desktop never publishes again —
    /// without this sweep that surface froze on the old wallpaper until an unrelated event
    /// re-rendered it ("统一发放后部分组件更新失败"). Idempotent: surfaces already built on the
    /// final snapshot are skipped, so a normal close where everyone converged costs nothing.
    /// </summary>
    private static void SweepSettleCloseOut()
    {
        long finalId;
        lock (Gate)
        {
            finalId = cached?.CaptureId ?? 0;
        }
        if (finalId == 0) return;
        var corrected = LiquidGlassSurface.SweepStaleMaterials(finalId);
        if (corrected > 0)
            GlassDiagnostics.Event($"settle close-out: {corrected} surface(s) forced onto the final published frame");
    }

    /// <summary>Short confirmation window reopened by the settle deadline fallback.</summary>
    private const int SettleRecoveryMs = 1500;

    /// <summary>Forced fallback publish attempts one zero-publish window may spend before giving up.</summary>
    private const int MaxSettleFallbacks = 3;

    /// <summary>Fallback publish attempts spent by the current settle window (reset by BeginSettle).</summary>
    private static int settleFallbacks;

    /// <summary>
    /// The deadline fallback for a settle window that is about to die having published nothing
    /// (RC1): retire the temporal acceptance grid and force one publish attempt past every guard.
    /// <para>
    /// The old behaviour let such a window simply expire — by design, per the section comment —
    /// but the real trace showed a window CAN spend its whole 6 s without a publish: a wallpaper
    /// switch is a wild jump against the replaced scene, so the first probes were rejected as
    /// "overlay transition", and with slow captures plus busy surfaces (2 rejected rounds + 1
    /// published round ≈ 3×750 ms of surface prepares each) the deadline arrived first. The
    /// surfaces then sat on the old wallpaper until an unrelated event — "sometimes not a single
    /// widget updates after changing the wallpaper". Invalidate/BeginSettle now retire the grid at
    /// the source, and this fallback guarantees the window can never end without an attempt.
    /// </para>
    /// </summary>
    private static async void RunSettleDeadlineFallback()
    {
        var attempt = Interlocked.Increment(ref settleFallbacks);
        if (attempt > MaxSettleFallbacks)
        {
            GlassDiagnostics.Event("settle window expired with nothing published — fallback budget exhausted, giving up");
            CloseSettle();
            return;
        }

        // Retire the stale scene reference BEFORE probing: with the grid present the forced round
        // would be rejected by the very misclassification this fallback exists to bypass.
        ResetTemporalAcceptance();
        GlassDiagnostics.Event(
            $"settle window expired with nothing published — forcing fallback publish attempt {attempt}/{MaxSettleFallbacks}");

        if (Interlocked.Exchange(ref probing, 1) == 1)
        {
            // A round is already in flight — forcing a second concurrent capture would race it.
            // Give it a short window to land (its outcome handling runs the same publish path).
            Volatile.Write(ref settleUntil, Environment.TickCount64 + SettleRecoveryMs);
            Volatile.Write(ref settleStable, 0);
            UpdateTimerState();
            return;
        }

        try
        {
            var outcome = await Task.Run(() => ProbeCapture(forced: true));
            switch (outcome)
            {
                case ProbeOutcome.Published:
                    Volatile.Write(ref settlePublished, 1);
                    Volatile.Write(ref settleStable, 0);
                    LiquidGlassSurface.RefreshAllImmediate();
                    // The refresh just dropped frames on any busy surface; the close-out sweep
                    // arms their catch-up flags so the forced frame is still guaranteed.
                    SweepSettleCloseOut();
                    // A short confirmation window lets the normal stability rounds verify the
                    // settled frame and close on their own terms.
                    Volatile.Write(ref settleUntil, Environment.TickCount64 + SettleRecoveryMs);
                    Volatile.Write(ref settleStable, 0);
                    UpdateTimerState();
                    break;
                case ProbeOutcome.Unchanged:
                    // The desktop verifiably equals what the surfaces render — the "missing"
                    // update never existed. Close for good.
                    CloseSettle();
                    break;
                default:
                    // Still nothing usable: retry within the bounded budget, then give up.
                    if (attempt >= MaxSettleFallbacks)
                    {
                        CloseSettle();
                    }
                    else
                    {
                        Volatile.Write(ref settleUntil, Environment.TickCount64 + SettleRecoveryMs);
                        Volatile.Write(ref settleStable, 0);
                        UpdateTimerState();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            // Same contract as the tick: a failing probe must not kill the async path.
            GlassDiagnostics.Failure(ex);
            Volatile.Write(ref settleUntil, Environment.TickCount64 + SettleRecoveryMs);
            UpdateTimerState();
        }
        finally
        {
            Volatile.Write(ref probing, 0);
        }
    }

    // ---- idle capture release ------------------------------------------------------------------
    // The desktop capture is the single largest allocation in the process — a full virtual desktop
    // in physical pixels, tens of MB on a large desktop — and with live sampling off nothing needs
    // it between renders: the blurred backdrops the glass actually samples are already built and
    // cached. Handing the capture (with its stale mask and probe buffer) back after a quiet spell
    // is what keeps a desktop that is merely *decorated* by widgets from paying for the sampling
    // pipeline forever. The next render re-captures on demand — the same path a fullscreen resume
    // already takes — and because the surface keeps drawing its previous material until the new
    // one publishes, the recapture is never visible as a flash.
    /// <summary>How long the capture is kept after the last render demand. Overridable for checks.</summary>
    private static readonly int IdleReleaseGraceMs = ReadIdleReleaseGrace();

    private static int ReadIdleReleaseGrace()
    {
        var raw = Environment.GetEnvironmentVariable("DESKCANVAS_GLASS_IDLE_RELEASE_MS");
        return int.TryParse(raw, out var ms) && ms >= 0 ? ms : 3 * 60_000;
    }

    /// <summary>When the last render demand arrived; drives the idle release.</summary>
    private static long lastFrameDemandAt = Environment.TickCount64;

    /// <summary>0/1 latch: at most one idle-release check is waiting at any time.</summary>
    private static int idleReleaseCheckArmed;

    /// <summary>Raised whenever the wallpaper is invalidated (system wallpaper change, display change, or manual refresh).</summary>
    public static event Action? WallpaperInvalidated;

    public static WallpaperSnapshot Get()
    {
        // Every render — and the settings previews — passes through here, so this is the one place
        // that needs to know the capture is still wanted.
        NoteFrameDemand();

        // The cache owns one reference and the caller gets another; the caller releases it with
        // WallpaperSnapshot.Dispose. Nothing here is kept alive by the sampler — a capture is a full
        // desktop bitmap and live sampling replaces it ten times a second.
        // The AddRef happens under the Gate on purpose: Invalidate / DropStale run on other
        // threads and free the snapshot the moment the cache lets go, so between GetShared's
        // return and an AddRef here a concurrent swap could dispose the pixels under us.
        lock (Gate)
        {
            var snapshot = GetShared();
            snapshot.AddRef();
            return snapshot;
        }
    }

    /// <summary>
    /// Record that a render is about to sample the capture and make sure the idle check is armed.
    /// Called from <see cref="Get"/> — the single entry every glass render goes through.
    /// </summary>
    private static void NoteFrameDemand()
    {
        Volatile.Write(ref lastFrameDemandAt, Environment.TickCount64);
        ScheduleIdleReleaseCheck(IdleReleaseGraceMs + 500);
    }

    private static void ScheduleIdleReleaseCheck(int delayMs)
    {
        if (Interlocked.CompareExchange(ref idleReleaseCheckArmed, 1, 0) != 0) return;
        var delay = Math.Clamp(delayMs, 1000, IdleReleaseGraceMs + 500);
        _ = Task.Delay(delay).ContinueWith(_ => RunIdleReleaseCheck());
    }

    /// <summary>
    /// Hand the desktop capture back once nothing has asked for a frame for
    /// <see cref="IdleReleaseGraceMs"/>. Runs on the UI thread: the decision reads surface state
    /// that only the UI thread may enumerate, and the release itself is thread-safe by design.
    /// Live sampling (when the user enabled it) owns the capture lifecycle, and a demand that
    /// arrives while the check waits simply re-arms it.
    /// </summary>
    private static void RunIdleReleaseCheck()
    {
        try
        {
            Interlocked.Exchange(ref idleReleaseCheckArmed, 0);
            if (Dispatcher.UIThread.CheckAccess()) EvaluateIdleRelease();
            else Dispatcher.UIThread.Post(EvaluateIdleRelease);
        }
        catch
        {
            // Idle maintenance must never surface as a widget failure.
        }
    }

    private static void EvaluateIdleRelease()
    {
        try
        {
            // The live sampler drives its own capture cadence, and a fullscreen suspension has
            // already released everything on purpose; releasing under either would only force an
            // immediate re-capture.
            if (liveSamplingEnabled || suspended)
                return;

            var idleFor = Environment.TickCount64 - Volatile.Read(ref lastFrameDemandAt);
            if (idleFor < IdleReleaseGraceMs || LiquidGlassSurface.AnySurfaceRendering)
            {
                ScheduleIdleReleaseCheck((int)Math.Max(1000, IdleReleaseGraceMs - idleFor));
                return;
            }

            if (!HasCachedCapture) return;

            Release();
            GlassDiagnostics.Event(
                $"idle release: desktop capture handed back after {idleFor / 1000}s without a render");
        }
        catch
        {
            // See RunIdleReleaseCheck.
        }
    }

    /// <summary>True while a capture (or its stale mask) is retained for rendering (diagnostics and checks).</summary>
    public static bool HasCachedCapture
    {
        get
        {
            lock (Gate)
            {
                return cached != null || stale != null;
            }
        }
    }

    /// <summary>Real capture bytes retained right now (diagnostics and checks).</summary>
    public static long RetainedCaptureBytes
    {
        get
        {
            lock (Gate)
            {
                long bytes = 0;
                if (cached?.CachedBitmap is { } cachedBitmap)
                    bytes += (long)cachedBitmap.RowBytes * cachedBitmap.Height;
                if (stale?.CachedBitmap is { } staleBitmap)
                    bytes += (long)staleBitmap.RowBytes * staleBitmap.Height;
                return bytes;
            }
        }
    }

    private static WallpaperSnapshot GetShared()
    {
        lock (Gate)
        {
            if (!OperatingSystem.IsWindows())
                return new WallpaperSnapshot(null, new SKColor(32, 38, 48));
            try
            {
                // Fullscreen suspension: the desktop capture was just released to hand its
                // memory to the covering application, and nothing that renders while covered
                // can be seen. Do NOT re-capture on demand — a resize, a theme flip or a
                // wallpaper event during a game would otherwise silently rebuild exactly what
                // SuspendAll released. Hand back whatever frame is still cached, or a flat
                // placeholder that degrades the glass to its cheap fallback; live sampling
                // restores a real frame on resume.
                if (suspended)
                {
                    if (cached is { LiveCapture: true }) return cached;
                    if (stale != null) return stale;
                    return new WallpaperSnapshot(null, new SKColor(32, 38, 48));
                }

                // Wallpaper-transition uniformity: while a settle window runs, individual surfaces
                // must NOT run their own captures — that race is what produced the "some cards show
                // the new wallpaper, some still show the old one" report. A switch is a one-to-two-
                // second cross-fade; each surface re-renders at its own moment, so per-surface
                // captures land on different fade steps (old frame, mid-fade blend, settled frame)
                // and get seeded into this shared cache at different instants. Here the window's
                // single-flight probe owns every capture instead, and each frame it publishes is
                // swept to all surfaces together; until the first publish, everyone keeps the last
                // good frame. (stale == null is a first-ever render — it must still capture.)
                if (cached == null && stale != null && SettleActive)
                    return stale;

                // Live sampling on: reuse the frame while it is fresh (so every widget samples
                // the same instant), otherwise grab a new one. Live sampling off: the frozen
                // frame wins; a single one-shot capture runs only if there is nothing to freeze.
                if (liveSamplingEnabled && CaptureOnce() is { } live && live.LiveCapture)
                    return live;

                if (!liveSamplingEnabled)
                {
                    if (cached is { LiveCapture: true }) return cached;
                    if (CaptureOnce() is { } once) return once;
                }

                // Live sampling with a persistently unreadable host (a long MPO overlay
                // transition, a wallpaper engine restarting): the frozen last frame is the
                // wallpaper the user actually has — the static file is whatever image was last
                // set through Windows, a different picture entirely. Freezing beats swapping
                // to it.
                if (liveSamplingEnabled && stale != null)
                    return stale;

                return FromFileFallback();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return cached ?? new WallpaperSnapshot(null, new SKColor(32, 38, 48));
            }
        }
    }

    /// <summary>Force a fresh capture (e.g. the user pressed 刷新壁纸 or the wallpaper changed).</summary>
    public static void Invalidate()
    {
        WallpaperSnapshot? previousStale;
        lock (Gate)
        {
            previousStale = stale;
            // Keep the last good frame as the mask for a run of unusable captures: the very next
            // capture is the one most likely to come back flat (a wallpaper change is exactly
            // when hosts re-composite), and masking it avoids a one-round flat flash.
            stale = cached;
            cached = null;
            cachedKey = null;
        }

        // A real invalidation means content is changing (or about to): the probe must not idle
        // through it at the backoff rate.
        Volatile.Write(ref idleRounds, 0);

        // The temporal acceptance grid described the scene being replaced. A wallpaper switch is,
        // by definition, a wild jump against that scene — leaving the grid in place made the first
        // probes after the event read the NEW wallpaper as an MPO overlay transition and reject it
        // (the reported "sometimes not a single widget updates"). Retire it here and in
        // BeginSettle so the incoming frame validates against nothing and publishes immediately.
        // lastPublishedSamples stays: it still describes what the surfaces render. The resolved
        // host is deliberately NOT forgotten — host invalidation is the capture's own self-healing
        // path (NoteUnusableCapture), not an invalidation concern.
        ResetTemporalAcceptance();

        Interlocked.Exchange(ref lastRoundAt, Environment.TickCount64);
        previousStale?.Dispose();

        NotifyInvalidated();
    }

    /// <summary>
    /// Drop the cached desktop capture and its decoded bitmap.
    /// <para>
    /// Called while every attached screen is covered by a fullscreen application: the capture is
    /// a full virtual desktop at physical resolution — the single largest allocation in the
    /// process (tens of megabytes with several monitors) — and nothing can be looking at glass
    /// right then. The next <see cref="Get"/> re-captures on demand. Releasing here only drops the
    /// cache's own reference: a render that is still holding one keeps the pixels valid until it
    /// lets go.
    /// </para>
    /// </summary>
    public static void Release()
    {
        WallpaperSnapshot? old;
        WallpaperSnapshot? oldStale;
        lock (Gate)
        {
            old = cached;
            cached = null;
            cachedKey = null;
            oldStale = stale;
            stale = null;
        }

        // The probe buffer is one more full-desktop allocation nothing can see right now.
        Interlocked.Exchange(ref probeBuffer, null)?.Dispose();

        old?.Dispose();
        oldStale?.Dispose();
    }

    private static void NotifyInvalidated()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) WallpaperInvalidated?.Invoke();
            else Dispatcher.UIThread.Post(() => WallpaperInvalidated?.Invoke());
        }
        catch
        {
            try { WallpaperInvalidated?.Invoke(); } catch { }
        }
    }

    private static WallpaperSnapshot? CaptureOnce()
    {
        // A captured frame stays valid until something explicitly invalidates it — the live sampler
        // on its next tick, a wallpaper change, a display change or a fullscreen transition. It is
        // deliberately NOT expired on a timer: keying the life of the frame to the sampling interval
        // meant that at a small interval the widgets stopped sharing one capture (and one shared
        // backdrop build) and each paid for its own, which is exactly backwards.
        if (cached is { LiveCapture: true }) return cached;

        // Two attempts: a wallpaper host can vanish mid-transition (monitor hot-plug, a
        // wallpaper engine restarting) and re-resolving usually succeeds on the second try.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bmp = DesktopCapturer.CaptureBitmap(out var flat);
            var jumped = false;

            if (bmp != null && !flat)
            {
                var identity = DesktopCapturer.SampleGrid(bmp, IdentityColumns, IdentityRows);
                if (MatchesLastPublished(identity) && stale != null)
                {
                    // The desktop is pixel-identical to the frame the surfaces already render
                    // from (stale is exactly that frame — Invalidate parks the outgoing one
                    // there). Publishing would rebuild every card for zero visual change, and
                    // returning nothing would drop the caller to the static-file fallback —
                    // different pixels entirely. Promote stale back to cached so subsequent
                    // Get() calls reuse it directly instead of re-capturing on every widget.
                    bmp.Dispose();
                    firstUnusableAt = 0;
                    var restored = stale;
                    cached = restored;
                    stale = null;
                    cachedKey = null;
                    Interlocked.Exchange(ref lastCaptureAt, Environment.TickCount64);
                    return restored;
                }

                var samples = DesktopCapturer.SampleGrid(bmp);
                if (lastAcceptedSamples != null
                    && lastAcceptedHost == DesktopCapturer.ResolvedHost
                    && DesktopCapturer.IsWildJump(samples, lastAcceptedSamples))
                {
                    // Almost nothing this frame shows matches the last accepted frame: an MPO
                    // overlay transition handed us the bare redirection surface instead of the
                    // wallpaper. Publish it and every glass card blinks to a solid colour.
                    bmp.Dispose();
                    bmp = null;
                    jumped = true;
                }
                else
                {
                    lock (SampleGate)
                    {
                        lastAcceptedSamples = samples;
                        lastPublishedSamples = identity;
                    }
                }
            }
            else if (bmp != null && flat)
            {
                bmp.Dispose();
                bmp = null;
            }

            if (bmp == null)
            {
                // Nothing usable this attempt. Mask with the last good frame for a short grace
                // instead of flashing the glass to a flat card / the static file for a round —
                // the usual cause clears within a few sampling rounds. Once the failure outlives
                // the grace, the normal fallback chain takes over; a persistently unusable host
                // is also forgiven so it is re-resolved with full validation instead of trusted.
                NoteUnusableCapture(flat, jumped);
                var now = Environment.TickCount64;
                if (now - firstUnusableAt < UnusableGraceMs && stale != null)
                    return stale;
                continue;
            }

            firstUnusableAt = 0;
            DropStale();
            lastAcceptedHost = DesktopCapturer.ResolvedHost;

            var snapshot = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), bmp, live: true);
            WallpaperSnapshot? old;
            lock (Gate)
            {
                old = cached;
                cached = snapshot;
                cachedKey = null;
            }

            // Frame coherence: every surface asked for a frame from now on samples this one, so a
            // round costs one desktop grab and one backdrop build however many widgets are on screen.
            Interlocked.Exchange(ref lastCaptureAt, Environment.TickCount64);
            Interlocked.Increment(ref captureCount);
            // Only the cache's reference goes; a render still using the previous frame keeps it.
            old?.Dispose();
            return snapshot;
        }
        return null;
    }

    /// <summary>
    /// What one probe round established. Drives the idle backoff in the tick: only
    /// <see cref="Unchanged"/> is evidence that the desktop is standing still — the other
    /// non-publishing outcome observed nothing and must not stretch the interval. Internal
    /// because the deadline fallback's check seam (<see cref="ForcedProbeOverride"/>) is typed
    /// on it.
    /// </summary>
    internal enum ProbeOutcome
    {
        /// <summary>New wallpaper content was published; the surfaces were refreshed.</summary>
        Published,
        /// <summary>The desktop was captured and is pixel-identical to the published frame.</summary>
        Unchanged,
        /// <summary>Nothing was observed: the capture failed, was flat or was an overlay transition.</summary>
        Unusable,
    }

    /// <summary>
    /// One live-sampling probe: capture the wallpaper host and publish the frame only when its
    /// pixels differ from the one every glass surface is already showing. Returns
    /// <see cref="ProbeOutcome.Published"/> when a new frame went out (the caller refreshes the
    /// surfaces), <see cref="ProbeOutcome.Unchanged"/> when the desktop is pixel-identical to what
    /// is already showing (nothing to refresh — the capture buffer is parked for the next probe),
    /// and <see cref="ProbeOutcome.Unusable"/> when the capture produced nothing usable (masked by
    /// the previous frame as before).
    /// <para>
    /// Runs on a worker outside the wallpaper Gate and single-flighted by the tick; the only
    /// shared mutable state it touches is swap-under-lock (the published snapshot, the sample
    /// grids) and the atomics the rest of the class already uses.
    /// </para>
    /// </summary>
    /// <summary>
    /// Whether a probe round is wanted at all: the user's live-sampling switch, or a running
    /// wallpaper-change settle window — which must keep probing even with live sampling off,
    /// because the watcher path has no other publisher (see <see cref="BeginSettle"/>). Extracted
    /// so the guard is assertable without capturing a real desktop.
    /// </summary>
    internal static bool ProbeWanted => !suspended && (liveSamplingEnabled || SettleActive);

    /// <summary>
    /// Check seam: stands in for a forced probe round so the settle deadline fallback is drivable
    /// without a real desktop. Holds the <see cref="ProbeOutcome"/> value to return (cast from
    /// int to keep the field reflection-friendly without generics over the nested enum); null in
    /// the app and never set by production code.
    /// </summary>
    internal static int? ForcedProbeOverride = null;

    private static ProbeOutcome ProbeCapture()
    {
        return ProbeCapture(forced: false);
    }

    private static ProbeOutcome ProbeCapture(bool forced)
    {
        // The enabled/suspended state is re-checked here: the tick queued this probe a moment ago
        // and a fullscreen transition may have landed in between. A settle window probes even with
        // live sampling off — this guard used to reject those probes outright, which silently
        // disabled the whole wallpaper-change convergence: the change-instant capture (mid-fade or
        // an MPO overlay artifact) froze on every glass card until an unrelated event re-rendered.
        // The deadline fallback (RunSettleDeadlineFallback) probes with forced: true — its window
        // has already expired, so SettleActive is false and the ordinary guard would refuse the
        // very round the fallback exists to run.
        if (ForcedProbeOverride is { } forcedOutcome) return (ProbeOutcome)forcedOutcome;
        if (!forced && !ProbeWanted) return ProbeOutcome.Unusable;

        var pooled = Interlocked.Exchange(ref probeBuffer, null);
        SKBitmap? bmp = null;
        var published = false;
        try
        {
            bmp = DesktopCapturer.CaptureBitmap(out var flat, pooled);
            if (bmp != null && !ReferenceEquals(bmp, pooled))
            {
                // A fresh bitmap came back, so the pooled one no longer matches the desktop size.
                pooled?.Dispose();
            }

            if (bmp == null)
            {
                // The capture failed but nothing consumed the pooled buffer: park it for the next
                // probe (the size is still the desktop's).
                Volatile.Write(ref probeBuffer, pooled);
                NoteUnusableCapture(flat: false, jumped: false);
                return ProbeOutcome.Unusable;
            }
            if (flat)
            {
                // A flat frame is worthless as wallpaper and the pooled buffer with it — the next
                // probe allocates a fresh one.
                bmp.Dispose();
                NoteUnusableCapture(flat: true, jumped: false);
                return ProbeOutcome.Unusable;
            }

            var identity = DesktopCapturer.SampleGrid(bmp, IdentityColumns, IdentityRows);
            if (MatchesLastPublished(identity))
            {
                // Pixel-identical to the published frame: publish nothing, and keep the capture
                // as the next probe's buffer so an idle desktop costs no allocation at all.
                firstUnusableAt = 0;
                Volatile.Write(ref probeBuffer, bmp);
                return ProbeOutcome.Unchanged;
            }

            var samples = DesktopCapturer.SampleGrid(bmp);
            bool jumped;
            lock (SampleGate)
            {
                jumped = lastAcceptedSamples != null
                         && lastAcceptedHost == DesktopCapturer.ResolvedHost
                         && DesktopCapturer.IsWildJump(samples, lastAcceptedSamples);
            }
            if (jumped)
            {
                bmp.Dispose();
                NoteUnusableCapture(flat: false, jumped: true);
                return ProbeOutcome.Unusable;
            }

            // Genuinely new content: publish it exactly as CaptureOnce would.
            var snapshot = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), bmp, live: true);
            WallpaperSnapshot? old;
            lock (Gate)
            {
                old = cached;
                cached = snapshot;
                cachedKey = null;
                DropStale();
            }
            lock (SampleGate)
            {
                lastAcceptedSamples = samples;
                lastPublishedSamples = identity;
                lastAcceptedHost = DesktopCapturer.ResolvedHost;
            }
            firstUnusableAt = 0;
            Interlocked.Exchange(ref lastCaptureAt, Environment.TickCount64);
            Interlocked.Increment(ref captureCount);
            // Only the cache's reference goes; a render still using the previous frame keeps it.
            old?.Dispose();
            // The glyph glass (the clock) and the popups re-render against the newer frame; the
            // live tick used to raise this every round through Invalidate, so this keeps their
            // contract while only real change raises it now.
            NotifyInvalidated();
            // The buffer's pixels now belong to the snapshot — the next probe captures fresh.
            published = true;
            return ProbeOutcome.Published;
        }
        catch
        {
            // Ownership by stage: nothing was published → the capture bitmap (pooled or fresh) is
            // still ours; once published the snapshot owns the pixels and the buffer is gone.
            if (!published) bmp?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Shared bookkeeping for a capture that produced nothing usable: the grace window, the
    /// diagnostics event and, once the failure persists, forgiving the remembered host so it is
    /// re-resolved with full validation. Reentrant on <see cref="Gate"/>: CaptureOnce calls this
    /// holding the wallpaper Gate, the probe does not.
    /// </summary>
    private static void NoteUnusableCapture(bool flat, bool jumped)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (firstUnusableAt == 0)
            {
                firstUnusableAt = now;
                GlassDiagnostics.Event(flat ? "wallpaper capture flat — masking with the previous frame"
                    : jumped ? "wallpaper capture jumped (overlay transition?) — masking with the previous frame"
                    : "wallpaper capture failed — masking with the previous frame");
            }
            else if (now - firstUnusableAt >= UnusableGraceMs)
            {
                DesktopCapturer.ForgetResolvedHost();
                // Forgiving the host alone cannot unstick a genuinely new wallpaper: the temporal
                // check would keep comparing every incoming frame against the OLD scene's grid and
                // rejecting it as an overlay transition — a wallpaper switch IS a wild jump against
                // the scene it replaced, and unlike a transition it never settles back to the old
                // content. Retire the acceptance reference too, so the next capture validates
                // against nothing and publishes; the grids re-arm from the current scene then.
                // lastPublishedSamples stays: it still describes what the surfaces render.
                // (The branch deliberately covers plain capture FAILURES too, not only flat/jumped
                // frames: a run of null captures — PrintWindow and the screen BitBlt both failing —
                // used to never reach any recovery at all, because there was no frame to classify.)
                lock (SampleGate)
                {
                    lastAcceptedSamples = null;
                    lastAcceptedHost = IntPtr.Zero;
                }
                GlassDiagnostics.Event("unusable wallpaper capture persisted — re-resolving the wallpaper host");
            }
        }
    }

    /// <summary>
    /// Retire the temporal acceptance grid (<c>lastAcceptedSamples</c>/<c>lastAcceptedHost</c>).
    /// Two call sites share the invariant: the capture source itself changing (window host ⇄
    /// screen BitBlt, see <see cref="DesktopCapturer"/>) — the two sources look very different for
    /// the same desktop and the wild-jump check would read the first frame from the new source as
    /// an overlay transition — and every wallpaper-change entry point
    /// (<see cref="Invalidate"/>/<see cref="BeginSettle"/>, see the doc there): a new wallpaper is,
    /// against the scene the grid describes, a wild jump by definition. With nothing to compare
    /// against, the next frame validates as fresh and publishes. Internal because
    /// <see cref="DesktopCapturer"/>'s screen-fallback transitions call it too (same
    /// source-change rationale).
    /// </summary>
    internal static void ResetTemporalAcceptance()
    {
        lock (SampleGate)
        {
            lastAcceptedSamples = null;
            lastAcceptedHost = IntPtr.Zero;
        }
    }

    /// <summary>
    /// True when <paramref name="samples"/> matches the identity grid of the frame that is already
    /// published — every cell within <see cref="IdentityTolerance"/> per channel. A match means
    /// the desktop has not visibly moved since the surfaces last rendered: the glass they show is
    /// still exactly what a fresh render would produce.
    /// </summary>
    private static bool MatchesLastPublished(int[] samples)
    {
        lock (SampleGate)
        {
            var previous = lastPublishedSamples;
            if (previous == null || previous.Length != samples.Length) return false;
            for (var i = 0; i < samples.Length; i++)
            {
                var a = samples[i];
                var b = previous[i];
                if (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) > IdentityTolerance
                    || Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) > IdentityTolerance
                    || Math.Abs((a & 255) - (b & 255)) > IdentityTolerance)
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>Drop the masking frame. Callers must hold <see cref="Gate"/>; in-flight renders keep their own references.</summary>
    private static void DropStale()
    {
        var old = stale;
        stale = null;
        old?.Dispose();
    }

    /// <summary>Static file based fallback (Windows wallpaper image + registry placement).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WallpaperSnapshot FromFileFallback()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (!File.Exists(path)) path = InteropService.GetWallpaperPath();
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
        var style = desktop?.GetValue("WallpaperStyle") as string ?? "10";
        var tile = desktop?.GetValue("TileWallpaper") as string == "1";
        var rgb = (colors?.GetValue("Background") as string ?? "32 38 48").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var background = rgb.Length == 3 && byte.TryParse(rgb[0], out var r) && byte.TryParse(rgb[1], out var g) && byte.TryParse(rgb[2], out var b)
            ? new SKColor(r, g, b) : new SKColor(32, 38, 48);
        var exists = File.Exists(path);
        var key = $"{path}|{(exists ? File.GetLastWriteTimeUtc(path).Ticks : 0)}|{style}|{tile}|{background}";
        if (cachedKey == key && cached is { LiveCapture: false }) return cached;
        var bytes = exists ? File.ReadAllBytes(path) : null;
        SKBitmap? bmp = null;
        if (bytes != null)
        {
            try
            {
                bmp = SKBitmap.Decode(bytes);
                bmp?.SetImmutable();
            }
            catch { }
        }
        var old = cached;
        cached = WallpaperSnapshot.FromBitmap(bytes, background, bmp, style, tile);
        cachedKey = key;
        old?.Dispose();
        // The static file is the source of truth now, so the last live frame no longer masks
        // anything — release it.
        DropStale();
        return cached;
    }
}

/// <summary>
/// Captures the live desktop wallpaper — exactly what the user sees behind the widgets —
/// including animated wallpapers.
/// <para>
/// A static wallpaper is painted by <c>Progman</c>, so <c>PrintWindow(Progman)</c> is enough.
/// Every wallpaper <i>engine</i> instead reparents its own render window into a <c>WorkerW</c>
/// that sits between the desktop icons and the wallpaper: Wallpaper Engine in its default
/// "desktop" mode does this, and its frames never reach Progman at all. The host is located the
/// documented way — the wallpaper <c>WorkerW</c> is the top-level <c>WorkerW</c> that follows the
/// one owning <c>SHELLDLL_DefView</c> — and the first candidate that yields a plausible
/// wallpaper is remembered, so the source only has to be resolved once.
/// </para>
/// <para>
/// When <i>no</i> host can be printed — UIPI refuses every cross-process <c>PrintWindow</c>
/// because the shell or the wallpaper engine runs at a higher integrity level — captures come
/// from a plain screen BitBlt instead (<see cref="TryScreenCapture"/>), with this process's own
/// windows excluded from the frame so the glass cannot sample itself.
/// </para>
/// </summary>
public static class DesktopCapturer
{
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        IntPtr bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height,
        IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extraData);

    // SM_* virtual desktop metrics (physical pixels; the process is DPI aware).
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCXVirtualScreen = 78, SmCYVirtualScreen = 79;

    /// <summary>BitBlt raster op that copies the source pixels verbatim.</summary>
    private const uint SrcCopy = 0x00CC0020;

    /// <summary>WDA_NONE: the window appears in captures again (the default affinity).</summary>
    private const uint WdaNone = 0x00000000;

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE (Windows 10 2004+): the window is omitted from every capture
    /// source at the DWM level — capture reads show the pixels behind it instead.
    /// </summary>
    private const uint WdaExcludeFromCapture = 0x00000011;

    /// <summary>
    /// Amortization window of the capture-affinity pass: a window created moments ago is
    /// excluded by the next pass, so the steady state costs no enumeration per capture.
    /// </summary>
    private const int ExclusionPassIntervalMs = 250;

    private static readonly object HostGate = new();
    private static IntPtr resolvedHost;
    private static string resolvedHostDescription = "unresolved";

    /// <summary>
    /// Capture-source override for diagnostics and support: <c>screen</c> pins the screen
    /// fallback (so the screen path can be exercised even where window printing works),
    /// <c>window</c> disables the automatic fallback (so the window path can be observed
    /// without the fallback masking it). Unset keeps the automatic behaviour.
    /// </summary>
    private static readonly string? CaptureOverride =
        Environment.GetEnvironmentVariable("DESKCANVAS_GLASS_CAPTURE")?.Trim().ToLowerInvariant();

    private static readonly bool ScreenFallbackDisabled = CaptureOverride == "window";

    /// <summary>
    /// 1 while captures come from the screen BitBlt instead of a wallpaper host window —
    /// because no host could be printed (UIPI against a higher-integrity shell), or because
    /// the <c>DESKCANVAS_GLASS_CAPTURE=screen</c> override pins it for testing.
    /// </summary>
    private static int screenFallback = CaptureOverride == "screen" ? 1 : 0;

    /// <summary>Tick of the last capture-affinity pass; see <see cref="EnsureCaptureVisibilityExclusion"/>.</summary>
    private static long lastExclusionPassAt;

    /// <summary>1 once <see cref="EnsureCaptureVisibilityExclusion"/> actually excluded a window.</summary>
    private static int exclusionApplied;

    /// <summary>
    /// Win32 error of the most recent PrintWindow refusal (0 when the last print succeeded).
    /// 5 (ERROR_ACCESS_DENIED) is the UIPI signature of a host at a higher integrity level, and
    /// the only failure the screen fallback exists for. A transient refusal (err 87 and friends)
    /// must NOT flip the source: the window path retries those, and the wallpaper-only content
    /// is strictly preferred over the screen.
    /// </summary>
    private static int lastPrintError;

    /// <summary>Capture outcomes already written to the trace, so a novel one is never dropped.</summary>
    private static readonly HashSet<string> seenCaptureTraces = new();

    private static long lastCaptureTraceAt;

    /// <summary>
    /// How long a already-seen outcome may be repeated in the trace.
    /// </summary>
    private const int CaptureTraceRepeatMs = 5000;

    /// <summary>
    /// Record how a capture died, in the glass trace. Without this a dead capture is invisible:
    /// <see cref="CaptureWindow"/>'s exception only reached <c>Debug.WriteLine</c>, and the slow
    /// path quietly skips a candidate it rejects — so "captured but not a wallpaper" and "the
    /// capture call itself failed" both surfaced as one bare <c>wallpaper capture failed</c> line
    /// with no HWND, no stage and no reason, which is not diagnosable from a user's report.
    /// <para>
    /// <paramref name="key"/> is what deduplicates; <paramref name="message"/> is what is written.
    /// Throttling on the message alone does not work here: a single failed resolution walks the
    /// candidate list, so the messages <i>alternate</i> and every one of them looks new — that
    /// filled 1216 of the trace's 2400 lines in one run. A novel key is still written immediately,
    /// so the first occurrence of any new failure mode is never lost.
    /// </para>
    /// </summary>
    private static void TraceCapture(string key, string message)
    {
        var now = Environment.TickCount64;
        var novel = seenCaptureTraces.Add(key);
        if (!novel && now - lastCaptureTraceAt < CaptureTraceRepeatMs) return;
        lastCaptureTraceAt = now;
        GlassDiagnostics.Event("[capture] " + message);
    }

    /// <summary>Which window the current capture comes from — surfaced for diagnostics.</summary>
    public static string CaptureSourceDescription
    {
        get
        {
            if (ScreenFallbackActive) return "screen BitBlt (window printing blocked by UIPI)";
            lock (HostGate) return resolvedHostDescription;
        }
    }

    /// <summary>
    /// True while captures come from the screen instead of a wallpaper host window: the
    /// window-print path was blocked (a shell at a higher integrity level) or the screen
    /// source is pinned by the capture override. Material-visible only in that the sampled
    /// content is the composited desktop rather than the wallpaper alone.
    /// </summary>
    public static bool ScreenFallbackActive => Volatile.Read(ref screenFallback) == 1;

    public static SKBitmap? CaptureBitmap() => CaptureBitmap(out _);

    /// <summary>
    /// Capture the resolved wallpaper host, or <c>null</c> when nothing usable was captured.
    /// <paramref name="flat"/> is true when a frame came back but is one uniform colour — the
    /// transiently empty surface <c>PrintWindow</c> hands out while the host re-composites. The
    /// caller masks it with the previous frame instead of re-resolving here: falling through to
    /// the next source on every flat frame would flip a genuinely dark animated scene back to
    /// Progman, a visible jump the fast path exists to avoid.
    /// <para>
    /// When every candidate is refused with ERROR_ACCESS_DENIED — UIPI against a shell at a
    /// higher integrity level, see <see cref="TryScreenCapture"/> — captures switch to the
    /// screen BitBlt for the session instead of dropping to the static file fallback. The
    /// wallpaper-only window path stays the preferred source everywhere it works.
    /// </para>
    /// </summary>
    /// <param name="reuse">
    /// Optional bitmap to capture into instead of allocating a new one; it must already be the
    /// virtual-screen size (verified below) and is never disposed here. The identity probe uses
    /// this so its steady state — the same unchanged desktop captured over and over — costs no
    /// allocation. A successful capture hands the buffer to the caller; a failure leaves it
    /// untouched for the next attempt.
    /// </param>
    public static SKBitmap? CaptureBitmap(out bool flat, SKBitmap? reuse = null)
    {
        flat = false;
        var width = GetSystemMetrics(SmCXVirtualScreen);
        var height = GetSystemMetrics(SmCYVirtualScreen);
        if (width <= 0 || height <= 0) return null;

        // Screen fallback fast path: engaged when no wallpaper host could be printed (UIPI —
        // see TryScreenCapture) or pinned by the capture override. A screen capture needs no
        // host, so this sits before the remembered-window check.
        if (ScreenFallbackActive)
        {
            var screen = TryScreenCapture(width, height, reuse);
            if (screen != null)
            {
                if (!LooksLikeWallpaper(screen)) flat = true;
                else screen.SetImmutable();
                return screen;
            }
            // The screen read itself failed (a session that cannot read the screen at all):
            // fall through — the window path stays the only remaining option.
        }

        // Fast path: the host resolved last time. This runs once per sampled frame, so it must not
        // walk every top-level window to rediscover a window that has not changed. The frame gets
        // one cheap validation — a coarse grid sample, well under a millisecond: a flat frame
        // published as real wallpaper paints every glass card as one solid colour until the next
        // round, which is the "widget suddenly blinks to a blank card" flicker.
        IntPtr remembered;
        lock (HostGate) remembered = resolvedHost;
        if (remembered != IntPtr.Zero && IsWindow(remembered))
        {
            var fast = CaptureWindow(remembered, width, height, reuse);
            if (fast != null)
            {
                if (!LooksLikeWallpaper(fast))
                {
                    flat = true;
                    return fast;
                }
                fast.SetImmutable();
                return fast;
            }
        }

        // Slow path: first frame, or the remembered host died (a wallpaper engine restarting, a
        // monitor hot-plug, a shell restart). Re-resolve from scratch.
        var candidates = ResolveCandidates();
        var captured = 0;
        var rejected = 0;
        var uipiBlocked = false;
        foreach (var (hwnd, description) in candidates)
        {
            var bmp = CaptureWindow(hwnd, width, height, reuse);
            if (bmp == null)
            {
                if (Volatile.Read(ref lastPrintError) == 5) uipiBlocked = true;
                continue;
            }
            captured++;
            if (!LooksLikeWallpaper(bmp))
            {
                // A frame came back but shows nothing (a uniform or pure-black surface — including
                // the empty one PrintWindow hands out while a host re-composites). Silently moving
                // on is why "captured but unusable" reached the trace as "capture failed".
                rejected++;
                bmp.Dispose();
                continue;
            }

            lock (HostGate)
            {
                resolvedHost = hwnd;
                resolvedHostDescription = description;
            }
            // A host printed its content again: if the screen fallback was engaged (a resume
            // crossed integrity levels, the shell restarted unelevated), the window path is
            // the better source once more — leave the fallback and let the windows back in.
            if (ScreenFallbackActive) LeaveScreenFallback();
            TraceCapture($"resolved:{description}",
                $"resolved '{description}' (0x{hwnd:X}) from {candidates.Count} candidate(s)");
            bmp.SetImmutable();
            return bmp;
        }

        if (captured == 0 && uipiBlocked && !ScreenFallbackDisabled)
        {
            // Not one candidate would print, and at least one refusal was ERROR_ACCESS_DENIED:
            // UIPI — the shell, the wallpaper host or both run at a higher integrity level (an
            // elevated explorer.exe, a UAC-disabled/UAC-hardened session, an elevated wallpaper
            // engine), so every cross-process PrintWindow is denied. The screen BitBlt involves
            // no cross-process call and keeps working there — the last-resort source, since the
            // wallpaper-only host content is what the material is designed around.
            EnterScreenFallback();
            var screen = TryScreenCapture(width, height, reuse);
            if (screen != null)
            {
                if (!LooksLikeWallpaper(screen)) flat = true;
                else screen.SetImmutable();
                return screen;
            }
        }

        TraceCapture($"nocandidate:{captured}:{rejected}",
            $"{candidates.Count} candidate(s), {captured} captured, {rejected} rejected as blank " +
            "— no wallpaper host produced a usable frame");
        return null;
    }

    /// <summary>
    /// Drop the remembered host so the next capture re-resolves from scratch. Called when the
    /// remembered window keeps printing flat frames: it either died without its handle going
    /// invalid or a genuinely uniform wallpaper took over, and in both cases trusting it again
    /// needs full validation.
    /// </summary>
    public static void ForgetResolvedHost()
    {
        lock (HostGate)
        {
            resolvedHost = IntPtr.Zero;
            resolvedHostDescription = "forgiven after persistent unusable captures";
        }

        // Full re-validation includes the capture source itself: drop back to the window path
        // (the next capture re-resolves it and re-engages the screen fallback if still blocked).
        LeaveScreenFallback();
    }

    /// <summary>The host the current capture comes from, for cross-checking capture coherence.</summary>
    public static IntPtr ResolvedHost
    {
        get { lock (HostGate) return resolvedHost; }
    }

    /// <summary>
    /// Sample a coarse grid of the bitmap as RGB ints, as the validity check uses. Cheap enough to
    /// run once per sampled frame; the identity probe samples a finer grid with explicit
    /// <paramref name="columns"/> / <paramref name="rows"/>.
    /// </summary>
    public static int[] SampleGrid(SKBitmap bmp, int columns = 16, int rows = 9)
    {
        var stepX = Math.Max(1, bmp.Width / columns);
        var stepY = Math.Max(1, bmp.Height / rows);
        var cols = Math.Max(1, (bmp.Width + stepX - 1) / stepX);
        var gridRows = Math.Max(1, (bmp.Height + stepY - 1) / stepY);
        var samples = new int[gridRows * cols];
        var index = 0;
        for (var y = stepY / 2; y < bmp.Height; y += stepY)
        for (var x = stepX / 2; x < bmp.Width; x += stepX)
        {
            var c = bmp.GetPixel(x, y);
            samples[index++] = (c.Red << 16) | (c.Green << 8) | c.Blue;
        }
        return samples;
    }

    /// <summary>
    /// True when a frame replaced nearly everything the last accepted one showed. Real wallpaper
    /// content never does that between two rounds; what does is the MPO overlay transition —
    /// while the wallpaper's hardware plane is promoted or demoted, PrintWindow reads the bare
    /// redirection surface: the desktop colour, sometimes with slivers of real content. Such a
    /// frame is unusable no matter how non-uniform it is.
    /// </summary>
    public static bool IsWildJump(int[] samples, int[] previous)
    {
        if (samples.Length != previous.Length || samples.Length == 0) return false;
        var changed = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = samples[i];
            var b = previous[i];
            var dr = Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
            var dg = Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255));
            var db = Math.Abs((a & 255) - (b & 255));
            if ((dr + dg + db) / 3 >= 35) changed++;
        }
        return changed >= samples.Length * 0.85;
    }

    /// <summary>Encode a one-off capture as PNG (diagnostics only).</summary>
    public static byte[] Capture()
    {
        using var bmp = CaptureBitmap(out _);
        if (bmp == null) return [];
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Candidate windows in priority order: the WorkerW inside Progman that hosts the animated
    /// wallpaper, then the classic top-level animated host, then the classic Progman wallpaper,
    /// then any visible child surface of an animated host (some engines render into a child of the
    /// WorkerW rather than into it). The already-remembered host is tried separately, before this
    /// runs, so a steady state never enumerates the window list at all.
    /// <para>
    /// <b>The Progman-child scan is not redundant with the top-level one.</b> On the shells that
    /// re-parent (Windows 11 here, but also anything else that moves the desktop windows),
    /// <c>SHELLDLL_DefView</c> hangs directly off Progman and so does the wallpaper WorkerW —
    /// Wallpaper Engine's <c>WPELiveWallpaper</c> DX11 window is a child of exactly such a WorkerW.
    /// <c>EnumWindows</c> only walks <i>top-level</i> windows, so that host was invisible to this
    /// function: the "follows the icons' WorkerW" rule could never fire (the DefView is not under
    /// any top-level WorkerW) and the "WorkerW parented to Progman" rule iterated the same
    /// top-level-only list. The only candidate left was Progman — which is what every capture then
    /// went through.
    /// </para>
    /// </summary>
    private static List<(IntPtr Hwnd, string Description)> ResolveCandidates()
    {
        var result = new List<(IntPtr, string)>();
        var seen = new HashSet<IntPtr>();

        void Add(IntPtr hwnd, string description)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !seen.Add(hwnd)) return;
            result.Add((hwnd, description));
        }

        var progman = FindWindow("Progman", null);
        var defViewHost = IntPtr.Zero;
        var workerViews = new List<IntPtr>();
        var progmanWorkers = new List<IntPtr>();

        if (progman != IntPtr.Zero)
            EnumChildWindows(progman, (child, _) =>
            {
                if (ClassNameOf(child) == "WorkerW") progmanWorkers.Add(child);
                return true;
            }, IntPtr.Zero);

        EnumWindows((hwnd, _) =>
        {
            if (ClassNameOf(hwnd) != "WorkerW") return true;
            workerViews.Add(hwnd);
            if (defViewHost == IntPtr.Zero &&
                FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                defViewHost = hwnd;
            return true;
        }, IntPtr.Zero);

        // The re-parented host comes first: it is where the animated wallpaper actually renders on
        // these shells, and a black candidate only costs one rejected capture before the next one.
        foreach (var worker in progmanWorkers)
        {
            Add(worker, "WorkerW in Progman (animated wallpaper host)");
            AddChildren(worker, "WorkerW child");
        }

        // Classic: the wallpaper WorkerW is the top-level WorkerW that follows the icons' WorkerW.
        // When the desktop is static there is no such sibling and this contributes nothing.
        if (defViewHost != IntPtr.Zero)
        {
            var afterIcons = false;
            foreach (var worker in workerViews)
            {
                if (afterIcons) { Add(worker, "WorkerW (animated wallpaper host)"); AddChildren(worker, "WorkerW child"); }
                else if (worker == defViewHost) afterIcons = true;
            }
        }

        // A top-level WorkerW owned by Progman also hosts wallpaper on some shell versions.
        foreach (var worker in workerViews)
            if (IsChildOf(progman, worker)) Add(worker, "WorkerW (Progman child)");

        Add(progman, "Progman");
        return result;

        void AddChildren(IntPtr parent, string label)
        {
            EnumChildWindows(parent, (child, _) =>
            {
                if (IsWindowVisible(child)) Add(child, label);
                return true;
            }, IntPtr.Zero);
        }
    }

    private static bool IsChildOf(IntPtr parent, IntPtr child)
    {
        var current = child;
        for (var depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
        {
            var owner = GetParent(current);
            if (owner == parent) return true;
            if (owner == IntPtr.Zero) return false;
            current = owner;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static SKBitmap? CaptureWindow(IntPtr hwnd, int width, int height, SKBitmap? reuse = null)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            TraceCapture($"getdc:{err}", $"GetDC(screen) failed (err {err}) — no capture possible");
            return null;
        }
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            // A GDI failure here is silent otherwise: SelectObject on a null DC, PrintWindow fails,
            // and the caller only ever sees "the capture failed".
            var err = Marshal.GetLastWin32Error();
            TraceCapture($"gdi:{err}", $"CreateCompatibleDC/Bitmap failed for {ClassNameOf(hwnd)} " +
                         $"{width}x{height} (memDc=0x{memDc:X} bmp=0x{bitmap:X} err {err})");
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }
        var previous = SelectObject(memDc, bitmap);
        try
        {
            // Windows refuses this outright when it cannot reach the window (UIPI against a
            // higher-integrity host) and otherwise hands back a frame that may have nothing drawn
            // on it — the two are indistinguishable to the caller without this.
            if (!PrintWindow(hwnd, memDc, PwRenderFullContent))
            {
                var err = Marshal.GetLastWin32Error();
                Volatile.Write(ref lastPrintError, err);
                var hint = err == 5
                    ? " — ERROR_ACCESS_DENIED: the window belongs to a process at a HIGHER integrity " +
                      "level (an elevated shell), so UIPI blocks the cross-process print. " +
                      "The screen-capture fallback covers this case."
                    : "";
                TraceCapture($"printwindow:{ClassNameOf(hwnd)}:{err}",
                    $"{ClassNameOf(hwnd)}: PrintWindow(PW_RENDERFULLCONTENT) returned false (err {err}){hint}");
                return null;
            }
            Volatile.Write(ref lastPrintError, 0);
            return ReadBitmap(memDc, bitmap, width, height, reuse);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wallpaper capture failed for {ClassNameOf(hwnd)}: {ex.Message}");
            TraceCapture($"throw:{ClassNameOf(hwnd)}:{ex.GetType().Name}",
                $"{ClassNameOf(hwnd)}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Capture the composited desktop itself — the cross-integrity-level alternative to printing
    /// a wallpaper host's window.
    /// <para>
    /// <c>PrintWindow</c> is a cross-process request, so UIPI refuses it when the target belongs
    /// to a process at a higher integrity level (an elevated explorer.exe, an elevated wallpaper
    /// engine — or a UAC-session where the shell is High while this process is Medium): every
    /// candidate fails with ERROR_ACCESS_DENIED and the material would never leave the static
    /// file fallback. A screen read sends no message and passes no permission gate, so it keeps
    /// working there. The trade is content: the screen contains every visible window, not only
    /// the wallpaper, so <see cref="EnsureCaptureVisibilityExclusion"/> keeps this process's own
    /// windows out of the frame (the pixels behind them show through instead).
    /// </para>
    /// <para>
    /// Same virtual-desktop geometry as <see cref="CaptureWindow"/>: the bitmap is the desktop
    /// at physical pixels from <c>(SM_XVIRTUALSCREEN, SM_YVIRTUALSCREEN)</c>, which is exactly
    /// the space the renderer's live-capture placement expects.
    /// </para>
    /// </summary>
    private static SKBitmap? TryScreenCapture(int width, int height, SKBitmap? reuse)
    {
        EnsureCaptureVisibilityExclusion();

        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            TraceCapture($"blt:getdc:{err}", $"screen capture: GetDC(screen) failed (err {err})");
            return null;
        }
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            TraceCapture($"blt:gdi:{err}",
                $"screen capture: CreateCompatibleDC/Bitmap failed ({width}x{height}, err {err})");
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            return null;
        }

        var previous = SelectObject(memDc, bitmap);
        try
        {
            var x = GetSystemMetrics(SmXVirtualScreen);
            var y = GetSystemMetrics(SmYVirtualScreen);
            if (!BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SrcCopy))
            {
                var err = Marshal.GetLastWin32Error();
                TraceCapture($"blt:copy:{err}", $"screen capture: BitBlt failed (err {err})");
                return null;
            }
            return ReadBitmap(memDc, bitmap, width, height, reuse);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Screen capture failed: {ex.Message}");
            TraceCapture($"blt:throw:{ex.GetType().Name}", $"screen capture: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>
    /// Engage the screen fallback, once: the content source changes fundamentally (the screen
    /// instead of a wallpaper host), and the temporal acceptance grid would read the first
    /// screen frame as an overlay-transition wild jump against the host's last frame and drop
    /// it. Retiring the acceptance reference lets the switch publish immediately.
    /// </summary>
    private static void EnterScreenFallback()
    {
        if (Interlocked.Exchange(ref screenFallback, 1) == 1) return;
        LiquidGlassWallpaper.ResetTemporalAcceptance();
        TraceCapture("screenfallback:engage",
            "no wallpaper host could be printed (UIPI — a shell/host at a higher integrity " +
            "level?); capturing the screen instead, with DeskCanvas's own windows excluded");
    }

    /// <summary>Leave the screen fallback and put this process's windows back into captures.</summary>
    private static void LeaveScreenFallback()
    {
        // The capture override pins the source for diagnostics; only the automatic fallback
        // may leave it.
        if (CaptureOverride == "screen") return;
        if (Interlocked.Exchange(ref screenFallback, 0) == 0) return;
        LiquidGlassWallpaper.ResetTemporalAcceptance();
        RestoreCaptureVisibility();
        TraceCapture("screenfallback:leave", "a wallpaper host prints again — back to window captures");
    }

    /// <summary>
    /// Keep this process's own windows out of the screen capture.
    /// <para>
    /// A screen capture contains every visible window — the widgets included — and feeding a
    /// glass card its own pixels is a feedback loop: the card would refract a slightly stale
    /// copy of itself, visibly echoing, and a neighbouring card's pixels would bleed through
    /// the blur the same way. WDA_EXCLUDEFROMCAPTURE removes a window from every capture source
    /// at the DWM level — what shows through is the content behind it, which is precisely "the
    /// desktop minus ourselves". Only a window's own process may set it; all of them are ours.
    /// </para>
    /// <para>
    /// Throttled and best-effort. The pass re-enumerates instead of tracking handles, so new
    /// widgets, popups and the settings window are covered without a call site each — a window
    /// created moments ago is excluded within <see cref="ExclusionPassIntervalMs"/>. A failure
    /// (an OS build without WDA_EXCLUDEFROMCAPTURE, a window that refuses the affinity) costs
    /// one call per window per pass and leaves that window merely included, as before.
    /// </para>
    /// </summary>
    private static void EnsureCaptureVisibilityExclusion()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref lastExclusionPassAt) < ExclusionPassIntervalMs) return;
        Interlocked.Exchange(ref lastExclusionPassAt, now);

        var applied = 0;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && IsOwnWindow(hwnd) &&
                SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                applied++;
            }
            return true;
        }, IntPtr.Zero);
        if (applied > 0) Volatile.Write(ref exclusionApplied, 1);
    }

    /// <summary>Undo <see cref="EnsureCaptureVisibilityExclusion"/>: the windows capture again.</summary>
    private static void RestoreCaptureVisibility()
    {
        if (Interlocked.Exchange(ref exclusionApplied, 0) == 0) return;
        EnumWindows((hwnd, _) =>
        {
            if (IsWindowVisible(hwnd) && IsOwnWindow(hwnd)) SetWindowDisplayAffinity(hwnd, WdaNone);
            return true;
        }, IntPtr.Zero);
    }

    private static bool IsOwnWindow(IntPtr hwnd) =>
        GetWindowThreadProcessId(hwnd, out var owner) != 0 && owner == (uint)Environment.ProcessId;

    /// <summary>A valid wallpaper capture has meaningful content: not uniform, not black.</summary>
    private static bool LooksLikeWallpaper(SKBitmap bmp)
    {
        var width = bmp.Width;
        var height = bmp.Height;
        var seen = new HashSet<uint>();
        var stepX = Math.Max(1, width / 16);
        var stepY = Math.Max(1, height / 9);

        for (var y = stepY / 2; y < height; y += stepY)
        for (var x = stepX / 2; x < width; x += stepX)
        {
            var color = bmp.GetPixel(x, y);
            seen.Add(((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue);
        }
        if (seen.Count < 3) return false;

        double sum = 0;
        foreach (var c in seen)
        {
            var r = (c >> 16) & 0xFF; var g = (c >> 8) & 0xFF; var b = c & 0xFF;
            sum += (299 * r + 587 * g + 114 * b) / 1000.0;
        }
        return sum / seen.Count > 4; // not a pure-black capture
    }

    private static SKBitmap? ReadBitmap(IntPtr memDc, IntPtr bitmap, int width, int height, SKBitmap? reuse = null)
    {
        var info = CreateHeader(width, height);
        // Reuse is only honoured at the exact size the desktop reports — anything else decodes
        // into a fresh allocation, and the caller retires the mismatched buffer.
        SKBitmap? skBitmap = reuse is { Width: var rw, Height: var rh } && rw == width && rh == height
            ? reuse
            : new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = skBitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            TraceCapture("nopixels", "the capture bitmap has no pixels (SKBitmap allocation failed)");
            if (!ReferenceEquals(skBitmap, reuse)) skBitmap.Dispose();
            return null;
        }

        var lines = GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref info, 0);
        if (lines != height)
        {
            var err = Marshal.GetLastWin32Error();
            TraceCapture($"getdibits:{lines}:{err}",
                $"GetDIBits copied {lines} of {height} rows (err {err})");
            if (!ReferenceEquals(skBitmap, reuse)) skBitmap.Dispose();
            return null;
        }

        return skBitmap;
    }

    private static BitmapInfo CreateHeader(int width, int height) => new()
    {
        Header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32,
            Compression = 0 // BI_RGB
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }
}
