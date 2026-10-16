using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

/// <summary>
/// Activity push implemented by widget content that reacts to host lifecycle transitions.
/// The host calls it on the UI thread, right after the same snapshot was recorded on the
/// session — content and session state can never disagree about the current reason.
/// </summary>
public interface IWidgetActivityConsumer
{
    /// <summary>Apply the host's activity transition (gate timers, pause work, …).</summary>
    void OnSessionActivity(ActivitySnapshot activity);
}

/// <summary>
/// Host-side store of the live widget sessions: one <see cref="WidgetSession"/> per widget
/// instance, created on the host's widget-creation path and released on teardown.
/// <para>
/// Purely a bookkeeping layer over the session — it never blocks the host: every async step
/// runs fire-and-forget and is serialized inside the session itself, and every lookup
/// degrades to a no-op for unknown instances (a report racing a teardown, legacy layout
/// entries without a stable identity, the preview, …). With no callbacks and an uncontended
/// gate the fire-and-forget sequences complete synchronously, so a teardown is fully done
/// by the time <see cref="Remove"/> returns.
/// </para>
/// </summary>
public static class WidgetSessionRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, WidgetSession> sessions = new();

    /// <summary>
    /// Return the session of a widget instance, creating (and synchronously initializing) it
    /// on first use. The session carries no callbacks, so initialization completes inline and
    /// the host can report activity immediately after this returns.
    /// </summary>
    public static IWidgetSession GetOrCreate(WidgetInstanceId instanceId)
    {
        if (instanceId.IsEmpty)
            throw new ArgumentException("InstanceId must not be empty.", nameof(instanceId));

        if (sessions.TryGetValue(instanceId.Value, out var existing)) return existing;

        var session = new WidgetSession(instanceId);
        if (!sessions.TryAdd(instanceId.Value, session))
            return sessions[instanceId.Value]; // A concurrent creator won the instance.
        _ = session.InitializeAsync();
        return session;
    }

    /// <summary>The live session of the instance, or null when none is registered.</summary>
    public static IWidgetSession? TryGet(WidgetInstanceId instanceId) =>
        sessions.TryGetValue(instanceId.Value, out var session) ? session : null;

    /// <summary>
    /// Drop the session of a widget instance. When <paramref name="finalActivity"/> is given,
    /// the session records it before disposing (in that order, inside the session's own
    /// serialization). Idempotent: an unknown instance reports false and stays untouched.
    /// </summary>
    public static bool Remove(WidgetInstanceId instanceId, ActivitySnapshot? finalActivity = null)
    {
        if (!sessions.TryRemove(instanceId.Value, out var session)) return false;

        RunQuietly(async () =>
        {
            if (finalActivity is { } activity)
            {
                try { await session.SetActivityAsync(activity).ConfigureAwait(false); }
                catch { /* A faulted/already-disposed session only drops the final report. */ }
            }

            await session.DisposeAsync().ConfigureAwait(false);
        });
        return true;
    }

    /// <summary>
    /// Record a host activity transition on the instance's session. No-op for unknown
    /// instances and sessions that are not Ready — the report is dropped instead of throwing.
    /// </summary>
    public static void ReportActivity(WidgetInstanceId instanceId, ActivitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!sessions.TryGetValue(instanceId.Value, out var session)) return;
        if (session.State != WidgetLifecycleState.Ready) return;

        RunQuietly(async () =>
        {
            try { await session.SetActivityAsync(snapshot).ConfigureAwait(false); }
            catch { /* A concurrent teardown only drops the report. */ }
        });
    }

    /// <summary>Test seam: drop every session (public reset convention — fixture assemblies
    /// have no InternalsVisibleTo; see <see cref="ResourceLedger.ResetForChecks"/>).</summary>
    public static void ResetForChecks()
    {
        foreach (var pair in sessions)
        {
            if (!sessions.TryRemove(pair.Key, out _)) continue;
            RunQuietly(async () =>
            {
                try { await pair.Value.DisposeAsync().ConfigureAwait(false); }
                catch { /* A leftover session must never fail the reset. */ }
            });
        }
    }

    private static void RunQuietly(Func<Task> action)
    {
        // Fire-and-forget, but executed inline: with an uncontended gate and no callbacks the
        // whole sequence completes synchronously. Faults are observed so nothing surfaces as
        // an UnobservedTaskException, and the host's close path never awaits anything here.
        _ = action().ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);
    }
}
