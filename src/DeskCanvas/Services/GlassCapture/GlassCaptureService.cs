using System;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// One capture session (a monitor or a window), kept alive and asked for pixels only when the
/// compositor produced a new frame.
/// <para>
/// This is the port of LiquidGlassCanvas' <c>CaptureService</c> — the "对象为屏幕" core — and its
/// point is <b>doing less work per frame</b>:
/// </para>
/// <list type="number">
///   <item><description><b>Region, not screen.</b> Only the rectangle the glass needs is copied out
///   of the frame (GPU-side <c>CopySubresourceRegion</c>) and only that rectangle is read back. A
///   2560×1440 desktop is 14.7 MB per frame; a card's neighbourhood is one to two.</description></item>
///   <item><description><b>Cadence.</b> <c>MinUpdateInterval</c> makes the system produce frames at
///   the render rate instead of at the compositor's ~60 Hz.</description></item>
///   <item><description><b>Coalescing.</b> Every queued frame is released except the last, so a
///   burst of pending frames collapses into one copy instead of N.</description></item>
///   <item><description><b>Re-crop from the held frame.</b> A region change (the card moved, the
///   window was dragged) is served from the frame already in hand rather than waiting for the next
///   one — otherwise a still desktop would keep showing the old neighbourhood indefinitely.</description></item>
///   <item><description><b>Idle costs one call.</b> On a still desktop <c>TryGetNextFrame</c> returns
///   nothing, and no copy, no readback and no allocation happen at all.</description></item>
/// </list>
/// <para>
/// Unlike the C++ original this reads the region back to CPU: DeskCanvas' material is evaluated by
/// Skia against an <see cref="SKBitmap"/>, and Avalonia's Skia backend is not a D3D11 context we can
/// hand a texture to. Everything else — session lifetime, region clamping, cadence, coalescing — is
/// the same state machine.
/// </para>
/// <para>
/// WGC also sidesteps the UIPI wall the GDI path hits: <c>PrintWindow</c> on <c>Progman</c> is denied
/// when the shell runs at a higher integrity level, while a capture session reads the DWM composition
/// and works across integrity levels.
/// </para>
/// </summary>
internal sealed class GlassCaptureService : IDisposable
{
    // ---- process-wide D3D11 device (sessions share it) ----
    private static readonly object DeviceGate = new();
    private static readonly object ContextGate = new();
    private static ID3D11Device? sharedDevice;
    private static IntPtr sharedWinRtDevice;
    private static bool deviceAttempted;
    private static string deviceFailure = "not attempted";

    private readonly object gate = new();

    private IntPtr pool;
    private IntPtr session;
    private ID3D11Texture2D? heldTexture;
    private long heldAt;

    private IntPtr monitor;
    private IntPtr window;

    private CaptureRect region;
    private bool haveRegion;
    private bool regionDirty;
    private int cadenceMs;

    private SKBitmap? latest;
    private int latestWidth;
    private int latestHeight;

    private ID3D11Texture2D? staging;
    private int stagingWidth;
    private int stagingHeight;

    private bool disposed;

    /// <summary>
    /// Whether this machine can capture at all. False means every caller must fall back to GDI;
    /// <see cref="Failure"/> says why, which is the only way to tell "WGC is disabled by policy"
    /// from "no D3D11 device" in a user report.
    /// </summary>
    public static bool IsAvailable
    {
        get
        {
            EnsureDevice();
            return sharedDevice != null && sharedWinRtDevice != IntPtr.Zero;
        }
    }

    /// <summary>Why <see cref="IsAvailable"/> is false (diagnostics).</summary>
    public static string Failure
    {
        get { lock (DeviceGate) return deviceFailure; }
    }

    public IntPtr CurrentMonitor { get { lock (gate) return monitor; } }

    public IntPtr CurrentWindow { get { lock (gate) return window; } }

    public bool HasSession { get { lock (gate) return pool != IntPtr.Zero; } }

    /// <summary>
    /// The newest region-sized pixels, <b>handed over</b> to the caller, or null when nothing has
    /// been pulled yet. The session forgets them, so the next pull rents a fresh buffer from
    /// <see cref="PatchBitmapPool"/> and no pixels are copied twice.
    /// <para>
    /// Ownership is still exactly one owner per allocation: the bitmap leaves this object for good,
    /// and whoever receives it (a <c>WallpaperSnapshot</c> lease) owns it and gives it back. What the
    /// old <c>CopyLatest</c> added on top was a whole extra copy of the patch per published frame —
    /// 4.7 MB of native memory at 1152×1024, tens of times a second, on a path whose whole point is
    /// that a still desktop costs nothing. That copy existed because the session reused its own
    /// buffer; renting from the pool gives the caller a buffer nobody else touches instead.
    /// </para>
    /// </summary>
    /// <param name="width">Pixels across in the returned bitmap (0 when nothing was taken).</param>
    /// <param name="height">Pixels down in the returned bitmap (0 when nothing was taken).</param>
    public SKBitmap? TakeLatest(out int width, out int height)
    {
        lock (gate)
        {
            var taken = latest;
            if (taken == null)
            {
                width = 0;
                height = 0;
                return null;
            }

            latest = null;
            width = latestWidth;
            height = latestHeight;
            return taken;
        }
    }

    /// <summary>Pixels across in the most recent crop (diagnostics; 0 before the first pull).</summary>
    public int LatestWidth { get { lock (gate) return latestWidth; } }

    /// <summary>Pixels down in the most recent crop (diagnostics; 0 before the first pull).</summary>
    public int LatestHeight { get { lock (gate) return latestHeight; } }

    /// <summary>
    /// A region-sized <b>copy</b> of the newest frame, owned by the caller, or null when nothing has
    /// been pulled yet.
    /// <para>
    /// This is the wallpaper chain's contract, kept deliberately: that snapshot is published to every
    /// widget at once and its buffer is shared far longer than a launcher frame's, so the copy — not
    /// a hand-off — is what keeps one owner per allocation there. The launcher's screen chain does
    /// not pay it: see <see cref="TakeLatest"/>, which hands the crop over and costs one patch per
    /// published frame instead of two.
    /// </para>
    /// </summary>
    public SKBitmap? CopyLatest()
    {
        lock (gate)
        {
            if (latest == null) return null;
            try { return latest.Copy(); }
            catch { return null; }
        }
    }

    /// <summary>Age of the pixels currently held, in milliseconds.</summary>
    public double HeldFrameAgeMs
    {
        get
        {
            lock (gate)
            {
                if (heldTexture == null || heldAt == 0) return 0;
                return Environment.TickCount64 - heldAt;
            }
        }
    }

    // ------------------------------------------------------------------ device

    private static void EnsureDevice()
    {
        lock (DeviceGate)
        {
            if (deviceAttempted) return;
            deviceAttempted = true;
            try
            {
                WinRtInterop.EnsureApartment();

                var device = D3D11.D3D11CreateDevice(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1);
                if (device == null)
                {
                    deviceFailure = "D3D11CreateDevice returned null";
                GlassDiagnostics.Event($"[glass-capture] {deviceFailure}");
                    return;
                }

                using var dxgi = device.QueryInterface<IDXGIDevice>();
                var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out var winRtDevice);
                if (hr < 0 || winRtDevice == IntPtr.Zero)
                {
                    device.Dispose();
                    deviceFailure = $"CreateDirect3D11DeviceFromDXGIDevice failed (0x{hr:X8})";
                    GlassDiagnostics.Event($"[glass-capture] {deviceFailure}");
                    return;
                }

                sharedDevice = device;
                sharedWinRtDevice = winRtDevice;
                deviceFailure = string.Empty;
            }
            catch (Exception ex)
            {
                deviceFailure = $"{ex.GetType().Name}: {ex.Message}";
                GlassDiagnostics.Event($"[glass-capture] D3D11 device unavailable — {deviceFailure}");
            }
        }
    }

    [DllImport("d3d11.dll")]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr inspectable);

    // ------------------------------------------------------------------ session

    /// <summary>Ensure a session for <paramref name="target"/> (a monitor).</summary>
    public bool EnsureSession(IntPtr target)
    {
        if (target == IntPtr.Zero) return false;
        EnsureDevice();
        lock (gate)
        {
            if (monitor == target && pool != IntPtr.Zero) return true;
            return Start(target, IntPtr.Zero, requireBorderFree: false);
        }
    }

    /// <summary>
    /// Ensure a session for a <b>window</b> (the wallpaper host). A capture border is not acceptable
    /// on the desktop, so a build that refuses to hide it is treated as "no session" and the caller
    /// falls back to the static wallpaper — the same rule as the C++ original.
    /// </summary>
    public bool EnsureSessionForWindow(IntPtr target)
    {
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return false;
        EnsureDevice();
        lock (gate)
        {
            if (window == target && pool != IntPtr.Zero) return true;
            return Start(IntPtr.Zero, target, requireBorderFree: true);
        }
    }

    private bool Start(IntPtr monitorTarget, IntPtr windowTarget, bool requireBorderFree)
    {
        if (sharedDevice == null || sharedWinRtDevice == IntPtr.Zero) return false;

        // RoGetActivationFactory needs the calling thread to be WinRT-initialised, and sessions are
        // started from whichever worker thread happens to be preparing a card. Idempotent and
        // cheap; skipping it made the second session fail on a machine where the first thread was
        // already initialised and the next one was not.
        WinRtInterop.EnsureApartment();

        var factory = IntPtr.Zero;
        var item = IntPtr.Zero;
        var newPool = IntPtr.Zero;
        var newSession = IntPtr.Zero;
        try
        {
            factory = WinRtInterop.CaptureItemInteropFactory();
            if (factory == IntPtr.Zero)
            {
                deviceFailure = "IGraphicsCaptureItemInterop unavailable (WGC disabled by policy?)";
                return false;
            }

            var target = windowTarget != IntPtr.Zero ? windowTarget : monitorTarget;
            item = windowTarget != IntPtr.Zero
                ? WinRtInterop.CreateForWindow(factory, target)
                : WinRtInterop.CreateForMonitor(factory, target);
            if (item == IntPtr.Zero)
            {
                GlassDiagnostics.Event($"[glass-capture] capture item refused for 0x{target:X}");
                return false;
            }

            if (!WinRtInterop.TryGetItemSize(item, out var width, out var height))
            {
                GlassDiagnostics.Event("[glass-capture] capture item reported no size");
                return false;
            }

            newPool = WinRtInterop.CreateFramePool(sharedWinRtDevice, width, height, 2);
            if (newPool == IntPtr.Zero)
            {
                GlassDiagnostics.Event($"[glass-capture] frame pool refused for {width}x{height}");
                return false;
            }

            newSession = WinRtInterop.CreateSession(newPool, item);
            if (newSession == IntPtr.Zero)
            {
                GlassDiagnostics.Event("[glass-capture] capture session refused");
                return false;
            }

            WinRtInterop.SetCursorCaptureEnabled(newSession, false);

            if (!WinRtInterop.SetBorderRequired(newSession, false) && requireBorderFree)
            {
                // A yellow capture border around the desktop is not an acceptable trade: the
                // wallpaper chain falls back to the static image instead.
                GlassDiagnostics.Event("[glass-capture] border could not be hidden — window capture unavailable");
                return false;
            }

            // Replace the old session only once the new one is fully built, so a failed start leaves
            // the caller with the session it already had rather than with none.
            TearDownLocked();

            pool = newPool;
            session = newSession;
            monitor = monitorTarget;
            window = windowTarget;
            heldAt = 0;
            newPool = IntPtr.Zero;
            newSession = IntPtr.Zero;

            ApplyCadenceLocked();
            WinRtInterop.StartCapture(session);
            return true;
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"[glass-capture] session start failed for 0x{(windowTarget != IntPtr.Zero ? windowTarget : monitorTarget):X} — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
        finally
        {
            if (newSession != IntPtr.Zero)
            {
                WinRtInterop.Close(newSession);
                Marshal.Release(newSession);
            }
            if (newPool != IntPtr.Zero) Marshal.Release(newPool);
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    /// <summary>
    /// Set the desired frame-production cadence. 0 = unthrottled. On builds without
    /// <c>IGraphicsCaptureSession5</c> this silently does nothing; the "no new frame, no copy" rule
    /// is then what keeps the cost down.
    /// </summary>
    public void SetCadence(int minIntervalMs)
    {
        lock (gate)
        {
            cadenceMs = Math.Max(0, minIntervalMs);
            ApplyCadenceLocked();
        }
    }

    private void ApplyCadenceLocked()
    {
        if (session != IntPtr.Zero) WinRtInterop.SetMinUpdateInterval(session, cadenceMs);
    }

    /// <summary>
    /// Set the region to keep (in the session item's <b>local</b> pixels, left/top inclusive).
    /// Returns true when the region actually changed — the caller should pull immediately in that
    /// case, because a still desktop produces no new frames and the old crop would otherwise be
    /// shown forever.
    /// </summary>
    public bool SetRegion(CaptureRect local)
    {
        lock (gate)
        {
            if (haveRegion && local == region) return false;
            region = local;
            haveRegion = true;
            regionDirty = true;
            return true;
        }
    }

    // ------------------------------------------------------------------ pull

    /// <summary>
    /// Drain the frame pool and re-crop the newest frame into the crop buffer.
    /// </summary>
    /// <returns>True when a crop now holds pixels (fresh, or re-cropped from the held frame). On a
    /// still desktop with an unchanged region this is one COM call and nothing else.</returns>
    public bool PullLatest() => PullLatest(out _);

    /// <summary>
    /// As <see cref="PullLatest()"/>, and reports whether the compositor actually produced a
    /// <b>new</b> frame.
    /// <para>
    /// The two answers differ exactly where a caller needs them to: a crop can come back
    /// non-fresh either because the region moved or because the previous crop was handed out
    /// through <see cref="TakeLatest"/> and the same held frame has to be re-cropped for this
    /// caller. "Is the desktop unchanged?" is the second question, and the live path must keep
    /// answering it — publishing a re-crop as if it were new would repaint a still desktop forever.
    /// </para>
    /// </summary>
    public bool PullLatest(out bool fresh)
    {
        fresh = false;
        EnsureDevice();
        if (sharedDevice == null) return false;
        WinRtInterop.EnsureApartment();

        lock (gate)
        {
            if (pool == IntPtr.Zero || disposed) return false;

            IntPtr newest = IntPtr.Zero;
            try
            {
                while (true)
                {
                    var frame = WinRtInterop.TryGetNextFrame(pool);
                    if (frame == IntPtr.Zero) break;
                    if (newest != IntPtr.Zero) Marshal.Release(newest);   // coalesce: newest only
                    newest = frame;
                }

                if (newest == IntPtr.Zero)
                {
                    // No new pixels. A region that just moved — or a crop that a caller already took
                    // — is still served from the held frame: otherwise the glass would refract the
                    // previous neighbourhood, or fall through to the slower screen grab below, for a
                    // frame that has not actually changed.
                    return (regionDirty || latest == null) && CopyFromHeldLocked();
                }

                var surface = WinRtInterop.GetFrameSurface(newest);
                if (surface == IntPtr.Zero) return false;

                ID3D11Texture2D? texture = null;
                try
                {
                    var texturePtr = WinRtInterop.GetTexture(surface);
                    if (texturePtr != IntPtr.Zero) texture = new ID3D11Texture2D(texturePtr);
                }
                finally { Marshal.Release(surface); }

                if (texture == null) return false;

                heldTexture?.Dispose();
                heldTexture = texture;
                heldAt = Environment.TickCount64;
                fresh = true;

                return CopyFromHeldLocked();
            }
            finally
            {
                if (newest != IntPtr.Zero) Marshal.Release(newest);
            }
        }
    }

    /// <summary>Copy the held frame's current region into a fresh bitmap (the region is clamped).</summary>
    private bool CopyFromHeldLocked()
    {
        if (heldTexture == null || sharedDevice == null) return false;

        var description = heldTexture.Description;
        var width = (int)description.Width;
        var height = (int)description.Height;

        int left, top, right, bottom;
        if (haveRegion)
        {
            left = Math.Clamp(region.Left, 0, width);
            top = Math.Clamp(region.Top, 0, height);
            right = Math.Clamp(region.Right, 0, width);
            bottom = Math.Clamp(region.Bottom, 0, height);
        }
        else
        {
            left = top = 0;
            right = width;
            bottom = height;
        }

        var boxWidth = right - left;
        var boxHeight = bottom - top;
        if (boxWidth <= 0 || boxHeight <= 0) return false;

        if (!EnsureStagingLocked(boxWidth, boxHeight)) return false;

        // Rent, do not allocate: at the launcher's sampling cadence this runs tens of times a second
        // and a fresh patch is 4.7 MB of native pixels the GC cannot see (see PatchBitmapPool).
        var bitmap = PatchBitmapPool.Rent(boxWidth, boxHeight);
        if (bitmap.GetPixels() == IntPtr.Zero)
        {
            bitmap.Dispose();
            return false;
        }

        var context = sharedDevice.ImmediateContext;
        var box = new Box(left, top, 0, right, bottom, 1);

        lock (ContextGate)
        {
            try
            {
                context.CopySubresourceRegion(staging!, 0, 0, 0, 0, heldTexture, 0, box);
                var mapped = context.Map(staging!, 0, MapMode.Read);
                try
                {
                    unsafe
                    {
                        var source = (byte*)mapped.DataPointer;
                        var destination = (byte*)bitmap.GetPixels();
                        var rowBytes = (long)(boxWidth * 4);
                        for (var y = 0; y < boxHeight; y++)
                            Buffer.MemoryCopy(source + y * mapped.RowPitch, destination + y * bitmap.RowBytes,
                                bitmap.RowBytes, rowBytes);
                    }
                }
                finally { context.Unmap(staging!, 0); }
            }
            catch (Exception ex)
            {
                bitmap.Dispose();
                GlassDiagnostics.Event($"[glass-capture] readback failed — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        // A recycled buffer carries the previous frame's pixels, and the generation id Skia keys its
        // caches on has to move with them. (Deliberately NOT SetImmutable: an immutable bitmap can
        // never be written again, and every buffer here is written on rent. The patch is only ever
        // drawn into the backdrop canvas — it is never a shader source — so nothing depends on the
        // immutable-content fast path.)
        try { bitmap.NotifyPixelsChanged(); } catch { }

        var previous = latest;
        latest = bitmap;
        latestWidth = boxWidth;
        latestHeight = boxHeight;

        // The previous crop was rented too: hand it back so the next pull reuses those pixels
        // instead of allocating another patch-sized block.
        if (previous != null) PatchBitmapPool.Return(previous);

        regionDirty = false;
        return true;
    }

    private bool EnsureStagingLocked(int width, int height)
    {
        if (staging != null && stagingWidth == width && stagingHeight == height) return true;
        staging?.Dispose();
        staging = null;
        stagingWidth = stagingHeight = 0;
        if (sharedDevice == null) return false;

        var description = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            BindFlags = BindFlags.None
        };

        try
        {
            staging = sharedDevice.CreateTexture2D(description);
            stagingWidth = width;
            stagingHeight = height;
            return true;
        }
        catch (Exception ex)
        {
            GlassDiagnostics.Event($"[glass-capture] staging texture {width}x{height} failed — {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ teardown

    public void Stop()
    {
        lock (gate)
        {
            TearDownLocked();
            monitor = IntPtr.Zero;
            window = IntPtr.Zero;
        }
    }

    private void TearDownLocked()
    {
        if (session != IntPtr.Zero)
        {
            WinRtInterop.Close(session);
            Marshal.Release(session);
            session = IntPtr.Zero;
        }
        if (pool != IntPtr.Zero)
        {
            // Close before releasing: the pool holds the frame buffers, and the C++ original closes
            // it explicitly for the same reason.
            WinRtInterop.Close(pool);
            Marshal.Release(pool);
            pool = IntPtr.Zero;
        }

        heldTexture?.Dispose();
        heldTexture = null;
        heldAt = 0;

        // The held frame is gone, so a cached crop describes a session that no longer exists.
        var previous = latest;
        latest = null;
        latestWidth = latestHeight = 0;
        if (previous != null) PatchBitmapPool.Return(previous);
        regionDirty = haveRegion;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        lock (gate)
        {
            staging?.Dispose();
            staging = null;
            if (latest != null) PatchBitmapPool.Return(latest);
            latest = null;
        }
    }
}
