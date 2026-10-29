using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using SkiaSharp;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// The desktop-widget backdrop: samples <b>the wallpaper itself</b>, live where possible.
/// <para>
/// This is the "对象为壁纸" core ported from LiquidGlassCanvas' <c>WallpaperBackdrop</c>. Its
/// contract is a three-step degradation chain, decided per acquisition, where any failure falls
/// silently through to the next step:
/// </para>
/// <list type="number">
///   <item><description><b>Capture the wallpaper host window.</b> Covers animated wallpapers
///   (Wallpaper Engine, Lively) because the host is found structurally — see
///   <see cref="WallpaperHostDiscovery"/> — not by a fixed class list.</description></item>
///   <item><description><b>The static wallpaper image.</b> Per-monitor path from
///   <c>IDesktopWallpaper</c>, placed with the shell's own style. Cannot see animated content but
///   its position and colour are always right.</description></item>
///   <item><description><b>Nothing.</b> The caller keeps whatever it already renders.</description></item>
/// </list>
/// <para>
/// <b>The debounce/backoff layer</b> is the other half of the port, and it is what makes live
/// sampling affordable:
/// </para>
/// <list type="bullet">
///   <item><description>host discovery is throttled to <see cref="DiscoveryIntervalMs"/> — the
///   window list is not walked per frame;</description></item>
///   <item><description>a failed session start backs off for <see cref="SessionCooldownMs"/>, and a
///   failed static build for <see cref="StaticCooldownMs"/>, so a broken environment costs one
///   attempt per window instead of one per round;</description></item>
///   <item><description>a pull only happens when the capture's frame event fired, the session is
///   new, or the region moved — a still desktop costs <b>zero</b> copies;</description></item>
///   <item><description>the region is clamped to the host's own bounds and expressed in host-local
///   pixels, so the copy is the card's neighbourhood, not the whole desktop.</description></item>
/// </list>
/// </summary>
internal sealed class WallpaperBackdropSource : IDisposable
{
    /// <summary>Where a frame came from (mirrors the C++ <c>Frame::source</c>).</summary>
    internal enum Source
    {
        None = 0,
        WindowCapture = 1,
        StaticWallpaper = 2
    }

    /// <summary>
    /// A region of wallpaper. <see cref="Bitmap"/> pixel (0,0) sits at
    /// (<see cref="OriginX"/>, <see cref="OriginY"/>) in virtual-desktop physical pixels, and one
    /// bitmap pixel is one desktop pixel.
    /// </summary>
    internal readonly record struct Frame(SKBitmap? Bitmap, int OriginX, int OriginY, Source Source)
    {
        public bool HasPixels => Bitmap != null;

        /// <summary>
        /// True when the chain is live but this acquisition found <b>nothing new</b> — no frame
        /// arrived and the region did not move. The caller then knows "the desktop is still" without
        /// being handed (and paying for) a fresh copy of pixels it already has.
        /// </summary>
        public bool IsCurrent { get; init; }
    }

    /// <summary>Host re-enumeration throttle (a still desktop re-discovers at this rate at most).</summary>
    internal const int DiscoveryIntervalMs = 2000;

    /// <summary>Backoff after a capture session refuses to start.</summary>
    internal const int SessionCooldownMs = 5000;

    /// <summary>Backoff after the static wallpaper chain fails to build.</summary>
    internal const int StaticCooldownMs = 2000;

    /// <summary>Default frame-production cadence handed to the capture session.</summary>
    internal const int DefaultCadenceMs = 250;

    private static readonly object Gate = new();

    private readonly GlassCaptureService capture = new();
    private bool disposed;

    private bool active;
    private int cadenceMs = DefaultCadenceMs;
    private bool everPulled;
    private bool sessionRestart = true;
    private bool regionChanged;

    // window-capture chain
    private IntPtr host;
    private CaptureRect hostBounds;
    private bool boundsDirty = true;
    private long lastDiscoveryAt = long.MinValue / 2;
    private long sessionCooldownUntil = long.MinValue / 2;
    private int originX;
    private int originY;

    // static chain
    private bool staticDirty = true;
    private IntPtr staticMonitor;
    private CaptureRect staticMonitorRect;
    private long staticCooldownUntil = long.MinValue / 2;

    /// <summary>The host the current frames come from (diagnostics).</summary>
    internal IntPtr Host { get { lock (Gate) return host; } }

    internal string HostDescription
    {
        get
        {
            lock (Gate)
            {
                if (host == IntPtr.Zero) return "unresolved";
                return $"{NativeMethods.ClassNameOf(host)} (0x{host:X})";
            }
        }
    }

    /// <summary>True once the window chain has produced at least one usable frame.</summary>
    internal bool WindowChainLive { get { lock (Gate) return everPulled; } }

    /// <summary>Set the capture cadence (milliseconds between produced frames; 0 = unthrottled).</summary>
    public void SetCadence(int milliseconds)
    {
        lock (Gate)
        {
            cadenceMs = Math.Max(0, milliseconds);
            capture.SetCadence(cadenceMs);
        }
    }

    /// <summary>
    /// Activate/deactivate the chain. Deactivating releases the session: a hidden widget page must
    /// not keep a capture session (and its frame pool) alive.
    /// </summary>
    public void SetActive(bool value)
    {
        lock (Gate)
        {
            if (active == value) return;
            active = value;
            if (value)
            {
                boundsDirty = true;     // re-discover immediately, the desktop may have changed
                sessionRestart = true;
                return;
            }

            capture.Stop();
            everPulled = false;
            host = IntPtr.Zero;
            hostBounds = default;
            boundsDirty = true;
        }
    }

    /// <summary>Wallpaper/display/DPI changed: the static chain must rebuild on the next acquisition.</summary>
    public void InvalidateStatic()
    {
        lock (Gate)
        {
            staticDirty = true;
            boundsDirty = true;
            sessionRestart = true;
        }
    }

    /// <summary>
    /// The wallpaper behind <paramref name="interestScreen"/> (virtual-desktop physical pixels,
    /// normally the union of the glass rects expanded by the blur margin).
    /// </summary>
    /// <param name="interestScreen">Region to sample, in virtual-desktop physical pixels.</param>
    /// <param name="freshOnly">
    /// Set by the live sampler's probe: it only needs pixels when they actually changed. A still
    /// desktop then costs one <c>TryGetNextFrame</c> and no copy at all, instead of a full-region
    /// copy per probe — at a 10 ms sampling interval that difference is two orders of magnitude.
    /// </param>
    public Frame Acquire(CaptureRect interestScreen, bool freshOnly = false)
    {
        if (interestScreen.IsEmpty) return default;

        lock (Gate)
        {
            if (disposed || !active) return default;

            // ---- chain 1: capture the wallpaper host window ----
            if (EnsureWindowSession(interestScreen))
            {
                // One pull per round. On a still desktop this is a single TryGetNextFrame call that
                // returns nothing — the copy, the readback and the allocation all stay skipped, and
                // the cadence above keeps the system from producing frames we would not read anyway.
                var gotFrame = capture.PullLatest();
                var regionMoved = regionChanged;
                regionChanged = false;

                // Cold start: a session delivers its first frame a few milliseconds after
                // StartCapture, and the old GDI path was synchronous, so "the first capture is
                // already the live wallpaper" is behaviour the material depends on. With live
                // sampling off there is no next round to pick the frame up in, so the wait is spent
                // here — once per session, bounded, and only when nothing has been pulled yet.
                if (!everPulled)
                {
                    var first = AwaitFirstFrame(FirstFrameBudgetMs);
                    everPulled |= first;
                    gotFrame |= first;
                }

                // Nothing arrived and the region did not move: say so instead of copying pixels the
                // caller already has. This is the still-desktop fast path.
                if (freshOnly && everPulled && !gotFrame && !regionMoved)
                    return new Frame(null, originX, originY, Source.WindowCapture) { IsCurrent = true };

                // A session that was just (re)started must really produce a frame before its pixels
                // can be paired with the new origin, or the first card would sample the previous
                // host's neighbourhood at the new host's offset.
                // CopyLatest, not Latest: the snapshot takes ownership and disposes it, while the
                // capture service keeps (and recycles) its own buffer. See CopyLatest.
                var bitmap = everPulled ? capture.CopyLatest() : null;
                if (bitmap != null && capture.LatestWidth > 0 && capture.LatestHeight > 0)
                {
                    NoteSource($"live host '{HostDescription}' {capture.LatestWidth}x{capture.LatestHeight} " +
                               $"@ ({originX},{originY}) via Windows.Graphics.Capture", Source.WindowCapture);
                    return new Frame(bitmap, originX, originY, Source.WindowCapture);
                }

                // First frame not here yet: fall through so the static chain can fill the gap.
            }
            else if (TryGdiHostFrame(interestScreen) is { HasPixels: true } gdi)
            {
                // Windows.Graphics.Capture is unavailable (or refused the session): the same host and
                // the same region, printed with GDI. Keeps live wallpapers working on machines where
                // the capture stack is disabled, at the old cost.
                return gdi;
            }

            // ---- chain 2: the static wallpaper image ----
            if (EnsureStatic(interestScreen, freshOnly) is { } still && (still.HasPixels || still.IsCurrent))
                return still;

            // ---- chain 3: nothing; the caller keeps what it has ----
            return default;
        }
    }

    // ------------------------------------------------------------------ chain 1

    private bool EnsureWindowSession(CaptureRect interestScreen)
    {
        // Discovery runs first, whatever the capture backend: the host handle is also what the GDI
        // fallback needs, so gating it on "WGC is available" left that fallback with nothing to
        // capture and pushed the whole chain down to the static image.
        var nowMs = Environment.TickCount64;
        if (nowMs < sessionCooldownUntil) return false;

        // Discovery is throttled; a dead host, a dirty flag or the throttle expiring all re-resolve.
        if (host == IntPtr.Zero || !NativeMethods.IsWindow(host) || boundsDirty ||
            nowMs - lastDiscoveryAt >= DiscoveryIntervalMs)
        {
            lastDiscoveryAt = nowMs;
            var found = WallpaperHostDiscovery.Discover(interestScreen, out var bounds);
            if (found != host)
            {
                host = found;
                hostBounds = bounds;
                sessionRestart = true;
            }
            else if (found != IntPtr.Zero && bounds != hostBounds)
            {
                // Same host, different rectangle (resolution/layout change): the frame pool's size
                // is stale, so the session must be rebuilt rather than reused.
                hostBounds = bounds;
                sessionRestart = true;
            }

            boundsDirty = false;
        }

        if (host == IntPtr.Zero)
        {
            if (capture.HasSession) capture.Stop();
            NoteSource($"no wallpaper host (WGC available: {GlassCaptureService.IsAvailable}" +
                       $"{UnavailableSuffix()})", Source.None);
            return false;
        }

        if (!GlassCaptureService.IsAvailable)
        {
            if (capture.HasSession) capture.Stop();
            return false;
        }

        // Sample region = interest clipped to the host, in host-local pixels. The texture origin is
        // "host origin + region origin" in desktop space, which is what the glass needs to map a
        // card pixel to a backdrop texel.
        var clipped = interestScreen.Intersect(hostBounds);
        if (clipped.IsEmpty) return false;
        var local = clipped.Offset(-hostBounds.Left, -hostBounds.Top);
        if (capture.SetRegion(local))
        {
            regionChanged = true;
            originX = hostBounds.Left + local.Left;
            originY = hostBounds.Top + local.Top;
        }

        if (sessionRestart && capture.HasSession)
        {
            // Explicit rebuild: EnsureSessionForWindow would happily reuse the old pool.
            capture.Stop();
        }

        if (sessionRestart || capture.CurrentWindow != host)
        {
            if (!capture.EnsureSessionForWindow(host))
            {
                sessionCooldownUntil = nowMs + SessionCooldownMs;
                return false;
            }

            capture.SetCadence(cadenceMs);
            sessionRestart = false;
            everPulled = false;
            return true;    // a fresh session: Acquire pulls one frame immediately
        }

        return true;
    }

    /// <summary>
    /// GDI window capture of the same host and region, used when the capture stack is unavailable.
    /// Failures back off for a second so a host that cannot be printed is not re-printed per round.
    /// </summary>
    private Frame TryGdiHostFrame(CaptureRect interestScreen)
    {
        if (host == IntPtr.Zero) return default;

        var nowMs = Environment.TickCount64;
        if (nowMs < gdiCooldownUntil) return default;

        var clipped = interestScreen.Intersect(hostBounds);
        if (clipped.IsEmpty) return default;
        var local = clipped.Offset(-hostBounds.Left, -hostBounds.Top);

        var bitmap = DesktopCapturer.CaptureWindowRegion(host, local);
        if (bitmap == null)
        {
            gdiCooldownUntil = nowMs + 1000;
            return default;
        }

        NoteSource($"live host '{HostDescription}' {bitmap.Width}x{bitmap.Height} @ " +
                   $"({clipped.Left},{clipped.Top}) via GDI fallback (WGC unavailable: " +
                   $"{GlassCaptureService.Failure})", Source.WindowCapture);
        return new Frame(bitmap, clipped.Left, clipped.Top, Source.WindowCapture);
    }

    private long gdiCooldownUntil = long.MinValue / 2;

    /// <summary>
    /// How long a freshly started session may take to deliver its first frame before the static
    /// chain fills the gap. Only ever spent once per session (see <see cref="Acquire"/>).
    /// </summary>
    private const int FirstFrameBudgetMs = 250;

    private bool AwaitFirstFrame(int budgetMs)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        do
        {
            if (capture.PullLatest()) return true;
            Thread.Sleep(8);
        }
        while (Environment.TickCount64 < deadline);

        return false;
    }

    private Source lastLoggedSource = (Source)(-1);

    private static string UnavailableSuffix() =>
        GlassCaptureService.IsAvailable ? string.Empty : $", {GlassCaptureService.Failure}";

    /// <summary>
    /// Trace a change of capture source — never a steady state. "Which object is the glass sampling"
    /// is the first question every sampling report turns into, and the answer is invisible from the
    /// outside because every source looks like "some wallpaper".
    /// </summary>
    private void NoteSource(string description, Source source)
    {
        if (lastLoggedSource == source) return;
        lastLoggedSource = source;
        GlassDiagnostics.Event($"[capture] source → {description}");
    }

    // ------------------------------------------------------------------ chain 2
    private Frame EnsureStatic(CaptureRect interestScreen, bool freshOnly)
    {
        var nowMs = Environment.TickCount64;
        if (nowMs < staticCooldownUntil) return default;

        var monitor = CaptureSpace.MonitorOf(interestScreen);
        if (monitor == IntPtr.Zero) return default;
        var monitorRect = CaptureSpace.MonitorBounds(monitor);
        if (monitorRect.IsEmpty) return default;

        var crop = interestScreen.Intersect(monitorRect);
        if (crop.IsEmpty) return default;

        if (!EnsureStaticSource(monitor, monitorRect))
        {
            staticCooldownUntil = nowMs + StaticCooldownMs;
            return default;
        }

        // A probe on a still desktop with the window chain dead has nothing new to show either: the
        // same wallpaper, the same crop. Report that instead of rasterising the crop again.
        if (freshOnly && lastStaticCrop.Contains(crop) && lastStaticMonitor == monitor)
            return new Frame(null, crop.Left, crop.Top, Source.StaticWallpaper) { IsCurrent = true };

        // Rasterised per call, from the cached source image. The decoded wallpaper is what is
        // expensive (tens of milliseconds for a 4K photo); drawing a crop of it is not. Caching the
        // rendered crop instead would mean handing the same bitmap to every snapshot — and a
        // snapshot owns (and disposes) its pixels, so the second hand-out would be a dangling read.
        var built = StaticWallpaperBuilder.Render(staticSource, staticBackground, monitorRect, crop,
            out var failure);
        if (built == null)
        {
            staticCooldownUntil = nowMs + StaticCooldownMs;
            GlassDiagnostics.Event($"[glass-capture] static wallpaper chain failed — {failure}");
            return default;
        }

        lastStaticCrop = crop;
        lastStaticMonitor = monitor;
        NoteSource($"static wallpaper {built.Width}x{built.Height} @ ({crop.Left},{crop.Top})",
            Source.StaticWallpaper);
        return new Frame(built, crop.Left, crop.Top, Source.StaticWallpaper);
    }

    /// <summary>
    /// Keep the decoded wallpaper for the monitor in hand. Re-resolved only when the desktop changed
    /// (<see cref="InvalidateStatic"/>) or the monitor did — the same debounce the C++ chain applies
    /// to its static texture, and the reason a probe round on a static desktop costs no decode.
    /// </summary>
    private bool EnsureStaticSource(IntPtr monitor, CaptureRect monitorRect)
    {
        if (!staticDirty && staticSourceReady && staticMonitor == monitor && staticMonitorRect == monitorRect)
            return true;

        var (path, background) = StaticWallpaperBuilder.Resolve(monitorRect);

        SKBitmap? image = null;
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try { image = SKBitmap.Decode(path); }
            catch (Exception ex)
            {
                GlassDiagnostics.Event($"[glass-capture] wallpaper decode failed — {Path.GetFileName(path)}: {ex.GetType().Name}");
            }
        }

        // A null image is not a failure: it is a solid-colour wallpaper, which Render fills with the
        // shell's background colour.
        staticSource?.Dispose();
        staticSource = image;
        staticBackground = background;
        staticMonitor = monitor;
        staticMonitorRect = monitorRect;
        staticDirty = false;
        staticSourceReady = true;
        staticSourceBuiltAt = Environment.TickCount64;
        return true;
    }

    /// <summary>Crop and monitor of the last rasterised static frame (the probe's still-desktop check).</summary>
    private CaptureRect lastStaticCrop;
    private IntPtr lastStaticMonitor;

    private SKBitmap? staticSource;
    private SKColor staticBackground;
    private bool staticSourceReady;
    private long staticSourceBuiltAt;

    /// <summary>Age of the cached decoded wallpaper, in milliseconds (diagnostics).</summary>
    internal double StaticSourceAgeMs =>
        staticSourceBuiltAt == 0 ? 0 : Environment.TickCount64 - staticSourceBuiltAt;

    // ------------------------------------------------------------------ teardown

    public void Dispose()
    {
        lock (Gate)
        {
            if (disposed) return;
            disposed = true;
            active = false;
            capture.Dispose();
            staticSource?.Dispose();
            staticSource = null;
            staticSourceReady = false;
            lastStaticCrop = default;
            lastStaticMonitor = IntPtr.Zero;
            host = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Renders the desktop's static wallpaper into a <b>crop</b> of a monitor, at 1:1 with the desktop.
/// <para>
/// Only the requested crop is rasterised, not the whole monitor: the fallback chain would otherwise
/// allocate a full 2560×1440 bitmap for a 700×500 card neighbourhood, which is exactly the cost the
/// region-limited capture exists to avoid.
/// </para>
/// </summary>
internal static class StaticWallpaperBuilder
{
    private const string DesktopWallpaperClsid = "C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD";

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, out IntPtr wallpaper);

        void GetMonitorDevicePathAt(uint monitorIndex, out IntPtr monitorId);

        void GetMonitorDevicePathCount(out uint count);

        void GetMonitorRectangle([MarshalAs(UnmanagedType.LPWStr)] string? monitorId,
            out NativeMethods.Rect displayRect);

        void SetBackgroundColor(uint color);

        void GetBackgroundColor(out uint color);

        void SetPosition(int position);

        void GetPosition(out int position);
    }

    /// <summary>
    /// Rasterise <paramref name="crop"/> (desktop physical pixels) of the wallpaper covering
    /// <paramref name="monitorRect"/>, from an already-decoded <paramref name="image"/>.
    /// <para>
    /// A fresh bitmap per call is deliberate: the caller hands it to a snapshot, which owns and
    /// disposes its pixels. The expensive half — finding and decoding the wallpaper — is cached by
    /// the caller (<c>WallpaperBackdropSource.EnsureStaticSource</c>); this half is a draw.
    /// </para>
    /// <para>
    /// A null <paramref name="image"/> is a solid-colour wallpaper, not an error: the crop is filled
    /// with the shell's background colour.
    /// </para>
    /// </summary>
    public static SKBitmap? Render(SKBitmap? image, SKColor background, CaptureRect monitorRect,
        CaptureRect crop, out string failure)
    {
        failure = string.Empty;
        var info = new SKImageInfo(crop.Width, crop.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        var bitmap = new SKBitmap(info);
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            failure = "SKBitmap allocation failed";
            bitmap.Dispose();
            return null;
        }

        try
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(background);
                if (image != null)
                {
                    canvas.Save();
                    // The canvas is the crop; shift so drawing can happen in desktop coordinates.
                    canvas.Translate(-crop.Left, -crop.Top);
                    Place(canvas, image, monitorRect);
                    canvas.Restore();
                }
                canvas.Flush();
            }

            bitmap.SetImmutable();
            return bitmap;
        }
        catch (Exception ex)
        {
            failure = $"{ex.GetType().Name}: {ex.Message}";
            bitmap.Dispose();
            return null;
        }
    }

    /// <summary>
    /// The wallpaper file for <paramref name="monitorRect"/> and the shell's background colour.
    /// <para>
    /// The caller caches the result and the decoded image; this is the expensive half (a COM call
    /// into <c>IDesktopWallpaper</c> plus a registry read) and must not run per frame.
    /// </para>
    /// </summary>
    public static (string? Path, SKColor Background) Resolve(CaptureRect monitorRect) =>
        (ResolvePath(monitorRect), ResolveBackground());

    /// <summary>
    /// The shell's own placement (fill / fit / stretch / centre / tile / span). Reads the same
    /// registry values the shell uses, so the fallback matches what the user sees.
    /// </summary>
    private static void Place(SKCanvas canvas, SKBitmap image, CaptureRect monitorRect)
    {
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var style = desktop?.GetValue("WallpaperStyle") as string ?? "10";
        var tile = desktop?.GetValue("TileWallpaper") as string == "1";

        var width = (float)monitorRect.Width;
        var height = (float)monitorRect.Height;
        var left = (float)monitorRect.Left;
        var top = (float)monitorRect.Top;

        if (tile)
        {
            canvas.Save();
            canvas.ClipRect(SKRect.Create(left, top, width, height));
            var matrix = SKMatrix.CreateTranslation(left, top);
            using var shader = image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, matrix);
            using var tiledPaint = new SKPaint { Shader = shader };
            canvas.DrawRect(SKRect.Create(left, top, width, height), tiledPaint);
            canvas.Restore();
            return;
        }

        float drawWidth, drawHeight;
        switch (style)
        {
            case "2": // stretched
                drawWidth = width;
                drawHeight = height;
                break;
            case "0": // centred, 1:1
                drawWidth = image.Width;
                drawHeight = image.Height;
                break;
            case "6": // fit
            {
                var ratio = Math.Min(width / image.Width, height / image.Height);
                drawWidth = image.Width * ratio;
                drawHeight = image.Height * ratio;
                break;
            }
            default: // "10" fill, "22" span — fill is the correct per-monitor reading of both
            {
                var ratio = Math.Max(width / image.Width, height / image.Height);
                drawWidth = image.Width * ratio;
                drawHeight = image.Height * ratio;
                break;
            }
        }

        canvas.Save();
        canvas.ClipRect(SKRect.Create(left, top, width, height));
        var destination = SKRect.Create(
            left + (width - drawWidth) / 2f,
            top + (height - drawHeight) / 2f,
            drawWidth,
            drawHeight);
        // The wallpaper is resampled to the monitor rect once, so an image wrapper around the
        // freshly decoded bitmap is a one-off cost, not a per-frame one. DrawBitmap itself can no
        // longer carry a sampling option (SKPaint.FilterQuality is gone in SkiaSharp 3).
        using var snapshot = SKImage.FromBitmap(image);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawImage(snapshot, destination, LiquidGlassRenderer.WallpaperSampling, paint);
        canvas.Restore();
    }

    /// <summary>
    /// The wallpaper file for the monitor covering <paramref name="monitorRect"/>. Uses
    /// <c>IDesktopWallpaper</c> so per-monitor wallpapers are honoured; falls back to the single
    /// system wallpaper when the shell refuses (a locked-down session, a third-party shell).
    /// </summary>
    private static string? ResolvePath(CaptureRect monitorRect)
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid(DesktopWallpaperClsid));
            if (type != null && Activator.CreateInstance(type) is IDesktopWallpaper wallpaper)
            {
                try
                {
                    wallpaper.GetMonitorDevicePathCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        wallpaper.GetMonitorDevicePathAt(i, out var idPtr);
                        if (idPtr == IntPtr.Zero) continue;
                        var id = Marshal.PtrToStringUni(idPtr);
                        Marshal.FreeCoTaskMem(idPtr);
                        if (string.IsNullOrEmpty(id)) continue;

                        wallpaper.GetMonitorRectangle(id, out var rect);
                        var bounds = new CaptureRect(rect.Left, rect.Top, rect.Right, rect.Bottom);
                        if (bounds != monitorRect) continue;

                        wallpaper.GetWallpaper(id, out var pathPtr);
                        if (pathPtr == IntPtr.Zero) continue;
                        var path = Marshal.PtrToStringUni(pathPtr);
                        Marshal.FreeCoTaskMem(pathPtr);
                        if (!string.IsNullOrEmpty(path)) return path;
                    }

                    // No monitor matched by rectangle (span mode, virtual-desktop quirks): the first
                    // entry is the shell's own answer for "the desktop wallpaper".
                    if (count > 0)
                    {
                        wallpaper.GetMonitorDevicePathAt(0, out var firstPtr);
                        if (firstPtr != IntPtr.Zero)
                        {
                            var first = Marshal.PtrToStringUni(firstPtr);
                            Marshal.FreeCoTaskMem(firstPtr);
                            if (!string.IsNullOrEmpty(first))
                            {
                                wallpaper.GetWallpaper(first, out var pathPtr);
                                if (pathPtr != IntPtr.Zero)
                                {
                                    var path = Marshal.PtrToStringUni(pathPtr);
                                    Marshal.FreeCoTaskMem(pathPtr);
                                    if (!string.IsNullOrEmpty(path)) return path;
                                }
                            }
                        }
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(wallpaper);
                }
            }
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"[glass-capture] IDesktopWallpaper unavailable — {ex.GetType().Name}: {ex.Message}");
        }

        var fromShell = InteropService.GetWallpaperPath();
        return string.IsNullOrEmpty(fromShell) ? null : fromShell;
    }

    private static SKColor ResolveBackground()
    {
        try
        {
            using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            var parts = (colors?.GetValue("Background") as string ?? "32 38 48")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3 && byte.TryParse(parts[0], out var r) && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
                return new SKColor(r, g, b);
        }
        catch { }

        return new SKColor(32, 38, 48);
    }
}
