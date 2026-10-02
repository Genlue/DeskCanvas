namespace DeskCanvas.Services;

/// <summary>
/// The降级 levels of the sidebar glass source, in priority order. The sidebar starts at the top and
/// drops one level at a time whenever the level above cannot produce a frame (DRM-protected or
/// exclusive-fullscreen content, a capture failure, an unsupported system) — it never goes blank.
/// </summary>
public enum ScreenCaptureFallback
{
    /// <summary>A composite of the target monitor with the sidebar's own region replaced by the previous frame (no recursion).</summary>
    LiveScreenFrame = 0,

    /// <summary>The last frame that was captured successfully (the scene is frozen instead of black).</summary>
    LastValidFrame = 1,

    /// <summary>The existing live wallpaper frame (<see cref="LiquidGlassWallpaper"/>).</summary>
    WallpaperLive = 2,

    /// <summary>The existing static wallpaper image.</summary>
    WallpaperStatic = 3,

    /// <summary>A flat glass colour (the material's own fallback).</summary>
    SolidColor = 4,
}

/// <summary>
/// Helpers describing the降级 chain. Kept separate from the capture loop so the ordering rule can be
/// asserted without a live desktop.
/// </summary>
public static class ScreenCaptureFallbackChain
{
    /// <summary>The next level after <paramref name="level"/> (clamped at <see cref="ScreenCaptureFallback.SolidColor"/>).</summary>
    public static ScreenCaptureFallback Next(ScreenCaptureFallback level) =>
        level >= ScreenCaptureFallback.SolidColor ? ScreenCaptureFallback.SolidColor : level + 1;

    /// <summary>Whether <paramref name="level"/> still needs the live screen capturer.</summary>
    public static bool UsesLiveCapture(ScreenCaptureFallback level) =>
        level <= ScreenCaptureFallback.LastValidFrame;
}
