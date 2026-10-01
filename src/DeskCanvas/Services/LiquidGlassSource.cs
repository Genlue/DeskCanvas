using System;
using System.Collections.Generic;
using System.Threading;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Identity of a cached backdrop: which capture it came from, and the blur/scale it was built with.
/// <para>
/// The snapshot is compared <b>by reference</b> on purpose — every live-sampling tick produces a
/// new snapshot object, and comparing wallpaper bitmaps by value would be pointlessly expensive
/// and would also treat two identical-looking frames as the same capture.
/// </para>
/// </summary>
internal readonly struct GlassSourceKey : IEquatable<GlassSourceKey>
{
    private readonly WallpaperSnapshot wallpaper;
    private readonly float sigma;
    private readonly float scale;

    public GlassSourceKey(WallpaperSnapshot wallpaper, float sigma, float scale)
    {
        this.wallpaper = wallpaper;
        this.sigma = sigma;
        this.scale = scale;
    }

    public bool Equals(GlassSourceKey other) =>
        ReferenceEquals(wallpaper, other.wallpaper) &&
        Math.Abs(sigma - other.sigma) < 0.01f &&
        Math.Abs(scale - other.scale) < 0.001f;

    public override bool Equals(object? obj) => obj is GlassSourceKey other && Equals(other);

    public override int GetHashCode() =>
        System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(wallpaper);
}

/// <summary>
/// The blurred, downscaled desktop backdrop the GPU material samples.
/// <para>
/// Reference counted because it is created on the UI/worker thread but read on the render thread:
/// the cache holds one reference, every in-flight
/// <see cref="Avalonia.Rendering.SceneGraph.ICustomDrawOperation"/> holds another, and the bitmap is
/// freed only when the last one goes away. Replacing a source while a frame is still being drawn
/// therefore cannot pull the texture out from under it.
/// </para>
/// </summary>
internal sealed class GlassSource : IDisposable
{
    private int references = 1;
    private readonly SKBitmap backdrop;

    public GlassSource(SKBitmap backdrop, float scale)
    {
        this.backdrop = backdrop;
        Scale = scale;
    }

    /// <summary>Blurred backdrop in desktop space; see <see cref="Scale"/> for the mapping.</summary>
    public SKBitmap Backdrop => backdrop;

    /// <summary>Backdrop pixels per render pixel.</summary>
    public float Scale { get; }

    public void AddRef() => Interlocked.Increment(ref references);

    public void Dispose()
    {
        if (Interlocked.Decrement(ref references) > 0) return;
        try { backdrop.Dispose(); } catch { }
    }
}

/// <summary>
/// Builds and caches <see cref="GlassSource"/> instances: one blurred, downscaled copy of the
/// desktop per capture, shared by every glass widget on screen.
/// <para>
/// This is the only per-pixel CPU work left in the GPU path. Doing it once per capture rather than
/// once per card is what makes live sampling affordable: the expensive optics then run on the GPU
/// against this single texture, and widgets with the same blur genuinely share one bitmap.
/// </para>
/// </summary>
internal static class LiquidGlassSourceCache
{
    /// <summary>
    /// Lowest backdrop scale accepted, whatever 背景清晰度 asks for. A quarter of the desktop still
    /// reads as a blurred wallpaper behind the lens; below that it is flat colour.
    /// </summary>
    private const float MinBackdropScale = (float)(LiquidGlassSettings.MinBackdropClarity / 100.0);

    /// <summary>Entries kept before the oldest is released. Widgets may differ in blur strength.</summary>
    private const int Capacity = 3;

    private static readonly object Gate = new();
    private static readonly List<Entry> entries = new();

    private sealed record Entry(GlassSourceKey Key, GlassSource Source);

    /// <summary>
    /// The shared backdrop for this frame, built when the capture or the blur changed.
    /// <para>
    /// The result carries a <b>reference the caller owns</b> and must dispose exactly once, even if
    /// it is used immediately: the cache may evict and free the entry the moment another widget asks
    /// for a newer capture, and a borrowed pointer would then be a disposed bitmap.
    /// </para>
    /// </summary>
    public static GlassSource? Get(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper)
    {
        // Degenerate geometry (headless, tests, a display that reports nothing): fall back to the
        // card box so the desktop mapping stays finite instead of collapsing to a zero-size surface.
        var desktopWidth = frame.DesktopWidth >= 1f ? frame.DesktopWidth : Math.Max(1f, frame.Width);
        var desktopHeight = frame.DesktopHeight >= 1f ? frame.DesktopHeight : Math.Max(1f, frame.Height);

        // 背景清晰度 is the user's own cost/quality trade for the shared backdrop, so it is honoured
        // directly instead of being capped by a constant of ours — and it is the knob that actually
        // moves the sampling round, because the blur below is what the round spends its time on.
        // Read through the active material's pipeline settings: 液态玻璃 and 新液态玻璃 carry
        // their own clarity/blur values on the shared cache.
        var glass = frame.Theme.EffectiveGlass;
        var scale = Math.Clamp((float)(glass.BackdropClarity / 100.0), MinBackdropScale, 1f);
        var sigma = (float)glass.Blur * frame.Scale / 8f * scale;
        var key = new GlassSourceKey(wallpaper, sigma, scale);

        GlassSource? built;
        GlassSource? evicted = null;
        lock (Gate)
        {
            foreach (var entry in entries)
            {
                if (!entry.Key.Equals(key)) continue;
                // Taken under the lock, so an eviction on another thread cannot free it in between.
                entry.Source.AddRef();
                return entry.Source;
            }

            // The build stays inside the lock on purpose. Every visible widget asks for the same
            // frame's backdrop at the same moment, and without this they would each capture-blur a
            // full desktop copy concurrently — the exact cost this cache exists to remove. The
            // waiters simply find the finished entry above.
            built = Build(frame, wallpaper, desktopWidth, desktopHeight, scale, sigma);
            if (built == null) return null;

            entries.Add(new Entry(key, built));
            while (entries.Count > Capacity)
            {
                evicted = entries[0].Source;
                entries.RemoveAt(0);
            }
            // The cache now holds its own reference; the caller gets one to own.
            built.AddRef();
        }

        // Drop the cache's reference to the evicted entry; live callers keep it alive through theirs.
        evicted?.Dispose();
        return built;
    }

    /// <summary>Release every cached source (memory pressure, display change). Callers keep theirs.</summary>
    public static void Clear()
    {
        List<GlassSource> dropped;
        lock (Gate)
        {
            dropped = new List<GlassSource>(entries.Count);
            foreach (var entry in entries) dropped.Add(entry.Source);
            entries.Clear();
        }
        foreach (var source in dropped) source.Dispose();
    }

    private static GlassSource? Build(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper,
        float desktopWidth, float desktopHeight, float scale, float sigma)
    {
        SKBitmap? image = wallpaper.CachedBitmap;
        var ownsImage = false;
        if (image == null)
        {
            // Only touch ImageBytes when there is no bitmap: its getter *encodes* the bitmap to PNG
            // when both are absent, which is far too expensive to hit once per capture.
            var bytes = wallpaper.ImageBytes;
            if (bytes == null) return null;
            try
            {
                image = SKBitmap.Decode(bytes);
                ownsImage = image != null;
            }
            catch { return null; }
        }

        try
        {
            var width = Math.Max(1, (int)MathF.Ceiling(desktopWidth * scale));
            var height = Math.Max(1, (int)MathF.Ceiling(desktopHeight * scale));
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            if (surface == null) return null;

            var canvas = surface.Canvas;
            canvas.Clear(wallpaper.Background);
            using var filter = sigma > 0.05f ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
            canvas.Save();
            canvas.Scale(scale);
            // Desktop space: DesktopX/Y are zeroed so the card's own origin drops out of the
            // placement, leaving the shader a lookup it can do with one multiply-add.
            LiquidGlassRenderer.DrawWallpaper(canvas, image!, paint,
                frame with { DesktopX = 0f, DesktopY = 0f }, wallpaper);
            canvas.Restore();

            var backdrop = Snapshot(surface, width, height);
            if (backdrop == null) return null;
            return new GlassSource(backdrop, scale);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (ownsImage) image?.Dispose();
        }
    }

    private static SKBitmap? Snapshot(SKSurface surface, int width, int height)
    {
        using var image = surface.Snapshot();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
        {
            bitmap.Dispose();
            return null;
        }
        bitmap.SetImmutable();
        return bitmap;
    }
}
