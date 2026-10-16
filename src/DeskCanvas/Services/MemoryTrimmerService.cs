using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

namespace DeskCanvas.Services;

/// <summary>
/// How eagerly a <see cref="MemoryTrimmerService.RequestTrim"/> caller wants its trim to run.
/// </summary>
public enum TrimPriority
{
    /// <summary>
    /// Background release (widget closes, profile switches, fullscreen transitions): the request
    /// waits out the cooldown, so a burst of releases still becomes one trim.
    /// </summary>
    Coalesced,

    /// <summary>
    /// A release the user is directly watching (the settings window being closed hands its page
    /// previews back): the request skips the cooldown so the freed memory actually leaves the
    /// process while the user looks for it. Single-flight and the floor still apply — a request
    /// that arrives while another trim is running is still coalesced.
    /// </summary>
    Immediate,

    /// <summary>
    /// Fullscreen or maximized suspension: skips cooldown and floor, aggressively compacting
    /// heap and emptying working set so background memory drops immediately.
    /// </summary>
    Force,
}

/// <summary>
/// The process-wide, <b>on-demand</b> memory maintenance entry.
///
/// What used to live here was a routine sweep: every 5 minutes, whenever the process held more
/// than 150 MiB, a blocking double gen-2 collection plus <c>SetProcessWorkingSetSize(-1, -1)</c>.
/// A desktop-widget host sits permanently above that threshold, so the routine fired on almost
/// every tick — and a working-set trim does not free memory, it only evicts resident pages that
/// are touched again moments later. The result was the periodic sawtooth in the task manager
/// (and the stutter of a compacting gen-2 collection every five minutes) without any real
/// reduction in committed memory.
///
/// The entry is kept for the moments where something large and native was <i>actually</i>
/// released — a widget (or the last widget) being removed, fullscreen suspension, a profile
/// switch, the settings window being torn down — so the freed pages are handed back promptly
/// instead of waiting for the next widget to happen to need them. It is deliberately not called
/// on a timer, and every request passes through three guards:
/// <list type="bullet">
/// <item><b>Single-flight</b> — a request while a trim is running is coalesced into one
/// follow-up (see below), never a second concurrent compaction.</item>
/// <item><b>Cooldown</b> — coalesced requests run at most once per <see cref="Cooldown"/>. A
/// burst of background releases (removing widgets one by one, switching profiles, fullscreen
/// toggles) does not queue a trim per event: the burst schedules <b>one</b> deferred trim for
/// when the cooldown lapses, so the process still comes back down after the burst instead of
/// never. A request flagged <see cref="TrimPriority.Immediate"/> — the settings window being
/// closed, the one release the user is directly watching — skips this gate.</item>
/// <item><b>Floor</b> — below <see cref="ActionableFloorBytes"/> the process is comfortable and
/// a trim would only cost a compacting collection for nothing.</item>
/// </list>
/// The actual work stays in <see cref="InteropService.TrimProcessMemory"/> and always runs off
/// the UI thread.
/// </summary>
public static class MemoryTrimmerService
{
    /// <summary>Below this the process is comfortable and a trim only costs CPU (a compacting gen-2 collection).</summary>
    private const long ActionableFloorBytes = 256L * 1024 * 1024;

    /// <summary>Minimum time between two completed trims.</summary>
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(90);

    private static readonly object Gate = new();
    private static bool inFlight;
    private static long lastTrimCompletedAt = long.MinValue;

    /// <summary>0/1 latch: at most one coalesced follow-up trim is pending at any time.</summary>
    private static int deferred;

    /// <summary>Priority of the pending follow-up (only meaningful while <see cref="deferred"/> is 1).</summary>
    private static int deferredPriority;

    /// <summary>Completed trims since process start — read by the check suites, not by the app itself.</summary>
    private static int completedTrims;

    /// <summary>Number of trims that have completed since process start (diagnostics/checks only).</summary>
    public static int CompletedTrims => Volatile.Read(ref completedTrims);

    /// <summary>
    /// Ask for one maintenance trim. Cheap and non-blocking: the caller never waits for the
    /// collection. A request the guards cannot honour right now is coalesced into a single
    /// deferred retry — unless the process is simply below the floor — so the memory released
    /// by a burst of widget teardowns still comes back down. (An earlier version dropped such
    /// requests; because the trigger events are not periodic, a suppressed request meant the
    /// release behind it was never revisited, and "everything was removed but memory stayed
    /// high" was the result.)
    /// </summary>
    /// <param name="priority">
    /// <see cref="TrimPriority.Immediate"/> for release events whose effect the user is directly
    /// watching (the settings window being closed) — the request skips the cooldown so the
    /// memory actually leaves the process while the user looks for it. Leave the default for
    /// background bursts, which coalesce behind the cooldown as before.
    /// </param>
    public static void RequestTrim(TrimPriority priority = TrimPriority.Coalesced)
    {
        // Pressure upgrade (§14.3, first graded tier): when the resource ledger reports High
        // pressure, the requested priority is lifted one level so the existing maintenance
        // action runs harder while the process is near its soft cap. This only strengthens a
        // trim that was already requested — it never triggers one on its own, and observation
        // failure degrades to "no pressure". Deferred follow-ups re-enter here and re-evaluate.
        priority = EscalateForPressure(priority, ObservePressure());

        var run = false;
        long deferMs = -1;   // < 0: nothing to defer

        lock (Gate)
        {
            var now = Environment.TickCount64;

            if (inFlight)
            {
                // A trim is running right now; whatever was just released may not be included
                // in its walk. Re-check shortly after it finishes.
                deferMs = 500;
            }
            else
            {
                var remaining = lastTrimCompletedAt == long.MinValue
                    ? 0L
                    : (long)Cooldown.TotalMilliseconds - (now - lastTrimCompletedAt);

                if (remaining > 0 && priority == TrimPriority.Coalesced)
                {
                    deferMs = remaining + 200;
                }
                else
                {
                    long privateBytes;
                    try
                    {
                        privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
                    }
                    catch
                    {
                        return;
                    }

                    if (priority == TrimPriority.Force || privateBytes >= ActionableFloorBytes)
                    {
                        inFlight = true;
                        run = true;
                    }
                    // Below the floor: comfortable, nothing to do and nothing to defer.
                }
            }
        }

        if (run) RunTrim();
        else if (deferMs >= 0) ScheduleDeferred(deferMs, priority);
    }

    /// <summary>
    /// Observes the process's rebuildable pixel memory against the middle experimental budget
    /// tier (384 MiB, chosen as the observation cap because the pressure experiment is expected
    /// to settle between the low and high tiers; see ResourcePressureEvaluator's calibration
    /// note). Pure observation — any failure degrades to <see cref="ResourcePressureLevel.None"/>.
    /// </summary>
    private static ResourcePressureLevel ObservePressure()
    {
        try
        {
            var snapshot = ResourceLedger.Instance.Snapshot();
            var cap = ResourceBudget.Tiers[1].SoftCapBytes;
            return ResourcePressureEvaluator.Evaluate(new ResourcePressureState(
                ResourcePressureEvaluator.TotalReclaimableBytes(snapshot), cap, Rising: false));
        }
        catch
        {
            return ResourcePressureLevel.None;
        }
    }

    /// <summary>
    /// Lifts the requested trim priority one level while pressure is High
    /// (<see cref="TrimPriority.Coalesced"/> → <see cref="TrimPriority.Immediate"/> →
    /// <see cref="TrimPriority.Force"/>); <see cref="TrimPriority.Force"/> stays Force.
    /// Watch and None leave the request untouched.
    /// </summary>
    private static TrimPriority EscalateForPressure(TrimPriority requested, ResourcePressureLevel level)
    {
        if (level != ResourcePressureLevel.High || requested == TrimPriority.Force) return requested;
        return requested == TrimPriority.Coalesced ? TrimPriority.Immediate : TrimPriority.Force;
    }

    /// <summary>
    /// Coalesces a suppressed request into one follow-up attempt (see RequestTrim). The most
    /// urgent priority among the suppressed requests is carried into the follow-up — an
    /// immediate release must stay immediate even when a timer was already pending for a
    /// coalesced one.
    /// </summary>
    private static void ScheduleDeferred(long delayMs, TrimPriority priority)
    {
        if ((int)priority > Volatile.Read(ref deferredPriority))
            Interlocked.Exchange(ref deferredPriority, (int)priority);

        if (Interlocked.Exchange(ref deferred, 1) == 1) return;

        _ = Task.Delay((int)Math.Clamp(delayMs, 100, 120_000)).ContinueWith(_ =>
        {
            Interlocked.Exchange(ref deferred, 0);
            var pending = (TrimPriority)Interlocked.Exchange(ref deferredPriority, (int)TrimPriority.Coalesced);
            RequestTrim(pending);
        });
    }

    private static void RunTrim()
    {
        _ = Task.Run(() =>
        {
            try
            {
                long before = 0;
                try
                {
                    before = Process.GetCurrentProcess().PrivateMemorySize64;
                }
                catch
                {
                    // Diagnostics only.
                }

                InteropService.TrimProcessMemory();

                long after = 0;
                try
                {
                    after = Process.GetCurrentProcess().PrivateMemorySize64;
                }
                catch
                {
                    // Diagnostics only.
                }

                lock (Gate)
                {
                    lastTrimCompletedAt = Environment.TickCount64;
                    completedTrims++;
                }

                Debug.WriteLine(
                    $"[MemoryTrimmer] maintenance trim: private {before / 1048576.0:F0} → {after / 1048576.0:F0} MiB");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MemoryTrimmer] maintenance trim failed: {ex.Message}");
            }
            finally
            {
                lock (Gate) inFlight = false;
            }
        });
    }
}
