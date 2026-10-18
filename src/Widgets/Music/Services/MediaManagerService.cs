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
    // This facade currently holds one engine poll pause (IWidgetSuspendable). Tracked per facade
    // so a resume from one consumer cannot un-pause a cover another consumer is still paused for,
    // and so disposing while paused gives the pause back.
    private bool pollingPaused;

    public MediaManagerService() : this(null) { }

    /// <summary>Injection seam: instances sharing one provider share one engine and one poll round.</summary>
    public MediaManagerService(MediaSessionSharedProvider? sharedProvider)
    {
        engine = MediaEngine.Acquire(sharedProvider ?? DefaultProvider, wantsGateway: sharedProvider is null);

        engine.TrackChanged += OnEngineTrackChanged;
        engine.TimelineChanged += OnEngineTimelineChanged;

        // Pull the engine's current state so a late-joining consumer (cached settings page) does not
        // wait for the next identity change to learn what is playing. The engine dedupes identity
        // changes process-wide, so an event that fired before this facade attached (the engine's
        // first round can complete synchronously inside Acquire) is never repeated — consumers that
        // render state call PublishCurrentTrack once they are subscribed.
        var snapshot = engine.CurrentSnapshot;
        if (snapshot is { SourceAppId: not null })
        {
            CurrentTrack = Project(snapshot);
        }
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
    /// <para>
    /// The pause is counted per consumer on the shared engine, so a resume from another widget
    /// cannot restart the poll while this one is still covered. Idempotent per facade.
    /// </para>
    /// </summary>
    public void PausePolling()
    {
        if (disposed || pollingPaused) return;
        pollingPaused = true;
        engine.PausePolling();
    }

    /// <summary>Restart the SMTC poll after <see cref="PausePolling"/>. Idempotent per facade.</summary>
    public void ResumePolling()
    {
        if (disposed || !pollingPaused) return;
        pollingPaused = false;
        engine.ResumePolling();
    }

    /// <summary>
    /// Replays the engine's current snapshot to this facade's subscribers. The shared engine
    /// publishes a snapshot only when the track identity changes, so a consumer that attaches
    /// after that round (a rebuilt widget view, a newly opened settings page) would otherwise stay
    /// blank until the next track change. Consumers call this right after subscribing.
    /// </summary>
    public void PublishCurrentTrack()
    {
        if (disposed) return;
        var snapshot = engine.CurrentSnapshot;
        if (snapshot is { SourceAppId: not null })
        {
            OnEngineTrackChanged(snapshot);
        }
    }

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

        // Giving the pause back matters: a widget unloaded while the desktop is covered must not
        // leave the shared poll stopped for the consumers that stay alive.
        if (pollingPaused)
        {
            pollingPaused = false;
            engine.ResumePolling();
        }

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
        if (disposed) return;
        CurrentTrack = snapshot.SourceAppId is null ? new MediaTrackInfo() : Project(snapshot);
        TrackChanged?.Invoke(CurrentTrack);
    }

    private void OnEngineTimelineChanged(TimeSpan position, TimeSpan duration)
    {
        if (disposed) return;
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
        private volatile bool shutdown;
        private int refCount;
        // Number of consumers currently suspended (IWidgetSuspendable). The poll stops while it is
        // above zero; it is per consumer, not a toggle, so overlapping suspend/resume pairs from
        // different widgets reconcile instead of fighting.
        private int pauseCount;

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
                // The previous engine (the last consumer) shut the shared gateway down; a new
                // engine owns it again and re-arms the manager handshake.
                gateway.Revive();
            }
        }

        public SmtcGateway? Gateway => gateway;

        /// <summary>The latest applied snapshot; read under the apply gate so a concurrently
        /// completing round cannot be observed half-applied.</summary>
        public SharedMediaSnapshot? CurrentSnapshot
        {
            get { lock (applyGate) return lastSnapshot; }
        }

        public event Action<SharedMediaSnapshot>? TrackChanged;
        public event Action<TimeSpan, TimeSpan>? TimelineChanged;

        public static MediaEngine Acquire(MediaSessionSharedProvider provider, bool wantsGateway)
        {
            MediaEngine engine;
            bool created;
            lock (engines)
            {
                if (!engines.TryGetValue(provider, out engine!))
                {
                    engines[provider] = engine = new MediaEngine(provider, wantsGateway);
                    created = true;
                }
                else
                {
                    created = false;
                }
                engine.refCount++;
            }

            // Start the async init outside the dictionary lock: its first round can complete
            // synchronously (a cached provider/manager), and running consumer code that raises
            // events while the engine registry is locked would serialize every other consumer.
            if (created) _ = engine.InitializeAsync();
            return engine;
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
            Interlocked.Increment(ref pauseCount);
            ApplyPollCadence();
        }

        public void ResumePolling()
        {
            if (shutdown) return;
            if (Interlocked.Decrement(ref pauseCount) < 0)
            {
                // A resume without a matching pause must not underflow into a permanently
                // "resumed" negative count that a later single pause cannot silence.
                Interlocked.Exchange(ref pauseCount, 0);
            }
            ApplyPollCadence();
        }

        /// <summary>Reconciles the timer with the pause count. Called on creation and on every
        /// suspend/resume edge, so the order of "timer created" vs "consumer suspended" cannot
        /// strand the poll in either state.</summary>
        private void ApplyPollCadence()
        {
            var timer = pollTimer;
            if (timer == null) return;
            try
            {
                if (Volatile.Read(ref pauseCount) > 0)
                {
                    timer.Change(Timeout.Infinite, Timeout.Infinite);
                }
                else
                {
                    timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500));
                }
            }
            catch (ObjectDisposedException) { }
        }

        public async Task RefreshAsync()
        {
            if (shutdown) return;
            try
            {
                // Retry the manager handshake on every round: a transient RequestAsync failure at
                // engine startup used to leave the poll silently reading Empty forever (the engine
                // stayed alive, so a rebuild was the only way out). EnsureInitializedAsync is a
                // no-op once bound.
                if (gateway != null && !await gateway.EnsureInitializedAsync().ConfigureAwait(false))
                {
                    // No manager yet: keep the last known state instead of resetting the widget to
                    // "not playing"; the next scheduled round retries.
                    return;
                }

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
                await RefreshAsync().ConfigureAwait(false);
                if (shutdown) return;

                if (gateway == null)
                {
                    // Injected source: no platform gateway and no poll timer — the consumer drives
                    // refreshes, which keeps tests and embedded hosts deterministic.
                    return;
                }

                // Poll every 1.5s as WinRT SMTC events often do not fire for Win32 apps like QQMusic.
                // The timer starts armed only through ApplyPollCadence so a suspend that arrived
                // while this init was in flight is honoured instead of being overwritten.
                var timer = new Timer(async _ =>
                {
                    if (shutdown) return;
                    try
                    {
                        await RefreshAsync().ConfigureAwait(false);
                    }
                    catch { }
                }, null, Timeout.Infinite, Timeout.Infinite);

                lock (engines)
                {
                    // Release() can run while the refresh above was in flight; publishing the
                    // timer then would leave a live poll behind a released engine.
                    if (shutdown)
                    {
                        timer.Dispose();
                        return;
                    }
                    pollTimer = timer;
                }

                ApplyPollCadence();
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
                    SameCover(lastSnapshot.ThumbnailData, snapshot.ThumbnailData);

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

        /// <summary>
        /// Whether the artwork counts as unchanged for identity purposes. A missing thumbnail is
        /// treated as "unknown", not as "no cover": players transiently fail to hand it out (or
        /// publish the track before the art), and clearing the widget's cover on every such round
        /// would flicker. An arriving cover — or a different image on the same track (placeholder
        /// replaced by the real artwork) — is an identity change and reaches the UI.
        /// </summary>
        private static bool SameCover(byte[]? current, byte[]? next)
        {
            if (current is null) return next is null;
            if (next is null) return true;
            return current.Length == next.Length && current.AsSpan().SequenceEqual(next);
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
    // Guards the manager/session binding. ReadSnapshotAsync rounds can overlap (the 1.5 s poll
    // racing a WinRT push) and Release can shut the gateway down in the middle of one; every
    // (un)subscription and every field swap is serialized here so a rebind can neither subscribe
    // a session twice nor drop a subscription it still needs.
    private readonly object gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? sessionManager;
    private GlobalSystemMediaTransportControlsSession? boundSession;
    private string? boundSessionId;
    private List<PlayerRule> rules = [];
    private volatile bool shutdown;
    // Incremented by Shutdown only. An EnsureInitializedAsync continuation that started before a
    // Release compares its epoch and becomes a no-op instead of nulling out / double-subscribing
    // the manager a live engine now uses — the previous code could leave the whole chain silently
    // fetching SharedMediaSnapshot.Empty until the widget was rebuilt.
    private int generation;

    public event Action? SessionActivity;

    /// <summary>
    /// Re-arms a gateway that the previous engine shut down when it was the last consumer. Only a
    /// newly attached engine may revive it: a refresh round that was already in flight during
    /// Release must never resurrect the manager with no engine left to own it.
    /// </summary>
    public void Revive()
    {
        lock (gate)
        {
            if (sessionManager == null) shutdown = false;
        }
    }

    /// <summary>
    /// Makes sure a session manager is bound, retrying on the next caller when a previous attempt
    /// failed (SMTC RequestAsync can fail transiently while a player starts or the system is busy).
    /// Returns true when a manager is available. Cheap and idempotent once bound.
    /// </summary>
    public async Task<bool> EnsureInitializedAsync()
    {
        int started;
        lock (gate)
        {
            if (shutdown) return false;
            if (sessionManager != null) return true;
            started = generation;
        }

        GlobalSystemMediaTransportControlsSessionManager? manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] SMTC RequestAsync error: {ex.Message}");
            return false;
        }

        lock (gate)
        {
            // A Shutdown ran while RequestAsync was in flight: never touch the shared manager from
            // a stale continuation (a newer engine may already own it).
            if (started != generation || shutdown) return sessionManager != null;

            if (manager == null)
            {
                DetachManagerLocked();
                return false;
            }

            if (ReferenceEquals(sessionManager, manager))
            {
                // A newer round already installed and subscribed this manager instance.
                return true;
            }

            DetachManagerLocked();
            sessionManager = manager;
            manager.SessionsChanged += OnSessionsChanged;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            return true;
        }
    }

    public void UpdateRules(IEnumerable<PlayerRule> newRules) =>
        rules = newRules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();

    /// <summary>The fetch the default provider delegates to; produces one shared snapshot round.</summary>
    public async Task<SharedMediaSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        var manager = CurrentManager();
        if (manager == null) return SharedMediaSnapshot.Empty;

        var session = SelectSession(manager, out var matchedRule);
        Bind(session);
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
        var manager = CurrentManager();
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
        var session = CurrentSession();
        if (session == null) return;
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
        var session = CurrentSession();
        if (session == null) return;
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
        var session = CurrentSession();
        if (session == null) return;
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
        var session = CurrentSession();
        if (session == null) return;
        await session.TryChangePlaybackPositionAsync(position.Ticks);
    }

    /// <summary>Stops every platform subscription; a later engine re-initializes with a fresh RequestAsync.</summary>
    public void Shutdown()
    {
        lock (gate)
        {
            // Bumping the generation before clearing makes any in-flight EnsureInitializedAsync
            // continuation a no-op, so it cannot resurrect (or null out) the manager afterwards.
            generation++;
            shutdown = true;
            UnbindCurrentSessionLocked();
            DetachManagerLocked();
        }
    }

    private GlobalSystemMediaTransportControlsSessionManager? CurrentManager()
    {
        lock (gate)
        {
            return shutdown ? null : sessionManager;
        }
    }

    private GlobalSystemMediaTransportControlsSession? CurrentSession()
    {
        lock (gate)
        {
            return shutdown ? null : boundSession;
        }
    }

    /// <summary>Unhooks the manager event subscriptions. Caller holds <see cref="gate"/>.</summary>
    private void DetachManagerLocked()
    {
        if (sessionManager == null) return;
        sessionManager.SessionsChanged -= OnSessionsChanged;
        sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
        sessionManager = null;
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

    /// <summary>Binds the selected session and its three change streams. Serialized with Shutdown
    /// and with overlapping refresh rounds so the handlers are subscribed exactly once.</summary>
    private void Bind(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (gate)
        {
            if (shutdown) return;

            if (session == null)
            {
                UnbindCurrentSessionLocked();
                return;
            }

            var appId = session.SourceAppUserModelId;
            if (boundSession != null && boundSessionId == appId)
            {
                // Same session, keep its event subscriptions.
                return;
            }

            UnbindCurrentSessionLocked();

            boundSession = session;
            boundSessionId = appId;

            boundSession.PlaybackInfoChanged += OnSessionPlaybackInfoChanged;
            boundSession.MediaPropertiesChanged += OnSessionMediaPropertiesChanged;
            boundSession.TimelinePropertiesChanged += OnSessionTimelineChanged;
        }
    }

    /// <summary>Drops the bound session's subscriptions. Caller holds <see cref="gate"/>.</summary>
    private void UnbindCurrentSessionLocked()
    {
        if (boundSession == null) return;
        try
        {
            boundSession.PlaybackInfoChanged -= OnSessionPlaybackInfoChanged;
            boundSession.MediaPropertiesChanged -= OnSessionMediaPropertiesChanged;
            boundSession.TimelinePropertiesChanged -= OnSessionTimelineChanged;
        }
        catch
        {
            // A session the player already tore down can reject unsubscription; the reference
            // must still be dropped so a later session does not inherit stale handlers.
        }
        boundSession = null;
        boundSessionId = null;
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
