using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia.Media.Imaging;

namespace Clock.Services;

/// <summary>
/// The pre-rendered liquid glass frames of the frameless clock, keyed by time string.
///
/// Every entry is a full-window bitmap, so the policy here is a <b>memory budget</b> rather than a
/// frame count: expired frames (older than the widget's own tick) are dropped, and the oldest
/// entries beyond either the frame-count ceiling or the byte budget are dropped too — a full-screen
/// frameless clock must not be able to park hundreds of megabytes of pre-rendered frames.
/// The byte count is the frame's real pixel cost (<c>宽 × 高 × 4</c>).
///
/// The one frame that must never be disposed behind the renderer's back is the one currently
/// composited on screen — <see cref="SetDisplayed"/> marks it, and every disposal path in this
/// class skips it. That contract used to live as a hand-copied <c>entry.Bitmap != liquidGlassBitmap</c>
/// guard repeated at four call sites in the control; it now exists in exactly one place.
/// </summary>
internal sealed class FramelessGlassFrameCache
{
    private readonly Dictionary<string, Entry> frames = new();

    /// <summary>Stable ledger owner index: keeps two frameless clocks apart without an unbounded id.</summary>
    private static int instanceCounter;
    private readonly int instanceIndex = Interlocked.Increment(ref instanceCounter);

    /// <summary>The frame currently on screen. Disposal paths must skip it.</summary>
    private Bitmap? displayed;

    /// <summary>Total bytes of the stored frames; kept exact by <see cref="Remove"/> / <see cref="Store"/>.</summary>
    private long totalBytes;

    /// <summary>Over-budget level already reported, so the diagnostic does not repeat every tick.</summary>
    private long loggedOverage;

    private readonly record struct Entry(DateTime ValidTime, Bitmap Bitmap, long Bytes);

    public int Count => frames.Count;

    /// <summary>Bytes currently held by the cache; diagnostics/tests.</summary>
    public long TotalBytes => totalBytes;

    public bool Contains(string key) => frames.ContainsKey(key);

    /// <summary>True while <paramref name="bitmap"/> is stored under any key.</summary>
    public bool Holds(Bitmap bitmap)
    {
        foreach (var entry in frames.Values)
        {
            if (ReferenceEquals(entry.Bitmap, bitmap)) return true;
        }
        return false;
    }

    public bool TryGet(string key, out Bitmap bitmap)
    {
        if (frames.TryGetValue(key, out var entry))
        {
            bitmap = entry.Bitmap;
            return true;
        }
        bitmap = null!;
        return false;
    }

    /// <summary>Mark which stored frame is currently composited on screen (never disposed here).</summary>
    public void SetDisplayed(Bitmap? bitmap) => displayed = bitmap;

    /// <summary>
    /// Insert or replace a frame. A replaced frame is disposed unless it is the one on screen —
    /// a key can be re-rendered (wallpaper refresh) while its old bitmap is still being shown.
    /// The byte accounting follows the same swap.
    /// </summary>
    public void Store(string key, DateTime validTime, Bitmap bitmap)
    {
        var bytes = BytesOf(bitmap);
        if (frames.TryGetValue(key, out var existing))
        {
            totalBytes -= existing.Bytes;
            if (!ReferenceEquals(existing.Bitmap, displayed) &&
                !ReferenceEquals(existing.Bitmap, bitmap))
            {
                existing.Bitmap.Dispose();
            }
        }
        frames[key] = new Entry(validTime, bitmap, bytes);
        totalBytes += bytes;
        Report();
    }

    /// <summary>
    /// Drop expired frames first (older than <paramref name="minAge"/>, the widget's own tick plus
    /// slack), then the oldest entries while more than <paramref name="maxFrames"/> remain or the
    /// stored pixels exceed <paramref name="maxBytes"/>. The displayed frame is never evicted; if
    /// it alone exceeds the budget, the excess is recorded instead (never silently degrade it).
    /// </summary>
    public void EvictExpired(DateTime now, TimeSpan minAge, int maxFrames, long maxBytes)
    {
        List<string>? expiredKeys = null;
        foreach (var (key, entry) in frames)
        {
            if (entry.ValidTime < now - minAge)
                (expiredKeys ??= new List<string>()).Add(key);
        }

        if (expiredKeys != null)
        {
            foreach (var key in expiredKeys) Remove(key);
        }

        while (frames.Count > maxFrames || totalBytes > maxBytes)
        {
            string? oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var (key, entry) in frames)
            {
                // The frame on screen is pinned outright: it cannot be removed from the cache
                // (its bytes stay accounted) and evicting it would pull the picture out from
                // under the renderer.
                if (ReferenceEquals(entry.Bitmap, displayed)) continue;
                if (entry.ValidTime < oldestTime)
                {
                    oldestTime = entry.ValidTime;
                    oldest = key;
                }
            }
            if (oldest == null || !Remove(oldest)) break;
        }

        // The pinned frames alone can exceed the budget (a single 4K frame is ~32 MiB, the
        // required current + upcoming frames more). That is accepted — the frames cost what the
        // clock needs — but it is recorded, so an unexplained rise has a named cause.
        var overage = totalBytes - maxBytes;
        if (overage > 0 && overage != loggedOverage)
        {
            loggedOverage = overage;
            Debug.WriteLine(
                $"[FramelessGlassFrameCache] required frames exceed the budget by " +
                $"{overage / 1048576.0:F1} MiB ({frames.Count} frames, {totalBytes / 1048576.0:F1} MiB held)");
        }
        else if (overage <= 0)
        {
            loggedOverage = 0;
        }
        Report();
    }

    /// <summary>Drop every frame except the one on screen.</summary>
    public void Clear()
    {
        foreach (var entry in frames.Values)
        {
            if (!ReferenceEquals(entry.Bitmap, displayed))
            {
                entry.Bitmap.Dispose();
            }
        }
        frames.Clear();
        totalBytes = 0;
        Report();
    }

    /// <summary>
    /// Observation only: reports the cache's absolute current usage to the shared resource
    /// ledger. Never throws into the caller's path (the ledger swallows its own errors).
    /// </summary>
    private void Report() =>
        DeskCanvas.Runtime.ResourceLedger.Instance.Report($"clock.frame-cache:{instanceIndex}",
            DeskCanvas.Contracts.ResourceKind.GlyphMask, frames.Count, totalBytes,
            Environment.CurrentManagedThreadId.ToString());

    private bool Remove(string key)
    {
        if (!frames.Remove(key, out var entry)) return false;
        totalBytes -= entry.Bytes;
        if (!ReferenceEquals(entry.Bitmap, displayed))
        {
            entry.Bitmap.Dispose();
        }
        return true;
    }

    /// <summary>The frame's real pixel cost (32-bit BGRA: 宽 × 高 × 4).</summary>
    internal static long BytesOf(Bitmap bitmap) =>
        (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height * 4;
}
