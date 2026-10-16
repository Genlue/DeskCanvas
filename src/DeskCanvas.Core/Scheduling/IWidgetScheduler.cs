#pragma warning disable CS1591
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCanvas.Core.Scheduling;

public enum SchedulerWorkKind { Ui, Poll, Deadline }
public enum SchedulerVisibility { Visible, Hidden, DeadlineOnly }
public enum SchedulerOverlapPolicy { Skip, QueueLatest, Allow }

public sealed record SchedulerJobOptions
{
    public required string OwnerId { get; init; }
    public required TimeSpan Period { get; init; }
    public SchedulerWorkKind WorkKind { get; init; } = SchedulerWorkKind.Poll;
    public SchedulerVisibility Visibility { get; init; } = SchedulerVisibility.Visible;
    public SchedulerOverlapPolicy Overlap { get; init; } = SchedulerOverlapPolicy.Skip;
    public TimeSpan? Timeout { get; init; }
    public bool StartImmediately { get; init; }
}

public readonly record struct SchedulerJobId(Guid Value)
{
    public static SchedulerJobId New() => new(Guid.NewGuid());
    public override string ToString() => Value.ToString("N");
}

public readonly record struct SchedulerJobSnapshot(
    SchedulerJobId JobId,
    string OwnerId,
    SchedulerWorkKind WorkKind,
    SchedulerVisibility Visibility,
    bool Paused,
    bool Running,
    long Started,
    long Completed,
    long Skipped,
    long Queued,
    long Failed,
    long Cancelled);

public interface IWidgetScheduler : IAsyncDisposable
{
    SchedulerLease Schedule(SchedulerJobOptions options, Func<CancellationToken, ValueTask> callback);
    void Pause(string? ownerId = null);
    void Resume(string? ownerId = null);
    IReadOnlyList<SchedulerJobSnapshot> Snapshot();
}

public sealed class SchedulerLease : IDisposable, IAsyncDisposable
{
    private readonly Func<ValueTask> _dispose;
    private int _disposed;
    internal SchedulerLease(SchedulerJobId jobId, Func<ValueTask> dispose)
    {
        JobId = jobId;
        _dispose = dispose;
    }
    public SchedulerJobId JobId { get; }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        return _dispose();
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
#pragma warning restore CS1591
