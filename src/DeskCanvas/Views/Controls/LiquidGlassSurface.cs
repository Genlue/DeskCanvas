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
using Avalonia.VisualTree;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Services.GlassCapture;
using DeskCanvas.Services.LiquidGlass;
using DeskCanvas.UI.Controls;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// A background-only glass surface. The optical model runs on the GPU whenever the platform gives
/// us a real Ganesh context, and the original CPU renderer stays as the fallback; work is coalesced
/// while moving, resizing or editing optics.
/// <para>
/// This is the single production implementation of the shared <see cref="GlassSurfaceBase"/> port
/// (DeskCanvas.UI): the secondary-panel shell and the panel window drive the surface through that
/// port (corner radius, render requests, animation-frame scale) while every optical property
/// (material, sampling origin, preview flags) stays a host concern here.
/// </para>
/// </summary>
public class LiquidGlassSurface : GlassSurfaceBase
{
    private static readonly HashSet<LiquidGlassSurface> Active = new();
    private static readonly SemaphoreSlim RenderSlots = new(2);

    /// <summary>Set once the render backend is known to lack a GPU; 0 = unknown, 1 = usable, 2 = never.</summary>
    private static int gpuState;

    /// <summary>Set when the first GPU frame is actually drawn, so the trace records it once.</summary>
    private static int drewGpuFrame;


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

    /// <summary>
    /// Check seam for the settle close-out sweep: replaces the per-surface action (the immediate
    /// re-render) with an observer, so the close-out contract is assertable without the render
    /// pipeline. Null in the app; the sweep's SELECTION logic (DemandsFrames + snapshot identity)
    /// always runs regardless.
    /// </summary>
    internal static Action<LiquidGlassSurface, long>? SweepRenderOverride = null;

    /// <summary>
    /// Settle close-out verification: force every live frame-demanding surface whose material was
    /// NOT built from the frame identified by <paramref name="captureId"/> onto that final frame.
    /// <para>
    /// The unified publish path (<see cref="RefreshAllImmediate"/>) drops frames on busy surfaces
    /// by design — and a static desktop never publishes again, so the one surface that was
    /// mid-prepare during the last publish used to sit on the old wallpaper forever ("统一发放后
    /// 部分组件更新失败"). The sweep runs when a settle window closes (and after its deadline
    /// fallback publish): idle surfaces re-render immediately, busy ones get their pending flag
    /// armed so RenderMaterial's finally catches up. Returns the number of surfaces corrected.
    /// </para>
    /// </summary>
    internal static int SweepStaleMaterials(long captureId)
    {
        var corrected = 0;
        foreach (var surface in Active)
        {
            if (!surface.DemandsFrames) continue;
            var current = surface.prepared;
            if (current != null && current.CaptureId == captureId) continue;

            if (surface.busy)
            {
                // Mid-prepare: its Get() already sampled an older frame. Arm the catch-up flag —
                // RenderMaterial's finally will run exactly one follow-up render.
                Interlocked.Exchange(ref surface.pendingSampledFrame, 1);
            }
            else if (SweepRenderOverride is { } hook)
            {
                hook(surface, captureId);
            }
            else
            {
                // Immediate, not through the debounce: the settle window just closed and there
                // will be no further publishes to restart it.
                surface.RenderMaterial();
            }
            corrected++;
        }
        return corrected;
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

    /// <summary>
    /// Sample the <b>live screen</b> instead of the wallpaper (<see cref="ScreenSnapshotProvider"/>).
    /// <para>
    /// Set by consumers that float over other applications (聚焦搜索): the wallpaper pipeline would
    /// refract a desktop that is not actually behind the panel. A capture failure falls back to the
    /// wallpaper, so the surface never goes blank because of this flag.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<bool> ScreenSamplingProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(ScreenSampling));

    /// <summary>
    /// An explicit outline for this surface, or null for the card's rounded rectangle.
    /// <para>
    /// This is the shape half of the material's contract (see <see cref="LiquidGlassShape"/>): any
    /// closed polygon renders with the same refraction, dispersion, coating and highlight, so a host
    /// can morph a card into a blob, a wedge or a hand-built outline without a second code path.
    /// <para>
    /// The shape is <b>shape-local</b>: its <see cref="LiquidGlassShape.Size"/> must equal the
    /// surface bounds (the vertices are in bounds pixels, origin at the centre). Rebuild the shape
    /// from <see cref="LiquidGlassShape.Transformed"/> when the bounds change, or leave this null
    /// and let the surface use its rounded rectangle.
    /// </para>
    /// </para>
    /// </summary>
    public static readonly StyledProperty<LiquidGlassShape?> ShapeProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, LiquidGlassShape?>(nameof(Shape));

    /// <summary>
    /// Compute the wallpaper sampling origin from <b>layout offsets only</b>, ignoring every
    /// ancestor render transform on the way to the window. DEFAULT <c>true</c> for every
    /// wallpaper-sampling surface.
    /// <para>
    /// Avalonia's <c>PointToScreen</c> composes render transforms per visual on the way up, so any
    /// ancestor matrix shifts the sampled region by an amount proportional to the surface's
    /// distance from the transform's anchor — the reported "采样壁纸与实际壁纸错位，位置因素显著"
    /// (some positions off, others fine). The shell's open/close zoom was the first known case
    /// (hence this flag's original opt-in on the secondary-panel shell); the frameless clock and
    /// surfaces nested inside a widget's scaled content
    /// (<see cref="Widget"/> puts a <c>ScaleTransform</c> on its <c>ContentPresenter</c> whenever
    /// ContentScale ≠ 1) are the same defect. The layout-offset walk
    /// (<see cref="GlassSamplingOrigin.Resolve"/>) equals the raw <c>PointToScreen</c> whenever no
    /// transform exists, so the default was flipped to <c>true</c> in 3.8.3. Set <c>false</c> only
    /// to explicitly opt back into the raw path.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<bool> IgnoresAncestorTransformProperty =
        AvaloniaProperty.Register<LiquidGlassSurface, bool>(nameof(IgnoresAncestorTransform), defaultValue: true);

    public Theme? Material { get => GetValue(MaterialProperty); set => SetValue(MaterialProperty, value); }
    public override CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public bool Preview { get => GetValue(PreviewProperty); set => SetValue(PreviewProperty, value); }
    public bool SettingsSurface { get => GetValue(SettingsSurfaceProperty); set => SetValue(SettingsSurfaceProperty, value); }
    public bool ScreenSampling { get => GetValue(ScreenSamplingProperty); set => SetValue(ScreenSamplingProperty, value); }
    public bool IgnoresAncestorTransform { get => GetValue(IgnoresAncestorTransformProperty); set => SetValue(IgnoresAncestorTransformProperty, value); }
    public LiquidGlassShape? Shape { get => GetValue(ShapeProperty); set => SetValue(ShapeProperty, value); }

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

        public Prepared(GlassSource? source, LiquidGlassRenderer.Frame frame, SKBitmap? cpu, long captureId)
        {
            Source = source;
            Frame = frame;
            Cpu = cpu;
            CaptureId = captureId;
        }

        public GlassSource? Source { get; }
        public LiquidGlassRenderer.Frame Frame { get; }
        public SKBitmap? Cpu { get; }

        /// <summary>
        /// Identity of the wallpaper snapshot the material was built from
        /// (<see cref="WallpaperSnapshot.CaptureId"/>). Only the NUMBER is stored — never the
        /// snapshot reference, which would extend the capture's lifetime behind the refcount's
        /// back. The settle close-out sweep compares this against the frame the settle window
        /// ended on to catch a surface whose published material missed the final publish.
        /// </summary>
        public long CaptureId { get; }

        public void AddRef() => Interlocked.Increment(ref references);

        public void Dispose()
        {
            if (Interlocked.Decrement(ref references) > 0) return;
            Cpu?.Dispose();
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

    public LiquidGlassSurface()
    {
        IsHitTestVisible = false;
        debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        debounce.Tick += (_, _) => { debounce.Stop(); RenderMaterial(); };
        ActualThemeVariantChanged += (_, _) => RequestRender();
        ScreenSamplingProperty.Changed.AddClassHandler<LiquidGlassSurface>((surface, _) => surface.RequestRender());
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
        animationScale = 1.0;
        animationShapeActive = false;
        ClearShaderMemo();
        ReleasePrepared();
        GlassInterest.Remove(this);
        LiquidGlassWallpaper.RefreshSamplerState();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e) => RequestRender();

    // ---- Panel open/close animation ----
    private double animationScale = 1.0;
    private Point animationAnchor;

    // ---- Card shape morph (聚焦搜索 bar → card) ----
    private Rect animationShape;
    private double animationShapeRadius;
    private bool animationShapeActive;

    /// <summary>
    /// Report the panel card's current animated scale (1.0 = at rest) and the scale anchor as a
    /// relative point inside this surface's bounds. Every value change repaints the surface with
    /// optics recomputed for the covered rect; the render pipeline itself (backdrop, debounce,
    /// prepare) is untouched.
    /// </summary>
    public override void SetAnimationFrameScale(double scale, Point anchorRelativeToSurface)
    {
        if (!attached || Material?.UsesRenderedGlass != true) return;
        animationShapeActive = false;
        var clamped = Math.Clamp(scale, 0.05, 1.0);
        if (clamped >= 0.999)
        {
            if (animationScale >= 0.999) return;
            animationScale = 1.0;
            InvalidateVisual();
            return;
        }
        animationScale = clamped;
        animationAnchor = anchorRelativeToSurface;
        InvalidateVisual();
    }

    /// <summary>
    /// Report the card rectangle (DIP, relative to this surface) the current morph frame covers
    /// and the radius it is drawn with — the shape twin of <see cref="SetAnimationFrameScale"/>.
    /// A height morph keeps the surface's own bounds (the glass control still fills the window)
    /// but only the covered sub-rect is drawn, sampling the backdrop at the desktop position that
    /// rect really sits at, so the refracted content never swims. Passing the surface's own
    /// bounds and its styled radius returns the material to rest.
    /// </summary>
    public override void SetAnimationFrameShape(Rect rect, double cornerRadius)
    {
        if (!attached || Material?.UsesRenderedGlass != true) return;

        // At rest: the rect is (within layout rounding) the whole surface at its styled radius.
        if (Math.Abs(rect.X) < 0.5 && Math.Abs(rect.Y) < 0.5
            && Math.Abs(rect.Width - Bounds.Width) < 0.5
            && Math.Abs(rect.Height - Bounds.Height) < 0.5
            && Math.Abs(cornerRadius - CornerRadius.TopLeft) < 0.5)
        {
            if (!animationShapeActive && animationScale >= 0.999) return;
            animationShapeActive = false;
            animationScale = 1.0;
            InvalidateVisual();
            return;
        }

        animationShape = rect;
        animationShapeRadius = Math.Max(0, cornerRadius);
        animationShapeActive = true;
        animationScale = 1.0;
        InvalidateVisual();
    }

    /// <summary>The frame geometry for the card rect the transition currently covers.</summary>
    private LiquidGlassRenderer.Frame BuildAnimationFrame(Prepared snapshot)
    {
        var frame = snapshot.Frame;

        // The shape morph: width, height and radius travel independently, and the sampling
        // origin must stay in DESKTOP coordinates — the surface's own origin plus the rect's
        // offset, both in render pixels. Everything else (screen geometry, theme, quality) is
        // the frame's own.
        if (animationShapeActive)
        {
            var shapeScale = frame.Scale;
            return frame with
            {
                Width = Math.Max(1, (int)MathF.Round((float)animationShape.Width * shapeScale)),
                Height = Math.Max(1, (int)MathF.Round((float)animationShape.Height * shapeScale)),
                Radius = (float)animationShapeRadius,
                DesktopX = frame.DesktopX + (float)animationShape.X * shapeScale,
                DesktopY = frame.DesktopY + (float)animationShape.Y * shapeScale,
            };
        }

        var t = animationScale;
        return frame with
        {
            Width = Math.Max(1, (int)MathF.Round(frame.Width * (float)t)),
            Height = Math.Max(1, (int)MathF.Round(frame.Height * (float)t)),
            Radius = (float)(frame.Radius * t),
            DesktopX = frame.DesktopX + (float)(animationAnchor.X * (1 - t) * frame.Width),
            DesktopY = frame.DesktopY + (float)(animationAnchor.Y * (1 - t) * frame.Height),
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MaterialProperty || change.Property == CornerRadiusProperty
            || change.Property == BoundsProperty || change.Property == IsVisibleProperty
            || change.Property == PreviewProperty || change.Property == SettingsSurfaceProperty
            || change.Property == ShapeProperty)
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
    internal void RequestSampledFrame()
    {
        if (!attached || !IsEffectivelyVisible || Material?.UsesRenderedGlass != true) return;
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

    /// <summary>Ask for a coalesced (debounced) render — the historic no-argument entry point.</summary>
    public void RequestRender() => RequestRender(immediate: false);

    public override void RequestRender(bool immediate)
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

    // ---- material shader memo -------------------------------------------------------------------
    // Rebuilding the material shader is not per-pixel work, it is per-frame work, and a live sample
    // draws tens of frames per second: a fresh SKRuntimeEffect shader over a fresh child bitmap
    // shader, which the GPU backend answers by uploading the whole backdrop again. On a still
    // desktop — the launcher's normal state — the backdrop, the optics, the card box and the shape
    // are identical from frame to frame, so the shader is identical too, and the rebuild buys
    // nothing. This memo hands the same shader back as long as every input that ends up in a
    // uniform is unchanged; the pixels are bit-identical by construction because the SkSL and its
    // uniform values are the same ones the rebuild would have produced.

    /// <summary>
    /// Identity of a built material shader: everything that goes into the SkSL uniforms, plus the
    /// backdrop it samples.
    /// <para>
    /// Every field is a value except <see cref="Source"/>, which is compared by reference on
    /// purpose — a different backdrop is a different capture, and the shader holds the old one's
    /// pixels alive. Polygon vertices are folded into <see cref="ShapeHash"/> rather than held.
    /// </para>
    /// </summary>
    private readonly record struct MaterialShaderKey(
        GlassSource Source,
        LiquidGlassEffect.Params Params,
        float ShapeWidth, float ShapeHeight, float ShapeRadius, float ShapeGradient,
        int ShapeEdges, int ShapeHash);

    private readonly object shaderGate = new();
    private MaterialShaderKey shaderKey;
    private SKShader? shaderMemo;

    private static MaterialShaderKey ShaderKeyFor(GlassSource source, in LiquidGlassEffect.Params p,
        LiquidGlassShape? shape)
    {
        // A null shape means "the card's own rounded rectangle", which CreateShader derives from the
        // parameters — those are already part of the key.
        if (shape == null) return new MaterialShaderKey(source, p, 0f, 0f, 0f, 0f, 0, 0);

        var hash = 17;
        foreach (var vertex in shape.Vertices)
        {
            hash = unchecked(hash * 31 + BitConverter.SingleToInt32Bits(vertex.X));
            hash = unchecked(hash * 31 + BitConverter.SingleToInt32Bits(vertex.Y));
        }

        return new MaterialShaderKey(source, p, shape.Size.Width, shape.Size.Height,
            shape.CornerRadius, shape.GradientRadius, shape.EdgeCount, hash);
    }

    /// <summary>
    /// The material shader for one draw, reused when the previous frame's inputs are identical, or
    /// null when the runtime shader cannot be built (the caller falls back to the plain backdrop).
    /// <para>
    /// <b>Lifetime:</b> the memo owns exactly one shader. Evicting it is safe even while a paint
    /// still refers to it (Skia refcounts the native object, so the paint keeps it alive), and a
    /// surface's draw operations are rendered one pass at a time on a single thread, so a swap can
    /// never pull a shader out from under a draw that is in flight.
    /// </para>
    /// </summary>
    private SKShader? AcquireShader(in MaterialShaderKey key, Func<SKShader?> build)
    {
        lock (shaderGate)
        {
            if (shaderMemo != null && shaderKey.Equals(key)) return shaderMemo;
        }

        var shader = build();
        if (shader == null) return null;

        SKShader? retired;
        lock (shaderGate)
        {
            if (shaderMemo != null && shaderKey.Equals(key))
            {
                // Another thread finished the same shader first: keep its one, drop ours.
                retired = shader;
                shader = shaderMemo;
            }
            else
            {
                retired = shaderMemo;
                shaderMemo = shader;
                shaderKey = key;
            }
        }

        try { retired?.Dispose(); } catch { }
        return shader;
    }

    /// <summary>Drop the memoized shader (the surface is going away, or the GPU context was lost).</summary>
    private void ClearShaderMemo()
    {
        SKShader? dropped;
        lock (shaderGate)
        {
            dropped = shaderMemo;
            shaderMemo = null;
            shaderKey = default;
        }

        try { dropped?.Dispose(); } catch { }
    }

    private async void RenderMaterial()
    {
        // Same visibility rule as DemandsFrames (IsEffectivelyVisible): a surface inside a
        // collapsed parent must not prepare, and the two guards used to disagree — a hidden
        // shell's glass could consume a publish while DemandsFrames said it wanted none.
        if (busy || !attached || !IsEffectivelyVisible || Material?.UsesRenderedGlass != true || Bounds.Width < 1 || Bounds.Height < 1) return;
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
            var path = "none";

            if (!attached || revision != current) return;
            var preview = Preview;
            // Read the styled property here: the prepare below runs on a worker thread, and a
            // StyledProperty must not be touched from there.
            var screenSampling = ScreenSampling;
            // Tell the capture layer where this card is, so the next capture samples its
            // neighbourhood instead of the whole desktop. UI thread, and before the capture below.
            ReportSamplingInterest(frame, screenSampling);
            // Compiling the runtime shader is a one-off cost, so it is resolved off the UI
            // thread together with the first backdrop.
            var wantGpu = Volatile.Read(ref gpuState) != 2;
            var result = await Task.Run<(GlassSource? Source, WallpaperSnapshot Wallpaper, int CaptureMs, int BackdropMs, string Sample)>(() =>
            {
                // Split the two stages that actually cost: grabbing the desktop, and building the
                // shared blurred backdrop from it. Both are per capture, not per pixel of the card.
                var captureStarted = Environment.TickCount64;
                // The launcher's own pipeline (live screen), with the wallpaper as the fallback so a
                // refused capture still yields a usable backdrop. Which one answered is recorded in
                // the trace: "the glass still shows the wallpaper" is otherwise unanswerable from
                // the outside, and the fallback is silent by design.
                var screen = screenSampling ? ScreenSnapshotProvider.Get() : null;
                var snapshot = screen ?? LiquidGlassWallpaper.Get();
                var sample = screen != null ? "screen" : "wallpaper";
                var captureMs = (int)(Environment.TickCount64 - captureStarted);
                // The preview only overrides the wallpaper's placement. The copy takes its own
                // reference, so exactly one reference leaves this lambda on every path.
                var sampled = preview ? snapshot.WithPlacement("10", false) : snapshot;
                if (preview) snapshot.Dispose();
                try
                {
                    if (wantGpu && LiquidGlassEffect.IsSupported)
                    {
                        var backdropStarted = Environment.TickCount64;
                        var shared = LiquidGlassSourceCache.Get(frame, sampled);
                        var backdropMs = (int)(Environment.TickCount64 - backdropStarted);
                        if (shared != null)
                        {
                            return (shared, sampled, captureMs, backdropMs, sample);
                        }
                    }
                    return (null, sampled, captureMs, 0, sample);
                }
                catch
                {
                    sampled.Dispose();
                    throw;
                }
            });

            // That own reference becomes the surface's; RetirePrepared releases it later.
            nextSource = result.Source;
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
                    nextCpu = await Task.Run(() => LiquidGlassRenderer.RenderBitmap(frame, wallpaper!));
                }
                finally { RenderSlots.Release(); }
            }

            if (!attached || revision != current)
            {
                GlassDiagnostics.Note($"discarded ({path}: geometry changed)", started, frame);
                RetirePrepared(new Prepared(nextSource, frame, null, wallpaper.CaptureId));
                return;
            }

            // Never publish an empty material. Doing so would replace working glass with the faint
            // placeholder and blank the card, which is strictly worse than showing a stale frame.
            if (nextSource == null && nextCpu == null)
            {
                GlassDiagnostics.Note($"empty ({path}) — keeping the previous material", started, frame);
                RetirePrepared(new Prepared(null, frame, null, wallpaper.CaptureId));
                return;
            }

            var previous = Interlocked.Exchange(ref prepared, new Prepared(nextSource, frame, nextCpu, wallpaper.CaptureId));
            RetirePrepared(previous);
            GlassDiagnostics.Published(Environment.TickCount64 - started,
                $"{path} {frame.Width}x{frame.Height} sample {result.Sample} capture {result.CaptureMs}ms " +
                $"backdrop {result.BackdropMs}ms captures {LiquidGlassWallpaper.CaptureCount}");
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
                // which supersedes anything the pending flag could ask for — but the flag STAYS
                // SET. Clearing it here used to lose the dropped frame for good when revisions
                // kept flipping (a settle publishing while the user drags): each re-render
                // restarted the 90 ms debounce, the storm outlived every publisher, and the
                // surface sat on old material for the whole settle window (5×750 ms discards in
                // the 3.8.2 trace). Keeping the flag means the re-render that finally completes
                // with a stable revision consumes it and runs exactly one catch-up render; the
                // settle close-out sweep is the second net under a storm that never ends.
                RequestRender();
            }
            else if (attached && Interlocked.Exchange(ref pendingSampledFrame, 0) == 1)
            {
                // Catch up with the sampled frame that was dropped while this prepare ran.
                RenderMaterial();
            }
        }
    }

    /// <summary>
    /// Publish this surface's sampling footprint to <see cref="GlassInterest"/>.
    /// <para>
    /// The capture layer samples the union of these rectangles, so the cost of the desktop grab
    /// tracks how much glass is on screen rather than the desktop's size. The margin is the blur's
    /// own reach (<c>3σ</c>, σ = 模糊 / 8 in desktop pixels): the shader's refraction reads up to
    /// that far outside the card, and a patch that stopped at the card edge would clamp to it.
    /// </para>
    /// </summary>
    private void ReportSamplingInterest(LiquidGlassRenderer.Frame frame, bool screenSampling)
    {
        if (!attached) return;

        var sigma = (float)frame.Theme.EffectiveGlass.Blur * frame.EffectiveBackdropScale / 8f;
        var margin = (int)MathF.Ceiling(Math.Max(sigma * 3f, 48f));
        var rect = CaptureRect.FromSize(
            (int)MathF.Round(frame.DesktopX),
            (int)MathF.Round(frame.DesktopY),
            frame.Width,
            frame.Height);

        // The preview cards draw a synthetic 120×90 desktop rather than the real one, so their
        // backdrop has to be the whole screen — a patch around the control would be nonsense.
        GlassInterest.Report(this, rect, margin, screenSampling, fullDesktop: Preview);

        // Trace only when the answer changes. "Which rectangle is being sampled" is otherwise
        // invisible, and it is the first thing to check when the glass shows the wrong piece of
        // desktop or the capture looks more expensive than it should.
        var region = screenSampling ? GlassInterest.ScreenSamplingRegion() : GlassInterest.SamplingRegion();
        if (region == lastReportedRegion) return;
        lastReportedRegion = region;
        GlassDiagnostics.Event(
            $"[capture] sampling region {region} ({GlassInterest.Count} surface(s), " +
            $"card {rect} margin {margin}{(Preview ? ", preview → full screen" : string.Empty)})");
    }

    private static CaptureRect lastReportedRegion;

    private LiquidGlassRenderer.Frame BuildFrame()
    {
        var scaling = window?.RenderScaling ?? 1;
        // On the GPU path the backdrop is already downscaled by BackdropClarity and the shader
        // runs at native window bounds; only the CPU fallback path needs per-card quality
        // downscaling. Keeping quality = 1 on GPU ensures large cards stay in physical desktop
        // coordinates and share the same GlassSource as normal cards.
        var useGpu = Volatile.Read(ref gpuState) != 2 && LiquidGlassEffect.IsSupported;
        var quality = useGpu
            ? 1.0
            : Math.Min(1, Math.Min(2048 / (Math.Max(Bounds.Width, Bounds.Height) * scaling),
                Math.Sqrt(1200000 / (Bounds.Width * Bounds.Height * scaling * scaling))));
        var renderScale = (float)(scaling * quality);
        var width = Math.Max(1, (int)Math.Ceiling(Bounds.Width * renderScale));
        var height = Math.Max(1, (int)Math.Ceiling(Bounds.Height * renderScale));
        var screens = window?.Screens.All;
        var primaryScaling = screens?.FirstOrDefault(s => s.IsPrimary)?.Scaling
                             ?? screens?.FirstOrDefault()?.Scaling
                             ?? scaling;
        var backdropScale = (float)(primaryScaling * quality);
        // Shared desktop geometry (extent + sampling origin), the one source of truth the clock's
        // glyph renderer also uses — see GlassSamplingOrigin. The sampling origin walks layout
        // offsets by default (IgnoresAncestorTransform), so an ancestor's mid-zoom matrix or a
        // content scale can never shift the wallpaper region the card samples.
        var geometry = GlassSamplingOrigin.FrameDesktopGeometry(window, this, quality);
        var widget = window as Widget;
        var (cols, rows) = widget?.CurrentSpan ?? (0, 0);
        var material = Material ?? new Theme(null, null, 0.18, true, false, "Microsoft YaHei UI", SurfaceStyle.LiquidGlass);
        var frame = new LiquidGlassRenderer.Frame(width, height, renderScale, (float)CornerRadius.TopLeft,
            geometry.DesktopX, geometry.DesktopY, geometry.DesktopWidth, geometry.DesktopHeight,
            geometry.ScreenX, geometry.ScreenY, geometry.ScreenWidth, geometry.ScreenHeight,
            material, ActualThemeVariant == ThemeVariant.Dark, SettingsSurface, (float)quality,
            Columns: cols, Rows: rows, BackdropScale: backdropScale);
        if (!Preview) return frame;
        // The preview card is tiny (45×45 in the theme button): calm the material down so the
        // miniature shows the recipe rather than a bloated lens.
        return frame with
        {
            DesktopX = 70 * renderScale, DesktopY = 40 * renderScale, ScreenX = 0, ScreenY = 0,
            DesktopWidth = 120 * renderScale, DesktopHeight = 90 * renderScale,
            ScreenWidth = 120 * renderScale, ScreenHeight = 90 * renderScale,
            BackdropScale = renderScale,
            Theme = frame.Theme with
            {
                LiquidGlass = frame.Theme.EffectiveLiquidGlass with
                {
                    Blur = frame.Theme.EffectiveLiquidGlass.Blur * 0.35,
                    Refraction = frame.Theme.EffectiveLiquidGlass.Refraction * 0.35
                }
            }
        };
    }

    /// <summary>
    /// The surface's top-left corner in screen (physical) pixels, computed from layout offsets
    /// only — see <see cref="GlassSamplingOrigin.Resolve"/> (the shared implementation the
    /// frameless clock uses too). Kept as a thin wrapper so the panel-shell checks can keep
    /// asserting the walk's behaviour directly.
    /// </summary>
    private PixelPoint DesktopPositionIgnoringRenderTransforms()
        => GlassSamplingOrigin.Resolve(this, window);

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
            if (animationShapeActive || animationScale < 0.999)
            {
                // The per-scale re-parameterisation is polish, never worth a render-thread
                // exception over: on any surprise, draw the at-rest material.
                try
                {
                    var animatedFrame = BuildAnimationFrame(current);
                    // The shape morph draws only the covered sub-rect (that is the whole point:
                    // the glass follows the card's edge, not the window); the uniform zoom keeps
                    // drawing the full bounds, scaled by the transition.
                    var dest = animationShapeActive ? animationShape : rect;
                    context.Custom(new GlassDrawOperation(this, current, Material, dest, animatedFrame, Shape));
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Animated glass frame failed: {ex}");
                    animationScale = 1.0;
                    animationShapeActive = false;
                }
            }
            context.Custom(new GlassDrawOperation(this, current, Material, rect, null, Shape));
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
    /// </summary>
    private sealed class GlassDrawOperation : ICustomDrawOperation
    {
        private readonly LiquidGlassSurface owner;
        private readonly Prepared prepared;
        private readonly GlassSource source;
        private readonly LiquidGlassRenderer.Frame frame;
        private readonly Theme material;
        private readonly LiquidGlassShape? shape;
        private int disposed;

        public Rect Bounds { get; }

        public GlassDrawOperation(LiquidGlassSurface owner, Prepared prepared, Theme material, Rect bounds,
            LiquidGlassRenderer.Frame? frameOverride = null, LiquidGlassShape? shape = null)
        {
            this.owner = owner;
            this.prepared = prepared;
            source = prepared.Source!;
            frame = frameOverride ?? prepared.Frame;
            this.material = material;
            this.shape = shape;
            Bounds = bounds;
            prepared.AddRef();
        }

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);

        public override bool Equals(object? obj) => obj is ICustomDrawOperation other && Equals(other);

        public override int GetHashCode() => source.GetHashCode();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                prepared.Dispose();
            }
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
                owner.ClearShaderMemo();
                LiquidGlassSourceCache.Clear();
                DrawBackdropPatch(lease.SkCanvas, source, frame, destination);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var surface in Active) surface.RequestRender(immediate: false);
                });
                return;
            }

            var parameters = LiquidGlassEffect.BuildParams(frame, source.Scale,
                (float)Bounds.Width, (float)Bounds.Height, source.RegionX, source.RegionY) with
            {
                DestOriginX = (float)Bounds.X,
                DestOriginY = (float)Bounds.Y
            };

            // Memoized: an unchanged frame (same capture, same optics, same box, same shape) reuses
            // the shader built last time instead of rebuilding one and re-uploading the backdrop.
            var shader = owner.AcquireShader(ShaderKeyFor(source, parameters, shape),
                () => LiquidGlassEffect.Create(source.Backdrop, parameters, shape));
            if (shader == null)
            {
                GlassDiagnostics.Event("draw: runtime shader could not be built — drawing the plain backdrop");
                DrawBackdropPatch(lease.SkCanvas, source, frame, destination);
                return;
            }

            if (Interlocked.Exchange(ref drewGpuFrame, 1) == 0)
                GlassDiagnostics.Event($"draw: first GPU frame at {destination.Width:F0}x{destination.Height:F0} " +
                                       $"(bounds {Bounds.X:F0},{Bounds.Y:F0}), backdrop {source.Backdrop.Width}x{source.Backdrop.Height} scale={source.Scale:F3}");

            if (Interlocked.Exchange(ref selfTested, 1) == 0 && !RendersOpaque(lease, shader, destination))
            {
                Interlocked.Exchange(ref gpuState, 2);
                owner.ClearShaderMemo();
                LiquidGlassSourceCache.Clear();
                GlassDiagnostics.Event("selftest: the GPU material renders empty — switching to the CPU material");
                DrawBackdropPatch(lease.SkCanvas, source, frame, destination);
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var surface in Active) surface.RequestRender(immediate: false);
                });
                return;
            }

            using var paint = new SKPaint { Shader = shader, IsAntialias = true };
            lease.SkCanvas.DrawRect(destination, paint);
        }

        /// <summary>
        /// Degraded draw: the part of the backdrop this card covers, without the optics.
        /// <para>
        /// A region-limited backdrop is a patch, so drawing the whole bitmap into the card rect
        /// would show a piece of desktop that is not behind the card. The patch is addressed the
        /// same way the shader addresses it — desktop position minus the patch origin, times the
        /// patch scale — which keeps this fallback geometrically honest.
        /// </para>
        /// </summary>
        private static void DrawBackdropPatch(SKCanvas canvas, GlassSource source,
            LiquidGlassRenderer.Frame frame, SKRect destination)
        {
            var bitmap = source.Backdrop;
            var scale = source.Scale;
            var left = (frame.DesktopX - source.RegionX) * scale;
            var top = (frame.DesktopY - source.RegionY) * scale;
            var right = left + frame.Width * scale;
            var bottom = top + frame.Height * scale;

            var sourceRect = new SKRect(
                Math.Clamp(left, 0, bitmap.Width),
                Math.Clamp(top, 0, bitmap.Height),
                Math.Clamp(right, 0, bitmap.Width),
                Math.Clamp(bottom, 0, bitmap.Height));
            if (sourceRect.Width <= 0 || sourceRect.Height <= 0)
            {
                canvas.DrawBitmap(bitmap, destination);
                return;
            }

            canvas.DrawBitmap(bitmap, sourceRect, destination);
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
