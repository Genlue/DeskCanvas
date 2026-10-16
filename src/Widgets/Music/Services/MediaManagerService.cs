using System.Diagnostics;
using System.Threading;
using DeskCanvas.Runtime;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Music.Models;

namespace Music.Services;

/// <summary>
/// Media facade for the Music view and settings page. Every instance attaches to one process-wide
/// engine per snapshot source, so the 1.5 s SMTC poll, the WinRT session subscriptions and the
/// event pipeline exist once no matter how many widgets or settings pages are alive. Platform
/// (WinRT SMTC) access stays on the Music widget side: it reaches DeskCanvas.Runtime only as
/// neutral <see cref="SharedMediaSnapshot"/> values through <see cref="MediaSessionSharedProvider"/>
/// injection, carried by the common single-flight/TTL/lease engine.
/// </summary>
public class MediaManagerService : IDisposable
{
    // Process-wide SMTC client. Only the default provider's engine drives it; injected providers
    // (tests, embedded hosts) never touch platform code. Declared before DefaultProvider so the
    // provider's fetch lambda references an already-initialized static field.
    private static readonly Lazy<SmtcGateway> SharedGateway =
        new(() => new SmtcGateway(), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly MediaSessionSharedProvider DefaultProvider = new((_, cancellationToken) =>
        SharedGateway.Value.ReadSnapshotAsync(cancellationToken));

    private readonly MediaEngine engine;
    private bool disposed;

    public MediaManagerService() : this(null) { }

    /// <summary>Injection seam: instances sharing one provider share one engine and one poll round.</summary>
    public MediaManagerService(MediaSessionSharedProvider? sharedProvider)
    {
        engine = MediaEngine.Acquire(sharedProvider ?? DefaultProvider, wantsGateway: sharedProvider is null);

        // Pull the engine's current state so a late-joining consumer (cached settings page) does not
        // wait for the next identity change to learn what is playing.
        var snapshot = engine.CurrentSnapshot;
        if (snapshot is { SourceAppId: not null })
        {
            CurrentTrack = Project(snapshot);
        }

        engine.TrackChanged += OnEngineTrackChanged;
        engine.TimelineChanged += OnEngineTimelineChanged;
    }

    public event Action<MediaTrackInfo>? TrackChanged;
    public event Action<TimeSpan, TimeSpan>? TimelineChanged;

    public MediaTrackInfo CurrentTrack { get; private set; } = new();

    public void UpdateRules(IEnumerable<PlayerRule> newRules) => engine.UpdateRules(newRules);

    public Task RefreshActiveSessionAsync() => engine.RefreshAsync();

    public async Task<List<(string AppId, string Title, string Artist, bool IsPlaying)>> GetActiveSessionsAsync()
    {
        var gateway = engine.Gateway;
        if (gateway == null) return [];
        return await gateway.GetActiveSessionsAsync().ConfigureAwait(false);
    }

    public Task TogglePlayPauseAsync() => engine.TogglePlayPauseAsync();

    public Task SkipNextAsync() => engine.SkipNextAsync();

    public Task SkipPreviousAsync() => engine.SkipPreviousAsync();

    public Task SeekAsync(TimeSpan position) => engine.SeekAsync(position);

    /// <summary>
    /// Stops the 1.5 s SMTC poll while the desktop is covered by a fullscreen or maximized
    /// application (host suspension, see <c>IWidgetSuspendable</c>). The push-based WinRT event
    /// subscriptions stay attached: they cost nothing between fires and a track change during
    /// suspension still refreshes the (invisible) widget state.
    /// </summary>
    public void PausePolling() => engine.PausePolling();

    /// <summary>Restart the SMTC poll after <see cref="PausePolling"/>. Idempotent.</summary>
    public void ResumePolling() => engine.ResumePolling();

    /// <summary>
    /// Releases this consumer's share of the shared engine. The engine — and its SMTC client and
    /// poll timer — keeps serving the remaining consumers; the last one out stops it. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        engine.TrackChanged -= OnEngineTrackChanged;
        engine.TimelineChanged -= OnEngineTimelineChanged;
        MediaEngine.Release(engine);
    }

    /// <summary>
    /// True once this facade's <see cref="Dispose"/> ran. Consumers whose control can be unloaded
    /// and re-added to the visual tree (cached settings pages) check this to build a replacement
    /// service instead of using a torn-down one.
    /// </summary>
    public bool IsDisposed => disposed;

    private void OnEngineTrackChanged(SharedMediaSnapshot snapshot)
    {
        CurrentTrack = snapshot.SourceAppId is null ? new MediaTrackInfo() : Project(snapshot);
        TrackChanged?.Invoke(CurrentTrack);
    }

    private void OnEngineTimelineChanged(TimeSpan position, TimeSpan duration)
    {
        CurrentTrack.Position = position;
        CurrentTrack.Duration = duration;
        TimelineChanged?.Invoke(position, duration);
    }

    private static MediaTrackInfo Project(SharedMediaSnapshot snapshot) => new()
    {
        Title = snapshot.Title,
        Artist = snapshot.Artist,
        AlbumTitle = snapshot.AlbumTitle,
        SourceAppId = snapshot.SourceAppId ?? string.Empty,
        PlayerName = snapshot.PlayerName ?? string.Empty,
        IsPlaying = snapshot.IsPlaying,
        Position = snapshot.Position,
        Duration = snapshot.Duration,
        CanPlayPause = snapshot.CanPlayPause,
        CanSkipNext = snapshot.CanSkipNext,
        CanSkipPrevious = snapshot.CanSkipPrevious,
        ThumbnailData = snapshot.ThumbnailData,
    };

    /// <summary>
    /// Process-shared polling engine, keyed by snapshot provider and reference counted by the
    /// facades. Raises snapshot-level events once per refresh; each facade projects them into its
    /// own MediaTrackInfo so consumers never share mutable models.
    /// </summary>
    private sealed class MediaEngine
    {
        private static readonly Dictionary<MediaSessionSharedProvider, MediaEngine> engines = [];

        private readonly MediaSessionSharedProvider provider;
        private readonly SmtcGateway? gateway;
        private Timer? pollTimer;
        private bool shutdown;
        private int refCount;

        private SharedMediaSnapshot? lastSnapshot;
        private string? currentSessionId;
        private TimeSpan heldPosition;
        private TimeSpan heldDuration;
        private DateTimeOffset lastSeekTime = DateTimeOffset.MinValue;
        private TimeSpan lastSeekTarget = TimeSpan.Zero;

        private MediaEngine(MediaSessionSharedProvider provider, bool wantsGateway)
        {
            this.provider = provider;
            if (wantsGateway)
            {
                gateway = SharedGateway.Value;
                gateway.SessionActivity += OnGatewayActivity;
            }
        }

        public SmtcGateway? Gateway => gateway;

        public SharedMediaSnapshot? CurrentSnapshot => lastSnapshot;

        public event Action<SharedMediaSnapshot>? TrackChanged;
        public event Action<TimeSpan, TimeSpan>? TimelineChanged;

        public static MediaEngine Acquire(MediaSessionSharedProvider provider, bool wantsGateway)
        {
            lock (engines)
            {
                if (!engines.TryGetValue(provider, out var engine))
                {
                    engines[provider] = engine = new MediaEngine(provider, wantsGateway);
                    _ = engine.InitializeAsync();
                }
                engine.refCount++;
                return engine;
            }
        }

        public static void Release(MediaEngine engine)
        {
            lock (engines)
            {
                if (--engine.refCount > 0) return;
                engines.Remove(engine.provider);
                engine.shutdown = true;
                try { engine.pollTimer?.Dispose(); } catch { }
                engine.pollTimer = null;
                if (engine.gateway != null)
                {
                    engine.gateway.SessionActivity -= engine.OnGatewayActivity;
                    engine.gateway.Shutdown();
                }
            }
        }

        public void UpdateRules(IEnumerable<PlayerRule> newRules)
        {
            gateway?.UpdateRules(newRules);
            _ = RefreshAsync();
        }

        public Task TogglePlayPauseAsync() => gateway is null || shutdown ? Task.CompletedTask : gateway.TogglePlayPauseAsync();

        public Task SkipNextAsync() => gateway is null || shutdown ? Task.CompletedTask : gateway.SkipNextAsync();

        public Task SkipPreviousAsync() => gateway is null || shutdown ? Task.CompletedTask : gateway.SkipPreviousAsync();

        public async Task SeekAsync(TimeSpan position)
        {
            if (gateway is null || shutdown) return;
            try
            {
                lock (applyGate)
                {
                    lastSeekTime = DateTimeOffset.UtcNow;
                    lastSeekTarget = position;
                }
                await gateway.ChangePlaybackPositionAsync(position).ConfigureAwait(false);
                lock (applyGate)
                {
                    heldPosition = position;
                    TimelineChanged?.Invoke(position, heldDuration);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaManagerService] Seek error: {ex.Message}");
            }
        }

        public void PausePolling()
        {
            if (shutdown) return;
            try { pollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
        }

        public void ResumePolling()
        {
            if (shutdown) return;
            try { pollTimer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500)); } catch { }
        }

        public async Task RefreshAsync()
        {
            if (shutdown) return;
            try
            {
                using var lease = await provider.AcquireAsync("media-engine").ConfigureAwait(false);
                if (shutdown) return;
                ApplySnapshot(lease.Snapshot.Value ?? SharedMediaSnapshot.Empty);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaManagerService] Refresh error: {ex.Message}");
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                if (gateway != null)
                {
                    await gateway.InitializeAsync().ConfigureAwait(false);
                    // Release() can run while this await is in flight (the last consumer unloads
                    // during startup). Subscribing then would leak the poll timer, so bail out.
                    if (shutdown) return;

                    await RefreshAsync().ConfigureAwait(false);
                    if (shutdown) return;

                    // Poll every 1.5s as WinRT SMTC events often do not fire for Win32 apps like QQMusic
                    pollTimer = new Timer(async _ =>
                    {
                        if (shutdown) return;
                        try
                        {
                            await RefreshAsync().ConfigureAwait(false);
                        }
                        catch { }
                    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500));
                }
                else
                {
                    // Injected source: no platform gateway and no poll timer — the consumer drives
                    // refreshes, which keeps tests and embedded hosts deterministic.
                    await RefreshAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MediaManagerService] Init error: {ex.Message}");
            }
        }

        private void OnGatewayActivity() => _ = RefreshAsync();

        // Concurrent refreshes (poll tick racing a push event, coalesced waiters resuming in
        // parallel) must not each take the identity path and fire TrackChanged repeatedly: the
        // state transition and its event round are serialized as one unit.
        private readonly object applyGate = new();

        private void ApplySnapshot(SharedMediaSnapshot snapshot)
        {
            lock (applyGate)
            {
                if (snapshot.SourceAppId is null)
                {
                    // No session at all: the legacy service reset to a fresh empty track and raised
                    // TrackChanged on every refresh while nothing played — preserve that.
                    currentSessionId = null;
                    lastSnapshot = null;
                    TrackChanged?.Invoke(SharedMediaSnapshot.Empty);
                    return;
                }

                var position = snapshot.Position;
                if (IsStaleSeekPosition(position))
                {
                    // In seek cooldown: don't overwrite the user seek position with a stale value.
                    position = heldPosition;
                }

                // If track identity and playback status are unchanged, just update the timeline.
                var sameSession = currentSessionId == snapshot.SourceAppId;
                var sameIdentity = lastSnapshot != null &&
                    lastSnapshot.Title == snapshot.Title &&
                    lastSnapshot.Artist == snapshot.Artist &&
                    lastSnapshot.AlbumTitle == snapshot.AlbumTitle &&
                    lastSnapshot.IsPlaying == snapshot.IsPlaying &&
                    (lastSnapshot.ThumbnailData != null || snapshot.ThumbnailData == null);

                var applied = position == snapshot.Position ? snapshot : snapshot with { Position = position };
                heldPosition = position;
                heldDuration = snapshot.Duration;

                if (sameSession && sameIdentity)
                {
                    TimelineChanged?.Invoke(position, snapshot.Duration);
                    return;
                }

                currentSessionId = snapshot.SourceAppId;
                lastSnapshot = snapshot;
                TrackChanged?.Invoke(applied);
                TimelineChanged?.Invoke(position, snapshot.Duration);
            }
        }

        private bool IsStaleSeekPosition(TimeSpan pos)
        {
            if (DateTimeOffset.UtcNow - lastSeekTime < TimeSpan.FromSeconds(2.5))
            {
                if (Math.Abs((pos - lastSeekTarget).TotalSeconds) > 2.0)
                {
                    return true;
                }
                lastSeekTime = DateTimeOffset.MinValue;
            }
            return false;
        }
    }
}

/// <summary>
/// WinRT SMTC platform access for the shared engine: session manager acquisition, session
/// enumeration/selection, property reads and transport commands. All Windows API usage stays here
/// on the Music widget side and reaches DeskCanvas.Runtime only as plain snapshot values.
/// </summary>
internal sealed class SmtcGateway
{
    private GlobalSystemMediaTransportControlsSessionManager? sessionManager;
    private GlobalSystemMediaTransportControlsSession? boundSession;
    private string? boundSessionId;
    private List<PlayerRule> rules = [];
    private bool shutdown;

    public event Action? SessionActivity;

    public async Task InitializeAsync()
    {
        shutdown = false;
        sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (sessionManager == null || shutdown)
        {
            if (shutdown) sessionManager = null;
            return;
        }

        sessionManager.SessionsChanged += OnSessionsChanged;
        sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
    }

    public void UpdateRules(IEnumerable<PlayerRule> newRules) =>
        rules = newRules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

    /// <summary>The fetch the default provider delegates to; produces one shared snapshot round.</summary>
    public async Task<SharedMediaSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var manager = sessionManager;
        if (manager == null || shutdown) return SharedMediaSnapshot.Empty;

        var session = SelectSession(manager, out var matchedRule);
        await BindAsync(session).ConfigureAwait(false);
        if (session == null || shutdown) return SharedMediaSnapshot.Empty;

        var playback = session.GetPlaybackInfo();
        var props = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();

        var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var appId = session.SourceAppUserModelId;

        byte[]? thumbnail = null;
        if (props?.Thumbnail != null)
        {
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var netStream = stream.AsStreamForRead();
                using var ms = new MemoryStream();
                await netStream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
                thumbnail = ms.ToArray();
            }
            catch
            {
                thumbnail = null;
            }
        }

        return new SharedMediaSnapshot(
            appId,
            props?.Title ?? string.Empty,
            props?.Artist ?? string.Empty,
            props?.AlbumTitle ?? string.Empty,
            matchedRule?.Name ?? ResolveAppFriendlyName(appId),
            isPlaying,
            GetAccuratePosition(timeline, isPlaying),
            timeline.EndTime,
            playback?.Controls.IsPlayPauseToggleEnabled ?? true,
            playback?.Controls.IsNextEnabled ?? true,
            playback?.Controls.IsPreviousEnabled ?? true,
            thumbnail);
    }

    public async Task<List<(string AppId, string Title, string Artist, bool IsPlaying)>> GetActiveSessionsAsync()
    {
        var result = new List<(string, string, string, bool)>();
        var manager = sessionManager;
        if (manager == null) return result;

        try
        {
            foreach (var s in manager.GetSessions())
            {
                var playback = s.GetPlaybackInfo();
                var props = await s.TryGetMediaPropertiesAsync();
                result.Add((
                    s.SourceAppUserModelId,
                    props?.Title ?? Music.Locales.Locale.Music_Untitled,
                    props?.Artist ?? Music.Locales.Locale.Music_UnknownArtist,
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ));
            }
        }
        catch { }

        return result;
    }

    public async Task TogglePlayPauseAsync()
    {
        var session = boundSession;
        if (session == null || shutdown) return;
        try
        {
            var info = session.GetPlaybackInfo();
            if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await session.TryPauseAsync();
            }
            else
            {
                await session.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] PlayPause error: {ex.Message}");
        }
    }

    public async Task SkipNextAsync()
    {
        var session = boundSession;
        if (session == null || shutdown) return;
        try
        {
            await session.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] SkipNext error: {ex.Message}");
        }
    }

    public async Task SkipPreviousAsync()
    {
        var session = boundSession;
        if (session == null || shutdown) return;
        try
        {
            await session.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] SkipPrevious error: {ex.Message}");
        }
    }

    public async Task ChangePlaybackPositionAsync(TimeSpan position)
    {
        var session = boundSession;
        if (session == null || shutdown) return;
        await session.TryChangePlaybackPositionAsync(position.Ticks);
    }

    /// <summary>Stops every platform subscription; a later engine re-initializes with a fresh RequestAsync.</summary>
    public void Shutdown()
    {
        shutdown = true;
        UnbindCurrentSession();
        if (sessionManager != null)
        {
            sessionManager.SessionsChanged -= OnSessionsChanged;
            sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            sessionManager = null;
        }
    }

    private GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager,
        out PlayerRule? matchedRule)
    {
        var sessions = manager.GetSessions();
        GlobalSystemMediaTransportControlsSession? bestSession = null;
        matchedRule = null;

        // 1. Try to find a playing session matching rules (by rule priority)
        foreach (var rule in rules)
        {
            var match = sessions.FirstOrDefault(s =>
                MatchesRule(rule, s.SourceAppUserModelId) &&
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

            if (match != null)
            {
                bestSession = match;
                matchedRule = rule;
                break;
            }
        }

        // 2. If no rule-matched session is playing, check if ANY session is currently playing
        bestSession ??= sessions.FirstOrDefault(s =>
            s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);

        // 3. If no session is playing, find a paused session matching rules (by priority)
        if (bestSession == null)
        {
            foreach (var rule in rules)
            {
                var match = sessions.FirstOrDefault(s =>
                    MatchesRule(rule, s.SourceAppUserModelId) &&
                    s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);

                if (match != null)
                {
                    bestSession = match;
                    matchedRule = rule;
                    break;
                }
            }
        }

        // 4. Fallback: take default current session from Windows SMTC
        bestSession ??= manager.GetCurrentSession();

        if (bestSession != null && matchedRule == null)
        {
            matchedRule = rules.FirstOrDefault(r => MatchesRule(r, bestSession.SourceAppUserModelId));
        }

        return bestSession;
    }

    private async Task BindAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null)
        {
            UnbindCurrentSession();
            return;
        }

        var appId = session.SourceAppUserModelId;
        if (boundSession != null && boundSessionId == appId)
        {
            // Same session, keep its event subscriptions.
            return;
        }

        UnbindCurrentSession();

        boundSession = session;
        boundSessionId = appId;

        boundSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
        boundSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
        boundSession.TimelinePropertiesChanged += OnSessionTimelineChanged;
    }

    private void UnbindCurrentSession()
    {
        if (boundSession != null)
        {
            boundSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
            boundSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
            boundSession.TimelinePropertiesChanged -= OnSessionTimelineChanged;
            boundSession = null;
            boundSessionId = null;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => RaiseActivity();

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => RaiseActivity();

    private void OnSessionPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => RaiseActivity();

    private void OnSessionMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => RaiseActivity();

    private void OnSessionTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => RaiseActivity();

    private void RaiseActivity() => SessionActivity?.Invoke();

    private bool MatchesRule(PlayerRule rule, string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var patterns = rule.MatchPattern.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return patterns.Any(p => appId.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private string ResolveAppFriendlyName(string appId)
    {
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(appId)) return Music.Locales.Locale.Music_DefaultPlayer;
        if (appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)) return isZh ? "网易云音乐" : "NetEase Cloud Music";
        if (appId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)) return isZh ? "QQ音乐" : "QQ Music";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("apple", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (appId.Contains("kugou", StringComparison.OrdinalIgnoreCase)) return isZh ? "酷狗音乐" : "KuGou Music";
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        return Path.GetFileNameWithoutExtension(appId);
    }

    private static TimeSpan GetAccuratePosition(GlobalSystemMediaTransportControlsSessionTimelineProperties timeline, bool isPlaying)
    {
        var pos = timeline.Position;
        if (isPlaying && timeline.LastUpdatedTime != default && timeline.LastUpdatedTime.Year > 2000)
        {
            var elapsed = DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            if (elapsed > TimeSpan.Zero && elapsed < TimeSpan.FromHours(2))
            {
                pos += elapsed;
            }
        }

        if (timeline.EndTime > TimeSpan.Zero)
        {
            if (pos > timeline.EndTime) pos = timeline.EndTime;
            if (pos < TimeSpan.Zero) pos = TimeSpan.Zero;
        }
        else if (pos < TimeSpan.Zero)
        {
            pos = TimeSpan.Zero;
        }

        return pos;
    }
}
