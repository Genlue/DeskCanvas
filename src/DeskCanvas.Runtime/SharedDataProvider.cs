using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

public sealed record SharedDataStatistics(long Requests, long CacheHits, long Coalesced, long Failures, int ActiveOwners);

/// <summary>Single-flight snapshots with independent caller leases. Cancellation only releases that caller.</summary>
public sealed class SharedDataProvider<TKey, TValue> where TKey : notnull
{
    private sealed class Entry
    {
        public Task<DataSnapshot<TValue>> Task = null!;
        public int Owners;
    }

    private readonly object gate = new();
    private readonly Dictionary<TKey, List<Entry>> entries = new();
    private readonly Func<TKey, CancellationToken, Task<TValue>> fetch;
    private readonly TimeSpan ttl;
    private readonly string source;
    private long requests, hits, coalesced, failures, version;

    public SharedDataProvider(Func<TKey, CancellationToken, Task<TValue>> fetch, TimeSpan ttl, string source)
    {
        this.fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        if (ttl < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(ttl));
        this.ttl = ttl;
        this.source = source;
    }

    public SharedDataStatistics Statistics
    {
        get { lock (gate) return new(requests, hits, coalesced, failures, entries.Values.SelectMany(x => x).Sum(x => x.Owners)); }
    }

    public async Task<Lease> AcquireAsync(string owner, TKey key, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(owner)) throw new ArgumentException("Owner is required.", nameof(owner));
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            requests++;
            // Sweep all keys so changing locations does not retain every historical response.
            foreach (var pair in entries.ToArray())
            {
                pair.Value.RemoveAll(x => x.Owners == 0 && IsExpired(x));
                if (pair.Value.Count == 0) entries.Remove(pair.Key);
            }
            if (!entries.TryGetValue(key, out var list)) entries[key] = list = [];
            entry = list.FirstOrDefault(x => !IsExpired(x))!;
            if (entry is null)
            {
                entry = new Entry();
                entry.Task = FetchAsync(key);
                list.Add(entry);
            }
            else if (entry.Task.IsCompleted) hits++;
            else coalesced++;
            entry.Owners++;
        }
        try
        {
            var snapshot = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new Lease(owner, snapshot, () => Release(key, entry));
        }
        catch { Release(key, entry); throw; }
    }

    private bool IsExpired(Entry entry) => entry.Task.IsCompleted &&
        (!entry.Task.IsCompletedSuccessfully || entry.Task.Result.FetchedAt + ttl <= DateTimeOffset.UtcNow);

    private async Task<DataSnapshot<TValue>> FetchAsync(TKey key)
    {
        try
        {
            var value = await fetch(key, CancellationToken.None).ConfigureAwait(false);
            return new(value, Interlocked.Increment(ref version), DateTimeOffset.UtcNow, ttl, source);
        }
        catch { Interlocked.Increment(ref failures); throw; }
    }

    private void Release(TKey key, Entry entry)
    {
        lock (gate)
        {
            entry.Owners--;
            if (entry.Owners == 0 && IsExpired(entry) && entries.TryGetValue(key, out var list))
            {
                list.Remove(entry);
                if (list.Count == 0) entries.Remove(key);
            }
        }
    }

    public sealed class Lease(string owner, DataSnapshot<TValue> snapshot, Action release) : IDisposable
    {
        private Action? release = release;
        public string Owner { get; } = owner;
        public DataSnapshot<TValue> Snapshot { get; } = snapshot;
        public bool IsDisposed => Volatile.Read(ref release) is null;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}
