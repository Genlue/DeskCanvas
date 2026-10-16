namespace DeskCanvas.Contracts;

public enum WidgetLifecycleState
{
    Created,
    Initializing,
    Ready,
    Faulted,
    Disposing,
    Disposed
}

[Flags]
public enum ActivityReason
{
    None = 0,
    DesktopVisible = 1 << 0,
    TrayHidden = 1 << 1,
    ScreenCovered = 1 << 2,
    SessionLocked = 1 << 3,
    DisplayDetached = 1 << 4,
    StackInactive = 1 << 5,
    Preview = 1 << 6,
    ResourcePressure = 1 << 7
}

/// <summary>Immutable activity input. Multiple reasons may be active at once.</summary>
public sealed record ActivitySnapshot(
    ActivityReason Reasons = ActivityReason.DesktopVisible,
    bool IsPreview = false)
{
    public bool IsVisible => Reasons.HasFlag(ActivityReason.DesktopVisible) &&
                             !Reasons.HasFlag(ActivityReason.TrayHidden) &&
                             !Reasons.HasFlag(ActivityReason.ScreenCovered) &&
                             !Reasons.HasFlag(ActivityReason.DisplayDetached);
    public bool RenderActive => IsVisible || IsPreview;
    public bool PollingActive => IsVisible && !IsPreview;
    public bool DeadlineAllowed => !Reasons.HasFlag(ActivityReason.SessionLocked) && !Reasons.HasFlag(ActivityReason.DisplayDetached);
    public static ActivitySnapshot Preview => new(ActivityReason.Preview, true);
}

public interface IWidgetSession : IAsyncDisposable
{
    WidgetInstanceId InstanceId { get; }
    WidgetLifecycleState State { get; }
    long Generation { get; }
    ActivitySnapshot Activity { get; }
    Exception? Fault { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
    ValueTask ApplySettingsAsync(object? settings, CancellationToken cancellationToken = default);
    ValueTask SetActivityAsync(ActivitySnapshot activity, CancellationToken cancellationToken = default);
}
