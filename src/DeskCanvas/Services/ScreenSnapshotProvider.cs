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

    /// <summary>
    /// The interest region the published frame was produced for. Guarded by <see cref="Gate"/>.
    /// <para>
    /// A capture is clipped to ONE monitor (<see cref="ScreenBackdropSource"/>), so on a multi-screen
    /// desktop the frame can legitimately be smaller than the union it was asked for. Without this
    /// record the coverage check would call that frame "too small" on every single call and capture
    /// forever; with it, "asked for exactly this region already" is answered from the frame in hand
    /// and the retry only happens when the requested region really changed.
    /// </para>
    /// </summary>
    private static CaptureRect lastInterest;

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
    /// <para>
    /// The cached frame is only handed out when it actually <b>covers</b> what the live surfaces are
    /// asking for. A live capture is region-limited (see <see cref="GlassInterest"/>), so the frame
    /// taken for the search card's own neighbourhood has no pixels where the four 分类圆钮 sit;
    /// the shader then addresses texels outside the patch and clamps to its edge, which is the
    /// reported "圆钮采样的是壁纸而不是屏幕". The interest is reported by every surface before it
    /// asks for a frame (LiquidGlassSurface.ReportSamplingInterest), so a surface that just became
    /// visible makes the next call capture instead of reusing a smaller frame.
    /// </para>
    /// </summary>
    public static WallpaperSnapshot? Get()
    {
        var interval = Volatile.Read(ref intervalMs);
        var interest = GlassInterest.ScreenSamplingRegion();
        lock (Gate)
        {
            if (current != null && Environment.TickCount64 - capturedAt < interval
                && (Covers(current, interest) || interest == lastInterest))
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

    /// <summary>
    /// Whether a published live frame holds pixels for every part of <paramref name="interest"/>.
    /// <para>
    /// A patch snapshot covers exactly the rectangle it was cropped from
    /// (<see cref="WallpaperSnapshot.RegionX"/> + the bitmap's size); a snapshot of the whole
    /// desktop covers everything. A wallpaper snapshot is not a live frame and is never used on
    /// this path, so it counts as "no coverage" and forces a real capture. The rule itself lives in
    /// <see cref="SnapshotCoverage"/> so the check programs can pin it.
    /// </para>
    /// </summary>
    private static bool Covers(WallpaperSnapshot? frame, CaptureRect interest)
    {
        if (interest.IsEmpty) return true;
        if (frame is not { LiveCapture: true, CachedBitmap: { } bitmap }) return false;
        return SnapshotCoverage.Covers(frame.RegionX, frame.RegionY, bitmap.Width, bitmap.Height, interest);
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
        // One read for both the throttle and the capture: the region the live surfaces want.
        var interest = GlassInterest.ScreenSamplingRegion();

        // Set when the published frame does not hold the requested rectangle at all. Such a round
        // must produce pixels even if the desktop itself did not change: the crop's geometry is
        // what changed, so "same pixels" (the signature below) is not a reason to keep the old
        // frame — the caller would go on sampling a region its control does not sit in.
        bool coverageMiss;
        lock (Gate)
        {
            var isFresh = current != null
                          && Environment.TickCount64 - capturedAt < Volatile.Read(ref intervalMs);
            var covers = Covers(current, interest) || interest == lastInterest;
            if (skipUnchanged && isFresh && covers) return false;
            coverageMiss = !covers;
        }

        WallpaperSnapshot? fresh = null;
        var exclude = Volatile.Read(ref excludeWindow);
        try
        {
            // Region-limited: the capture samples the union of the screen-sampling glass rects (the
            // launcher's own neighbourhood), not the whole desktop.
            var frame = ScreenBackdropSource.Acquire(interest, exclude, freshOnly: skipUnchanged && !coverageMiss);
            if (frame.IsCurrent)
            {
                // Nothing on screen changed: skip the publish (and the copy behind it) instead of
                // re-capturing and then discovering the same thing from the pixel signature.
                lock (Gate)
                {
                    capturedAt = Environment.TickCount64;
                    lastInterest = interest;
                }
                return false;
            }

            if (frame.Bitmap != null)
            {
                // pooled: the bitmap came out of the patch pool, so the lease hands it back when the
                // last render is done with it instead of freeing it — a live sample then allocates
                // nothing per published frame (see PatchBitmapPool).
                fresh = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), frame.Bitmap,
                    live: true, regionX: frame.OriginX, regionY: frame.OriginY, pooled: frame.Pooled);

                // "The frame did not contain my rectangle" is otherwise invisible, and it is the
                // first thing to check when a surface refracts a desktop that is not behind it.
                if (coverageMiss)
                    GlassDiagnostics.Event(
                        $"[screen-sample] recropped for a larger interest {interest} → " +
                        $"{frame.Bitmap.Width}x{frame.Bitmap.Height} at {frame.OriginX},{frame.OriginY}");
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
        // overlay keeps drawing the material it has, which is still the truth. Never on a coverage
        // round — there the frame's *geometry* is what is out of date, so the pixels must publish
        // even when they look identical (see coverageMiss above).
        if (fresh.CachedBitmap is { } bitmap)
        {
            var signature = DesktopCapturer.SampleGrid(bitmap, SignatureColumns, SignatureRows);
            if (skipUnchanged && !coverageMiss && lastSignature is { } previous && SameSignature(signature, previous))
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
            lastInterest = interest;
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
            lastInterest = default;
            capturedAt = long.MinValue / 2;
        }

        previous?.Dispose();
    }
}
