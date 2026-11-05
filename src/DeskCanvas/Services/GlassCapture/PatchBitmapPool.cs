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

    /// <summary>
    /// Copy <paramref name="source"/>'s pixels into <paramref name="destination"/> <b>without
    /// touching either buffer's allocation</b>.
    /// <para>
    /// <c>SKBitmap.CopyTo</c> is the obvious call and the wrong one: it installs fresh pixel storage
    /// on the destination — the pixel pointer changes — so the rented buffer's own block is orphaned
    /// and a whole patch is allocated on every published frame anyway. That is exactly the churn this
    /// pool exists to remove, and it is invisible unless you check the pointer (see PatchPoolCheck).
    /// Rows are copied by hand here, the same way the capture's readback does it.
    /// </para>
    /// </summary>
    /// <returns>False when the two buffers do not describe the same rectangle.</returns>
    public static bool Copy(SKBitmap source, SKBitmap destination)
    {
        if (source.Width != destination.Width || source.Height != destination.Height) return false;

        var sourcePixels = source.GetPixels();
        var destinationPixels = destination.GetPixels();
        if (sourcePixels == IntPtr.Zero || destinationPixels == IntPtr.Zero) return false;

        var rowBytes = (long)(source.Width * 4);
        unsafe
        {
            var from = (byte*)sourcePixels;
            var to = (byte*)destinationPixels;
            for (var y = 0; y < source.Height; y++)
            {
                Buffer.MemoryCopy(from + (long)y * source.RowBytes, to + (long)y * destination.RowBytes,
                    destination.RowBytes, rowBytes);
            }
        }

        return true;
    }

    /// <summary>
    /// Give a buffer back the way it came: rented ones to the free list, everything else disposed.
    /// <para>
    /// The flag has to travel with the pixels — a caller that only knows "here is an SKBitmap" would
    /// either leak a decoded wallpaper or push a freshly allocated bitmap into the pool and hand it
    /// out again.
    /// </para>
    /// </summary>
    public static void Release(SKBitmap? bitmap, bool pooled)
    {
        if (bitmap == null) return;
        if (pooled)
        {
            Return(bitmap);
            return;
        }

        try { bitmap.Dispose(); } catch { }
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
