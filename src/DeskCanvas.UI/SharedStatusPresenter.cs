namespace DeskCanvas.UI;

/// <summary>Small headless-friendly status presentation primitive shared by widgets.</summary>
public enum SharedStatusKind { Loading, Ready, Empty, Stale, Error }

public sealed record SharedStatus(SharedStatusKind Kind, string Message, bool CanRetry = false);

public sealed class SharedStatusPresenter
{
    public SharedStatus Current { get; private set; } = new(SharedStatusKind.Empty, string.Empty);
    public event EventHandler<SharedStatus>? Changed;

    public void SetLoading(string message = "Loading") => Set(new(SharedStatusKind.Loading, message));
    public void SetReady(string message = "Ready") => Set(new(SharedStatusKind.Ready, message));
    public void SetEmpty(string message = "Empty") => Set(new(SharedStatusKind.Empty, message));
    public void SetStale(string message = "Stale") => Set(new(SharedStatusKind.Stale, message, true));
    public void SetError(string message, bool canRetry = true) => Set(new(SharedStatusKind.Error, message, canRetry));

    private void Set(SharedStatus next)
    {
        if (next == Current) return;
        Current = next;
        Changed?.Invoke(this, next);
    }
}

/// <summary>Clock's adapter preserves its domain vocabulary while sharing the producer.</summary>
public sealed class ClockStatusPresenter
{
    public SharedStatusPresenter Shared { get; } = new();
    public void ShowTime(string text) => Shared.SetReady(text);
    public void ShowUnavailable() => Shared.SetError("Clock unavailable");
}

/// <summary>Countdown's adapter preserves its domain vocabulary while sharing the producer.</summary>
public sealed class CountdownStatusPresenter
{
    public SharedStatusPresenter Shared { get; } = new();
    public void ShowCountdown(string text) => Shared.SetReady(text);
    public void ShowNoEvents() => Shared.SetEmpty("No events");
}
