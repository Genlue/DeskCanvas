namespace DeskCanvas.Contracts;

public interface IWidgetResourceLease : IDisposable
{
    string ResourceKey { get; }
    long Bytes { get; }
    bool IsDisposed { get; }
}

public sealed record DataSnapshot<T>(
    T? Value,
    long Version,
    DateTimeOffset FetchedAt,
    TimeSpan Freshness,
    string Source,
    string? ErrorCode = null)
{
    public bool HasValue => Value is not null;
    public bool IsStale(DateTimeOffset now) => now - FetchedAt > Freshness;
}

public interface IDataSubscription : IDisposable
{
    WidgetInstanceId Owner { get; }
    bool IsDisposed { get; }
}
