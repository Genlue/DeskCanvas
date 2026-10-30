namespace Music.Models;

public class MediaTrackInfo
{
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string AlbumTitle { get; set; } = string.Empty;
    public string PlayerName { get; set; } = string.Empty;
    public string SourceAppId { get; set; } = string.Empty;
    public bool IsPlaying { get; set; }
    public TimeSpan Position { get; set; }
    public TimeSpan Duration { get; set; }
    public byte[]? ThumbnailData { get; set; }
    public bool CanPlayPause { get; set; } = true;
    public bool CanSkipNext { get; set; } = true;
    public bool CanSkipPrevious { get; set; } = true;

    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);
}
