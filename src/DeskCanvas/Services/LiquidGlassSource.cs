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
    private readonly int width;
    private readonly int height;

    public GlassSourceKey(WallpaperSnapshot wallpaper, float sigma, float scale, int width, int height)
    {
        this.wallpaper = wallpaper;
        this.sigma = sigma;
        this.scale = scale;
        this.width = width;
        this.height = height;
    }

    public bool Equals(GlassSourceKey other) =>
        ReferenceEquals(wallpaper, other.wallpaper) &&
        width == other.width &&
        height == other.height &&
        Math.Abs(sigma - other.sigma) < 0.01f &&
        Math.Abs(scale - other.scale) < 0.001f;

    public override bool Equals(object? obj) => obj is GlassSourceKey other && Equals(other);

    public override int GetHashCode() =>
        HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(wallpaper), width, height);
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
/// <para>
/// Eviction is bounded by both an entry count and a real-pixel byte budget (<see cref="BudgetBytes"/>).
/// Everything the render side still needs is protected by reference counting rather than by
/// special cases here: the caller of <see cref="Get"/> owns a reference, so eviction can only ever
/// drop the cache's own copy — never the backdrop a frame is being drawn from.
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

    /// <summary>
    /// Byte budget for the evictable backdrops. The entry count alone cannot bound real memory:
    /// a 2560×1440 backdrop is ~14 MiB, so three entries are ~42 MiB, while a 4K multi-monitor
    /// desktop at full clarity is well over 60 MiB for a single entry — on such a desktop the
    /// count limit would happily pin multiples of that. The budget is measured from the real
    /// pixels (<c>RowBytes × Height</c>, which includes the row padding), and it is deliberately
    /// an <i>eviction</i> budget only: the entry just built (and therefore about to be drawn) is
    /// never evicted by it, so a single oversized backdrop is kept rather than silently degraded —
    /// 4K/multi-screen clarity must not be traded away to hit a constant.
    /// </summary>
    private const long BudgetBytes = 64L * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly List<Entry> entries = new();

    /// <summary>Total bytes held by <see cref="entries"/>; guarded by <see cref="Gate"/>.</summary>
    private static long cachedBytes;

    private sealed record Entry(GlassSourceKey Key, GlassSource Source);

    private static long BytesOf(GlassSource source) => (long)source.Backdrop.RowBytes * source.Backdrop.Height;

    /// <summary>Number of entries currently held in the shared backdrop cache (for diagnostics and tests).</summary>
    internal static int CachedEntryCount { get { lock (Gate) return entries.Count; } }

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
        var width = Math.Max(1, (int)MathF.Ceiling(desktopWidth * scale));
        var height = Math.Max(1, (int)MathF.Ceiling(desktopHeight * scale));
        // The blur is stated in **desktop pixels**, not backdrop-texture pixels. Build draws the
        // wallpaper under a canvas scaled by `scale`, and Skia maps an image filter's sigma
        // through the CTM — so the CTM's one `scale` is the desktop-px → texture-px conversion,
        // and pre-multiplying here as well would square it: the texture received
        // 模糊 · scale², i.e. the very same 模糊 value read ever softer the lower 背景清晰度
        // was set (a quarter of the blur at the 25% default).
        // Use EffectiveBackdropScale (desktop-wide reference DPI) so monitors with different DPIs
        // share a single virtual-desktop backdrop instead of splitting and ping-pong evicting.
        var backdropScale = frame.EffectiveBackdropScale;
        var sigma = (float)glass.Blur * backdropScale / 8f;
        var key = new GlassSourceKey(wallpaper, sigma, scale, width, height);

        GlassSource? built;
        List<GlassSource>? evicted = null;
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
            built = Build(frame, wallpaper, width, height, scale, sigma, backdropScale);
            if (built == null) return null;

            entries.Add(new Entry(key, built));
            cachedBytes += BytesOf(built);

            // Oldest first, both limits enforced. The newest entry (the one just built, about to
            // be drawn by the caller) is never evicted: if its bytes alone exceed the budget there
            // is nothing left to give and it stays — clarity wins over the constant.
            while (entries.Count > Capacity || cachedBytes > BudgetBytes)
            {
                if (entries.Count <= 1) break;

                var oldest = entries[0].Source;
                entries.RemoveAt(0);
                cachedBytes -= BytesOf(oldest);
                (evicted ??= new List<GlassSource>()).Add(oldest);
            }

            // Observation only: report the cache's absolute current usage to the shared ledger.
            DeskCanvas.Runtime.ResourceLedger.Instance.Report("glass.source-cache",
                DeskCanvas.Contracts.ResourceKind.GlassBackdrop, entries.Count, cachedBytes,
                Environment.CurrentManagedThreadId.ToString());

            // The cache now holds its own reference; the caller gets one to own.
            built.AddRef();
        }

        // Drop the cache's references to evicted entries; live callers keep them alive through
        // their own (an evicted backdrop that a draw operation still holds is not freed here).
        if (evicted != null)
        {
            foreach (var source in evicted) source.Dispose();
        }
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
            cachedBytes = 0;
            DeskCanvas.Runtime.ResourceLedger.Instance.Report("glass.source-cache",
                DeskCanvas.Contracts.ResourceKind.GlassBackdrop, 0, 0,
                Environment.CurrentManagedThreadId.ToString());
        }
        foreach (var source in dropped) source.Dispose();
    }

    private static GlassSource? Build(LiquidGlassRenderer.Frame frame, WallpaperSnapshot wallpaper,
        int width, int height, float scale, float sigma, float backdropScale)
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

        SKBitmap? backdrop = null;
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            backdrop = new SKBitmap(info);
            if (backdrop.GetPixels() == IntPtr.Zero)
            {
                backdrop.Dispose();
                return null;
            }

            using (var canvas = new SKCanvas(backdrop))
            {
                canvas.Clear(wallpaper.Background);
                // sigma arrives in desktop px and is converted to texture px by the Scale below —
                // Skia maps an image filter's sigma through the CTM (see Get for why that matters).
                using var filter = sigma > 0.05f ? SKImageFilter.CreateBlur(sigma, sigma, SKShaderTileMode.Clamp) : null;
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High, ImageFilter = filter };
                canvas.Save();
                canvas.Scale(scale);
                // Desktop space: DesktopX/Y are zeroed so the card's own origin drops out of the
                // placement, leaving the shader a lookup it can do with one multiply-add.
                LiquidGlassRenderer.DrawWallpaper(canvas, image!, paint,
                    frame with { DesktopX = 0f, DesktopY = 0f, Scale = backdropScale }, wallpaper);
                canvas.Restore();
                canvas.Flush();
            }

            backdrop.SetImmutable();
            var result = new GlassSource(backdrop, scale);
            backdrop = null;
            return result;
        }
        catch
        {
            backdrop?.Dispose();
            return null;
        }
        finally
        {
            if (ownsImage) image?.Dispose();
        }
    }
}
