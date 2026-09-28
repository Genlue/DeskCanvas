using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
using uWidgets.Core.Models.Settings;
using uWidgets.Services;

namespace uWidgets.Views.Controls;

/// <summary>
/// A background-only glass surface. The optical model runs on the GPU whenever the platform gives
/// us a real Ganesh context, and the original CPU renderer stays as the fallback; work is coalesced
/// while moving, resizing or editing optics.
/// </summary>
public sealed class LiquidGlassSurface : Control
{
    private static readonly HashSet<LiquidGlassSurface> Active = new();
    private static readonly SemaphoreSlim RenderSlots = new(2);

    /// <summary>Set once the render backend is known to lack a GPU; 0 = unknown, 1 = usable, 2 = never.</summary>
    private static int gpuState;

    /// <summary>Set when the first GPU frame is actually drawn, so the trace records it once.</summary>
    private static int drewGpuFrame;

    /// <summary>Set after the one-off shader readback, so it only runs once.</summary>
    private static int probedShader;

    /// <summary>Set once the material has proved it renders on this device.</summary>
    private static int selfTested;

    /// <summary>
    /// Re-render every live surface because something structural changed (wallpaper swapped,
    /// optics edited, display reconfigured). In-flight prepares are invalidated: their geometry
    /// and material are stale.
    /// </summary>
    public static void RefreshAll() => RefreshAll(immediate: false);

    /// <summary>
    /// Re-render every live surface because a newer wallpaper frame was captured.
    /// <para>
    /// This deliberately does <b>not</b> invalidate in-flight work — see
    /// <see cref="RequestSampledFrame"/>. The sampling timer uses this instead of
    /// <see cref="RefreshAll"/>, because that one is restarted before its debounce can elapse
    /// (the interval may be shorter than the debounce) and would starve the surface entirely.
    /// </para>
    /// </summary>
    public static void RefreshAllImmediate()
    {
        foreach (var surface in Active) surface.RequestSampledFrame();
    }

    private static void RefreshAll(bool immediate)
    {
        foreach (var surface in Active) surface.RequestRender(immediate);
    }

    /// <summary>
    /// True while a glass surface actually wants frames — attached, visible, and on the rendered
    /// material. The live sampler idles otherwise, so switching to 纯色 or hiding every widget
    /// stops the desktop capture instead of grabbing frames nobody can see.
    /// </summary>
    public static bool HasActiveSurfaces
    {
        get
        {
            foreach (var surface in Active)
                if (surface.DemandsFrames) return true;
            return false;
        }
    }

    /// <summary>
    /// True while any glass surface is mid-prepare.
    /// <para>
    /// A sampling round only starts when this is false. One round costs one desktop capture plus one
    /// shared backdrop build, and every widget that renders during it samples that same frame — but
    /// only if none of them is still busy from the previous round. Starting a round while widgets
    /// are mid-prepare hands the new frame to whoever happens to ask first and leaves the rest to
    /// grab their own: at a 3 ms interval that measured one capture <i>per publish</i> instead of one
    /// per six, which is why a smaller interval produced a slower glass.
    /// </para>
    /// </summary>
    public static bool AnySurfaceRendering
    {
        get
        {
            foreach (var surface in Active)
                if (surface.busy && surface.DemandsFrames) return true;
            return false;
        }
    }

    private bool DemandsFrames => attached && IsEffectivelyVisible && Material?.UsesRenderedGlass == true;

    public static readonly StyledProperty<Theme?> MaterialProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, Theme?>(nameof(Material));
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, CornerRadius>(nameof(CornerRadius));
    public static readonly StyledProperty<bool> PreviewProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(Preview));
    public static readonly StyledProperty<bool> SettingsSurfaceProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(SettingsSurface));

    public Theme? Material { get => GetValue(MaterialProperty); set => SetValue(MaterialProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public bool SettingsSurface { get => GetValue(SettingsSurfaceProperty); set => SetValue(SettingsSurfaceProperty, value); }

    private readonly DispatcherTimer debounce;
    private Window? window;

    /// <summary>
    /// Everything the render thread needs, swapped as one reference and <b>reference counted</b>.
    /// <para>
    /// The render thread reads this field once per frame and gets a self-consistent set; the UI
    /// thread publishes a whole new set at a time and drops its own reference. That is not enough on
    /// its own: Avalonia keeps a custom draw operation in its composition tree and re-invokes it on
    /// later frames, so a draw operation can outlive the snapshot it was built for by an unbounded
    /// amount. Each draw operation therefore takes its own reference and gives it back from
    /// <see cref="ICustomDrawOperation.Dispose"/>, which is what makes the material safe to retire
    /// immediately — the old time-based grace period both failed to guarantee that (the GPU backlog
    /// this theme shipped with came from exactly that use-after-free) and held full-size bitmaps
    /// alive far longer than needed.
    /// </para>
    /// </summary>
    private sealed class Prepared : IDisposable
    {
        private int references = 1;

        public Prepared(GlassSource? source, AuraTexture aura, LiquidGlassRenderer.Frame frame, SKBitmap? cpu)
        {
            Source = source;
            Aura = aura;
            Frame = frame;
            Cpu = cpu;
        }

        public GlassSource? Source { get; }
        public AuraTexture Aura { get; }
        public LiquidGlassRenderer.Frame Frame { get; }
        public SKBitmap? Cpu { get; }

        public void AddRef() => Interlocked.Increment(ref references);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref references) > 0) return;
            Cpu?.Dispose();
            Aura.Bitmap?.Dispose();
            Source?.Dispose();
        }
    }

    private volatile Prepared? prepared;
    private bool attached;
    private bool busy;
    private int revision;

    // A sampled frame arrived while this surface was mid-prepare and was dropped (frames are
    // dropped under load, not queued). For an animated wallpaper the next publish retries the
    // surface naturally — but when the scene has just gone static (a wallpaper switch to a
    // still image, a paused video) that dropped frame is the last publish ever, and without a
    // follow-up the glass would sit on the previous wallpaper until the next real change.
    // Consumed by exactly one render in RenderMaterial's finally.
    private int pendingSampledFrame;

    /// <summary>The dye bloom grid and the metadata the shader needs to address it.</summary>
    private readonly record struct AuraTexture(SKBitmap? Bitmap, float Columns, float Rows, float Step, float Margin)
    {
        public bool IsUsable => Bitmap != null;
    }

    public LiquidGlassSurface()
    {
        IsHitTestVisible = false;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        debounce.Tick += (_, _) => { debounce.Stop(); RenderMaterial(); };
        ActualThemeVariantChanged += (_, _) => RequestRender();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        attached = true;
        Active.Add(this);
        // The live sampler only ticks while glass is on screen.
        LiquidGlassWallpaper.RefreshSamplerState();
        window = TopLevel.GetTopLevel(this) as Window;
        if (window != null) window.PositionChanged += OnPositionChanged;
        RequestRender();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        attached = false;
        Active.Remove(this);
        revision++;
        Interlocked.Exchange(ref pendingSampledFrame, 0);
        debounce.Stop();
        if (window != null) window.PositionChanged -= OnPositionChanged;
        window = null;
        ReleasePrepared();
        LiquidGlassWallpaper.RefreshSamplerState();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => RequestRender();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MaterialProperty || change.Property == CornerRadiusProperty
            || change.Property == BoundsProperty || change.Property == IsVisibleProperty
            || change.Property == PreviewProperty || change.Property == SettingsSurfaceProperty)
            RequestRender();
    }

    /// <summary>
    /// A newer wallpaper frame is available.
    /// <para>
    /// <b>This must not bump <see cref="revision"/>.</b> The revision exists to throw away work
    /// whose geometry or material went stale — but a new wallpaper frame does not invalidate
    /// anything: a prepare that is already in flight holds perfectly valid positions and should be
    /// allowed to publish. Bumping here (which is what the sampler used to do) meant that once the
    /// desktop took longer to sample than the interval — the normal case, since every frame
    /// captures the screen and blurs it — every finished frame was discarded as stale, so the glass
    /// rendered once and then never updated again. Skipping a frame when the surface is busy is
    /// still the behaviour — frames are dropped under load, not queued — but the drop now queues
    /// exactly one follow-up render, so the last frame of a scene that has just gone static cannot
    /// be lost for good (see <see cref="pendingSampledFrame"/>).
    /// </para>
    /// </summary>
    private void RequestSampledFrame()
    {
        if (!attached || !IsVisible || Material?.UsesRenderedGlass != true) return;
        if (busy)
        {
            // Dropped on purpose — but remember it, so the frame is not lost forever when the
            // desktop goes still right after (the sampler then publishes nothing new to retry
            // with). Same contract as FramelessDigital's wallpaperStale follow-up.
            Interlocked.Exchange(ref pendingSampledFrame, 1);
            return;
        }
        RenderMaterial();
    }

    public void RequestRender(bool immediate = false)
    {
        revision++;
        if (!attached) return;
        debounce.Stop();
        // Visibility and material both decide whether the live sampler should be running.
        LiquidGlassWallpaper.RefreshSamplerState();
        if (!IsVisible || Material?.UsesRenderedGlass != true)
        {
            ReleasePrepared();
            InvalidateVisual();
            return;
        }
        if (immediate) RenderMaterial();
        else debounce.Start();
    }

    /// <summary>Drop every prepared resource. Safe to call repeatedly.</summary>
    private void ReleasePrepared() => RetirePrepared(Interlocked.Exchange(ref prepared, null));

    /// <summary>
    /// Drop this surface's reference to a snapshot. The bitmaps are freed when the last draw
    /// operation holding one has also let go (see <see cref="Prepared"/>).
    /// </summary>
    private static void RetirePrepared(Prepared? old) => old?.Dispose();

    private async void RenderMaterial()
    {
        if (busy || !attached || !IsVisible || Material?.UsesRenderedGlass != true || Bounds.Width < 1 || Bounds.Height < 1) return;
        busy = true;
        var current = revision;
        var started = Environment.TickCount64;
        // The caller's reference on the desktop capture. Held for the whole prepare (the JPEG-free
        // CPU render is a second worker hop that reuses it) and released in the finally below.
        WallpaperSnapshot? wallpaper = null;
        try
        {
            var frame = BuildFrame();
            SKBitmap? nextCpu = null;
            GlassSource? nextSource = null;
            var nextAura = default(AuraTexture);
            var path = "none";

            if (!attached || revision != current) return;
            var preview = Preview;
            // Compiling the runtime shader is a one-off cost, so it is resolved off the UI
            // thread together with the first backdrop.
            var wantGpu = Volatile.Read(ref gpuState) != 2;
            var result = await Task.Run<(GlassSource? Source, WallpaperSnapshot Wallpaper, AuraTexture Aura, int CaptureMs, int BackdropMs)>(() =>
            {
                // Split the two stages that actually cost: grabbing the desktop, and building the
                // shared blurred backdrop from it. Both are per capture, not per pixel of the card.
                var captureStarted = Environment.TickCount64;
                var snapshot = LiquidGlassWallpaper.Get();
                var captureMs = (int)(Environment.TickCount64 - captureStarted);
                // The preview only overrides the wallpaper's placement. The copy takes its own
                // reference, so exactly one reference leaves this lambda on every path.
                var sampled = preview ? snapshot.WithPlacement("10", false) : snapshot;
                if (preview) snapshot.Dispose();
                try
                {
                    if (wantGpu && LiquidGlassGpuEffect.IsSupported)
                    {
                        var backdropStarted = Environment.TickCount64;
                        var shared = LiquidGlassSourceCache.Get(frame, sampled);
                        var backdropMs = (int)(Environment.TickCount64 - backdropStarted);
                        if (shared != null)
                        {
                            // Already an owned reference — the cache handed one over, so it cannot
                            // be evicted and freed out from under the dye-field build below.
                            try
                            {
                                return (shared, sampled, BuildAura(frame, shared), captureMs, backdropMs);
                            }
                            catch
                            {
                                shared.Dispose();
                                throw;
                            }
                        }
                    }
                    return (null, sampled, default(AuraTexture), captureMs, 0);
                }
                catch
                {
                    sampled.Dispose();
                    throw;
                }
            });

            // That own reference becomes the surface's; RetirePrepared releases it later.
            nextSource = result.Source;
            nextAura = result.Aura;
            wallpaper = result.Wallpaper;

            // A snapshot with no pixels anywhere — the capture failed and the wallpaper file is
            // missing too — still "renders": as one flat fill of the desktop colour. Mid-session
            // that is the sudden blink to a solid card, so keep the previous material until a
            // real frame exists again. The first material still publishes: a flat card beats no
            // card. Checked before the CPU path so the wasted ~100 ms render is skipped too.
            if (nextSource == null && wallpaper.CachedBitmap == null && wallpaper.ImageBytes == null && prepared != null)
            {
                GlassDiagnostics.Note("capture has no pixels — keeping the previous material", started, frame);
                return;
            }

            if (nextSource != null)
            {
                path = "gpu";
            }
            else
            {
                // Only the CPU render is concurrency-limited: it is the one that costs ~100 ms per
                // large card. Gating the GPU prepare behind the same semaphore was what starved
                // every widget past the second — they waited for a slot, and by the time they got
                // one their revision had moved on, so their work was thrown away and those cards
                // never received a material at all.
                path = "cpu";
                await RenderSlots.WaitAsync();
                try
                {
                    if (!attached || revision != current) return;
                    nextCpu = await Task.Run(() => LiquidGlassDispatch.RenderBitmap(frame, wallpaper!));
                }
                finally { RenderSlots.Release(); }
            }

            if (!attached || revision != current)
            {
                GlassDiagnostics.Note($"discarded ({path}: geometry changed)", started, frame);
                RetirePrepared(new Prepared(nextSource, nextAura, frame, null));
                return;
            }

            // Never publish an empty material. Doing so would replace working glass with the faint
            // placeholder and blank the card, which is strictly worse than showing a stale frame.
            if (nextSource == null && nextCpu == null)
            {
                GlassDiagnostics.Note($"empty ({path}) — keeping the previous material", started, frame);
                RetirePrepared(new Prepared(null, nextAura, frame, null));
                return;
            }

            var previous = Interlocked.Exchange(ref prepared, new Prepared(nextSource, nextAura, frame, nextCpu));
            RetirePrepared(previous);
            GlassDiagnostics.Published(Environment.TickCount64 - started,
                $"{path} {frame.Width}x{frame.Height} capture {result.CaptureMs}ms backdrop {result.BackdropMs}ms " +
                $"captures {LiquidGlassWallpaper.CaptureCount}");
            InvalidateVisual();
        }
        catch (Exception ex)
        {
            // A missing/unsupported wallpaper or rendering device must not crash a widget.
            GlassDiagnostics.Failure(ex);
            Debug.WriteLine($"Liquid glass render failed: {ex}");
            InvalidateVisual();
        }
        finally
        {
            wallpaper?.Dispose();
            busy = false;
            if (attached && revision != current)
            {
                // The revision re-render calls Get() and picks up the newest published frame,
                // which supersedes anything the pending flag could ask for.
                Interlocked.Exchange(ref pendingSampledFrame, 0);
                RequestRender();
            }
            else if (attached && Interlocked.Exchange(ref pendingSampledFrame, 0) == 1)
            {
                // Catch up with the sampled frame that was dropped while this prepare ran.
                RenderMaterial();
            }
        }
    }

    private LiquidGlassRenderer.Frame BuildFrame()
    {
        var scaling = window?.RenderScaling ?? 1;
        // Bound CPU/memory use for very large widgets. Text is rendered separately at native DPI.
        var quality = Math.Min(1, Math.Min(2048 / (Math.Max(Bounds.Width, Bounds.Height) * scaling),
            Math.Sqrt(1200000 / (Bounds.Width * Bounds.Height * scaling * scaling))));
        var renderScale = (float)(scaling * quality);
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * renderScale));
        var screen = window?.Screens.ScreenFromWindow(window);
        var screens = window?.Screens.All;
        var left = screens?.Min(s => s.Bounds.X) ?? 0;
        var top = screens?.Min(s => s.Bounds.Y) ?? 0;
        var desktopWidth = (screens?.Max(s => s.Bounds.Right) ?? 1920) - left;
        var desktopHeight = (screens?.Max(s => s.Bounds.Bottom) ?? 1080) - top;
        var position = window != null ? this.PointToScreen(default) : default;
        var widget = window as Widget;
        var (cols, rows) = widget?.CurrentSpan ?? (0, 0);
        var material = Material ?? new Theme(null, null, 0.18, true, false, "Inter", SurfaceStyle.LiquidGlass);
        var frame = new LiquidGlassRenderer.Frame(width, height, renderScale, (float)CornerRadius.TopLeft,
            (float)((position.X - left) * quality), (float)((position.Y - top) * quality),
            (float)(desktopWidth * quality), (float)(desktopHeight * quality),
            (float)(((screen?.Bounds.X ?? 0) - left) * quality), (float)(((screen?.Bounds.Y ?? 0) - top) * quality),
            (float)((screen?.Bounds.Width ?? 1920) * quality), (float)((screen?.Bounds.Height ?? 1080) * quality),
            material, ActualThemeVariant == ThemeVariant.Dark, SettingsSurface, (float)quality,
            Columns: cols, Rows: rows);
        if (!Preview) return frame;
        // The preview card is tiny (45×45 in the theme button): calm both materials down so the
        // miniature shows the recipe rather than a bloated lens / flooded diffusion.
        return frame.Theme.IsLiquidGlassV2
            ? frame with
            {
                DesktopX = 70 * renderScale, DesktopY = 40 * renderScale, ScreenX = 0, ScreenY = 0,
                DesktopWidth = 120 * renderScale, DesktopHeight = 90 * renderScale,
                ScreenWidth = 120 * renderScale, ScreenHeight = 90 * renderScale,
                Theme = frame.Theme with
                {
                    LiquidGlassV2 = frame.Theme.EffectiveLiquidGlassV2 with
                    { Blur = frame.Theme.EffectiveLiquidGlassV2.Blur * 0.35, Refraction = frame.Theme.EffectiveLiquidGlassV2.Refraction * 0.35 }
                }
            }
            : frame with
            {
                DesktopX = 70 * renderScale, DesktopY = 40 * renderScale, ScreenX = 0, ScreenY = 0,
                DesktopWidth = 120 * renderScale, DesktopHeight = 90 * renderScale,
                ScreenWidth = 120 * renderScale, ScreenHeight = 90 * renderScale,
                Theme = material with
                {
                    LiquidGlass = material.EffectiveLiquidGlass with
                    { Blur = material.EffectiveLiquidGlass.Blur * 0.35, EdgeWidth = material.EffectiveLiquidGlass.EdgeWidth * 0.35 }
                }
            };
    }

    /// <summary>
    /// The soft recipe's dye bloom, built from the shared backdrop. Cell-centre sampling keeps this
    /// O(cells) rather than O(area) — the backdrop is already blurred, so a cell's average is its
    /// centre value.
    /// </summary>
    private static AuraTexture BuildAura(LiquidGlassRenderer.Frame frame, GlassSource shared)
    {
        if (!frame.Theme.IsSoftGlow) return default;
        try
        {
            // The 对齐 offset is already baked into the backdrop by DrawWallpaper, so the card's
            // desktop origin alone maps a card pixel to a backdrop texel.
            var originX = frame.DesktopX;
            var originY = frame.DesktopY;
            var backdrop = shared.Backdrop;
            var field = LiquidGlassRenderer.AuraField.BuildFromSampler((x, y) =>
            {
                var sx = Math.Clamp((int)((x + originX) * shared.Scale), 0, backdrop.Width - 1);
                var sy = Math.Clamp((int)((y + originY) * shared.Scale), 0, backdrop.Height - 1);
                return backdrop.GetPixel(sx, sy);
            }, frame.Width, frame.Height);
            return new AuraTexture(field.ToBitmap(), field.Columns, field.Rows, field.Step, field.Margin);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Glass dye field failed: {ex.Message}");
            return default;
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Material?.UsesRenderedGlass != true) return;
        var rect = new Rect(Bounds.Size);

        // One read of the swapped snapshot: the render thread must never see a new backdrop
        // paired with a stale frame, and the draw operation takes its own reference so the
        // bitmaps survive however long the compositor keeps it.
        var current = prepared;
        if (current is { Source: not null })
        {
            context.Custom(new GlassDrawOperation(current, Material, rect));
            return;
        }
        if (current is { Cpu: not null })
        {
            context.Custom(new ImageDrawOperation(current, rect));
            return;
        }

        // Readable first frame/fallback until the material is ready.
        var dark = ActualThemeVariant == ThemeVariant.Dark;
        var hex = dark ? Material.EffectiveSolidBackgroundDark : Material.EffectiveSolidBackgroundLight;
        var color = Color.TryParse(hex, out var parsed) ? parsed : dark ? Colors.Black : Colors.White;
        var fallbackOpacity = double.IsFinite(Material.OpacityLevel) ? Math.Clamp(Material.OpacityLevel, 0.05, 0.4) : 0.18;
        context.DrawRectangle(new SolidColorBrush(color, fallbackOpacity), new Pen(new SolidColorBrush(Colors.White, 0.25), 1),
            rect.Deflate(0.5), CornerRadius.TopLeft, CornerRadius.TopLeft);
    }

    /// <summary>
    /// Runs the optical shader on the render thread.
    /// <para>
    /// It holds one reference on the <see cref="Prepared"/> snapshot it draws, taken in the
    /// constructor and given back from <see cref="Dispose"/>. Avalonia keeps a custom draw
    /// operation in its composition tree and re-invokes it on later frames, so without that
    /// reference a backdrop could be freed while a queued operation still pointed at it — which is
    /// a use-after-free inside Skia, not a recoverable exception.
    /// </para>
    /// <para>
    /// <b>The GrContext check is not optional.</b> SkRuntimeEffect shaders are GPU-only in this
    /// Skia: drawing one onto a raster canvas kills the process with an SEHException that managed
    /// code cannot catch. When there is no GrContext the surface is switched permanently to the CPU
    /// material (one frame of the plain blurred backdrop covers the gap) and the widgets re-render.
    /// </para>
    /// </summary>
    private sealed class GlassDrawOperation : ICustomDrawOperation
    {
        private readonly Prepared prepared;
        private readonly GlassSource source;
        private readonly AuraTexture aura;
        private readonly LiquidGlassRenderer.Frame frame;
        private readonly Theme material;
        private int disposed;

        public Rect Bounds { get; }

        public GlassDrawOperation(Prepared prepared, Theme material, Rect bounds)
        {
            this.prepared = prepared;
            source = prepared.Source!;
            aura = prepared.Aura;
            frame = prepared.Frame;
            this.material = material;
            Bounds = bounds;
            prepared.AddRef();
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => obj is ICustomDrawOperation other && Equals(other);

        public override int GetHashCode() => source.GetHashCode();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) prepared.Dispose();
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
            {
                GlassDiagnostics.Event("draw: no ISkiaSharpApiLeaseFeature — cannot draw the glass material");
                return;
            }
            using var lease = feature.Lease();
            var destination = new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom);

            if (lease.GrContext == null)
            {
                // Software backend: a runtime shader would terminate the process here.
                if (Interlocked.Exchange(ref gpuState, 2) != 2)
                    GlassDiagnostics.Event("draw: no GrContext — software backend, switching to the CPU material permanently");
                LiquidGlassSourceCache.Clear();
                lease.SkCanvas.DrawBitmap(source.Backdrop, destination);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var surface in Active) surface.RequestRender(immediate: false);
                });
                return;
            }

            // The 新液态玻璃 material runs its own shader; the lens materials share the older one.
            var isV2 = material.EffectiveSurface == SurfaceStyle.LiquidGlassV2;
            var v2Parameters = default(LiquidGlassV2Effect.Params);
            var parameters = default(LiquidGlassGpuEffect.Params);
            if (isV2)
            {
                v2Parameters = LiquidGlassV2Effect.BuildParams(frame, source.Scale,
                    (float)Bounds.Width, (float)Bounds.Height) with
                {
                    DestOriginX = (float)Bounds.X,
                    DestOriginY = (float)Bounds.Y
                };
            }
            else
            {
                parameters = LiquidGlassGpuEffect.BuildParams(frame, source.Scale,
                    (float)Bounds.Width, (float)Bounds.Height) with
                {
                    DestOriginX = (float)Bounds.X,
                    DestOriginY = (float)Bounds.Y,
                    AuraEnabled = aura.IsUsable,
                    AuraColumns = aura.Columns,
                    AuraRows = aura.Rows,
                    AuraStep = aura.Step,
                    AuraMargin = aura.Margin
                };
            }

            using var shader = isV2
                ? LiquidGlassV2Effect.Create(source.Backdrop, v2Parameters)
                : LiquidGlassGpuEffect.Create(source.Backdrop, aura.Bitmap, parameters);
            if (shader == null)
            {
                GlassDiagnostics.Event("draw: runtime shader could not be built — drawing the plain backdrop");
                lease.SkCanvas.DrawBitmap(source.Backdrop, destination);
                return;
            }

            if (Interlocked.Exchange(ref drewGpuFrame, 1) == 0)
                GlassDiagnostics.Event($"draw: first GPU frame at {destination.Width:F0}x{destination.Height:F0} " +
                                       $"(bounds {Bounds.X:F0},{Bounds.Y:F0}), backdrop {source.Backdrop.Width}x{source.Backdrop.Height} scale={source.Scale:F3}");

            // One-off readback of what the shader actually produces. The GPU path cannot be
            // validated offline, so this is the only way to tell "the shader drew nothing" apart
            // from "the shader drew something that never reached the screen". The staged walk is
            // tuned to the lens material; the new material relies on the opaque self-test below.
            if (!isV2 && Interlocked.Exchange(ref probedShader, 1) == 0) Probe(lease, shader, destination);

            // A runtime-effect program is only built for the device at draw time, from a narrower
            // dialect than SKRuntimeEffect.Create accepts, and a program that fails to build there
            // draws nothing at all instead of reporting an error — the card simply renders
            // transparent. Nothing offline can catch that, so the material proves itself once:
            // render it offscreen, and if it comes back empty, use the CPU renderer from here on.
            if (Interlocked.Exchange(ref selfTested, 1) == 0 && !RendersOpaque(lease, shader, destination))
            {
                Interlocked.Exchange(ref gpuState, 2);
                LiquidGlassSourceCache.Clear();
                GlassDiagnostics.Event("selftest: the GPU material renders empty — switching to the CPU material");
                lease.SkCanvas.DrawBitmap(source.Backdrop, destination);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var surface in Active) surface.RequestRender(immediate: false);
                });
                return;
            }

            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            lease.SkCanvas.DrawRect(destination, paint);
        }

        /// <summary>Render the material into an offscreen GPU surface and report a few pixels.</summary>
        private void Probe(ISkiaSharpApiLease lease, SKShader shader, SKRect destination)
        {
            try
            {
                var matrix = lease.SkCanvas.TotalMatrix;
                GlassDiagnostics.Event(
                    $"probe: canvas ctm scale=({matrix.ScaleX:F3},{matrix.ScaleY:F3}) trans=({matrix.TransX:F1},{matrix.TransY:F1}) " +
                    $"opacity={lease.CurrentOpacity:F2} dest=({destination.Left:F1},{destination.Top:F1})-({destination.Right:F1},{destination.Bottom:F1}) " +
                    $"frame={frame.Width}x{frame.Height} radius={frame.Radius:F1}");

                var width = Math.Max(1, (int)destination.Width);
                var height = Math.Max(1, (int)destination.Height);
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(lease.GrContext!, false, info);
                if (surface == null)
                {
                    GlassDiagnostics.Event("probe: could not create an offscreen GPU surface");
                    return;
                }

                // Control: the backdrop bitmap itself, to prove the texture carries the wallpaper.
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawBitmap(source.Backdrop, new SKRect(0, 0, width, height));
                GlassDiagnostics.Event($"probe: backdrop centre={ReadPixel(surface, width / 2, height / 2)}");

                // Walk the material's building blocks from trivial to complete. A transparent result
                // at every pixel is what a failed GPU program looks like — and it is the same
                // picture whether the program was rejected or the maths came out empty, so the only
                // way to tell is to find the first stage that stops producing colour.
                var rect = new SKRect(0, 0, width, height);
                using var contentChild = source.Backdrop.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                var fullSource = LiquidGlassGpuEffect.SourceForDiagnostics;

                Stage("a constant colour", "half4 main(float2 p) { return half4(0.2, 0.8, 0.4, 1.0); }",
                    null, null, surface, rect);
                Stage("b one-tap child sample",
                    "uniform shader content; half4 main(float2 p) { return sample(content, p); }",
                    contentChild, null, surface, rect);
                Stage("c four-tap bilinear", BilinearStage, contentChild, null, surface, rect);
                var uniforms = new (string, object)[]
                {
                    ("size", new[] { (float)frame.Width, (float)frame.Height }),
                    ("radius", frame.Radius * frame.Scale),
                    ("destOrigin", new[] { 0f, 0f }),
                    ("destToRender", frame.Width / Math.Max(1f, (float)rect.Width))
                };

                Stage("a constant colour", "half4 main(float2 p) { return half4(0.2, 0.8, 0.4, 1.0); }",
                    null, null, surface, rect);
                Stage("b one-tap child sample",
                    "uniform shader content; half4 main(float2 p) { return sample(content, p); }",
                    contentChild, null, surface, rect);
                Stage("c four-tap bilinear", BilinearStage, contentChild, null, surface, rect);
                Stage("d0 uniforms echoed", UniformEchoStage, null, uniforms, surface, rect);
                Stage("d1 rounded-rect mask, no out param", MaskStage, null, uniforms, surface, rect);
                Stage("d2 same mask, WITH out param", MaskOutParamStage, null, uniforms, surface, rect);
                // The full model, then with the seven-tap spectrum removed. If one of these starts
                // producing colour while the other does not, the blocker is that group of texture
                // fetches rather than any single construct.
                Stage("e0 full model (unchanged)", fullSource, contentChild, uniforms, surface, rect);
                Stage("e1 full model, no 7-tap spectrum",
                    StripMarked(fullSource, "spectrum", ""), contentChild, uniforms, surface, rect);
                // Two constructs the full model uses that no passing stage above does.
                Stage("f1 global const", GlobalConstStage, contentChild, null, surface, rect);
                Stage("f2 two child shaders", TwoChildrenStage, contentChild, null, surface, rect);

                using var probePaint = new SKPaint { Shader = shader, IsAntialias = true };
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.DrawRect(rect, probePaint);
                GlassDiagnostics.Event(
                    $"probe: real shader centre={ReadPixel(surface, width / 2, height / 2)} " +
                    $"topEdge={ReadPixel(surface, width / 2, 2)} leftEdge={ReadPixel(surface, 2, height / 2)} " +
                    $"corner={ReadPixel(surface, 1, 1)}");
            }
            catch (Exception ex)
            {
                GlassDiagnostics.Failure(ex);
            }
        }

        private const string BilinearStage = @"
uniform shader content;
float3 texel(float2 c) { return float3(sample(content, c + float2(0.5)).rgb) * 255.0; }
half4 main(float2 p) {
  float2 b = floor(p);
  float2 f = p - b;
  float3 c = mix(mix(texel(b), texel(b + float2(1.0, 0.0)), f.x),
                 mix(texel(b + float2(0.0, 1.0)), texel(b + float2(1.0, 1.0)), f.x), f.y);
  return half4(half3(c / 255.0), 1.0);
}";

        /// <summary>A global <c>const</c> plus a child sample, as the full model has.</summary>
        private const string GlobalConstStage = @"
uniform shader content;
const float PI = 3.14159265;
half4 main(float2 p) {
  return sample(content, p) * half(PI / PI);
}";

        /// <summary>Two declared child shaders, both sampled — the full model's other trait.</summary>
        private const string TwoChildrenStage = @"
uniform shader content;
uniform shader aura;
half4 main(float2 p) {
  half4 a = sample(content, p);
  half4 b = sample(aura, p);
  return half4(a.rgb, 1.0) * b.a + half4(a.rgb * half(0.5), 1.0);
}";

        /// <summary>Writes the uniforms straight out, so a zero means the binding never arrived.</summary>
        private const string UniformEchoStage = @"
uniform float2 size;
uniform float radius;
uniform float destToRender;
half4 main(float2 p) {
  return half4(half(size.x / 1024.0), half(size.y / 1024.0), half(destToRender / 4.0), 1.0);
}";

        /// <summary>Rounded-rect coverage and the bevel, with plain return values.</summary>
        private const string MaskStage = @"
uniform float2 size;
uniform float radius;
uniform float destToRender;
float3 bevelOf(float2 p, float2 h, float r) {
  float2 v = p - h;
  float2 s = float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
  float2 a = abs(v);
  float2 q = a - (h - float2(r));
  if (q.x > 0.0 && q.y > 0.0) {
    float len = length(q);
    return float3(q.x / max(len, 1e-5) * s.x, q.y / max(len, 1e-5) * s.y, r - len);
  }
  return float3(s.x, s.y, min(h.x - a.x, h.y - a.y));
}
half4 main(float2 xy) {
  float2 rxy = xy * destToRender;
  float2 h = size * 0.5;
  float2 p = rxy - h;
  float2 q = abs(p) - h + float2(radius);
  float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
  float mask = clamp(1.0 - sd, 0.0, 1.0);
  float3 b = bevelOf(rxy, h, radius);
  return half4(half(b.x * 0.5 + 0.5), half(b.y * 0.5 + 0.5), half(b.z / max(size.x, 1.0)), half(mask));
}";

        /// <summary>The same bevel through an <c>out</c>-parameter helper, which is the suspect.</summary>
        private const string MaskOutParamStage = @"
uniform float2 size;
uniform float radius;
uniform float destToRender;
void bevelOf(float2 p, float2 h, float r, out float2 n, out float depth) {
  float2 v = p - h;
  float2 s = float2(v.x >= 0.0 ? 1.0 : -1.0, v.y >= 0.0 ? 1.0 : -1.0);
  float2 a = abs(v);
  float2 q = a - (h - float2(r));
  if (q.x > 0.0 && q.y > 0.0) {
    float len = length(q);
    n = float2(q.x / max(len, 1e-5) * s.x, q.y / max(len, 1e-5) * s.y);
    depth = r - len;
    return;
  }
  n = s;
  depth = min(h.x - a.x, h.y - a.y);
}
half4 main(float2 xy) {
  float2 rxy = xy * destToRender;
  float2 h = size * 0.5;
  float2 p = rxy - h;
  float2 q = abs(p) - h + float2(radius);
  float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - radius;
  float mask = clamp(1.0 - sd, 0.0, 1.0);
  float2 n;
  float depth;
  bevelOf(rxy, h, radius, n, depth);
  return half4(half(n.x * 0.5 + 0.5), half(n.y * 0.5 + 0.5), half(depth / max(size.x, 1.0)), half(mask));
}";

        /// <summary>
        /// Replace a <c>//#probe:name:begin … :end</c> marked region of the shader, so the diagnostic
        /// can measure how the material behaves with one group of work removed.
        /// </summary>
        private static string StripMarked(string source, string name, string replacement)
        {
            var begin = $"//#probe:{name}:begin";
            var end = $"//#probe:{name}:end";
            var start = source.IndexOf(begin, StringComparison.Ordinal);
            if (start < 0) return source;
            var stop = source.IndexOf(end, start, StringComparison.Ordinal);
            if (stop < 0) return source;
            return source.Remove(start, stop + end.Length - start).Insert(start, replacement);
        }

        /// <summary>Compile, bind and draw one stage offscreen, reporting the centre pixel.</summary>
        private void Stage(string label, string source, SKShader? content, (string Name, object Value)[]? values,
            SKSurface surface, SKRect rect)
        {
            try
            {
                var effect = SKRuntimeEffect.Create(source, out var errors);
                if (effect == null)
                {
                    GlassDiagnostics.Event($"stage {label}: COMPILE FAILED — {errors.Trim()}");
                    return;
                }

                var uniforms = new SKRuntimeEffectUniforms(effect);
                foreach (var (name, value) in values ?? [])
                {
                    var uniform = value switch
                    {
                        float single => (SKRuntimeEffectUniform)single,
                        float[] many => many,
                        _ => SKRuntimeEffectUniform.Empty
                    };
                    if (uniforms.Contains(name)) uniforms[name] = uniform;
                }

                SKShader? shader;
                if (content != null && effect.Children.Contains("content"))
                {
                    // Every declared child must be bound: leaving one out makes the whole program
                    // fail to build, which looks exactly like the failure being measured.
                    var children = new SKRuntimeEffectChildren(effect);
                    foreach (var child in effect.Children) children[child] = content;
                    shader = effect.ToShader(false, uniforms, children);
                }
                else
                {
                    shader = effect.ToShader(false, uniforms);
                }

                if (shader == null)
                {
                    GlassDiagnostics.Event($"stage {label}: ToShader returned null");
                    return;
                }

                using (shader)
                using (var paint = new SKPaint { Shader = shader, IsAntialias = false })
                {
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.DrawRect(rect, paint);
                    GlassDiagnostics.Event(
                        $"stage {label}: centre={ReadPixel(surface, (int)(rect.Width / 2f), (int)(rect.Height / 2f))}");
                }
            }
            catch (Exception ex)
            {
                GlassDiagnostics.Failure(ex);
            }
        }

        /// <summary>
        /// Render the material into an offscreen GPU surface and report whether it produced any
        /// opaque pixel. A shader whose device program failed to build draws nothing, which is
        /// indistinguishable from a transparent material except by looking at the result.
        /// </summary>
        private static bool RendersOpaque(ISkiaSharpApiLease lease, SKShader shader, SKRect destination)
        {
            try
            {
                var width = Math.Max(1, (int)destination.Width);
                var height = Math.Max(1, (int)destination.Height);
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var surface = SKSurface.Create(lease.GrContext!, false, info);
                if (surface == null) return false;

                surface.Canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { Shader = shader, IsAntialias = false };
                surface.Canvas.DrawRect(new SKRect(0, 0, width, height), paint);

                var centre = ReadPixel(surface, width / 2, height / 2);
                GlassDiagnostics.Event($"selftest: centre={centre} (alpha {centre.Alpha}) at {width}x{height}");
                return centre.Alpha > 8;
            }
            catch (Exception ex)
            {
                GlassDiagnostics.Failure(ex);
                return false;
            }
        }

        private static SKColor ReadPixel(SKSurface surface, int x, int y)
        {
            using var image = surface.Snapshot();
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            if (!image.ReadPixels(info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) return SKColors.Empty;
            return bitmap.GetPixel(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1));
        }
    }

    /// <summary>
    /// Draws the CPU-rendered material. It exists so that the fallback path owns its bitmap the
    /// same way the GPU path does — a plain <c>DrawImage</c> would leave the composition tree
    /// pointing at a bitmap the surface had already disposed.
    /// </summary>
    private sealed class ImageDrawOperation : ICustomDrawOperation
    {
        private readonly Prepared prepared;
        private int disposed;

        public Rect Bounds { get; }

        public ImageDrawOperation(Prepared prepared, Rect bounds)
        {
            this.prepared = prepared;
            Bounds = bounds;
            prepared.AddRef();
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => obj is ICustomDrawOperation other && Equals(other);

        public override int GetHashCode() => prepared.GetHashCode();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) prepared.Dispose();
        }

        public void Render(ImmediateDrawingContext context)
        {
            var bitmap = prepared.Cpu;
            if (bitmap == null) return;
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature) return;
            using var lease = feature.Lease();
            lease.SkCanvas.DrawBitmap(bitmap,
                new SKRect((float)Bounds.X, (float)Bounds.Y, (float)Bounds.Right, (float)Bounds.Bottom));
        }
    }
}
