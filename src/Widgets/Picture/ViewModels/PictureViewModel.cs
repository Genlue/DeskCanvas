using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Picture.Models;
using Picture.Services;
using ReactiveUI;

namespace Picture.ViewModels;

/// <summary>
/// Drives the picture widget: slideshow timing, cross-fade transition, GIF playback and
/// bitmap lifetime management.
///
/// Bitmap ownership rules (this is what keeps the widget from crashing while switching):
/// <list type="bullet">
/// <item>every decoded picture lives in <see cref="pictureCache"/> while it is wanted;</item>
/// <item>a picture currently assigned to a rendering slot (<see cref="CurrentBitmap"/> /
/// <see cref="PreviousBitmap"/>) is <i>pinned</i> and can never be disposed while pinned —
/// otherwise the compositor renders a freed bitmap and the process dies (native crash);</item>
/// <item>a picture that loses its owner goes to the pending queue and is disposed only after a
/// short delay and only once nothing is rendering it, so an in-flight cross-fade / render pass
/// is never invalidated.</item>
/// </list>
/// Every decoded picture is tracked in <see cref="allPictures"/> and handed to the queue the
/// moment it has no owner any more (<see cref="SweepOrphans"/>). Ownership is <b>always</b>
/// recorded when given up — a picture the renderer still holds is queued and skipped by the
/// flushes until its slot lets go, rather than being dropped "to the GC": its pixel buffer is
/// full-size (megabytes) and waiting for a finalizer is what made a slideshow over a photo
/// folder hold on to one decoded picture per switch.
/// </summary>
public class PictureViewModel : ReactiveObject, IDisposable
{
    /// <summary>How many decoded pictures stay in RAM. 2 = current + one prefetch slot.</summary>
    private const int MaxCacheCount = 2;

    /// <summary>How long a released bitmap is kept alive before disposal (ms). Must outlive one
    /// render/cross-fade frame so the compositor never sees a freed bitmap.</summary>
    private const int DisposeDelayMs = 250;

    private PictureModel model;
    private readonly DispatcherTimer slideshowTimer;
    private readonly DispatcherTimer transitionTimer;
    private readonly DispatcherTimer gifAnimationTimer;
    private readonly DispatcherTimer disposeTimer;

    private readonly Dictionary<string, DecodedPicture> pictureCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Stable ledger owner index: keeps two picture widgets apart without an unbounded id.</summary>
    private static int instanceCounter;
    private readonly int instanceIndex = Interlocked.Increment(ref instanceCounter);

    /// <summary>Every decoded picture that has not been disposed yet (see the ownership rules).</summary>
    private readonly HashSet<DecodedPicture> allPictures = [];

    private readonly List<DecodedPicture> pendingDispose = [];
    private readonly Random random = new();
    private double currentTransitionStep;
    private DecodedPicture? activePicture;
    private int currentGifFrameIndex;
    private bool isDisposed;

    public PictureViewModel(PictureModel model)
    {
        this.model = model;

        slideshowTimer = new DispatcherTimer();
        slideshowTimer.Tick += OnSlideshowTick;

        transitionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        transitionTimer.Tick += OnTransitionTick;

        gifAnimationTimer = new DispatcherTimer();
        gifAnimationTimer.Tick += OnGifAnimationTick;

        // Single-shot: started whenever a bitmap is released, disposes it once the renderer
        // is guaranteed to be done with it.
        disposeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(DisposeDelayMs) };
        disposeTimer.Tick += OnDisposeTimerTick;

        ApplyModel(model);
    }

    public void ApplyModel(PictureModel newModel)
    {
        isDisposed = false; // a reloaded widget must come back to life
        model = newModel;

        // Drop cached pictures that are no longer part of the item list.
        var validPaths = new HashSet<string>(model.GetItems().Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var key in pictureCache.Keys.ToList())
        {
            if (validPaths.Contains(key)) continue;
            if (pictureCache.Remove(key, out var stale)) ReleasePicture(stale);
        }

        ConfigureSlideshowTimer();
        LoadCurrentPicture(immediate: true);
        this.RaisePropertyChanged(nameof(ShowCaption));
        this.RaisePropertyChanged(nameof(CornerRadius));
    }

    private void ConfigureSlideshowTimer()
    {
        slideshowTimer.Stop();

        // Suspended (desktop covered): Resume() calls this again once the desktop is back.
        if (isSuspended) return;

        if (model.Interval == SlideshowInterval.Manual || model.Order == PlayOrder.Fixed)
            return;

        var span = model.Interval switch
        {
            SlideshowInterval.Seconds5 => TimeSpan.FromSeconds(5),
            SlideshowInterval.Seconds10 => TimeSpan.FromSeconds(10),
            SlideshowInterval.Seconds30 => TimeSpan.FromSeconds(30),
            SlideshowInterval.Minutes1 => TimeSpan.FromMinutes(1),
            SlideshowInterval.Minutes5 => TimeSpan.FromMinutes(5),
            SlideshowInterval.Minutes15 => TimeSpan.FromMinutes(15),
            SlideshowInterval.Minutes30 => TimeSpan.FromMinutes(30),
            SlideshowInterval.Hours1 => TimeSpan.FromHours(1),
            SlideshowInterval.Days1 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromMinutes(5)
        };

        slideshowTimer.Interval = span;
        slideshowTimer.Start();
    }

    private void OnSlideshowTick(object? sender, EventArgs e)
    {
        NextPicture();
    }

    public void NextPicture()
    {
        var items = model.GetItems();
        if (items.Count <= 1) return;

        int nextIndex;
        if (model.Order == PlayOrder.Shuffle)
        {
            do
            {
                nextIndex = random.Next(items.Count);
            } while (nextIndex == model.CurrentIndex && items.Count > 1);
        }
        else
        {
            nextIndex = (model.CurrentIndex + 1) % items.Count;
        }

        model = model with { CurrentIndex = nextIndex };
        LoadCurrentPicture(immediate: false);
    }

    public void PreviousPicture()
    {
        var items = model.GetItems();
        if (items.Count <= 1) return;

        int prevIndex = (model.CurrentIndex - 1 + items.Count) % items.Count;
        model = model with { CurrentIndex = prevIndex };
        LoadCurrentPicture(immediate: false);
    }

    private void OnGifAnimationTick(object? sender, EventArgs e)
    {
        if (activePicture == null || !activePicture.IsAnimated || activePicture.Frames.Count <= 1)
        {
            gifAnimationTimer.Stop();
            return;
        }

        currentGifFrameIndex = (currentGifFrameIndex + 1) % activePicture.Frames.Count;
        var frame = activePicture.Frames[currentGifFrameIndex];
        CurrentBitmap = frame.Bitmap;
        gifAnimationTimer.Interval = TimeSpan.FromMilliseconds(frame.DurationMs);
    }

    // ------------------------------------------------------------------
    //  Bitmap cache / ownership
    // ------------------------------------------------------------------

    private DecodedPicture? GetOrLoadPicture(string path, int maxDimension = PictureImageLoader.DefaultMaxDimension)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        if (pictureCache.TryGetValue(path, out var cached))
            return cached;

        var decoded = PictureImageLoader.Load(path, maxDimension);
        if (decoded == null)
            return null;

        picturesCreated++;
        allPictures.Add(decoded);

        // Make room by releasing entries that are not currently on screen. A picture the
        // renderer is still drawing (the outgoing one during a fade) is never evicted here —
        // dropping it would leave it with no owner while its pixels are still being read; the
        // cache may stay one entry over the cap for that moment and the next load finishes
        // the eviction.
        while (pictureCache.Count >= MaxCacheCount)
        {
            string? evictKey = null;
            foreach (var kvp in pictureCache)
            {
                if (IsInUse(kvp.Value)) continue;
                evictKey = kvp.Key;
                break;
            }
            if (evictKey == null) break;
            if (pictureCache.Remove(evictKey, out var evicted)) ReleasePicture(evicted);
        }

        pictureCache[path] = decoded;
        ReportFrameCache();
        return decoded;
    }

    /// <summary>
    /// Observation only: reports the frame cache's absolute current usage to the shared
    /// resource ledger. Bytes are estimated BGRA (width × height × 4) per frame.
    /// </summary>
    private void ReportFrameCache()
    {
        var frames = 0;
        long bytes = 0;
        foreach (var cachedPicture in pictureCache.Values)
        {
            foreach (var frame in cachedPicture.Frames)
            {
                frames++;
                bytes += (long)frame.Bitmap.PixelSize.Width * frame.Bitmap.PixelSize.Height * 4;
            }
        }
        DeskCanvas.Runtime.ResourceLedger.Instance.Report($"picture:{instanceIndex}",
            DeskCanvas.Contracts.ResourceKind.PictureFrame, frames, bytes,
            Environment.CurrentManagedThreadId.ToString());
    }

    /// <summary>Diagnostics: decoded pictures created minus disposed. Should stay tiny (≈ cache size).</summary>
    public int LivePictureCount => picturesCreated - picturesDisposed;
    public int PicturesCreated => picturesCreated;
    public int PicturesDisposed => picturesDisposed;
    private int picturesCreated;
    private int picturesDisposed;

    /// <summary>
    /// True while a decoded picture is still reachable from a rendering slot. Such a picture
    /// must never be freed: the compositor would render a disposed bitmap and hard-crash.
    /// </summary>
    private bool IsInUse(DecodedPicture picture)
    {
        if (ReferenceEquals(picture, activePicture)) return true;
        return ContainsBitmap(picture, currentBitmap) || ContainsBitmap(picture, previousBitmap);
    }

    /// <summary>Whether <paramref name="bitmap"/> belongs to <paramref name="picture"/>
    /// (either its primary bitmap or one of its animation frames).</summary>
    private static bool ContainsBitmap(DecodedPicture picture, Bitmap? bitmap)
    {
        if (bitmap == null) return false;
        var frames = picture.Frames;
        for (int i = 0; i < frames.Count; i++)
        {
            if (ReferenceEquals(frames[i].Bitmap, bitmap)) return true;
        }
        return false;
    }

    /// <summary>
    /// Gives up ownership of a picture: it goes to the pending queue. The queue defers until
    /// nothing is rendering it — a picture the renderer still holds is <b>kept queued</b> and
    /// skipped by the flushes until its slot lets go, so ownership is never silently lost. An
    /// earlier version returned early for in-use pictures, and every switch that overlapped a
    /// fade left one full-size decoded picture reachable only from the renderer until the GC
    /// finalizer happened to run.
    /// </summary>
    private void ReleasePicture(DecodedPicture picture)
    {
        if (picture.IsDisposed || pendingDispose.Contains(picture))
            return;

        pendingDispose.Add(picture);
        ScheduleDispose();
    }

    /// <summary>Disposes a picture exactly once (<see cref="DecodedPicture.Dispose"/> is
    /// idempotent; the counter must be too).</summary>
    private void DisposePicture(DecodedPicture picture)
    {
        if (picture.IsDisposed) return;
        picturesDisposed++;
        allPictures.Remove(picture);
        picture.Dispose();
    }

    private void ScheduleDispose()
    {
        if (pendingDispose.Count == 0) return;

        // Do NOT restart an already running countdown. Slot changes happen many times per
        // second (GIF frames, cross-fade), and re-arming the timer on each one would push
        // the deadline back forever so nothing ever gets freed.
        if (disposeTimer.IsEnabled) return;
        disposeTimer.Start();
    }

    private void OnDisposeTimerTick(object? sender, EventArgs e)
    {
        disposeTimer.Stop();
        FlushPendingDisposals();
    }

    /// <summary>
    /// Frees released pictures. Anything still on screen stays <b>queued</b>: it has no owner
    /// any more, and the ownership change that finally releases its slot (or the next sweep in
    /// <see cref="ReleaseUnusedPictures"/>) flushes it then. Dropping it from the queue here
    /// would strand it — it is not in the cache, so no later pass could ever find it again.
    /// </summary>
    private void FlushPendingDisposals()
    {
        for (int i = pendingDispose.Count - 1; i >= 0; i--)
        {
            var picture = pendingDispose[i];
            if (IsInUse(picture))
                continue;
            pendingDispose.RemoveAt(i);
            DisposePicture(picture);
        }
    }

    private void RefreshDisposeTimer()
    {
        if (pendingDispose.Count == 0) disposeTimer.Stop();
        else ScheduleDispose();
    }

    /// <summary>
    /// Queue every live picture that no longer has an owner: not in the cache, not the active
    /// picture, not in either rendering slot. Slot replacement in the middle of a fade and an
    /// overlapping switch used to strand such pictures — nothing referenced them any more, but
    /// nothing disposed them either. Running this after every ownership change keeps the rule
    /// airtight: a decoded picture is always either owned (cache / slot / active) or queued.
    /// </summary>
    private void SweepOrphans()
    {
        if (allPictures.Count == 0) return;

        foreach (var picture in allPictures)
        {
            if (picture.IsDisposed || pendingDispose.Contains(picture)) continue;
            if (ReferenceEquals(picture, activePicture) || IsInUse(picture)) continue;

            var cached = false;
            foreach (var candidate in pictureCache.Values)
            {
                if (!ReferenceEquals(candidate, picture)) continue;
                cached = true;
                break;
            }
            if (cached) continue;

            pendingDispose.Add(picture);
        }
    }

    /// <summary>
    /// Called after loading/switching: releases everything that is no longer wanted.
    /// All disposal goes through here, so the "never free an on-screen bitmap" rule holds
    /// no matter which code path triggered the change.
    /// </summary>
    private void ReleaseUnusedPictures()
    {
        foreach (var kvp in pictureCache.ToList())
        {
            if (IsInUse(kvp.Value)) continue;
            // Keep the active picture cached so returning to it does not re-decode.
            if (ReferenceEquals(kvp.Value, activePicture)) continue;
            pictureCache.Remove(kvp.Key);
            ReleasePicture(kvp.Value);
        }

        SweepOrphans();
        RefreshDisposeTimer();
        ReportFrameCache();
    }

    // ------------------------------------------------------------------
    //  Loading / switching
    // ------------------------------------------------------------------

    /// <summary>Releases every rendering slot before the bitmaps they point at are freed.</summary>
    private void ClearSlots()
    {
        CurrentBitmap = null;
        PreviousBitmap = null;
        TransitionProgress = 1.0;
    }

    private void LoadCurrentPicture(bool immediate)
    {
        if (isDisposed) return;

        // Suspended (desktop covered): nothing may decode or re-arm a timer. The model was
        // already updated, so Resume() reloads whatever is current when the desktop returns.
        if (isSuspended) return;

        var items = model.GetItems();
        if (items.Count == 0)
        {
            gifAnimationTimer.Stop();
            transitionTimer.Stop();
            activePicture = null;
            ClearSlots();
            ReleaseUnusedPictures();   // everything cached is now unreferenced
            FlushPendingDisposals();
            HasPictures = false;
            Caption = null;
            return;
        }

        int index = Math.Clamp(model.CurrentIndex, 0, items.Count - 1);
        var item = items[index];

        var pic = GetOrLoadPicture(item.Path);
        if (pic == null)
        {
            // Fall back to the first readable picture in the list
            foreach (var fallback in items)
            {
                pic = GetOrLoadPicture(fallback.Path);
                if (pic != null)
                {
                    item = fallback;
                    break;
                }
            }
        }

        activePicture = pic;

        gifAnimationTimer.Stop();
        currentGifFrameIndex = 0;

        HasPictures = pic != null;
        Caption = !string.IsNullOrWhiteSpace(item.Name) ? item.Name : Path.GetFileNameWithoutExtension(item.Path);
        CropX = item.CropX;
        CropY = item.CropY;
        Zoom = item.Zoom;
        FitMode = model.FitMode;

        var targetBmp = pic?.PrimaryBitmap;

        if (targetBmp == null)
        {
            // Nothing decodable: never keep showing the previous picture.
            transitionTimer.Stop();
            ClearSlots();
        }
        else if (immediate || CurrentBitmap == null)
        {
            transitionTimer.Stop();
            ClearSlots();
            CurrentBitmap = targetBmp;
        }
        else
        {
            // Cross-fade. PreviousBitmap pins the outgoing picture for the whole transition.
            transitionTimer.Stop();
            PreviousBitmap = CurrentBitmap;
            CurrentBitmap = targetBmp;
            TransitionProgress = 0.0;
            currentTransitionStep = 0.0;
            transitionTimer.Start();
        }

        if (pic?.IsAnimated == true && pic.Frames.Count > 1)
        {
            gifAnimationTimer.Interval = TimeSpan.FromMilliseconds(pic.Frames[0].DurationMs);
            gifAnimationTimer.Start();
        }

        // The outgoing picture just left its slot (or is only held by the fading slot):
        // this is the single place that decides what can be freed after a switch.
        ReleaseUnusedPictures();
        RefreshDisposeTimer();
    }

    private void OnTransitionTick(object? sender, EventArgs e)
    {
        currentTransitionStep += 0.05; // 20 steps = ~400ms
        if (currentTransitionStep >= 1.0)
        {
            transitionTimer.Stop();
            TransitionProgress = 1.0;
            PreviousBitmap = null; // releases the outgoing picture, then it is disposed safely
            FlushPendingDisposals();
        }
        else
        {
            TransitionProgress = currentTransitionStep;
        }
    }

    public void OpenCurrentInShell()
    {
        var items = model.GetItems();
        if (items.Count == 0) return;

        int index = Math.Clamp(model.CurrentIndex, 0, items.Count - 1);
        var path = items[index].Path;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // Ignored
            }
        }
    }

    // ------------------------------------------------------------------
    //  Bindable state
    // ------------------------------------------------------------------

    private Bitmap? currentBitmap;
    public Bitmap? CurrentBitmap
    {
        get => currentBitmap;
        private set
        {
            if (ReferenceEquals(currentBitmap, value)) return;
            this.RaiseAndSetIfChanged(ref currentBitmap, value);
            ReleaseUnusedPictures();
        }
    }

    private Bitmap? previousBitmap;
    public Bitmap? PreviousBitmap
    {
        get => previousBitmap;
        private set
        {
            if (ReferenceEquals(previousBitmap, value)) return;
            this.RaiseAndSetIfChanged(ref previousBitmap, value);
            ReleaseUnusedPictures();
        }
    }

    private double transitionProgress = 1.0;
    public double TransitionProgress
    {
        get => transitionProgress;
        private set => this.RaiseAndSetIfChanged(ref transitionProgress, value);
    }

    private double cropX = 0.5;
    public double CropX
    {
        get => cropX;
        private set => this.RaiseAndSetIfChanged(ref cropX, value);
    }

    private double cropY = 0.5;
    public double CropY
    {
        get => cropY;
        private set => this.RaiseAndSetIfChanged(ref cropY, value);
    }

    private double zoom = 1.0;
    public double Zoom
    {
        get => zoom;
        private set => this.RaiseAndSetIfChanged(ref zoom, value);
    }

    private PictureFitMode fitMode = PictureFitMode.CustomCrop;
    public PictureFitMode FitMode
    {
        get => fitMode;
        private set => this.RaiseAndSetIfChanged(ref fitMode, value);
    }

    private bool hasPictures;
    public bool HasPictures
    {
        get => hasPictures;
        private set => this.RaiseAndSetIfChanged(ref hasPictures, value);
    }

    private string? caption;
    public string? Caption
    {
        get => caption;
        private set => this.RaiseAndSetIfChanged(ref caption, value);
    }

    public bool ShowCaption => model.ShowCaption && !string.IsNullOrEmpty(Caption);

    public CornerRadius CornerRadius => model.CornerRadius > 0 ? new CornerRadius(model.CornerRadius) : new CornerRadius(0);

    public bool ClickToNext => model.ClickToNext;
    public bool DoubleClickToOpen => model.DoubleClickToOpen;

    // ------------------------------------------------------------------
    //  Fullscreen suspension (host: IWidgetSuspendable)
    // ------------------------------------------------------------------

    private bool isSuspended;

    /// <summary>Diagnostics: whether the host currently has this widget suspended.</summary>
    public bool IsSuspended => isSuspended;

    /// <summary>Diagnostics: whether the slideshow timer is currently armed.</summary>
    public bool IsSlideshowRunning => slideshowTimer.IsEnabled;

    /// <summary>Diagnostics: whether an animation is currently playing.</summary>
    public bool IsAnimationRunning => gifAnimationTimer.IsEnabled;

    /// <summary>
    /// Called by the host when every attached screen is covered by a fullscreen or maximized
    /// application (see <c>IWidgetSuspendable</c>). This widget is driven by its <b>own</b>
    /// timers — none of them runs on the shared TimerService clocks the host pauses — so
    /// without suspending it keeps playing GIFs frame by frame (a render pass per frame) and
    /// keeps decoding every slideshow step behind the covering window: constant CPU plus a
    /// steady stream of decode allocations for pixels nobody can see, while the decoded
    /// animations (up to ~64 MB each at the frame budget) stay pinned in RAM.
    /// <para>
    /// Stops every timer the widget owns and disposes ALL decoded pictures — the cache, the
    /// pending queue and any picture the renderer held. Mirrors the Dispose() ordering:
    /// rendering slots are detached before any bitmap is freed, so no render pass can reach a
    /// disposed bitmap. <see cref="Resume"/> re-decodes and restarts. Idempotent.
    /// </para>
    /// </summary>
    public void Suspend()
    {
        if (isSuspended || isDisposed) return;
        isSuspended = true;

        // Detach the renderer from every bitmap before freeing them — the same ordering
        // guarantee as Dispose().
        ClearSlots();
        activePicture = null;
        currentGifFrameIndex = 0;

        foreach (var picture in allPictures.ToList())
        {
            DisposePicture(picture);
        }
        allPictures.Clear();
        pictureCache.Clear();
        pendingDispose.Clear();
        ReportFrameCache();

        // ClearSlots' property updates can re-arm the dispose timer through the slot setters,
        // so the timers are stopped last: after this nothing the widget owns may tick.
        slideshowTimer.Stop();
        transitionTimer.Stop();
        gifAnimationTimer.Stop();
        disposeTimer.Stop();
    }

    /// <summary>Undoes <see cref="Suspend"/>: re-decodes the current picture (with the model
    /// as of the last <see cref="ApplyModel"/>, if any) and restarts the slideshow and
    /// animation timers. Idempotent.</summary>
    public void Resume()
    {
        if (!isSuspended || isDisposed) return;
        isSuspended = false;

        // Same path as a freshly created widget: everything the suspension released is
        // rebuilt from the stored model here.
        ConfigureSlideshowTimer();
        LoadCurrentPicture(immediate: true);
    }

    // ------------------------------------------------------------------
    //  Teardown
    // ------------------------------------------------------------------

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        slideshowTimer.Stop();
        transitionTimer.Stop();
        gifAnimationTimer.Stop();
        disposeTimer.Stop();

        // 1. detach the renderer from every bitmap before anything is freed
        ClearSlots();

        // 2. no slot references any picture any more, so everything can go
        activePicture = null;

        foreach (var picture in allPictures.ToList())
        {
            DisposePicture(picture);
        }
        allPictures.Clear();
        pictureCache.Clear();
        pendingDispose.Clear();
        ReportFrameCache();

        // Deliberately no GC.Collect here: forcing a collection on every widget unload caused
        // a full blocking GC on the UI thread. Bitmaps release their native memory in Dispose().
        GC.SuppressFinalize(this);
    }
}
