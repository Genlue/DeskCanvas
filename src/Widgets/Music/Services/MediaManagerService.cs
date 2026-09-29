using System.Diagnostics;
using System.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Music.Models;

namespace Music.Services;

public class MediaManagerService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? sessionManager;
    private GlobalSystemMediaTransportControlsSession? currentSession;
    private string? currentSessionId;
    private List<PlayerRule> rules = [];
    private bool disposed;

    public event Action<MediaTrackInfo>? TrackChanged;
    public event Action<TimeSpan, TimeSpan>? TimelineChanged;

    public MediaTrackInfo CurrentTrack { get; private set; } = new();

    private Timer? pollTimer;
    private DateTimeOffset lastSeekTime = DateTimeOffset.MinValue;
    private TimeSpan lastSeekTarget = TimeSpan.Zero;

    public MediaManagerService()
    {
        InitializeAsync();
    }

    public void UpdateRules(IEnumerable<PlayerRule> newRules)
    {
        rules = newRules.Where(r => r.IsEnabled).OrderBy(r => r.Priority).ToList();
        _ = RefreshActiveSessionAsync();
    }

    private async void InitializeAsync()
    {
        try
        {
            sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (sessionManager != null)
            {
                // Dispose() can run while this await is in flight (the widget unloads during startup).
                // Subscribing then would leak the session manager and the poll timer, so bail out and
                // release what this task already created.
                if (disposed)
                {
                    sessionManager = null;
                    return;
                }

                sessionManager.SessionsChanged += OnSessionsChanged;
                sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                await RefreshActiveSessionAsync();

                // Same race as above, one await later: a dispose that slipped in while the refresh was
                // running must not leave a live 1.5 s poll timer behind.
                if (disposed) return;

                // Poll every 1.5s as WinRT SMTC events often do not fire for Win32 apps like QQMusic
                pollTimer = new Timer(async _ =>
                {
                    if (disposed) return;
                    try
                    {
                        await RefreshActiveSessionAsync();
                    }
                    catch { }
                }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] Init error: {ex.Message}");
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _ = RefreshActiveSessionAsync();
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _ = RefreshActiveSessionAsync();
    }

    public async Task RefreshActiveSessionAsync()
    {
        if (sessionManager == null || disposed) return;

        try
        {
            var sessions = sessionManager.GetSessions();
            GlobalSystemMediaTransportControlsSession? bestSession = null;
            PlayerRule? matchedRule = null;

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
            if (bestSession == null)
            {
                bestSession = sessions.FirstOrDefault(s =>
                    s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            }

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
            if (bestSession == null)
            {
                bestSession = sessionManager.GetCurrentSession();
            }

            if (bestSession != null && matchedRule == null)
            {
                matchedRule = rules.FirstOrDefault(r => MatchesRule(r, bestSession.SourceAppUserModelId));
            }

            // Bind to the selected session
            await BindToSessionAsync(bestSession, matchedRule);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] Refresh error: {ex.Message}");
        }
    }

    private bool MatchesRule(PlayerRule rule, string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;
        var patterns = rule.MatchPattern.Split('|', StringSplitOptions.RemoveEmptyEntries);
        return patterns.Any(p => appId.Contains(p.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private async Task BindToSessionAsync(GlobalSystemMediaTransportControlsSession? session, PlayerRule? rule)
    {
        if (session == null)
        {
            UnbindCurrentSession();
            CurrentTrack = new MediaTrackInfo();
            TrackChanged?.Invoke(CurrentTrack);
            return;
        }

        var appId = session.SourceAppUserModelId;
        if (currentSession != null && currentSessionId == appId)
        {
            // Same session, refresh its state
            await UpdateTrackPropertiesAsync(session, rule);
            return;
        }

        UnbindCurrentSession();

        currentSession = session;
        currentSessionId = appId;

        currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
        currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
        currentSession.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        await UpdateTrackPropertiesAsync(currentSession, rule);
    }

    private void UnbindCurrentSession()
    {
        if (currentSession != null)
        {
            currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            currentSession.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            currentSession = null;
            currentSessionId = null;
        }
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = UpdateTrackPropertiesAsync(sender, null);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = UpdateTrackPropertiesAsync(sender, null);
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        UpdateTimeline(sender);
    }

    private async Task UpdateTrackPropertiesAsync(GlobalSystemMediaTransportControlsSession session, PlayerRule? rule)
    {
        try
        {
            var playback = session.GetPlaybackInfo();
            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();

            var isPlaying = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var title = props?.Title ?? string.Empty;
            var artist = props?.Artist ?? string.Empty;
            var album = props?.AlbumTitle ?? string.Empty;
            var appId = session.SourceAppUserModelId;
            var accuratePos = GetAccuratePosition(timeline, isPlaying);

            // If in seek cooldown and accuratePos is stale, don't overwrite user seek position
            if (IsStaleSeekPosition(accuratePos))
            {
                accuratePos = CurrentTrack.Position;
            }

            // If track identity and playback status are unchanged, just update timeline
            if (currentSessionId == appId &&
                CurrentTrack.Title == title &&
                CurrentTrack.Artist == artist &&
                CurrentTrack.AlbumTitle == album &&
                CurrentTrack.IsPlaying == isPlaying &&
                (CurrentTrack.ThumbnailData != null || props?.Thumbnail == null))
            {
                CurrentTrack.Position = accuratePos;
                CurrentTrack.Duration = timeline.EndTime;
                TimelineChanged?.Invoke(accuratePos, timeline.EndTime);
                return;
            }

            var track = new MediaTrackInfo
            {
                Title = title,
                Artist = artist,
                AlbumTitle = album,
                SourceAppId = appId,
                PlayerName = rule?.Name ?? ResolveAppFriendlyName(appId),
                IsPlaying = isPlaying,
                Position = accuratePos,
                Duration = timeline.EndTime,
                CanPlayPause = playback?.Controls.IsPlayPauseToggleEnabled ?? true,
                CanSkipNext = playback?.Controls.IsNextEnabled ?? true,
                CanSkipPrevious = playback?.Controls.IsPreviousEnabled ?? true,
            };

            if (props?.Thumbnail != null)
            {
                try
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms);
                    track.ThumbnailData = ms.ToArray();
                }
                catch
                {
                    track.ThumbnailData = null;
                }
            }

            CurrentTrack = track;
            TrackChanged?.Invoke(track);
            TimelineChanged?.Invoke(track.Position, track.Duration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] UpdateTrackProperties error: {ex.Message}");
        }
    }

    private void UpdateTimeline(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var isPlaying = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var timeline = session.GetTimelineProperties();
            var accuratePos = GetAccuratePosition(timeline, isPlaying);

            if (IsStaleSeekPosition(accuratePos))
            {
                return;
            }

            CurrentTrack.Position = accuratePos;
            CurrentTrack.Duration = timeline.EndTime;
            TimelineChanged?.Invoke(accuratePos, timeline.EndTime);
        }
        catch { }
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

    public async Task<List<(string AppId, string Title, string Artist, bool IsPlaying)>> GetActiveSessionsAsync()
    {
        var result = new List<(string, string, string, bool)>();
        if (sessionManager == null) return result;

        try
        {
            foreach (var s in sessionManager.GetSessions())
            {
                var playback = s.GetPlaybackInfo();
                var props = await s.TryGetMediaPropertiesAsync();
                result.Add((
                    s.SourceAppUserModelId,
                    props?.Title ?? "无标题",
                    props?.Artist ?? "未知艺术家",
                    playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                ));
            }
        }
        catch { }

        return result;
    }

    public async Task TogglePlayPauseAsync()
    {
        if (currentSession == null) return;
        try
        {
            var info = currentSession.GetPlaybackInfo();
            if (info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await currentSession.TryPauseAsync();
            }
            else
            {
                await currentSession.TryPlayAsync();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] PlayPause error: {ex.Message}");
        }
    }

    public async Task SkipNextAsync()
    {
        if (currentSession == null) return;
        try
        {
            await currentSession.TrySkipNextAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] SkipNext error: {ex.Message}");
        }
    }

    public async Task SkipPreviousAsync()
    {
        if (currentSession == null) return;
        try
        {
            await currentSession.TrySkipPreviousAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] SkipPrevious error: {ex.Message}");
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (currentSession == null) return;
        try
        {
            lastSeekTime = DateTimeOffset.UtcNow;
            lastSeekTarget = position;
            await currentSession.TryChangePlaybackPositionAsync(position.Ticks);
            CurrentTrack.Position = position;
            TimelineChanged?.Invoke(position, CurrentTrack.Duration);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[MediaManagerService] Seek error: {ex.Message}");
        }
    }

    private string ResolveAppFriendlyName(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return "音乐";
        if (appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)) return "网易云音乐";
        if (appId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)) return "QQ音乐";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("apple", StringComparison.OrdinalIgnoreCase)) return "Apple Music";
        if (appId.Contains("kugou", StringComparison.OrdinalIgnoreCase)) return "酷狗音乐";
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Edge";
        if (appId.Contains("firefox", StringComparison.OrdinalIgnoreCase)) return "Firefox";
        return Path.GetFileNameWithoutExtension(appId);
    }

    /// <summary>
    /// Stops the 1.5 s SMTC poll while the desktop is covered by a fullscreen or maximized
    /// application (host suspension, see <c>IWidgetSuspendable</c>). The push-based WinRT event
    /// subscriptions stay attached: they cost nothing between fires and a track change during
    /// suspension still refreshes the (invisible) widget state.
    /// </summary>
    public void PausePolling()
    {
        if (disposed) return;
        try { pollTimer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { }
    }

    /// <summary>Restart the SMTC poll after <see cref="PausePolling"/>. Idempotent.</summary>
    public void ResumePolling()
    {
        if (disposed) return;
        try { pollTimer?.Change(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(1500)); } catch { }
    }

    /// <summary>
    /// Stops the 1.5 s SMTC poll timer and unsubscribes every manager and session event this service
    /// added, so no callback can keep the service (or its listeners) alive. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        pollTimer?.Dispose();
        pollTimer = null;
        UnbindCurrentSession();
        if (sessionManager != null)
        {
            sessionManager.SessionsChanged -= OnSessionsChanged;
            sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
            sessionManager = null;
        }
    }

    /// <summary>
    /// True once <see cref="Dispose"/> ran. Consumers whose control can be unloaded and re-added to
    /// the visual tree (cached settings pages) check this to build a replacement service instead of
    /// using a torn-down one.
    /// </summary>
    public bool IsDisposed => disposed;
}
