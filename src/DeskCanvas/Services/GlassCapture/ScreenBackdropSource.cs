using System;
using SkiaSharp;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// The launcher's backdrop: samples <b>the composited screen</b> behind a floating panel.
/// <para>
/// This is the "对象为屏幕" core. It is deliberately a different object from
/// <see cref="WallpaperBackdropSource"/>: a Spotlight-style overlay floats over real window content,
/// so refracting the wallpaper would show a desktop that is not there.
/// </para>
/// <para>
/// The sampling rules are the ones LiquidGlassCanvas' monitor capture established:
/// </para>
/// <list type="bullet">
///   <item><description><b>One session per monitor</b>, switched when the region crosses a screen
///   edge. The region is clipped to that monitor and expressed in its local pixels; the texture
///   origin is the clipped box's desktop position, so the shader maps card pixels correctly even
///   when the panel straddles two monitors.</description></item>
///   <item><description><b>Region, not screen.</b> Only the panel's neighbourhood is copied and read
///   back — the desktop is 14.7 MB at 2560×1440, a panel's neighbourhood is ~1–2 MB.</description></item>
///   <item><description><b>Event-driven with cadence.</b> No frame event → no copy. The session is
///   throttled so the system produces frames at the sampling rate rather than at ~60 Hz.</description></item>
///   <item><description><b>Drain to newest.</b> A burst of queued frames coalesces into one copy.</description></item>
///   <item><description><b>Re-crop on region change.</b> A panel that moved is re-cropped from the
///   held frame instead of waiting for the next one — otherwise a still desktop would show the old
///   neighbourhood forever.</description></item>
/// </list>
/// <para>
/// When WGC is unavailable the same region is grabbed with a GDI <c>BitBlt</c>: still region-limited,
/// just not event-driven. Feedback is prevented in both cases by the window-affinity pass
/// (<c>WDA_EXCLUDEFROMCAPTURE</c>), which hides only the sampling overlay and whatever of ours is
/// drawn above it.
/// </para>
/// </summary>
internal static class ScreenBackdropSource
{
    internal readonly record struct Frame(SKBitmap? Bitmap, int OriginX, int OriginY, bool Live)
    {
        /// <summary>
        /// The chain is live but nothing changed since the last acquisition — no new frame and no
        /// region move. The caller then skips the publish (and the copy) entirely.
        /// </summary>
        public bool IsCurrent { get; init; }

        /// <summary>
        /// <see cref="Bitmap"/> came out of <see cref="PatchBitmapPool"/> and must be given back to
        /// it instead of being disposed — the whole reason a live sample allocates nothing per
        /// frame. False for the GDI fallback, whose bitmap is allocated (and disposed) by the
        /// caller's lease as before.
        /// </summary>
        public bool Pooled { get; init; }
    }

    /// <summary>Frame cadence handed to the capture session (milliseconds).</summary>
    private const int DefaultCadenceMs = 100;

    private static readonly object Gate = new();

    private static GlassCaptureService? capture;
    private static bool everPulled;
    private static bool regionChanged;
    private static IntPtr currentMonitor;
    private static int originX;
    private static int originY;
    private static int cadenceMs = DefaultCadenceMs;
    private static long sessionCooldownUntil;

    /// <summary>Set the sampling cadence (milliseconds between produced frames).</summary>
    public static void SetCadence(int milliseconds)
    {
        lock (Gate)
        {
            cadenceMs = Math.Max(0, milliseconds);
            capture?.SetCadence(cadenceMs);
        }
    }

    /// <summary>True when the screen chain is served by Windows.Graphics.Capture (diagnostics).</summary>
    internal static bool UsingCapture => GlassCaptureService.IsAvailable;

    /// <summary>
    /// Sample <paramref name="interestScreen"/> (virtual-desktop physical pixels).
    /// <paramref name="excludeWindow"/> is the overlay that will draw this frame; it and the windows
    /// above it are kept out of the capture so the glass cannot feed back a copy of itself.
    /// </summary>
    /// <param name="freshOnly">
    /// Set by the live path: pixels are only handed over when they changed, so a still screen costs
    /// one <c>TryGetNextFrame</c> instead of a region copy per tick.
    /// </param>
    public static Frame Acquire(CaptureRect interestScreen, IntPtr excludeWindow, bool freshOnly = false)
    {
        if (interestScreen.IsEmpty) return default;

        lock (Gate)
        {
            // The overlay has to be out of the frame *before* the frame is produced. WGC produces
            // frames on its own schedule, so this cannot be done in the GDI fallback only — that
            // was the whole bug: with WGC available the fallback never ran, the affinity was only
            // ever set by the one first-frame fallback (taken before the overlay was even shown),
            // and the card spent the session refracting a copy of itself: every frame darker,
            // thicker and more saturated than the desktop it was supposed to show.
            DesktopCapturer.EnsureCaptureVisibilityExclusion(excludeWindow);

            if (GlassCaptureService.IsAvailable && AcquireWithCapture(interestScreen, freshOnly, out var isCurrent))
            {
                if (isCurrent) return new Frame(null, originX, originY, Live: true) { IsCurrent = true };

                // TakeLatest, not CopyLatest: the session hands its crop over and rents a fresh
                // buffer for the next pull, so a published frame costs exactly one patch — not two
                // (the old copy existed only because the session kept reusing its own buffer).
                var width = 0;
                var height = 0;
                var bitmap = everPulled ? capture!.TakeLatest(out width, out height) : null;
                if (bitmap != null)
                {
                    if (width > 0 && height > 0)
                        return new Frame(bitmap, originX, originY, Live: true) { Pooled = true };

                    // No usable geometry: hand the buffer straight back rather than dropping it.
                    PatchBitmapPool.Return(bitmap);
                }
            }

            // Fallback: the same region, grabbed with GDI. Region-limited either way.
            var region = DesktopCapturer.CaptureScreenRegion(excludeWindow, interestScreen);
            return region == null
                ? default
                : new Frame(region, interestScreen.Left, interestScreen.Top, Live: true);
        }
    }

    private static bool AcquireWithCapture(CaptureRect interestScreen, bool freshOnly, out bool isCurrent)
    {
        isCurrent = false;
        var nowMs = Environment.TickCount64;
        if (nowMs < sessionCooldownUntil) return false;

        if (!CaptureSpace.RegionToLocal(interestScreen, out var monitor, out var local, out var effective))
            return false;

        capture ??= new GlassCaptureService();
        if (capture.SetRegion(local))
        {
            regionChanged = true;
            originX = effective.Left;
            originY = effective.Top;
        }

        if (currentMonitor != monitor)
        {
            if (capture.HasSession) capture.Stop();
            currentMonitor = monitor;
            everPulled = false;
        }

        if (!capture.HasSession)
        {
            if (!capture.EnsureSession(monitor))
            {
                sessionCooldownUntil = nowMs + WallpaperBackdropSource.SessionCooldownMs;
                return false;
            }

            capture.SetCadence(cadenceMs);
            everPulled = false;
            return true;
        }

        // One pull per round; on a still desktop it is a single TryGetNextFrame call and no copy.
        // `fresh` — not `gotFrame` — answers "did the desktop change": a re-crop of the held frame
        // for a second caller in the same tick now also returns pixels, and treating that as news
        // would repaint a still desktop on every tick.
        var gotFrame = capture.PullLatest(out var fresh);
        var regionMoved = regionChanged;
        regionChanged = false;
        if (gotFrame) everPulled = true;

        if (freshOnly && everPulled && !fresh && !regionMoved)
        {
            isCurrent = true;
            return true;
        }

        return everPulled;
    }

    /// <summary>Drop the session (the overlay closed, a display changed, a fullscreen app took over).</summary>
    public static void Release()
    {
        lock (Gate)
        {
            capture?.Stop();
            everPulled = false;
            regionChanged = false;
            currentMonitor = IntPtr.Zero;
            sessionCooldownUntil = 0;
        }

        // The overlay is gone: nobody needs the recycled patches either. Buffers still referenced by
        // a snapshot are not in the pool, so this only drops the idle ones.
        PatchBitmapPool.Clear();
    }

    /// <summary>Dispose the session entirely (process shutdown / device loss).</summary>
    public static void Dispose()
    {
        lock (Gate)
        {
            capture?.Dispose();
            capture = null;
            everPulled = false;
            regionChanged = false;
            currentMonitor = IntPtr.Zero;
        }
    }
}
