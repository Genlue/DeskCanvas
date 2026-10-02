using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using SkiaSharp;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// The <b>single</b> live-sampling pipeline: one composited-screen grab per monitor, shared by every
/// piece of glass in the process — desktop widget cards, the frameless clock's glyph glass and the
/// sidebar's cards alike.
/// <para>
/// There used to be two samplers running side by side. Desktop widgets sampled the wallpaper host
/// (<c>PrintWindow(Progman/WorkerW)</c> — see <c>LiquidGlassWallpaper</c>), while the sidebar
/// sampled its monitor off the screen DC. Both captured a full-size frame on their own interval,
/// blurred their own backdrop and published their own snapshots, so a single desktop paid twice for
/// the same pixels — and the two disagreed about what "behind the glass" even means (the wallpaper
/// host never contains application windows, the screen grab always does). This service is the one
/// that survived: every consumer registers, the frames are per monitor (a card can only ever show
/// what is behind it on its own screen), and the wallpaper file remains only as the degraded
/// fallback when a grab is unavailable.
/// </para>
/// <para>
/// <b>Cost control matters more than the grab itself.</b> A screen grab is a full-resolution
/// <c>StretchBlt</c> plus a <c>GetDIBits</c>, and every published frame makes every surface rebuild
/// its material — the shared blurred backdrop included. Publishing unconditionally on the sampling
/// interval therefore re-built every card's glass ~30 times a second on a desktop that had not moved
/// a pixel, which is where the process spent both its CPU and its memory. So a round:
/// </para>
/// <list type="number">
/// <item>captures into a <b>pooled</b> bitmap (no per-round allocation),</item>
/// <item>compares a coarse identity grid against the frame that is already published,</item>
/// <item>publishes nothing at all — keeping the very same <c>WallpaperSnapshot</c> object, so the
/// shared-backdrop cache key does not change either — when the screen is unchanged, and</item>
/// <item>backs the interval off (up to <see cref="MaxIdleIntervalMs"/>) while that keeps being true.</item>
/// </list>
/// <para>
/// A <b>changing</b> screen still runs the full pipeline at the configured rate: that is what live
/// glass is.
/// </para>
/// <para>
/// The sidebar must not sample <i>itself</i>. That is Windows' job first
/// (<see cref="SidebarZOrder.TryExcludeFromCapture"/>): with the window excluded, the grab already
/// contains the real desktop behind the sidebar, which is precisely the backdrop the glass should
/// refract, so the image is published as-is. Desktop widget windows are deliberately <b>not</b>
/// capture-excluded — excluding them would also erase them from the user's own screenshots and
/// recordings — so their rectangles are patched out of the frame instead
/// (<see cref="CaptureTarget.PatchRects"/>). Either way the glass never refracts itself.
/// </para>
/// <para>
/// Capture runs on its own worker loop, never on the UI thread. When the theme's live sampling is
/// already on, the theme's interval is reused; otherwise a temporary 30 FPS (33 ms) interval is used
/// and torn down with the last consumer. Any failure drops to the next level of
/// <see cref="ScreenCaptureFallback"/>.
/// </para>
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>
    /// Interval used while the theme's own live sampling is off. 15 FPS rather than the 30 a
    /// sidebar used to run at: a published frame is a full-screen bitmap handed to every glass
    /// surface on that monitor, so the publish rate is the process's allocation rate — and glass
    /// that refracts a blurred backdrop does not read any better at 30 than at 15.
    /// </summary>
    private const int TemporaryIntervalMs = 66;

    /// <summary>
    /// Longest the sampler stretches between grabs while the screen stays unchanged. Without it an
    /// idle desktop would pay a full-resolution grab ~15 times a second forever.
    /// </summary>
    private const int MaxIdleIntervalMs = 500;

    /// <summary>
    /// Largest edge of a published frame; larger screens are downscaled. The glass never shows this
    /// image at full size — it blurs it by 模糊 and further downscales it by 背景清晰度 before a
    /// card samples it — so 1600 px on the long edge is already past the point where a larger
    /// capture could change what a card looks like, and every pixel below it is one the backdrop
    /// build and the identity probe do not have to touch.
    /// </summary>
    private const int MaxFrameEdge = 1600;

    /// <summary>Resolution of the identity grid a round compares its grab against the published frame on.</summary>
    private const int IdentityColumns = 32;

    /// <summary>Row count of the identity grid.</summary>
    private const int IdentityRows = 18;

    /// <summary>Per-channel slack of the identity comparison, in 255ths (see LiquidGlassWallpaper).</summary>
    private const int IdentityTolerance = 2;

    /// <summary>
    /// One monitor to sample, with everything the grab needs to know about the glass sitting on it.
    /// </summary>
    /// <param name="Bounds">The monitor's bounds, in physical virtual-desktop coordinates.</param>
    /// <param name="PatchRects">
    /// Rectangles (same coordinates) that must be overwritten with the previous frame before
    /// publishing — the windows on this monitor that Windows does <b>not</b> already leave out of
    /// the grab, i.e. the desktop widgets. Empty when the monitor's only glass is the sidebar, whose
    /// capture exclusion already keeps it out of the image.
    /// </param>
    /// <param name="ScreenId">Stable id of the screen, for diagnostics only.</param>
    public readonly record struct CaptureTarget(PixelRect Bounds, PixelRect[] PatchRects, string ScreenId);

    private static readonly object Gate = new();
    private static int consumerCount;
    private static CancellationTokenSource? loop;
    private static int idleRounds;

    /// <summary>The frames currently published, one per sampled monitor, keyed by the monitor's bounds.</summary>
    private static readonly Dictionary<PixelRect, Published> published = new();

    /// <summary>
    /// One reusable capture bitmap per monitor. Reusing it is what keeps a steady-state round
    /// allocation-free: the grab writes into the same pixels, and only a genuinely new frame hands
    /// the buffer over to a snapshot (so a buffer is never both pooled and reachable by a render).
    /// </summary>
    private static readonly Dictionary<PixelRect, SKBitmap> pool = new();

    /// <summary>
    /// The monitors to sample, recomputed by <see cref="RefreshDemand"/> on the UI thread and read
    /// by the capture worker without touching any window (which only the UI thread may).
    /// </summary>
    private static volatile IReadOnlyList<CaptureTarget> targets = [];

    private static ScreenCaptureFallback level = ScreenCaptureFallback.LiveScreenFrame;

    /// <summary>The sampling interval the loop is currently running at (the patch's freshness bound).</summary>
    private static int currentIntervalMs = TemporaryIntervalMs;

    /// <summary>A published frame plus the identity grid it was accepted on.</summary>
    private sealed class Published(WallpaperSnapshot frame, int[] identity)
    {
        public WallpaperSnapshot Frame { get; } = frame;

        public int[] Identity { get; } = identity;
    }

    /// <summary>True while the capture loop is running.</summary>
    public static bool IsActive
    {
        get { lock (Gate) return loop != null; }
    }

    /// <summary>The current降级 level (for diagnostics / the settings page).</summary>
    public static ScreenCaptureFallback Level
    {
        get { lock (Gate) return level; }
    }

    /// <summary>How many monitors are being sampled right now (diagnostics).</summary>
    public static int TargetCount => targets.Count;

    /// <summary>
    /// Recompute what has to be sampled and start or stop the worker accordingly.
    /// <para>
    /// Called on the UI thread whenever the set of glass surfaces, their visibility or their
    /// geometry changes. It is the only place that reads windows, so the worker never has to.
    /// </para>
    /// </summary>
    public static void RefreshDemand()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshDemand);
            return;
        }

        var list = BuildTargets();
        var wanted = list.Count > 0;
        targets = list;

        lock (Gate)
        {
            if (wanted)
            {
                if (loop == null)
                {
                    level = ScreenCaptureFallback.LiveScreenFrame;
                    Volatile.Write(ref idleRounds, 0);
                    var cts = new CancellationTokenSource();
                    loop = cts;
                    _ = Task.Run(() => RunAsync(cts.Token));
                }
            }
            else if (loop != null)
            {
                var cts = loop;
                loop = null;
                DisposePublished();
                DisposePool();
                try { cts.Cancel(); } catch { /* already disposed */ }
            }
        }
    }

    /// <summary>Release everything (app shutdown).</summary>
    public static void Shutdown()
    {
        targets = [];
        CancellationTokenSource? cts;
        lock (Gate)
        {
            cts = loop;
            loop = null;
            consumerCount = 0;
            DisposePublished();
            DisposePool();
        }
        try { cts?.Cancel(); } catch { /* already disposed */ }
    }

    /// <summary>
    /// An owned snapshot of the screen <paramref name="window"/> sits on, or <c>null</c> when that
    /// screen has no published frame (the capture has not caught up yet, or it degraded) — the
    /// caller then falls back to the static wallpaper. The caller disposes it (one reference is
    /// transferred).
    /// </summary>
    public static WallpaperSnapshot? TryGetSnapshot(Window? window)
    {
        if (!OperatingSystem.IsWindows() || window == null) return null;

        PixelRect bounds;
        try
        {
            var screen = window.Screens.ScreenFromWindow(window);
            if (screen == null) return null;
            bounds = screen.Bounds;
        }
        catch
        {
            // A closing window has no platform handle left to resolve a screen from.
            return null;
        }

        lock (Gate)
        {
            if (!published.TryGetValue(bounds, out var entry)) return null;
            entry.Frame.AddRef();
            return entry.Frame;
        }
    }

    /// <summary>Count of glass surfaces that currently want frames (diagnostics).</summary>
    public static int ConsumerCount => Volatile.Read(ref consumerCount);

    /// <summary>
    /// Windows that need a live frame but carry no <see cref="LiquidGlassSurface"/>: the 无边框时钟
    /// draws its material inside its own glyphs rather than on a card, so nothing else registers its
    /// window and its monitor would never be sampled (see <see cref="IFramelessGlassMask"/>).
    /// </summary>
    private static readonly HashSet<Window> directConsumers = new();

    /// <summary>
    /// Add or remove a direct consumer. <paramref name="wanted"/> is the whole truth — the clock
    /// re-asserts it on every material change — so this is a set, not a ref-count.
    /// </summary>
    public static void SetDirectConsumer(Window? window, bool wanted)
    {
        if (window == null) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => SetDirectConsumer(window, wanted));
            return;
        }

        var changed = wanted ? directConsumers.Add(window) : directConsumers.Remove(window);
        if (changed) RefreshDemand();
    }

    // ---------- Target computation ----------

    /// <summary>
    /// Group the windows that currently demand frames by the monitor they sit on, and describe each
    /// monitor for the grab. Runs on the UI thread (it resolves screens and reads window geometry).
    /// </summary>
    private static List<CaptureTarget> BuildTargets()
    {
        var groups = new Dictionary<PixelRect, (List<PixelRect> Patch, string Id)>();
        var consumers = 0;

        void Include(Window window)
        {
            consumers++;

            PixelRect own;
            string id;
            PixelRect bounds;
            try
            {
                var screen = window.Screens.ScreenFromWindow(window);
                if (screen == null) return;

                // Read live from the window: the sampler and the glass frame mapping have to agree
                // on the same monitor rectangle even after a resolution or DPI change.
                var scaling = window.RenderScaling <= 0 ? 1.0 : window.RenderScaling;
                own = new PixelRect(window.Position, new PixelSize(
                    (int)Math.Round(window.Bounds.Width * scaling),
                    (int)Math.Round(window.Bounds.Height * scaling)));
                bounds = screen.Bounds;
                id = window is SidebarWindow sidebar ? sidebar.ScreenId : $"screen@{bounds.X},{bounds.Y}";
            }
            catch
            {
                // Closing / not yet created: skip this round; the next demand refresh re-reads it.
                return;
            }

            // The sidebar keeps itself out of the capture (WDA_EXCLUDEFROMCAPTURE), so its own
            // rectangle needs no patching — the grab already holds the real desktop behind it, which
            // is exactly the backdrop its glass is meant to refract. Every other window (the desktop
            // widgets) has to have its rectangle patched out of the frame.
            if (!groups.TryGetValue(bounds, out var group))
                group = ([], id);
            if (window is not SidebarWindow { CaptureExclusionActive: true }) group.Patch.Add(own);
            groups[bounds] = group;
        }

        foreach (var surface in LiquidGlassSurface.FrameConsumers) Include(surface.OwnerWindow!);
        foreach (var window in directConsumers.ToList()) Include(window);

        Volatile.Write(ref consumerCount, consumers);

        var result = new List<CaptureTarget>(groups.Count);
        foreach (var (bounds, group) in groups)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            // Overlapping widgets would patch the same pixels twice; de-duplicating keeps the
            // per-round cost proportional to distinct areas.
            var patch = group.Patch
                .Distinct()
                .Where(rect => rect.Width > 0 && rect.Height > 0)
                .ToArray();

            result.Add(new CaptureTarget(bounds, patch, group.Id));
        }
        return result;
    }

    private static async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var interval = LiquidGlassWallpaper.LiveSamplingEnabled
                ? Math.Max(8, LiquidGlassWallpaper.LiveSamplingIntervalMs)
                : TemporaryIntervalMs;
            Volatile.Write(ref currentIntervalMs, interval);

            var idle = Math.Min(Volatile.Read(ref idleRounds), 6);
            var wait = Math.Min(Math.Max(interval << idle, interval), Math.Max(interval, MaxIdleIntervalMs));

            try
            {
                if (CaptureAndPublish())
                {
                    Volatile.Write(ref idleRounds, 0);
                    // Only a screen that actually changed refreshes the surfaces: an unchanged grab
                    // publishes nothing and re-renders nothing, which is what keeps an idle desktop
                    // from rebuilding every card's glass 30 times a second.
                    if (Dispatcher.UIThread.CheckAccess()) LiquidGlassSurface.RefreshScreenSurfaces();
                    else Dispatcher.UIThread.Post(LiquidGlassSurface.RefreshScreenSurfaces);
                }
                else
                {
                    Volatile.Write(ref idleRounds, idle + 1);
                }

                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Any failure degrades one level; after the last level the loop stops trying.
                lock (Gate)
                {
                    level = ScreenCaptureFallbackChain.Next(level);
                    if (!ScreenCaptureFallbackChain.UsesLiveCapture(level)) return;
                }
                try { await Task.Delay(500, token).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    /// <summary>
    /// One sampling round. Returns <c>true</c> when at least one monitor published a new frame.
    /// </summary>
    private static bool CaptureAndPublish()
    {
        var list = targets;
        if (list.Count == 0) return false;

        var next = new Dictionary<PixelRect, Published>(list.Count);
        var built = new List<WallpaperSnapshot>(list.Count);
        var anyChanged = false;

        // The pixels a desktop widget's own rectangle is patched with. They come from the wallpaper
        // rather than from the previous frame: a previous frame already has this same patch applied
        // (or, on the first frame, the widget painted over itself), so restoring from it would feed
        // the patch back into itself and converge on black/whatever the card last drew. The
        // wallpaper host capture never contains the widget windows, so it is the real backdrop.
        WallpaperSnapshot? wallpaper = null;
        var wallpaperAt = 0L;

        try
        {
            foreach (var target in list)
            {
                if (target.Bounds.Width <= 0 || target.Bounds.Height <= 0) continue;

                Published? previous;
                lock (Gate) published.TryGetValue(target.Bounds, out previous);
                // Held for the whole round: the UI thread may stop the sampler (and with it release
                // every published frame) while this grab is still in flight, and the round reads the
                // previous frame's pixels to compare against and to patch with.
                previous?.Frame.AddRef();
                try
                {
                    // The buffer is *taken* out of the pool for the duration of the round and only
                    // put back when it is still ours at the end: publishing hands its pixels to the
                    // snapshot, and leaving a second owner behind would free them under a live
                    // render.
                    pool.Remove(target.Bounds, out var reuse);
                    var bitmap = CaptureScreen(target.Bounds, reuse);
                    if (bitmap == null)
                    {
                        // Unusable grab (a display mode change, a lost desktop): keep showing the
                        // frame that is already published rather than blanking the glass, and park
                        // the buffer for the next attempt.
                        if (reuse != null) pool[target.Bounds] = reuse;
                        if (previous != null) next[target.Bounds] = previous;
                        continue;
                    }

                if (target.PatchRects.Length > 0)
                {
                    // Live, not frozen: a widget's own footprint is what its glass samples, and on an
                    // animated wallpaper a static copy is exactly "the desktop glass went still".
                    // Refreshed at the sampling interval, on the same worker, by the same loop —
                    // there is still only one scheduler.
                    var now = Environment.TickCount64;
                    if (wallpaper == null || now - wallpaperAt >= Math.Max(33, currentIntervalMs))
                    {
                        wallpaper?.Dispose();
                        // Recapture, not Get: the cache is only refreshed by invalidation now that
                        // the sampler is gone, and a frozen frame is exactly the "the desktop glass
                        // went still" this patch exists to prevent.
                        wallpaper = LiquidGlassWallpaper.Recapture();
                        wallpaperAt = now;
                    }
                    FillRegionsFromWallpaper(bitmap, target, wallpaper?.CachedBitmap);
                }

                    var identity = DesktopCapturer.SampleGrid(bitmap, IdentityColumns, IdentityRows);
                    if (previous != null && SameSize(previous.Frame.CachedBitmap, bitmap)
                        && Matches(identity, previous.Identity))
                    {
                        // Pixel-identical to what every surface is already rendering. Keeping the
                        // very same snapshot object matters twice over: the shared backdrop cache is
                        // keyed by the snapshot reference, so an unchanged screen re-uses the
                        // blurred backdrop instead of rebuilding it, and no surface is asked to
                        // re-render at all.
                        next[target.Bounds] = previous;
                        pool[target.Bounds] = bitmap;
                        continue;
                    }

                    // Publishing transfers the pixels to the snapshot: the buffer is deliberately
                    // NOT returned to the pool. A buffer that no longer matches the captured size (a
                    // resolution change) is retired here.
                    if (!ReferenceEquals(bitmap, reuse)) reuse?.Dispose();

                    var snapshot = WallpaperSnapshot.FromBitmap(null, SKColors.Black, bitmap,
                        style: "10", tile: false, live: true);
                    next[target.Bounds] = new Published(snapshot, identity);
                    built.Add(snapshot);
                    anyChanged = true;
                }
                finally
                {
                    previous?.Frame.Dispose();
                }
            }
        }
        catch
        {
            foreach (var snapshot in built) snapshot.Dispose();
            throw;
        }
        finally
        {
            // The wallpaper reference was only borrowed for the patching above.
            wallpaper?.Dispose();
        }

        lock (Gate)
        {
            if (loop == null)
            {
                foreach (var snapshot in built) snapshot.Dispose();
                return false;
            }

            foreach (var (bounds, previous) in published)
            {
                // A monitor that is no longer sampled (its last consumer went away, or it was
                // unplugged): release the frame and the pooled buffer that belonged to it.
                if (next.ContainsKey(bounds)) continue;
                previous.Frame.Dispose();
                if (pool.Remove(bounds, out var stale)) stale.Dispose();
            }

            published.Clear();
            foreach (var (bounds, entry) in next) published[bounds] = entry;
            if (anyChanged) level = ScreenCaptureFallback.LiveScreenFrame;
        }

        // A published frame is what the frameless clock's glyph glass re-renders against; the live
        // tick used to raise this for it (see LiquidGlassWallpaper.WallpaperInvalidated).
        if (anyChanged) LiquidGlassWallpaper.NotifyLiveFramePublished();
        return anyChanged;
    }

    private static void DisposePublished()
    {
        lock (Gate)
        {
            foreach (var entry in published.Values) entry.Frame.Dispose();
            published.Clear();
        }
    }

    private static void DisposePool()
    {
        lock (Gate)
        {
            foreach (var bitmap in pool.Values) bitmap.Dispose();
            pool.Clear();
        }
    }

    private static bool SameSize(SKBitmap? a, SKBitmap b) =>
        a is { Width: > 0 } && a.Width == b.Width && a.Height == b.Height;

    /// <summary>Whether two identity grids describe the same picture (see LiquidGlassWallpaper).</summary>
    private static bool Matches(int[] samples, int[] previous)
    {
        if (previous.Length != samples.Length) return false;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = samples[i];
            var b = previous[i];
            if (Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255)) > IdentityTolerance
                || Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255)) > IdentityTolerance
                || Math.Abs((a & 255) - (b & 255)) > IdentityTolerance)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Overwrite the rectangles the glass itself occupies with the wallpaper, so a desktop widget
    /// never refracts its own card.
    /// <para>
    /// The wallpaper — not the previous frame: the previous frame already has this very patch
    /// applied (and on the first frame the widget had painted over itself in the grab), so restoring
    /// from it would feed the patch back into itself and freeze the card on black. The wallpaper
    /// image is captured from the wallpaper host, which never contains any application window, so it
    /// is the real content behind a widget that sits at the bottom of the z-order.
    /// </para>
    /// <para>
    /// Used only for windows Windows does <b>not</b> already leave out of the capture. The sidebar
    /// opts out through <c>WDA_EXCLUDEFROMCAPTURE</c> instead, which keeps its rectangle real live
    /// desktop content — and is why the sidebar is not excluded from the user's own screenshots the
    /// way it would have to be otherwise.
    /// </para>
    /// </summary>
    private static void FillRegionsFromWallpaper(SKBitmap target, CaptureTarget capture, SKBitmap? wallpaper)
    {
        var scale = target.Width / (float)Math.Max(1, capture.Bounds.Width);

        // The wallpaper image spans the whole virtual desktop; a monitor is a sub-rectangle of it,
        // so the mapping is a straight proportional one.
        var virtualX = GetSystemMetrics(SmXVirtualScreen);
        var virtualY = GetSystemMetrics(SmYVirtualScreen);
        var virtualWidth = Math.Max(1, GetSystemMetrics(SmCXVirtualScreen));
        var virtualHeight = Math.Max(1, GetSystemMetrics(SmCYVirtualScreen));

        using var canvas = new SKCanvas(target);
        foreach (var rect in capture.PatchRects)
        {
            var dst = SKRectI.Create(
                (int)Math.Round((rect.X - capture.Bounds.X) * scale),
                (int)Math.Round((rect.Y - capture.Bounds.Y) * scale),
                (int)Math.Round(rect.Width * scale),
                (int)Math.Round(rect.Height * scale));
            if (dst.Width <= 0 || dst.Height <= 0) continue;

            var clip = SKRectI.Intersect(dst, SKRectI.Create(0, 0, target.Width, target.Height));
            if (clip.Width <= 0 || clip.Height <= 0) continue;

            canvas.Save();
            canvas.ClipRect(clip);
            if (wallpaper is { Width: > 0, Height: > 0 })
            {
                var kx = wallpaper.Width / (float)virtualWidth;
                var ky = wallpaper.Height / (float)virtualHeight;
                var src = SKRect.Create(
                    (clip.Left / scale + capture.Bounds.X - virtualX) * kx,
                    (clip.Top / scale + capture.Bounds.Y - virtualY) * ky,
                    clip.Width / scale * kx,
                    clip.Height / scale * ky);
                canvas.DrawBitmap(wallpaper, src, SKRect.Create(clip.Left, clip.Top, clip.Width, clip.Height));
            }
            else
            {
                using var paint = new SKPaint { Color = new SKColor(0, 0, 0, 255) };
                canvas.DrawRect(clip, paint);
            }
            canvas.Restore();
        }
    }

    private static SKBitmap? CaptureScreen(PixelRect bounds, SKBitmap? reuse)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memDc = CreateCompatibleDC(screenDc);
        if (memDc == IntPtr.Zero) { ReleaseDC(IntPtr.Zero, screenDc); return null; }

        // Bound the captured size so the per-frame cost stays sane on very large (or very
        // high-DPI) screens. The glass frame learns this factor from the bitmap itself, so the
        // downscale never shifts what the card samples.
        var scale = Math.Min(1.0, MaxFrameEdge / (double)Math.Max(bounds.Width, bounds.Height));
        var capW = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var capH = Math.Max(1, (int)Math.Round(bounds.Height * scale));

        var bitmap = CreateCompatibleBitmap(screenDc, capW, capH);
        if (bitmap == IntPtr.Zero) { DeleteDC(memDc); ReleaseDC(IntPtr.Zero, screenDc); return null; }

        var old = SelectObject(memDc, bitmap);
        try
        {
            // CAPTUREBLT includes layered windows; SRCCOPY copies the composited desktop.
            if (!StretchBlt(memDc, 0, 0, capW, capH, screenDc,
                    bounds.X, bounds.Y, bounds.Width, bounds.Height, SRCCOPY | CAPTUREBLT))
                return null;

            return ReadBitmap(memDc, bitmap, capW, capH, reuse);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static SKBitmap? ReadBitmap(IntPtr memDc, IntPtr bitmap, int width, int height, SKBitmap? reuse)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB
            }
        };

        // Reuse is only honoured at the exact captured size; anything else decodes into a fresh
        // allocation (the caller retires the mismatched buffer).
        var result = reuse is { Width: var rw, Height: var rh } && rw == width && rh == height
            ? reuse
            : new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        if (result.GetPixels() == IntPtr.Zero)
        {
            if (!ReferenceEquals(result, reuse)) result.Dispose();
            return null;
        }

        if (GetDIBits(memDc, bitmap, 0, (uint)height, result.GetPixels(), ref info, DIB_RGB_COLORS) == 0)
        {
            if (!ReferenceEquals(result, reuse)) result.Dispose();
            return null;
        }
        return result;
    }

    // ---------- Win32 ----------

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    // SM_* virtual-desktop metrics, in physical pixels (the process is DPI aware). Used to map a
    // monitor's rectangle onto the wallpaper image, which spans the whole virtual desktop.
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77;
    private const int SmCXVirtualScreen = 78, SmCYVirtualScreen = 79;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h,
        IntPtr src, int srcX, int srcY, int srcW, int srcH, int rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
        IntPtr bits, ref BITMAPINFO info, uint usage);
}
