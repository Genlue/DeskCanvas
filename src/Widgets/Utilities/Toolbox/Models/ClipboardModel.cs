namespace Tools.Models;

public record ClipboardModel(
    int MaxHistoryCount = 20,
    bool CaptureText = true,
    bool CaptureImages = true,
    bool CaptureFiles = true,
    bool AutoCopyOnClick = true)
{
    public int MaxHistoryCount { get; init; } = Math.Clamp(MaxHistoryCount, 5, 100);
}
