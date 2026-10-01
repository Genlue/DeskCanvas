using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;

namespace Clock.Services;

/// <summary>
/// The pre-rendered liquid glass frames of the frameless clock, keyed by time string.
///
/// Every entry is a full-window bitmap, so the policy here is a <b>memory budget</b> rather than a
/// frame count: expired frames (older than the widget's own tick) and the oldest entries beyond
/// the budget are dropped, and a hard ceiling keeps a full-screen frameless clock from parking
/// hundreds of megabytes of pre-rendered frames.
///
/// The one frame that must never be disposed behind the renderer's back is the one currently
/// composited on screen — <see cref="SetDisplayed"/> marks it, and every disposal path in this
/// class skips it. That contract used to live as a hand-copied <c>entry.Bitmap != liquidGlassBitmap</c>
/// guard repeated at four call sites in the control; it now exists in exactly one place.
/// </summary>
internal sealed class FramelessGlassFrameCache
{
    private readonly Dictionary<string, (DateTime ValidTime, Bitmap Bitmap)> frames = new();

    /// <summary>The frame currently on screen. Disposal paths must skip it.</summary>
    private Bitmap? displayed;

    public int Count => frames.Count;

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
    /// </summary>
    public void Store(string key, DateTime validTime, Bitmap bitmap)
    {
        if (frames.TryGetValue(key, out var existing) &&
            !ReferenceEquals(existing.Bitmap, displayed) &&
            !ReferenceEquals(existing.Bitmap, bitmap))
        {
            existing.Bitmap.Dispose();
        }
        frames[key] = (validTime, bitmap);
    }

    /// <summary>
    /// Drop expired frames first (older than <paramref name="minAge"/>, the widget's own tick plus
    /// slack), then the oldest entries while more than <paramref name="maxFrames"/> remain.
    /// </summary>
    public void EvictExpired(DateTime now, TimeSpan minAge, int maxFrames)
    {
        List<string>? expiredKeys = null;
        foreach (var (key, (validTime, _)) in frames)
        {
            if (validTime < now - minAge)
                (expiredKeys ??= new List<string>()).Add(key);
        }

        if (expiredKeys != null)
        {
            foreach (var key in expiredKeys) Remove(key);
        }

        while (frames.Count > maxFrames)
        {
            string? oldest = null;
            var oldestTime = DateTime.MaxValue;
            foreach (var (key, (validTime, _)) in frames)
            {
                if (validTime < oldestTime)
                {
                    oldestTime = validTime;
                    oldest = key;
                }
            }
            if (oldest == null || !Remove(oldest)) break;
        }
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
    }

    private bool Remove(string key)
    {
        if (!frames.Remove(key, out var entry)) return false;
        if (!ReferenceEquals(entry.Bitmap, displayed))
        {
            entry.Bitmap.Dispose();
        }
        return true;
    }
}
