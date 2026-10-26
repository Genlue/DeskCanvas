using System;
using System.Threading;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services.GlassCapture;

namespace DeskCanvas.Services;

/// <summary>
/// The launcher's liquid-glass source: the <b>live screen</b> behind the overlay, not the wallpaper.
/// <para>
/// Why a separate provider instead of <see cref="LiquidGlassWallpaper.Get"/>: the wallpaper pipeline
/// answers "which image is under the card" — correct for a desktop widget that sits on the desktop,
/// wrong for a panel floating over a browser or an editor. This one captures the composited virtual
/// desktop, so the glass refracts what the user is actually looking at.
/// </para>
/// <para>
/// <b>Feedback safety:</b> the overlay must not be part of the frame it samples, or the material
/// feeds on its own output — every frame a little darker, thicker and more saturated than the
/// desktop it is supposed to show. The affinity (<c>WDA_EXCLUDEFROMCAPTURE</c>) is applied by
/// <see cref="ScreenBackdropSource"/> itself, on the capture path, before the frame is produced:
/// the pass used to live only in the GDI fallback, so a WGC session never applied it and the
/// launcher refracted a copy of itself.
/// </para>
/// <para>
/// <b>Cost:</b> one BitBlt of the virtual desktop per capture (single-digit milliseconds at
/// 2560×1440). Captures are throttled to <see cref="MinIntervalMs"/> and shared through a
/// reference-counted snapshot, so the surface may ask on every prepare without re-capturing.
/// </para>
/// </summary>
public static class ScreenSnapshotProvider
{
    /// <summary>
    /// Default minimum age before a new capture replaces the cached one.
    /// <para>
    /// A launcher is open for seconds, so this is a freshness/cost trade, not a frame-rate knob:
    /// 400 ms keeps animated content behind the overlay visibly alive without capturing on every
    /// render. It is the <b>default</b> only — the launcher's 液态玻璃 settings own the live value
    /// (采样间隔), see <see cref="Configure"/>.
    /// </para>
    /// </summary>
    public const int DefaultIntervalMs = LauncherSettings.DefaultGlassSampleInterval;

    /// <summary>The interval in force. Written by <see cref="Configure"/>, read on every capture.</summary>
    private static int intervalMs = DefaultIntervalMs;

    /// <summary>The interval currently used between two captures (milliseconds).</summary>
    public static int IntervalMs => Volatile.Read(ref intervalMs);

    /// <summary>
    /// The overlay that samples this frame, kept out of every capture
    /// (<see cref="DesktopCapturer.CaptureScreenSnapshot"/>). Only this window and whatever of ours
    /// is drawn above it is excluded — windows behind it must stay in the frame, otherwise the glass
    /// refracts the wallpaper instead of the panel the user summoned it from.
    /// </summary>
    private static IntPtr excludeWindow;

    /// <summary>The window currently kept out of the screen capture (0 = every own window).</summary>
    public static IntPtr ExcludeWindow => excludeWindow;

    private static readonly object Gate = new();

    /// <summary>The provider's own reference. Every <see cref="Get"/> hands out another one.</summary>
    private static WallpaperSnapshot? current;

    private static long capturedAt = long.MinValue / 2;

    /// <summary>
    /// Coarse sample of the frame currently published, used by <see cref="RefreshIfDue"/> to tell
    /// "the desktop changed" from "it is the same frame again". Guarded by <see cref="Gate"/>.
    /// </summary>
    private static int[]? lastSignature;

    /// <summary>Diagnostics: how many captures have been taken since process start.</summary>
    public static int CaptureCount { get; private set; }

    /// <summary>
    /// True when the last capture produced pixels. A false here is what makes the glass fall back
    /// to the wallpaper pipeline, so the settings page surfaces it instead of leaving the user to
    /// guess why the card refracts a desktop that is not there.
    /// </summary>
    public static bool HasFrame
    {
        get { lock (Gate) return current?.CachedBitmap != null; }
    }

    /// <summary>
    /// Apply the launcher's 液态玻璃 sampling settings: whether screen sampling is on at all, how
    /// often a frame is refreshed, and which window must be kept out of the frame.
    /// <para>
    /// Turning sampling off drops the cached frame — otherwise a later re-enable would paint one
    /// stale frame from whenever it was last on, which reads as "the setting did nothing".
    /// </para>
    /// <para>
    /// <paramref name="excludeWindow"/> is the overlay's native handle (0 when it has none yet, e.g.
    /// before the first <c>Show</c>): with a handle the capture excludes only that window and the
    /// ones above it, so panels and widgets <i>behind</i> the overlay are sampled like any other
    /// window. Without one the capture falls back to excluding the whole process.
    /// </para>
    /// </summary>
    public static void Configure(bool enabled, int sampleIntervalMs, IntPtr excludeWindow = default)
    {
        var clamped = Math.Clamp(sampleIntervalMs,
            LauncherSettings.MinGlassSampleInterval, LauncherSettings.MaxGlassSampleInterval);
        Volatile.Write(ref intervalMs, clamped);
        Interlocked.Exchange(ref ScreenSnapshotProvider.excludeWindow, excludeWindow);
        // Hand the cadence to the capture session: the system then produces frames at this rate
        // instead of at the compositor's, which is the whole point of the interval.
        ScreenBackdropSource.SetCadence(clamped);

        if (!enabled)
        {
            Interlocked.Exchange(ref ScreenSnapshotProvider.excludeWindow, IntPtr.Zero);
            Release();
        }
    }

    /// <summary>
    /// The current screen snapshot. The caller <b>owns</b> the returned reference and must dispose
    /// it; returns <c>null</c> when the screen could not be captured at all (the caller then falls
    /// back to the wallpaper pipeline).
    /// </summary>
    public static WallpaperSnapshot? Get()
    {
        var interval = Volatile.Read(ref intervalMs);
        lock (Gate)
        {
            if (current != null && Environment.TickCount64 - capturedAt < interval)
            {
                current.AddRef();
                return current;
            }
        }

        Capture();

        lock (Gate)
        {
            if (current == null) return null;
            current.AddRef();
            return current;
        }
    }

    /// <summary>Force a fresh capture (the overlay was just summoned; the previous frame is stale).</summary>
    public static void Capture() => CaptureFrame(skipUnchanged: false);

    /// <summary>
    /// Bring the frame up to date for a <b>live</b> overlay (the launcher stays open for seconds).
    /// <para>
    /// Unlike <see cref="Capture"/> this never publishes a frame whose pixels are the same as the
    /// one already in hand: the caller's next step is a full glass prepare (a desktop blur plus a
    /// GPU draw), and republishing an identical desktop pays that cost for zero visual change. The
    /// comparison is a coarse grid of the new bitmap — 24×14 samples, microseconds — which is more
    /// than enough to tell "the window behind me changed" from "the desktop is still".
    /// </para>
    /// <para>
    /// Also the throttle: a call inside <see cref="IntervalMs"/> of the last capture is a no-op,
    /// so the caller may tick faster than the user's 采样间隔 without ignoring it.
    /// </para>
    /// </summary>
    /// <returns>True when a new frame with different pixels is now current.</returns>
    public static bool RefreshIfDue() => CaptureFrame(skipUnchanged: true);

    /// <summary>Whether the last capture actually replaced the frame (and therefore must be redrawn).</summary>
    private static bool CaptureFrame(bool skipUnchanged)
    {
        if (skipUnchanged)
        {
            lock (Gate)
            {
                if (current != null && Environment.TickCount64 - capturedAt < Volatile.Read(ref intervalMs))
                    return false;
            }
        }

        WallpaperSnapshot? fresh = null;
        var exclude = Volatile.Read(ref excludeWindow);
        try
        {
            // Region-limited: the capture samples the union of the screen-sampling glass rects (the
            // launcher's own neighbourhood), not the whole desktop.
            var interest = GlassInterest.ScreenSamplingRegion();
            var frame = ScreenBackdropSource.Acquire(interest, exclude, freshOnly: skipUnchanged);
            if (frame.IsCurrent)
            {
                // Nothing on screen changed: skip the publish (and the copy behind it) instead of
                // re-capturing and then discovering the same thing from the pixel signature.
                lock (Gate) capturedAt = Environment.TickCount64;
                return false;
            }

            if (frame.Bitmap != null)
            {
                fresh = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), frame.Bitmap,
                    live: true, regionX: frame.OriginX, regionY: frame.OriginY);
            }
        }
        catch (Exception ex)
        {
            // A GDI refusal must degrade to "no screen frame" — the caller falls back to the
            // wallpaper, which is still a usable backdrop. Recorded because that fallback is
            // invisible: the card simply refracts the desktop instead of the window behind it.
            GlassDiagnostics.Event($"[screen-sample] capture threw {ex.GetType().Name}: {ex.Message}");
        }

        if (fresh == null)
        {
            GlassDiagnostics.Event("[screen-sample] capture produced no frame");

            // Keep the last screen frame instead of dropping to the wallpaper. A refused BitBlt is
            // usually transient (a fullscreen transition, a GPU driver reset); replacing a stale
            // screenshot of the desktop with the wallpaper is a bigger lie than showing the frame
            // from 400 ms ago, and it is exactly what made the glass look like it "samples the
            // wallpaper" for the rest of the session.
            lock (Gate)
            {
                capturedAt = Environment.TickCount64;
                if (current != null)
                {
                    GlassDiagnostics.Event("[screen-sample] keeping the previous screen frame");
                    return false;
                }
            }
            return false;
        }

        // Same desktop as the frame already published: drop the new bitmap and stay silent. The
        // overlay keeps drawing the material it has, which is still the truth.
        if (fresh.CachedBitmap is { } bitmap)
        {
            var signature = DesktopCapturer.SampleGrid(bitmap, SignatureColumns, SignatureRows);
            if (skipUnchanged && lastSignature is { } previous && SameSignature(signature, previous))
            {
                fresh.Dispose();
                lock (Gate) capturedAt = Environment.TickCount64;
                return false;
            }

            lastSignature = signature;
        }

        NoteFrame(fresh);

        WallpaperSnapshot? old;
        lock (Gate)
        {
            old = current;
            current = fresh;
            capturedAt = Environment.TickCount64;
            CaptureCount++;
        }

        old?.Dispose();
        return true;
    }

    /// <summary>Sample grid used to decide "did the screen change at all" (coarse, deliberately).</summary>
    private const int SignatureColumns = 24;
    private const int SignatureRows = 14;

    /// <summary>A changed sample means a changed desktop; a small tolerance absorbs capture dither.</summary>
    private static bool SameSignature(int[] a, int[] b)
    {
        var changed = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var ar = (a[i] >> 16) & 255;
            var ag = (a[i] >> 8) & 255;
            var ab = a[i] & 255;
            var br = (b[i] >> 16) & 255;
            var bg = (b[i] >> 8) & 255;
            var bb = b[i] & 255;
            if (Math.Abs(ar - br) + Math.Abs(ag - bg) + Math.Abs(ab - bb) < 12) continue;
            changed++;
        }

        // One or two texels is noise (a caret blinking under a 24×14 probe); a moved window or a
        // new foreground app always changes several.
        return changed <= 2;
    }

    /// <summary>
    /// The mean colour of the last captured frame, as <c>#RRGGBB</c>. This is the number that
    /// answers "the glass shows the wallpaper": compare it with what is actually on screen behind
    /// the overlay — if the frame is light and the card is dark, the pipeline is at fault; if the
    /// frame is already dark, the card is telling the truth about a dark desktop.
    /// </summary>
    public static string LastFrameMean { get; private set; } = "-";

    /// <summary>
    /// Record the frame's mean colour, and trace it when it changes noticeably (a wallpaper switch,
    /// a window appearing behind the overlay). Sampling a 16×9 grid costs microseconds.
    /// </summary>
    private static void NoteFrame(WallpaperSnapshot snapshot)
    {
        if (snapshot.CachedBitmap is not { } bitmap) return;

        long r = 0, g = 0, b = 0;
        var count = 0;
        var stepX = Math.Max(1, bitmap.Width / 16);
        var stepY = Math.Max(1, bitmap.Height / 9);
        for (var y = stepY / 2; y < bitmap.Height; y += stepY)
        for (var x = stepX / 2; x < bitmap.Width; x += stepX)
        {
            var pixel = bitmap.GetPixel(x, y);
            r += pixel.Red;
            g += pixel.Green;
            b += pixel.Blue;
            count++;
        }

        if (count == 0) return;

        var mean = $"#{r / count:X2}{g / count:X2}{b / count:X2}";
        if (mean == LastFrameMean) return;

        var previous = LastFrameMean;
        LastFrameMean = mean;
        GlassDiagnostics.Event(
            $"[screen-sample] frame {bitmap.Width}x{bitmap.Height} mean {mean} (was {previous})");
    }

    /// <summary>Drop the cached frame (display change, overlay closed for good).</summary>
    public static void Release()
    {
        ScreenBackdropSource.Release();

        WallpaperSnapshot? previous;
        lock (Gate)
        {
            previous = current;
            current = null;
            lastSignature = null;
            capturedAt = long.MinValue / 2;
        }

        previous?.Dispose();
    }
}
