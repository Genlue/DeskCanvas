namespace DeskCanvas.Runtime;

/// <summary>
/// Platform-neutral media transport snapshot. The Music widget supplies SMTC (Windows API)
/// collection behind the injected fetch delegate; DeskCanvas.Runtime only carries this value.
/// Position is already extrapolated by the collector; identity fields (source app, title, artist,
/// album, playing state, thumbnail presence) drive change detection in consumers.
/// </summary>
public sealed record SharedMediaSnapshot(
    string? SourceAppId,
    string Title,
    string Artist,
    string AlbumTitle,
    string? PlayerName,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan Duration,
    bool CanPlayPause,
    bool CanSkipNext,
    bool CanSkipPrevious,
    byte[]? ThumbnailData)
{
    public static SharedMediaSnapshot Empty { get; } =
        new(null, string.Empty, string.Empty, string.Empty, null, false, TimeSpan.Zero, TimeSpan.Zero, true, true, true, null);

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>Single logical key: the process-wide current media session.</summary>
public sealed record MediaSnapshotKey
{
    public static readonly MediaSnapshotKey Current = new();
    private MediaSnapshotKey() { }
}

/// <summary>
/// Shared SMTC snapshot carriage with single-flight, owner leases and statistics. The TTL only
/// coalesces burst reads (a push event racing a poll tick); at the default 250 ms it stays far
/// below the 1.5 s poll cadence so scheduled polls still perform a fresh SMTC read.
/// </summary>
public sealed class MediaSessionSharedProvider
{
    private readonly SharedDataProvider<MediaSnapshotKey, SharedMediaSnapshot> data;

    public MediaSessionSharedProvider(Func<MediaSnapshotKey, CancellationToken, Task<SharedMediaSnapshot>> fetch, TimeSpan? ttl = null) =>
        data = new(fetch, ttl ?? TimeSpan.FromMilliseconds(250), "media-smtc");

    public SharedDataStatistics Statistics => data.Statistics;

    public Task<SharedDataProvider<MediaSnapshotKey, SharedMediaSnapshot>.Lease> AcquireAsync(string owner, CancellationToken cancellationToken = default) =>
        data.AcquireAsync(owner, MediaSnapshotKey.Current, cancellationToken);
}
