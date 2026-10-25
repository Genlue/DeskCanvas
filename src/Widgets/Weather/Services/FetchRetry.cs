using Avalonia.Threading;

namespace Weather.Services;

/// <summary>
/// Backoff scheduler for a first fetch that failed because the machine had no network yet
/// (a fresh boot, a VPN still dialling, a proxy that has not come up).
/// <para>
/// The hourly refresh alone left the widget showing <c>--°</c> for up to an hour after boot:
/// the request fails once, returns <c>null</c>, and nothing asks again until the next
/// <c>Timer1Hour</c> tick. This retries on a short, capped backoff and stops as soon as a
/// request succeeds, so a network that appears 30 seconds after login is picked up within a
/// minute instead of an hour.
/// </para>
/// </summary>
public sealed class FetchRetry : IDisposable
{
    /// <summary>Attempt delays: quick enough for a boot race, slow enough to not hammer a dead link.</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
    ];

    private DispatcherTimer? timer;
    private int attempt;
    private bool disposed;

    /// <summary>How many retries have been scheduled since the last success (check seam).</summary>
    public int AttemptForChecks => attempt;

    /// <summary>The delay the next <see cref="Schedule"/> would use (check seam).</summary>
    public TimeSpan NextDelayForChecks => Delays[Math.Min(attempt, Delays.Count - 1)];

    /// <summary>True while a retry is queued (check seam).</summary>
    public bool IsPendingForChecks => timer != null;

    /// <summary>
    /// Queue <paramref name="retry"/> after the current backoff step. Calling again replaces the
    /// pending timer instead of stacking a second one.
    /// </summary>
    public void Schedule(Action retry)
    {
        if (disposed) return;

        var delay = NextDelayForChecks;
        if (attempt < Delays.Count) attempt++;

        timer?.Stop();

        var pending = new DispatcherTimer { Interval = delay };
        pending.Tick += (_, _) =>
        {
            pending.Stop();
            if (ReferenceEquals(timer, pending)) timer = null;
            if (disposed) return;
            retry();
        };

        timer = pending;
        pending.Start();
    }

    /// <summary>Drop the pending retry and restart the backoff (a request succeeded).</summary>
    public void Reset()
    {
        timer?.Stop();
        timer = null;
        attempt = 0;
    }

    /// <summary>
    /// Drop the pending retry but keep the backoff position: used while the widget is not
    /// rendering, so a hidden card does not keep polling. <see cref="Schedule"/> resumes it at the
    /// same step.
    /// </summary>
    public void Stop()
    {
        timer?.Stop();
        timer = null;
    }

    public void Dispose()
    {
        disposed = true;
        Reset();
    }
}
