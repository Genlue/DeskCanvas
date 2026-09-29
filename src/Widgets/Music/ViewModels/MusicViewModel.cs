using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Music.Models;
using Music.Services;

namespace Music.ViewModels;

public class MusicViewModel : INotifyPropertyChanged, IDisposable
{
    /// <summary>Max width (px) the album art is decoded at. Covers can be 3000×3000 (36 MB decoded),
    /// while the largest the widget ever draws is a thumbnail — so the decode is capped here.</summary>
    private const int CoverWidth = 256;

    private readonly MediaManagerService mediaService;
    private readonly DispatcherTimer timer;
    private MusicModel model;
    private bool isDisposed;

    // High-resolution clock & smooth timeline synchronization
    private TimeSpan basePosition = TimeSpan.Zero;
    private long lastSyncTimestamp;
    private DateTimeOffset lastSeekTime = DateTimeOffset.MinValue;

    private string trackTitle = "未在播放";
    private string artist = "启动音乐软件开始聆听";
    private string albumTitle = string.Empty;
    private string playerName = "音乐";
    private Bitmap? coverBitmap;
    private bool isPlaying;
    private bool hasTrack;
    private TimeSpan position = TimeSpan.Zero;
    private TimeSpan duration = TimeSpan.Zero;
    private double progressPercent;
    private string positionText = "00:00";
    private string durationText = "00:00";
    private bool canPlayPause = true;
    private bool canSkipNext = true;
    private bool canSkipPrevious = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MusicModel Model => model;

    public string TrackTitle
    {
        get => trackTitle;
        set => SetField(ref trackTitle, value);
    }

    public string Artist
    {
        get => artist;
        set => SetField(ref artist, value);
    }

    public string AlbumTitle
    {
        get => albumTitle;
        set => SetField(ref albumTitle, value);
    }

    public string PlayerName
    {
        get => playerName;
        set => SetField(ref playerName, value);
    }

    public Bitmap? CoverBitmap
    {
        get => coverBitmap;
        set
        {
            var previous = coverBitmap;
            if (!SetField(ref coverBitmap, value)) return;

            // The outgoing cover owns an unmanaged pixel block (up to 36 MB for a full-size decode)
            // that the GC has no reason to reclaim, so it is released as soon as it is replaced.
            previous?.Dispose();
        }
    }

    public bool IsPlaying
    {
        get => isPlaying;
        set => SetField(ref isPlaying, value);
    }

    public bool HasTrack
    {
        get => hasTrack;
        set => SetField(ref hasTrack, value);
    }

    public TimeSpan Position
    {
        get => position;
        set
        {
            if (SetField(ref position, value))
            {
                PositionText = FormatTime(value);
                UpdateProgress();
            }
        }
    }

    public TimeSpan Duration
    {
        get => duration;
        set
        {
            if (SetField(ref duration, value))
            {
                DurationText = FormatTime(value);
                UpdateProgress();
            }
        }
    }

    public double ProgressPercent
    {
        get => progressPercent;
        set => SetField(ref progressPercent, value);
    }

    public string PositionText
    {
        get => positionText;
        set => SetField(ref positionText, value);
    }

    public string DurationText
    {
        get => durationText;
        set => SetField(ref durationText, value);
    }

    public bool CanPlayPause
    {
        get => canPlayPause;
        set => SetField(ref canPlayPause, value);
    }

    public bool CanSkipNext
    {
        get => canSkipNext;
        set => SetField(ref canSkipNext, value);
    }

    public bool CanSkipPrevious
    {
        get => canSkipPrevious;
        set => SetField(ref canSkipPrevious, value);
    }

    public bool AmbientGlow => model.AmbientGlow;
    public bool ShowProgressBar => model.ShowProgressBar;

    public MusicViewModel(MusicModel model)
    {
        this.model = model;
        lastSyncTimestamp = Stopwatch.GetTimestamp();
        mediaService = new MediaManagerService();
        mediaService.UpdateRules(model.PlayerRules);

        mediaService.TrackChanged += OnTrackChanged;
        mediaService.TimelineChanged += OnTimelineChanged;

        timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        timer.Tick += OnTimerTick;
        timer.Start();
    }

    public void UpdateModel(MusicModel newModel)
    {
        model = newModel;
        mediaService.UpdateRules(model.PlayerRules);
        OnPropertyChanged(nameof(AmbientGlow));
        OnPropertyChanged(nameof(ShowProgressBar));
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (isDisposed || !IsPlaying || Duration <= TimeSpan.Zero) return;

        var elapsedTicks = Stopwatch.GetTimestamp() - lastSyncTimestamp;
        var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
        var current = basePosition + TimeSpan.FromSeconds(elapsedSeconds);

        if (current > Duration)
            current = Duration;

        // Monotonic check: progress must never jump backward during normal playback
        if (current >= Position)
        {
            Position = current;
        }
    }

    private void OnTrackChanged(MediaTrackInfo track)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // The service can raise this just before Dispose() unsubscribes; the posted work then runs
            // after teardown and must not touch a disposed view model or allocate a new cover bitmap.
            if (isDisposed) return;

            HasTrack = track.HasTrack;
            TrackTitle = track.HasTrack ? track.Title : "未在播放";
            Artist = track.HasTrack ? (string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist) : "启动音乐软件开始聆听";
            AlbumTitle = track.AlbumTitle;
            PlayerName = string.IsNullOrWhiteSpace(track.PlayerName) ? "音乐" : track.PlayerName;
            IsPlaying = track.IsPlaying;
            CanPlayPause = track.CanPlayPause;
            CanSkipNext = track.CanSkipNext;
            CanSkipPrevious = track.CanSkipPrevious;

            Position = track.Position;
            Duration = track.Duration;
            basePosition = track.Position;
            lastSyncTimestamp = Stopwatch.GetTimestamp();
            lastSeekTime = DateTimeOffset.MinValue;

            if (track.ThumbnailData != null && track.ThumbnailData.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(track.ThumbnailData);
                    // Scaled decode: the raw cover is up to 3000×3000, which would cost 36 MB of
                    // unmanaged pixels per track change for an image drawn at a few hundred px.
                    CoverBitmap = Bitmap.DecodeToWidth(ms, CoverWidth, BitmapInterpolationMode.LowQuality);
                }
                catch
                {
                    CoverBitmap = null;
                }
            }
            else
            {
                CoverBitmap = null;
            }
        });
    }

    private void OnTimelineChanged(TimeSpan pos, TimeSpan dur)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (isDisposed) return;

            Duration = dur;

            // 1. Seek cooldown protection: ignore stale SMTC timeline updates
            if (DateTimeOffset.UtcNow - lastSeekTime < TimeSpan.FromSeconds(2.5))
            {
                if (Math.Abs((pos - Position).TotalSeconds) > 2.0)
                {
                    // Stale SMTC timeline snapshot from before the seek -> ignore
                    return;
                }
                lastSeekTime = DateTimeOffset.MinValue;
            }

            if (!IsPlaying)
            {
                // When paused or stopped, directly sync position
                basePosition = pos;
                lastSyncTimestamp = Stopwatch.GetTimestamp();
                Position = pos;
                return;
            }

            // 2. When playing: calculate difference between SMTC position and our monotonic clock
            var elapsedTicks = Stopwatch.GetTimestamp() - lastSyncTimestamp;
            var elapsedSeconds = (double)elapsedTicks / Stopwatch.Frequency;
            var currentEstimated = basePosition + TimeSpan.FromSeconds(elapsedSeconds);

            var diff = (pos - currentEstimated).TotalSeconds;

            if (Math.Abs(diff) >= 1.5)
            {
                // Significant jump (e.g. user seeked or looped in external player) -> Snap immediately
                basePosition = pos;
                lastSyncTimestamp = Stopwatch.GetTimestamp();
                Position = pos;
            }
            else if (Math.Abs(diff) > 0.25)
            {
                // Small drift: gently slew basePosition so it converges smoothly without jitter
                basePosition += TimeSpan.FromSeconds(diff * 0.25);
            }
        });
    }

    private void UpdateProgress()
    {
        if (duration.TotalSeconds > 0)
        {
            ProgressPercent = Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0.0, 1.0);
        }
        else
        {
            ProgressPercent = 0.0;
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero) time = TimeSpan.Zero;
        if (time.TotalHours >= 1)
            return time.ToString(@"h\:mm\:ss");
        return time.ToString(@"m\:ss");
    }

    public void TogglePlayPause()
    {
        _ = mediaService.TogglePlayPauseAsync();
    }

    public void SkipNext()
    {
        _ = mediaService.SkipNextAsync();
    }

    public void SkipPrevious()
    {
        _ = mediaService.SkipPreviousAsync();
    }

    public void Seek(double percent)
    {
        if (duration.TotalSeconds > 0)
        {
            var targetSeconds = duration.TotalSeconds * Math.Clamp(percent, 0.0, 1.0);
            var targetTime = TimeSpan.FromSeconds(targetSeconds);
            Position = targetTime;
            basePosition = targetTime;
            lastSyncTimestamp = Stopwatch.GetTimestamp();
            lastSeekTime = DateTimeOffset.UtcNow;
            _ = mediaService.SeekAsync(targetTime);
        }
    }

    public async Task<List<(string AppId, string Title, string Artist, bool IsPlaying)>> GetActiveSessionsAsync()
    {
        return await mediaService.GetActiveSessionsAsync();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// Tears the view model down: the 250 ms UI timer, the media service (1.5 s SMTC poll timer plus
    /// its manager/session event subscriptions) and the cover bitmap's unmanaged pixels. Idempotent —
    /// the view can unload twice while a cached settings page holds it.
    /// </summary>
    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        timer.Stop();
        timer.Tick -= OnTimerTick;

        mediaService.TrackChanged -= OnTrackChanged;
        mediaService.TimelineChanged -= OnTimelineChanged;
        mediaService.Dispose();

        // Assigning null routes the previous bitmap through the setter, which disposes it.
        CoverBitmap = null;
    }

    /// <summary>True once <see cref="Dispose"/> ran; the view uses this to know it must rebuild and
    /// rebind a fresh view model when it is loaded again.</summary>
    public bool IsDisposed => isDisposed;

    // ------------------------------------------------------------------
    //  Fullscreen suspension (host: IWidgetSuspendable)
    // ------------------------------------------------------------------

    private bool suspended;

    /// <summary>
    /// Called by the host when every attached screen is covered (see <c>IWidgetSuspendable</c>).
    /// Stops the 250 ms progress timer — every tick invalidates the position bindings and forces a
    /// real render pass, even though the widget is behind the covering window — and pauses the
    /// 1.5 s SMTC poll. The push-based media events stay attached: they cost nothing between fires.
    /// Idempotent; a disposed view model ignores it.
    /// </summary>
    public void Suspend()
    {
        if (isDisposed || suspended) return;
        suspended = true;

        timer.Stop();
        mediaService.PausePolling();
    }

    /// <summary>Undoes <see cref="Suspend"/>: restarts the progress timer and the SMTC poll.</summary>
    public void Resume()
    {
        if (isDisposed || !suspended) return;
        suspended = false;

        // The position is a Stopwatch delta anchored at lastSyncTimestamp; re-anchor it so the
        // paused stretch does not surface as a jump on the first tick after resume.
        lastSyncTimestamp = Stopwatch.GetTimestamp();

        mediaService.ResumePolling();
        timer.Start();
    }
}
