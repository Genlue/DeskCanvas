#pragma warning disable CS1591
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCanvas.Core.Scheduling;

/// <summary>
/// Host-independent scheduler for widget work. Each registration owns a cancellable lease;
/// callbacks are isolated so one failing widget cannot stop the scheduler. QueueLatest keeps at
/// most one pending invocation, while Skip drops overlapping ticks.
/// </summary>
public sealed class WidgetScheduler : IWidgetScheduler
{
    private readonly ConcurrentDictionary<SchedulerJobId, Job> _jobs = new();
    private readonly CancellationTokenSource _shutdown = new();
    private int _disposed;

    public SchedulerLease Schedule(SchedulerJobOptions options, Func<CancellationToken, ValueTask> callback)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(callback);
        if (string.IsNullOrWhiteSpace(options.OwnerId)) throw new ArgumentException("OwnerId is required.", nameof(options));
        if (options.Period <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Period must be positive.");
        if (options.Timeout is { } timeout && timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Timeout must be positive.");
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        var job = new Job(this, SchedulerJobId.New(), options, callback);
        if (!_jobs.TryAdd(job.JobId, job)) throw new InvalidOperationException("Unable to register scheduler job.");
        job.Start();
        return new SchedulerLease(job.JobId, job.StopAsync);
    }

    public void Pause(string? ownerId = null)
    {
        foreach (var pair in _jobs)
            if (ownerId is null || string.Equals(pair.Value.Options.OwnerId, ownerId, StringComparison.Ordinal)) pair.Value.SetPaused(true);
    }

    public void Resume(string? ownerId = null)
    {
        foreach (var pair in _jobs)
            if (ownerId is null || string.Equals(pair.Value.Options.OwnerId, ownerId, StringComparison.Ordinal)) pair.Value.SetPaused(false);
    }

    public IReadOnlyList<SchedulerJobSnapshot> Snapshot() => _jobs.Values.Select(j => j.GetSnapshot()).ToArray();

    internal void Remove(Job job) => _jobs.TryRemove(job.JobId, out _);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _shutdown.Cancel();
        var jobs = _jobs.Values.ToArray();
        foreach (var job in jobs) await job.StopAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    internal sealed class Job
    {
        private readonly WidgetScheduler _owner;
        private readonly Func<CancellationToken, ValueTask> _callback;
        private readonly CancellationTokenSource _stop = new();
        private readonly object _gate = new();
        private Task? _loop;
        private bool _paused;
        private bool _running;
        private bool _queued;
        private long _started, _completed, _skipped, _queuedCount, _failed, _cancelled;

        internal Job(WidgetScheduler owner, SchedulerJobId id, SchedulerJobOptions options, Func<CancellationToken, ValueTask> callback)
        { _owner = owner; JobId = id; Options = options; _callback = callback; }
        internal SchedulerJobId JobId { get; }
        internal SchedulerJobOptions Options { get; }
        internal void Start() => _loop = Task.Run(RunAsync);
        internal void SetPaused(bool value) { lock (_gate) _paused = value; }
        internal SchedulerJobSnapshot GetSnapshot()
        {
            lock (_gate) return new(JobId, Options.OwnerId, Options.WorkKind, Options.Visibility, _paused, _running,
                _started, _completed, _skipped, _queuedCount, _failed, _cancelled);
        }
        internal async ValueTask StopAsync()
        {
            _stop.Cancel();
            var loop = _loop;
            if (loop is not null) try { await loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
            _stop.Dispose();
            _owner.Remove(this);
        }

        private async Task RunAsync()
        {
            try
            {
                if (Options.StartImmediately) await TriggerAsync().ConfigureAwait(false);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _owner._shutdown.Token);
                using var timer = new PeriodicTimer(Options.Period);
                while (await timer.WaitForNextTickAsync(linked.Token).ConfigureAwait(false))
                    await TriggerAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested || _owner._shutdown.IsCancellationRequested) { }
        }

        private async ValueTask TriggerAsync()
        {
            lock (_gate)
            {
                // Visibility is a policy label for the host. The host pauses a hidden owner
                // explicitly so deadline jobs can remain active while ordinary polling stops.
                if (_paused) { _skipped++; return; }
                if (_running)
                {
                    if (Options.Overlap == SchedulerOverlapPolicy.Skip) { _skipped++; return; }
                    if (Options.Overlap == SchedulerOverlapPolicy.QueueLatest)
                    { if (!_queued) { _queued = true; _queuedCount++; } else _skipped++; return; }
                }
                _running = true; _started++;
            }
            await ExecuteAsync().ConfigureAwait(false);
            while (true)
            {
                lock (_gate) { if (!_queued) break; _queued = false; _running = true; _started++; }
                await ExecuteAsync().ConfigureAwait(false);
            }
        }

        private async ValueTask ExecuteAsync()
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _owner._shutdown.Token);
            if (Options.Timeout is { } timeout) linked.CancelAfter(timeout);
            try { await _callback(linked.Token).ConfigureAwait(false); lock (_gate) _completed++; }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { lock (_gate) _cancelled++; }
            catch (Exception ex) { lock (_gate) _failed++; Debug.WriteLine($"[WidgetScheduler:{Options.OwnerId}] {ex.Message}"); }
            finally { lock (_gate) _running = false; }
        }
    }
}
#pragma warning restore CS1591
