using System;
using System.Collections.Generic;
using SkiaSharp;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// A tiny free list of capture-sized pixel buffers, so a live screen sample does not allocate a
/// fresh multi-megabyte bitmap on every published frame.
/// <para>
/// <b>Why this exists.</b> A launcher that samples the screen at the user's cadence publishes tens
/// of frames per second, and every published frame used to allocate a whole new patch — a
/// 1152×1024 neighbourhood is 4.7 MB, and at 43 publishes/s that is ~200 MB/s. SkiaSharp puts those
/// pixels <i>outside</i> the GC heap, so the collection rate never sees them: the process grew to
/// 1.5 GB and only came back when an unrelated managed allocation happened to trigger a collection
/// and the finalizers ran. Recycling the buffers removes the allocation instead of trying to
/// collect it faster — the steady state is two or three buffers however long the launcher stays
/// open, and the pixels are identical because every row is overwritten on rent.
/// </para>
/// <para>
/// <b>Writability.</b> Pooled bitmaps are deliberately left <i>mutable</i>: a bitmap marked
/// immutable can never be written again, and every buffer here is written on rent. The patch is
/// only ever drawn into the backdrop canvas (never handed to a shader), so nothing downstream
/// depends on the immutable-content optimisation.
/// </para>
/// <para>
/// <b>Ownership.</b> <see cref="Rent"/> hands out a buffer the caller owns; <see cref="Return"/>
/// takes it back. A buffer still referenced by a snapshot simply never comes back — the pool is
/// capped and allocates fresh instead, so a missed return costs one buffer, never unbounded growth.
/// </para>
/// </summary>
internal static class PatchBitmapPool
{
    /// <summary>
    /// Idle buffers kept. Three covers the honest in-flight set: the frame being published, the one
    /// still being read by a backdrop build, and one spare while those two swap.
    /// </summary>
    private const int MaxIdle = 3;

    private static readonly object Gate = new();
    private static readonly List<SKBitmap> Idle = new();

    /// <summary>
    /// A writable buffer of exactly <paramref name="width"/>×<paramref name="height"/> physical
    /// pixels — from the free list when one of that size is idle, freshly allocated otherwise.
    /// </summary>
    public static SKBitmap Rent(int width, int height)
    {
        lock (Gate)
        {
            for (var i = Idle.Count - 1; i >= 0; i--)
            {
                var candidate = Idle[i];
                if (candidate.Width != width || candidate.Height != height) continue;
                Idle.RemoveAt(i);
                return candidate;
            }
        }

        return new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
    }

    /// <summary>
    /// Give a rented buffer back. When the pool is full the oldest idle buffer is dropped, so a
    /// sampling region that changes size (a card that morphs, a bar that grows into a card) does not
    /// pin a buffer per historical size.
    /// </summary>
    public static void Return(SKBitmap? bitmap)
    {
        if (bitmap == null) return;

        SKBitmap? dropped = null;
        lock (Gate)
        {
            if (Idle.Count >= MaxIdle)
            {
                dropped = Idle[0];
                Idle.RemoveAt(0);
            }

            Idle.Add(bitmap);
        }

        // Outside the lock: dropping a buffer is a native free and nothing else can observe it.
        if (dropped != null)
        {
            try { dropped.Dispose(); } catch { }
        }
    }

    /// <summary>Drop every idle buffer (the capture session is being released).</summary>
    public static void Clear()
    {
        List<SKBitmap> dropped;
        lock (Gate)
        {
            if (Idle.Count == 0) return;
            dropped = new List<SKBitmap>(Idle);
            Idle.Clear();
        }

        foreach (var bitmap in dropped)
        {
            try { bitmap.Dispose(); } catch { }
        }
    }
}
