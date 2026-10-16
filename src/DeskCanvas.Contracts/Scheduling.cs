namespace DeskCanvas.Contracts;

public enum WidgetJobKind { EventDriven, DataPolling, UiUpdate, VisualAnimation, Debounce, BusinessDeadline }
public enum JobOverlapPolicy { SkipIfRunning, ReplaceLatest, Queue }

public sealed record WidgetScheduleRequest(
    WidgetInstanceId Owner,
    WidgetJobKind Kind,
    TimeSpan? Period = null,
    JobOverlapPolicy Overlap = JobOverlapPolicy.SkipIfRunning,
    TimeSpan? Timeout = null,
    bool RunWhenHidden = false);

public interface IWidgetScheduleLease : IDisposable
{
    Guid JobId { get; }
    WidgetScheduleRequest Request { get; }
    bool IsDisposed { get; }
}

public interface IWidgetScheduler
{
    IWidgetScheduleLease Schedule(WidgetScheduleRequest request, Func<CancellationToken, ValueTask> callback);
}
