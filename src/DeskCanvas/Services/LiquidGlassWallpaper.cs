using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Win32;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Views.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// Resolves the wallpaper the glass samples. Primary source: a 1:1 capture of the
/// real desktop wallpaper — including animated wallpapers, which do not live in
/// Progman (see <see cref="DesktopCapturer"/>) — so the sampled background is
/// pixel-identical to what is actually behind the widget. The static-file path
/// (Windows wallpaper image + registry placement) remains as a fallback when no
/// capture is available.
/// </summary>
public static class LiquidGlassWallpaper
{
    /// <summary>
    /// Lower bound of the live sampling rate — effectively "as fast as possible".
    /// </summary>
    public const int MinIntervalMs = LiquidGlassSettings.MinLiveSamplingInterval;

    /// <summary>Upper bound of the live sampling rate (1 fps).</summary>
    public const int MaxIntervalMs = LiquidGlassSettings.MaxLiveSamplingInterval;

    /// <summary>Default live sampling interval (10 fps).</summary>
    public const int DefaultIntervalMs = LiquidGlassSettings.DefaultLiveSamplingInterval;

    private static readonly object Gate = new();
    private static WallpaperSnapshot? cached;
    private static string? cachedKey;

    // The last good live frame, kept alive so a failed or flat capture can be masked with it
    // instead of flashing every glass card to one flat colour. Reference counted: handing it to
    // a render only lends it, so dropping the field later cannot pull pixels from under a draw.
    private static WallpaperSnapshot? stale;

    // When the current run of unusable captures (failed or flat) began; reset by the next good one.
    private static long firstUnusableAt;

    /// <summary>How long a run of unusable captures is masked with the previous frame before the normal fallback chain takes over.</summary>
    private const int UnusableGraceMs = 250;

    /// <summary>
    /// Resolution of the identity grid the probe compares captures on. Two cells per 80 desktop
    /// pixels: any change a viewer could see through the glass spans many cells, while a change
    /// strictly inside one cell is under 0.3% of the desktop and dissolves in the backdrop blur.
    /// </summary>
    private const int IdentityColumns = 32;

    /// <summary>The row count of the identity grid; see <see cref="IdentityColumns"/>.</summary>
    private const int IdentityRows = 18;

    /// <summary>
    /// Per-channel slack of the identity comparison, in 255ths. Absorbs the sub-perceptual
    /// re-encode noise of a "paused" video wallpaper; any real animation moves cells further
    /// than this and publishes.
    /// </summary>
    private const int IdentityTolerance = 2;

    /// <summary>
    /// Longest the sampler stretches between probes while the desktop stays unchanged. The
    /// stretch exists so an idle desktop costs a few captures per second instead of the
    /// pipeline's maximum rate; it does not throttle a changing one — the first probe that
    /// finds new content snaps the interval back to the user's setting.
    /// </summary>
    private const int MaxIdleIntervalMs = 500;

    // Coarse grid of the last accepted capture, and the host it came from. The grid is the
    // reference for the temporal check (a frame that replaced nearly everything at once is an
    // MPO overlay transition, not wallpaper content); the host handle lets a genuinely NEW host
    // (a wallpaper engine restarting with different content) skip the check entirely.
    private static int[]? lastAcceptedSamples;
    private static IntPtr lastAcceptedHost;

    // The finer identity grid of the frame that is actually published, under its own gate: the
    // probe runs on a worker outside the wallpaper Gate while CaptureOnce (under the Gate)
    // touches the same fields. The reference the identity check compares against must move
    // exactly when a new frame is published, never in between — comparing against a grid that
    // was updated without its frame being published would silently freeze the glass.
    private static readonly object SampleGate = new();
    private static int[]? lastPublishedSamples;

    // The probe's reusable capture buffer. A probe that finds the desktop unchanged hands the
    // bitmap straight back: the steady state — a static desktop probed over and over — then
    // allocates nothing, instead of feeding the large-object-heap a full desktop bitmap per
    // round. Only a published frame consumes the buffer (it becomes the snapshot's pixels), so
    // a buffer is never both pooled and reachable by a render.
    private static SKBitmap? probeBuffer;

    // Single-flight flag for the probe: the sampler tick is async and the timer fires again
    // while a capture is still running.
    private static int probing;

    // Consecutive probes that found the desktop unchanged; drives the idle backoff.
    private static int idleRounds;

    private static bool liveSamplingEnabled = true;
    private static int liveIntervalMs = DefaultIntervalMs;
    private static bool suspended;
    private static DispatcherTimer? liveSamplingTimer;

    /// <summary>
    /// Turn continuous desktop sampling on or off. Switching it off is the static-wallpaper
    /// mode: the last captured frame is frozen (a single capture still runs if nothing has
    /// been sampled yet, so the glass never falls back to a stale wallpaper file).
    /// </summary>
    public static void ConfigureLiveSampling(bool enabled, int intervalMs)
    {
        liveSamplingEnabled = enabled;
        liveIntervalMs = Math.Clamp(intervalMs, MinIntervalMs, MaxIntervalMs);
        Volatile.Write(ref idleRounds, 0);

        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var timer = EnsureTimer();
            timer.Interval = TimeSpan.FromMilliseconds(liveIntervalMs);
            UpdateTimerState();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Live glass sampling unavailable: {ex.Message}");
        }
    }

    /// <summary>
    /// Stop asking for new frames — every screen is covered by a fullscreen application and
    /// nobody can see the glass. Unrelated to the user's 实时采样 switch.
    /// </summary>
    public static void SuspendLiveSampling()
    {
        suspended = true;
        UpdateTimerState();
    }

    /// <summary>Resume asking for frames after a fullscreen application went away.</summary>
    public static void ResumeLiveSampling()
    {
        suspended = false;
        UpdateTimerState();
    }

    /// <summary>True while the desktop is covered by a fullscreen/maximized application (SuspendLiveSampling).</summary>
    public static bool IsSuspended => suspended;

    /// <summary>True when the user's 实时采样 switch is on.</summary>
    public static bool LiveSamplingEnabled => liveSamplingEnabled;

    /// <summary>True when the sampler is actually ticking (switch on, glass on screen, not covered).</summary>
    public static bool LiveSamplingActive =>
        liveSamplingEnabled && !suspended && liveSamplingTimer is { IsEnabled: true };

    /// <summary>The effective sampling interval in milliseconds.</summary>
    public static int LiveSamplingIntervalMs => liveIntervalMs;

    /// <summary>
    /// Re-evaluate whether the sampler should be ticking. Called when glass surfaces attach or
    /// detach — the sampler is pointless while no widget is on screen, and starting it lazily
    /// also means a theme switch made before any widget existed still begins sampling later.
    /// </summary>
    internal static void RefreshSamplerState() => UpdateTimerState();

    private static DispatcherTimer EnsureTimer()
    {
        if (liveSamplingTimer != null) return liveSamplingTimer;
        liveSamplingTimer = new DispatcherTimer();
        liveSamplingTimer.Tick += OnLiveSamplingTick;
        return liveSamplingTimer;
    }

    private static void UpdateTimerState()
    {
        // Live sampling moved to <see cref="ScreenCaptureService"/>: that one grabs the composited
        // screen (which, unlike the wallpaper host this class captures, contains the application
        // windows actually behind the glass), once per monitor, and shares the frame with every
        // consumer — desktop widgets and the sidebar alike. This class now owns only the
        // *fallback*: a lazily cached capture of the wallpaper host, or the wallpaper file when
        // even that is unavailable.
        //
        // Its own sampler must therefore never tick. Leaving both running is precisely the double
        // cost the merge removed: two full-size captures per interval, two blurred backdrops, two
        // published frames for the same pixels. The timer object still exists (ConfigureLiveSampling
        // creates it and the settings page reads the interval back), but it is never started.
        liveSamplingTimer?.Stop();
    }

    /// <summary>
    /// One sampling tick: capture one probe frame off the UI thread and refresh the glass surfaces
    /// only when its pixels actually differ from what they are already showing.
    /// <para>
    /// The glass is a pure function of the captured frame, the widget geometry and the optics —
    /// republishing an identical frame re-runs the whole pipeline (a full desktop capture, a
    /// full-desktop blur, one prepare per card, a GPU draw each) for zero visual change, and at a
    /// 5 ms interval that is what the tick used to demand unconditionally: ~20 captures and ~180
    /// card publishes per second on a desktop that had not moved a pixel. The probe inverts the
    /// cost: an unchanged frame is the cheapest outcome (the capture buffer is reused, nothing is
    /// republished, and consecutive idle rounds stretch the period out to
    /// <see cref="MaxIdleIntervalMs"/>), while the first changed frame publishes exactly as before
    /// and snaps the interval back. A static wallpaper therefore idles at a few captures per
    /// second and an animated one runs at the full pipeline rate, identical visuals either way.
    /// </para>
    /// <para>
    /// The capture does not run on the UI thread — the tick awaits it on a worker, single-flighted
    /// through <see cref="probing"/> so a slow capture cannot stack ticks.
    /// </para>
    /// <para>
    /// The staleness guard is the safety net: if nothing has been captured for a long time while
    /// widgets are still asking, the tick forces a round anyway, so a surface that somehow never
    /// reports itself idle cannot stall live sampling permanently.
    /// </para>
    /// </summary>
    private static async void OnLiveSamplingTick(object? sender, EventArgs e)
    {
        if (!liveSamplingEnabled || suspended) { UpdateTimerState(); return; }
        if (!LiquidGlassSurface.HasActiveSurfaces) { UpdateTimerState(); return; }

        var now = Environment.TickCount64;
        // The period is measured from the start of the previous round, not from its capture. A
        // capture lands a few milliseconds after the round begins, so measuring from it made every
        // other tick arrive "too early" and halved the rate — 30 publishes/s instead of 60 at a
        // 100 ms interval. While the desktop sits still the period is the backoff-stretched one.
        if (now - Interlocked.Read(ref lastRoundAt) < EffectiveIntervalMs) return;
        if (LiquidGlassSurface.AnySurfaceRendering && now - Interlocked.Read(ref lastCaptureAt) < StallGuardMs)
            return;

        Interlocked.Exchange(ref lastRoundAt, now);
        if (Interlocked.Exchange(ref probing, 1) == 1) return;
        try
        {
            var published = await Task.Run(ProbeCapture);
            if (published)
            {
                Volatile.Write(ref idleRounds, 0);
                // Immediate: the interval can be shorter than the surface's move/resize debounce,
                // and a debounce restarted on every tick would never elapse — freezing the glass.
                LiquidGlassSurface.RefreshAllImmediate();
            }
            else
            {
                Interlocked.Increment(ref idleRounds);
            }
        }
        catch (Exception ex)
        {
            // A failing capture must not kill the timer's async tick; the next one retries.
            GlassDiagnostics.Failure(ex);
        }
        finally
        {
            Volatile.Write(ref probing, 0);
        }
    }

    /// <summary>The period the sampler actually waits for: the configured interval, stretched while the desktop stays unchanged.</summary>
    private static int EffectiveIntervalMs
    {
        get
        {
            var idle = Math.Min(Volatile.Read(ref idleRounds), 6);
            var stretched = liveIntervalMs << idle;
            return Math.Min(Math.Max(stretched, liveIntervalMs), Math.Max(liveIntervalMs, MaxIdleIntervalMs));
        }
    }

    /// <summary>When the desktop was last captured; the stall guard is measured from here.</summary>
    private static long lastCaptureAt;

    /// <summary>When the current sampling round began; the interval is measured from here.</summary>
    private static long lastRoundAt;

    /// <summary>Captures taken since the process started, for the rate diagnostic.</summary>
    private static int captureCount;

    /// <summary>Total desktop captures; the achieved sharing is the publish rate divided by this.</summary>
    internal static int CaptureCount => Volatile.Read(ref captureCount);

    /// <summary>Longest the sampler may go without capturing while widgets are still asking.</summary>
    private const int StallGuardMs = 1000;

    /// <summary>Raised whenever the wallpaper is invalidated (system wallpaper change, display change, or manual refresh).</summary>
    public static event Action? WallpaperInvalidated;

    public static WallpaperSnapshot Get()
    {
        // The cache owns one reference and the caller gets another; the caller releases it with
        // WallpaperSnapshot.Dispose. Nothing here is kept alive by a timer — a capture is a full
        // desktop bitmap and live sampling replaces it ten times a second.
        // The AddRef happens under the Gate on purpose: Invalidate / DropStale run on other
        // threads and free the snapshot the moment the cache lets go, so between GetShared's
        // return and an AddRef here a concurrent swap could dispose the pixels under us.
        lock (Gate)
        {
            var snapshot = GetShared();
            snapshot.AddRef();
            return snapshot;
        }
    }

    private static WallpaperSnapshot GetShared()
    {
        lock (Gate)
        {
            if (!OperatingSystem.IsWindows())
                return new WallpaperSnapshot(null, new SKColor(32, 38, 48));
            try
            {
                // Fullscreen suspension: the desktop capture was just released to hand its
                // memory to the covering application, and nothing that renders while covered
                // can be seen. Do NOT re-capture on demand — a resize, a theme flip or a
                // wallpaper event during a game would otherwise silently rebuild exactly what
                // SuspendAll released. Hand back whatever frame is still cached, or a flat
                // placeholder that degrades the glass to its cheap fallback; live sampling
                // restores a real frame on resume.
                if (suspended)
                {
                    if (cached is { LiveCapture: true }) return cached;
                    if (stale != null) return stale;
                    return new WallpaperSnapshot(null, new SKColor(32, 38, 48));
                }

                // Live sampling on: reuse the frame while it is fresh (so every widget samples
                // the same instant), otherwise grab a new one. Live sampling off: the frozen
                // frame wins; a single one-shot capture runs only if there is nothing to freeze.
                if (liveSamplingEnabled && CaptureOnce() is { } live && live.LiveCapture)
                    return live;

                if (!liveSamplingEnabled)
                {
                    if (cached is { LiveCapture: true }) return cached;
                    if (CaptureOnce() is { } once) return once;
                }

                // Live sampling with a persistently unreadable host (a long MPO overlay
                // transition, a wallpaper engine restarting): the frozen last frame is the
                // wallpaper the user actually has — the static file is whatever image was last
                // set through Windows, a different picture entirely. Freezing beats swapping
                // to it.
                if (liveSamplingEnabled && stale != null)
                    return stale;

                return FromFileFallback();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return cached ?? new WallpaperSnapshot(null, new SKColor(32, 38, 48));
            }
        }
    }

    /// <summary>Force a fresh capture (e.g. the user pressed 刷新壁纸 or the wallpaper changed).</summary>
    public static void Invalidate()
    {
        WallpaperSnapshot? previousStale;
        lock (Gate)
        {
            previousStale = stale;
            // Keep the last good frame as the mask for a run of unusable captures: the very next
            // capture is the one most likely to come back flat (a wallpaper change is exactly
            // when hosts re-composite), and masking it avoids a one-round flat flash.
            stale = cached;
            cached = null;
            cachedKey = null;
        }

        // A real invalidation means content is changing (or about to): the probe must not idle
        // through it at the backoff rate.
        Volatile.Write(ref idleRounds, 0);

        Interlocked.Exchange(ref lastRoundAt, Environment.TickCount64);
        previousStale?.Dispose();

        NotifyInvalidated();
    }

    /// <summary>
    /// A capture taken <b>now</b>, bypassing the cache.
    /// <para>
    /// The screen pipeline owns live sampling, so nothing refreshes this cache on a timer any more —
    /// and the widget footprints it patches are exactly what those widgets' glass samples, which
    /// means a cached frame would freeze the glass over an animated wallpaper. Only
    /// <see cref="ScreenCaptureService"/> calls this, at its own sampling cadence.
    /// </para>
    /// <para>
    /// The outgoing frame is parked in <c>stale</c> rather than dropped, on purpose: when the
    /// wallpaper has not actually moved, <see cref="CaptureOnce"/>'s identity check hands that same
    /// frame back instead of allocating a fresh full-desktop bitmap every round. And deliberately no
    /// <see cref="NotifyInvalidated"/> on the way in — the glass surfaces do not read this frame (they
    /// read the screen frame), so there is nothing to invalidate, and a notify per sampling round
    /// would make the frameless clock re-render its glyphs for nothing.
    /// </para>
    /// </summary>
    internal static WallpaperSnapshot? Recapture()
    {
        WallpaperSnapshot? previousStale;
        lock (Gate)
        {
            previousStale = stale;
            stale = cached;
            cached = null;
            cachedKey = null;
        }

        previousStale?.Dispose();
        return Get();
    }

    /// <summary>
    /// Drop the cached desktop capture and its decoded bitmap.
    /// <para>
    /// Called while every attached screen is covered by a fullscreen application: the capture is
    /// a full virtual desktop at physical resolution — the single largest allocation in the
    /// process (tens of megabytes with several monitors) — and nothing can be looking at glass
    /// right then. The next <see cref="Get"/> re-captures on demand. Releasing here only drops the
    /// cache's own reference: a render that is still holding one keeps the pixels valid until it
    /// lets go.
    /// </para>
    /// </summary>
    public static void Release()
    {
        WallpaperSnapshot? old;
        WallpaperSnapshot? oldStale;
        lock (Gate)
        {
            old = cached;
            cached = null;
            cachedKey = null;
            oldStale = stale;
            stale = null;
        }

        // The probe buffer is one more full-desktop allocation nothing can see right now.
        Interlocked.Exchange(ref probeBuffer, null)?.Dispose();

        old?.Dispose();
        oldStale?.Dispose();
    }

    /// <summary>
    /// A newer live frame was published by <see cref="ScreenCaptureService"/>. Raises the same
    /// notification <see cref="Invalidate"/> does — the frameless clock's glyph glass listens for it
    /// to re-render against the newer frame — but drops nothing from this cache: the screen pipeline
    /// owns the live pixels now, and what is cached here is only the fallback.
    /// </summary>
    internal static void NotifyLiveFramePublished() => NotifyInvalidated();

    private static void NotifyInvalidated()
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) WallpaperInvalidated?.Invoke();
            else Dispatcher.UIThread.Post(() => WallpaperInvalidated?.Invoke());
        }
        catch
        {
            try { WallpaperInvalidated?.Invoke(); } catch { }
        }
    }

    private static WallpaperSnapshot? CaptureOnce()
    {
        // A captured frame stays valid until something explicitly invalidates it — the live sampler
        // on its next tick, a wallpaper change, a display change or a fullscreen transition. It is
        // deliberately NOT expired on a timer: keying the life of the frame to the sampling interval
        // meant that at a small interval the widgets stopped sharing one capture (and one shared
        // backdrop build) and each paid for its own, which is exactly backwards.
        if (cached is { LiveCapture: true }) return cached;

        // Two attempts: a wallpaper host can vanish mid-transition (monitor hot-plug, a
        // wallpaper engine restarting) and re-resolving usually succeeds on the second try.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var bmp = DesktopCapturer.CaptureBitmap(out var flat);
            var jumped = false;

            if (bmp != null && !flat)
            {
                var identity = DesktopCapturer.SampleGrid(bmp, IdentityColumns, IdentityRows);
                if (MatchesLastPublished(identity) && stale != null)
                {
                    // The desktop is pixel-identical to the frame the surfaces already render
                    // from (stale is exactly that frame — Invalidate parks the outgoing one
                    // there). Publishing would rebuild every card for zero visual change, and
                    // returning nothing would drop the caller to the static-file fallback —
                    // different pixels entirely. Hand back the identical frame.
                    bmp.Dispose();
                    firstUnusableAt = 0;
                    return stale;
                }

                var samples = DesktopCapturer.SampleGrid(bmp);
                if (lastAcceptedSamples != null
                    && lastAcceptedHost == DesktopCapturer.ResolvedHost
                    && DesktopCapturer.IsWildJump(samples, lastAcceptedSamples))
                {
                    // Almost nothing this frame shows matches the last accepted frame: an MPO
                    // overlay transition handed us the bare redirection surface instead of the
                    // wallpaper. Publish it and every glass card blinks to a solid colour.
                    bmp.Dispose();
                    bmp = null;
                    jumped = true;
                }
                else
                {
                    lock (SampleGate)
                    {
                        lastAcceptedSamples = samples;
                        lastPublishedSamples = identity;
                    }
                }
            }
            else if (bmp != null && flat)
            {
                bmp.Dispose();
                bmp = null;
            }

            if (bmp == null)
            {
                // Nothing usable this attempt. Mask with the last good frame for a short grace
                // instead of flashing the glass to a flat card / the static file for a round —
                // the usual cause clears within a few sampling rounds. Once the failure outlives
                // the grace, the normal fallback chain takes over; a persistently unusable host
                // is also forgiven so it is re-resolved with full validation instead of trusted.
                NoteUnusableCapture(flat, jumped);
                var now = Environment.TickCount64;
                if (now - firstUnusableAt < UnusableGraceMs && stale != null)
                    return stale;
                continue;
            }

            firstUnusableAt = 0;
            DropStale();
            lastAcceptedHost = DesktopCapturer.ResolvedHost;

            var snapshot = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), bmp, live: true);
            WallpaperSnapshot? old;
            lock (Gate)
            {
                old = cached;
                cached = snapshot;
                cachedKey = null;
            }

            // Frame coherence: every surface asked for a frame from now on samples this one, so a
            // round costs one desktop grab and one backdrop build however many widgets are on screen.
            Interlocked.Exchange(ref lastCaptureAt, Environment.TickCount64);
            Interlocked.Increment(ref captureCount);
            // Only the cache's reference goes; a render still using the previous frame keeps it.
            old?.Dispose();
            return snapshot;
        }
        return null;
    }

    /// <summary>
    /// One live-sampling probe: capture the wallpaper host and publish the frame only when its
    /// pixels differ from the one every glass surface is already showing. Returns true when a new
    /// frame was published (the caller refreshes the surfaces); false when the desktop is
    /// unchanged (nothing to refresh — the capture buffer is parked for the next probe) or the
    /// capture was unusable (masked by the previous frame as before).
    /// <para>
    /// Runs on a worker outside the wallpaper Gate and single-flighted by the tick; the only
    /// shared mutable state it touches is swap-under-lock (the published snapshot, the sample
    /// grids) and the atomics the rest of the class already uses.
    /// </para>
    /// </summary>
    private static bool ProbeCapture()
    {
        // The enabled/suspended state is re-checked here: the tick queued this probe a moment ago
        // and a fullscreen transition may have landed in between.
        if (!liveSamplingEnabled || suspended) return false;

        var pooled = Interlocked.Exchange(ref probeBuffer, null);
        SKBitmap? bmp = null;
        var published = false;
        try
        {
            bmp = DesktopCapturer.CaptureBitmap(out var flat, pooled);
            if (bmp != null && !ReferenceEquals(bmp, pooled))
            {
                // A fresh bitmap came back, so the pooled one no longer matches the desktop size.
                pooled?.Dispose();
            }

            if (bmp == null)
            {
                // The capture failed but nothing consumed the pooled buffer: park it for the next
                // probe (the size is still the desktop's).
                Volatile.Write(ref probeBuffer, pooled);
                NoteUnusableCapture(flat: false, jumped: false);
                return false;
            }
            if (flat)
            {
                // A flat frame is worthless as wallpaper and the pooled buffer with it — the next
                // probe allocates a fresh one.
                bmp.Dispose();
                NoteUnusableCapture(flat: true, jumped: false);
                return false;
            }

            var identity = DesktopCapturer.SampleGrid(bmp, IdentityColumns, IdentityRows);
            if (MatchesLastPublished(identity))
            {
                // Pixel-identical to the published frame: publish nothing, and keep the capture
                // as the next probe's buffer so an idle desktop costs no allocation at all.
                firstUnusableAt = 0;
                Volatile.Write(ref probeBuffer, bmp);
                return false;
            }

            var samples = DesktopCapturer.SampleGrid(bmp);
            bool jumped;
            lock (SampleGate)
            {
                jumped = lastAcceptedSamples != null
                         && lastAcceptedHost == DesktopCapturer.ResolvedHost
                         && DesktopCapturer.IsWildJump(samples, lastAcceptedSamples);
            }
            if (jumped)
            {
                bmp.Dispose();
                NoteUnusableCapture(flat: false, jumped: true);
                return false;
            }

            // Genuinely new content: publish it exactly as CaptureOnce would.
            var snapshot = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), bmp, live: true);
            WallpaperSnapshot? old;
            lock (Gate)
            {
                old = cached;
                cached = snapshot;
                cachedKey = null;
                DropStale();
            }
            lock (SampleGate)
            {
                lastAcceptedSamples = samples;
                lastPublishedSamples = identity;
                lastAcceptedHost = DesktopCapturer.ResolvedHost;
            }
            firstUnusableAt = 0;
            Interlocked.Exchange(ref lastCaptureAt, Environment.TickCount64);
            Interlocked.Increment(ref captureCount);
            // Only the cache's reference goes; a render still using the previous frame keeps it.
            old?.Dispose();
            // The glyph glass (the clock) and the popups re-render against the newer frame; the
            // live tick used to raise this every round through Invalidate, so this keeps their
            // contract while only real change raises it now.
            NotifyInvalidated();
            // The buffer's pixels now belong to the snapshot — the next probe captures fresh.
            published = true;
            return true;
        }
        catch
        {
            // Ownership by stage: nothing was published → the capture bitmap (pooled or fresh) is
            // still ours; once published the snapshot owns the pixels and the buffer is gone.
            if (!published) bmp?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Shared bookkeeping for a capture that produced nothing usable: the grace window, the
    /// diagnostics event and, once the failure persists, forgiving the remembered host so it is
    /// re-resolved with full validation. Reentrant on <see cref="Gate"/>: CaptureOnce calls this
    /// holding the wallpaper Gate, the probe does not.
    /// </summary>
    private static void NoteUnusableCapture(bool flat, bool jumped)
    {
        lock (Gate)
        {
            var now = Environment.TickCount64;
            if (firstUnusableAt == 0)
            {
                firstUnusableAt = now;
                GlassDiagnostics.Event(flat ? "wallpaper capture flat — masking with the previous frame"
                    : jumped ? "wallpaper capture jumped (overlay transition?) — masking with the previous frame"
                    : "wallpaper capture failed — masking with the previous frame");
            }
            else if ((flat || jumped) && now - firstUnusableAt >= UnusableGraceMs)
            {
                DesktopCapturer.ForgetResolvedHost();
                // Forgiving the host alone cannot unstick a genuinely new wallpaper: the temporal
                // check would keep comparing every incoming frame against the OLD scene's grid and
                // rejecting it as an overlay transition — a wallpaper switch IS a wild jump against
                // the scene it replaced, and unlike a transition it never settles back to the old
                // content. Retire the acceptance reference too, so the next capture validates
                // against nothing and publishes; the grids re-arm from the current scene then.
                // lastPublishedSamples stays: it still describes what the surfaces render.
                lock (SampleGate)
                {
                    lastAcceptedSamples = null;
                    lastAcceptedHost = IntPtr.Zero;
                }
                GlassDiagnostics.Event("unusable wallpaper capture persisted — re-resolving the wallpaper host");
            }
        }
    }

    /// <summary>
    /// True when <paramref name="samples"/> matches the identity grid of the frame that is already
    /// published — every cell within <see cref="IdentityTolerance"/> per channel. A match means
    /// the desktop has not visibly moved since the surfaces last rendered: the glass they show is
    /// still exactly what a fresh render would produce.
    /// </summary>
    private static bool MatchesLastPublished(int[] samples)
    {
        lock (SampleGate)
        {
            var previous = lastPublishedSamples;
            if (previous == null || previous.Length != samples.Length) return false;
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
    }

    /// <summary>Drop the masking frame. Callers must hold <see cref="Gate"/>; in-flight renders keep their own references.</summary>
    private static void DropStale()
    {
        var old = stale;
        stale = null;
        old?.Dispose();
    }

    /// <summary>Static file based fallback (Windows wallpaper image + registry placement).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static WallpaperSnapshot FromFileFallback()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Themes\TranscodedWallpaper");
        if (!File.Exists(path)) path = InteropService.GetWallpaperPath();
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        using var colors = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
        var style = desktop?.GetValue("WallpaperStyle") as string ?? "10";
        var tile = desktop?.GetValue("TileWallpaper") as string == "1";
        var rgb = (colors?.GetValue("Background") as string ?? "32 38 48").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var background = rgb.Length == 3 && byte.TryParse(rgb[0], out var r) && byte.TryParse(rgb[1], out var g) && byte.TryParse(rgb[2], out var b)
            ? new SKColor(r, g, b) : new SKColor(32, 38, 48);
        var exists = File.Exists(path);
        var key = $"{path}|{(exists ? File.GetLastWriteTimeUtc(path).Ticks : 0)}|{style}|{tile}|{background}";
        if (cachedKey == key && cached is { LiveCapture: false }) return cached;
        var bytes = exists ? File.ReadAllBytes(path) : null;
        SKBitmap? bmp = null;
        if (bytes != null)
        {
            try
            {
                bmp = SKBitmap.Decode(bytes);
                bmp?.SetImmutable();
            }
            catch { }
        }
        var old = cached;
        cached = WallpaperSnapshot.FromBitmap(bytes, background, bmp, style, tile);
        cachedKey = key;
        old?.Dispose();
        // The static file is the source of truth now, so the last live frame no longer masks
        // anything — release it.
        DropStale();
        return cached;
    }
}

/// <summary>
/// Captures the live desktop wallpaper — exactly what the user sees behind the widgets —
/// including animated wallpapers.
/// <para>
/// A static wallpaper is painted by <c>Progman</c>, so <c>PrintWindow(Progman)</c> is enough.
/// Every wallpaper <i>engine</i> instead reparents its own render window into a <c>WorkerW</c>
/// that sits between the desktop icons and the wallpaper: Wallpaper Engine in its default
/// "desktop" mode does this, and its frames never reach Progman at all. The host is located the
/// documented way — the wallpaper <c>WorkerW</c> is the top-level <c>WorkerW</c> that follows the
/// one owning <c>SHELLDLL_DefView</c> — and the first candidate that yields a plausible
/// wallpaper is remembered, so the source only has to be resolved once.
/// </para>
/// </summary>
public static class DesktopCapturer
{
    private const uint PwRenderFullContent = 0x00000002;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

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
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines,
        IntPtr bits, ref BitmapInfo info, uint usage);

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extraData);

    // SM_* virtual desktop metrics (physical pixels; the process is DPI aware).
    private const int SmXVirtualScreen = 76, SmYVirtualScreen = 77, SmCXVirtualScreen = 78, SmCYVirtualScreen = 79;

    private static readonly object HostGate = new();
    private static IntPtr resolvedHost;
    private static string resolvedHostDescription = "unresolved";

    /// <summary>Which window the current capture comes from — surfaced for diagnostics.</summary>
    public static string CaptureSourceDescription
    {
        get { lock (HostGate) return resolvedHostDescription; }
    }

    public static SKBitmap? CaptureBitmap() => CaptureBitmap(out _);

    /// <summary>
    /// Capture the resolved wallpaper host, or <c>null</c> when nothing usable was captured.
    /// <paramref name="flat"/> is true when a frame came back but is one uniform colour — the
    /// transiently empty surface <c>PrintWindow</c> hands out while the host re-composites. The
    /// caller masks it with the previous frame instead of re-resolving here: falling through to
    /// the next source on every flat frame would flip a genuinely dark animated scene back to
    /// Progman, a visible jump the fast path exists to avoid.
    /// </summary>
    /// <param name="reuse">
    /// Optional bitmap to capture into instead of allocating a new one; it must already be the
    /// virtual-screen size (verified below) and is never disposed here. The identity probe uses
    /// this so its steady state — the same unchanged desktop captured over and over — costs no
    /// allocation. A successful capture hands the buffer to the caller; a failure leaves it
    /// untouched for the next attempt.
    /// </param>
    public static SKBitmap? CaptureBitmap(out bool flat, SKBitmap? reuse = null)
    {
        flat = false;
        var width = GetSystemMetrics(SmCXVirtualScreen);
        var height = GetSystemMetrics(SmCYVirtualScreen);
        if (width <= 0 || height <= 0) return null;

        // Fast path: the host resolved last time. This runs once per sampled frame, so it must not
        // walk every top-level window to rediscover a window that has not changed. The frame gets
        // one cheap validation — a coarse grid sample, well under a millisecond: a flat frame
        // published as real wallpaper paints every glass card as one solid colour until the next
        // round, which is the "widget suddenly blinks to a blank card" flicker.
        IntPtr remembered;
        lock (HostGate) remembered = resolvedHost;
        if (remembered != IntPtr.Zero && IsWindow(remembered))
        {
            var fast = CaptureWindow(remembered, width, height, reuse);
            if (fast != null)
            {
                if (!LooksLikeWallpaper(fast))
                {
                    flat = true;
                    return fast;
                }
                fast.SetImmutable();
                return fast;
            }
        }

        // Slow path: first frame, or the remembered host died (a wallpaper engine restarting, a
        // monitor hot-plug, a shell restart). Re-resolve from scratch.
        foreach (var (hwnd, description) in ResolveCandidates())
        {
            var bmp = CaptureWindow(hwnd, width, height, reuse);
            if (bmp == null) continue;
            if (!LooksLikeWallpaper(bmp))
            {
                bmp.Dispose();
                continue;
            }

            lock (HostGate)
            {
                resolvedHost = hwnd;
                resolvedHostDescription = description;
            }
            bmp.SetImmutable();
            return bmp;
        }
        return null;
    }

    /// <summary>
    /// Drop the remembered host so the next capture re-resolves from scratch. Called when the
    /// remembered window keeps printing flat frames: it either died without its handle going
    /// invalid or a genuinely uniform wallpaper took over, and in both cases trusting it again
    /// needs full validation.
    /// </summary>
    public static void ForgetResolvedHost()
    {
        lock (HostGate)
        {
            resolvedHost = IntPtr.Zero;
            resolvedHostDescription = "forgiven after persistent unusable captures";
        }
    }

    /// <summary>The host the current capture comes from, for cross-checking capture coherence.</summary>
    public static IntPtr ResolvedHost
    {
        get { lock (HostGate) return resolvedHost; }
    }

    /// <summary>
    /// Sample a coarse grid of the bitmap as RGB ints, as the validity check uses. Cheap enough to
    /// run once per sampled frame; the identity probe samples a finer grid with explicit
    /// <paramref name="columns"/> / <paramref name="rows"/>.
    /// </summary>
    public static int[] SampleGrid(SKBitmap bmp, int columns = 16, int rows = 9)
    {
        var stepX = Math.Max(1, bmp.Width / columns);
        var stepY = Math.Max(1, bmp.Height / rows);
        var cols = Math.Max(1, (bmp.Width + stepX - 1) / stepX);
        var gridRows = Math.Max(1, (bmp.Height + stepY - 1) / stepY);
        var samples = new int[gridRows * cols];
        var index = 0;
        for (var y = stepY / 2; y < bmp.Height; y += stepY)
        for (var x = stepX / 2; x < bmp.Width; x += stepX)
        {
            var c = bmp.GetPixel(x, y);
            samples[index++] = (c.Red << 16) | (c.Green << 8) | c.Blue;
        }
        return samples;
    }

    /// <summary>
    /// True when a frame replaced nearly everything the last accepted one showed. Real wallpaper
    /// content never does that between two rounds; what does is the MPO overlay transition —
    /// while the wallpaper's hardware plane is promoted or demoted, PrintWindow reads the bare
    /// redirection surface: the desktop colour, sometimes with slivers of real content. Such a
    /// frame is unusable no matter how non-uniform it is.
    /// </summary>
    public static bool IsWildJump(int[] samples, int[] previous)
    {
        if (samples.Length != previous.Length || samples.Length == 0) return false;
        var changed = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var a = samples[i];
            var b = previous[i];
            var dr = Math.Abs(((a >> 16) & 255) - ((b >> 16) & 255));
            var dg = Math.Abs(((a >> 8) & 255) - ((b >> 8) & 255));
            var db = Math.Abs((a & 255) - (b & 255));
            if ((dr + dg + db) / 3 >= 35) changed++;
        }
        return changed >= samples.Length * 0.85;
    }

    /// <summary>Encode a one-off capture as PNG (diagnostics only).</summary>
    public static byte[] Capture()
    {
        using var bmp = CaptureBitmap(out _);
        if (bmp == null) return [];
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Candidate windows in priority order: the animated wallpaper host, then the classic Progman
    /// wallpaper, then any visible child surface of the animated host (some engines render into a
    /// child of the WorkerW rather than into it). The already-remembered host is tried separately,
    /// before this runs, so a steady state never enumerates the window list at all.
    /// </summary>
    private static List<(IntPtr Hwnd, string Description)> ResolveCandidates()
    {
        var result = new List<(IntPtr, string)>();
        var seen = new HashSet<IntPtr>();

        void Add(IntPtr hwnd, string description)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !seen.Add(hwnd)) return;
            result.Add((hwnd, description));
        }

        var progman = FindWindow("Progman", null);
        var defViewHost = IntPtr.Zero;
        var workerViews = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            if (ClassNameOf(hwnd) != "WorkerW") return true;
            workerViews.Add(hwnd);
            if (defViewHost == IntPtr.Zero &&
                FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                defViewHost = hwnd;
            return true;
        }, IntPtr.Zero);

        // The wallpaper WorkerW is the top-level WorkerW that follows the icons' WorkerW. When
        // the desktop is static there is no such sibling and this contributes nothing.
        if (defViewHost != IntPtr.Zero)
        {
            var afterIcons = false;
            foreach (var worker in workerViews)
            {
                if (afterIcons) { Add(worker, "WorkerW (animated wallpaper host)"); AddChildren(worker, "WorkerW child"); }
                else if (worker == defViewHost) afterIcons = true;
            }
        }

        // A WorkerW parented to Progman also hosts wallpaper on some shell versions.
        foreach (var worker in workerViews)
            if (IsChildOf(progman, worker)) Add(worker, "WorkerW (Progman child)");

        Add(progman, "Progman");
        return result;

        void AddChildren(IntPtr parent, string label)
        {
            EnumChildWindows(parent, (child, _) =>
            {
                if (IsWindowVisible(child)) Add(child, label);
                return true;
            }, IntPtr.Zero);
        }
    }

    private static bool IsChildOf(IntPtr parent, IntPtr child)
    {
        var current = child;
        for (var depth = 0; depth < 32 && current != IntPtr.Zero; depth++)
        {
            var owner = GetParent(current);
            if (owner == parent) return true;
            if (owner == IntPtr.Zero) return false;
            current = owner;
        }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    private static string ClassNameOf(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static SKBitmap? CaptureWindow(IntPtr hwnd, int width, int height, SKBitmap? reuse = null)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;
        var memDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var previous = SelectObject(memDc, bitmap);
        try
        {
            if (!PrintWindow(hwnd, memDc, PwRenderFullContent)) return null;
            return ReadBitmap(memDc, bitmap, width, height, reuse);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Wallpaper capture failed for {ClassNameOf(hwnd)}: {ex.Message}");
            return null;
        }
        finally
        {
            SelectObject(memDc, previous);
            DeleteObject(bitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>A valid wallpaper capture has meaningful content: not uniform, not black.</summary>
    private static bool LooksLikeWallpaper(SKBitmap bmp)
    {
        var width = bmp.Width;
        var height = bmp.Height;
        var seen = new HashSet<uint>();
        var stepX = Math.Max(1, width / 16);
        var stepY = Math.Max(1, height / 9);

        for (var y = stepY / 2; y < height; y += stepY)
        for (var x = stepX / 2; x < width; x += stepX)
        {
            var color = bmp.GetPixel(x, y);
            seen.Add(((uint)color.Red << 16) | ((uint)color.Green << 8) | color.Blue);
        }
        if (seen.Count < 3) return false;

        double sum = 0;
        foreach (var c in seen)
        {
            var r = (c >> 16) & 0xFF; var g = (c >> 8) & 0xFF; var b = c & 0xFF;
            sum += (299 * r + 587 * g + 114 * b) / 1000.0;
        }
        return sum / seen.Count > 4; // not a pure-black capture
    }

    private static SKBitmap? ReadBitmap(IntPtr memDc, IntPtr bitmap, int width, int height, SKBitmap? reuse = null)
    {
        var info = CreateHeader(width, height);
        // Reuse is only honoured at the exact size the desktop reports — anything else decodes
        // into a fresh allocation, and the caller retires the mismatched buffer.
        SKBitmap? skBitmap = reuse is { Width: var rw, Height: var rh } && rw == width && rh == height
            ? reuse
            : new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        var pixels = skBitmap.GetPixels();
        if (pixels == IntPtr.Zero)
        {
            if (!ReferenceEquals(skBitmap, reuse)) skBitmap.Dispose();
            return null;
        }

        if (GetDIBits(memDc, bitmap, 0, (uint)height, pixels, ref info, 0) != height)
        {
            if (!ReferenceEquals(skBitmap, reuse)) skBitmap.Dispose();
            return null;
        }

        return skBitmap;
    }

    private static BitmapInfo CreateHeader(int width, int height) => new()
    {
        Header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down rows
            Planes = 1,
            BitCount = 32,
            Compression = 0 // BI_RGB
        }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }
}
