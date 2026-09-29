using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using uWidgets.Services;
using uWidgets.Views.Controls;

namespace uWidgets.Views;

/// <summary>One pre-rendered glass frame of a panel's open/close animation, drawn for the card rect covered at <see cref="Scale"/>.</summary>
public readonly record struct PanelAnimationFrame(double Scale, Avalonia.Media.Imaging.Bitmap Bitmap);

/// <summary>
/// Shared template for the widgets' secondary panel windows (weather forecast, reminders,
/// big folder, clipboard). Everything a panel needs to behave identically lives here:
///   - spawn position: centered on the opening widget (clamped to the screen's work area),
///   - open transition: the card scales up straight out of the opening widget (render
///     transform origin pinned to the widget's center) while fading in, on the Material 3
///     "emphasized decelerate" curve,
///   - close transition: the card folds back into the widget on the "emphasized accelerate" curve,
///   - dismissal: Escape, focus loss (after a grace period), or an explicit Close(),
///   - per-panel-type rules: one instance at a time and no immediate re-open after a close.
/// Derived windows keep their own AXAML, theming and content; they expose the animatable
/// card via <see cref="PanelCard"/> and call <see cref="InitializePanel"/> right after
/// InitializeComponent. <see cref="Close"/> runs the exit animation before really closing.
/// </summary>
public abstract class SecondaryPanelWindow : Window
{
    private static readonly Dictionary<Type, SecondaryPanelWindow> ActivePanels = new();
    private static readonly Dictionary<Type, DateTime> LastCloseTimes = new();

    // Material Design 3 motion tokens (https://m3.material.io/styles/motion/easing-and-duration/tokens-specs):
    // emphasized decelerate going in, emphasized accelerate going out.
    private static readonly SplineEasing OpenEasing = new(new KeySpline(0.05, 0.7, 0.1, 1.0));
    private static readonly SplineEasing CloseEasing = new(new KeySpline(0.3, 0.0, 0.8, 0.15));

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(180);
    // Transitions start on the first rendered frame after the value change; pad the close
    // timer so base.Close() never fires while the exit transition is still running.
    private static readonly TimeSpan CloseTimerDuration = CloseDuration + TimeSpan.FromMilliseconds(40);

    // The card grows out of the widget from 60% of its final size — small enough that the
    // anchored origin reads clearly, large enough that text never looks scrambled mid-flight.
    private const double OpenScaleFrom = 0.60;
    private const double CloseScaleTo = 0.60;
    private const int ReopenCooldownMs = 250;

    private ScaleTransform? cardScale;
    private bool panelLoaded;
    private bool closeAnimationRunning;
    private bool wasActivated;
    private DateTime activatedAtUtc;

    // ---- Glass transition during the open/close zoom ----
    // The card's glass must follow the zoom: with live wallpaper sampling the GPU surface
    // re-parameterises its shader for every interpolated scale, and without it a strip of
    // pre-rendered frames (one per covered scale, cached by PopupLiquidGlassService) is played
    // back through the card's backdrop image. Both paths are driven by the scale transform's
    // per-tick property changes, so they stay exactly in phase with the visible zoom.
    private RelativePoint? spawnAnchor;
    private LiquidGlassSurface? glassSurface;
    private Image? glassBackdropImage;
    private IImage? glassRestSource;
    private bool usingGlassFrames;
    private bool glassFramesPending;
    private IReadOnlyList<PanelAnimationFrame>? glassFrames;

    /// <summary>The card visual that carries the open/close transition (the panel's root border).</summary>
    protected abstract Visual? PanelCard { get; }

    /// <summary>Physical-pixel center of the widget the panel spawns from; null centers on the primary work area.</summary>
    protected abstract Point? SpawnScreenCenter { get; }

    /// <summary>
    /// Corner radius (DIP) propagated from the opening widget's card so the panel's corners
    /// match it exactly; 0 keeps the panel's own AXAML radius. Must be handed to
    /// <see cref="InitializePanel"/> before derived theming runs, so every glass path
    /// (live surface, pre-rendered bitmap, native window region) picks it up.
    /// </summary>
    protected double SpawnCornerRadius { get; private set; }

    /// <summary>
    /// Whether the card may scale and rise. Must stay off when the panel clips itself with a
    /// native window region (SetWindowRgn): the region cannot follow a render transform, so
    /// the transition falls back to opacity only.
    /// </summary>
    protected virtual bool TransformAnimationEnabled => true;

    /// <summary>How long the panel tolerates losing focus right after taking it before auto-closing.</summary>
    protected virtual int DeactivateCloseGraceMs => 300;

    /// <summary>When the panel's window finished loading; starts at MinValue until then.</summary>
    protected DateTime LoadedAtUtc { get; private set; } = DateTime.MinValue;

    /// <summary>True while the exit animation is playing (all close paths are no-ops then).</summary>
    protected bool CloseAnimationRunning => closeAnimationRunning;

    /// <summary>Extra load work (focus, glass pre-render, window region, hooks); positioning and the open animation are handled by the base.</summary>
    protected virtual void OnPanelLoaded() { }

    /// <summary>Return true to consume Escape (e.g. close an inner drawer instead of the panel).</summary>
    protected virtual bool OnPanelEscape() => false;

    /// <summary>
    /// Called by derived constructors right after InitializeComponent: applies the spawn
    /// corner radius (before theming runs, so glass surfaces inherit it), hides the card,
    /// wires the panel behavior, and registers the window as its type's active panel.
    /// </summary>
    protected void InitializePanel(double? cornerRadius = null)
    {
        SpawnCornerRadius = cornerRadius ?? 0;

        var card = PanelCard;
        if (card != null)
        {
            if (SpawnCornerRadius > 0 && card is Border cardBorder)
                cardBorder.CornerRadius = new CornerRadius(SpawnCornerRadius);

            card.Opacity = 0.0;
            if (TransformAnimationEnabled)
            {
                cardScale = card.RenderTransform as ScaleTransform ?? new ScaleTransform();
                card.RenderTransform = cardScale;
                cardScale.ScaleX = OpenScaleFrom;
                cardScale.ScaleY = OpenScaleFrom;
                // Every interpolated tick drives the glass with the scale it covers.
                cardScale.PropertyChanged += OnCardScalePropertyChanged;
            }
            else if (card.RenderTransform is ScaleTransform staleScale)
            {
                // Panels that clip themselves with a native window region must not carry
                // the AXAML's decorative zoom transform — it would shrink the card forever.
                staleScale.ScaleX = 1.0;
                staleScale.ScaleY = 1.0;
            }
        }

        ActivePanels[GetType()] = this;

        Loaded += OnPanelWindowLoaded;
        Activated += OnPanelWindowActivated;
        Deactivated += OnPanelWindowDeactivated;
        Closed += OnPanelWindowClosed;
        KeyDown += OnPanelKeyDown;
    }

    /// <summary>Show + pin above the widget band + activate — the standard way every panel appears.</summary>
    protected void ShowAsSecondaryPanel(Window? owner)
    {
        if (owner != null)
            Show(owner);
        else
            Show();

        // Secondary panel: above the widget band, below ordinary application windows.
        WidgetZOrder.PinPanelAboveWidgets(this);

        Activate();
    }

    /// <summary>True within <paramref name="cooldownMs"/> after this panel type was last closed (re-open flicker guard).</summary>
    protected static bool PanelCoolingDown<T>(int cooldownMs = ReopenCooldownMs) where T : SecondaryPanelWindow
    {
        return LastCloseTimes.TryGetValue(typeof(T), out var closedAt)
               && (DateTime.UtcNow - closedAt).TotalMilliseconds < cooldownMs;
    }

    /// <summary>
    /// Close this panel type's active instance (animated). Returns true when there was one,
    /// in which case the caller must not open a new panel — this is the "toggle" path.
    /// </summary>
    protected static bool TryCloseActivePanel<T>() where T : SecondaryPanelWindow
    {
        if (!ActivePanels.TryGetValue(typeof(T), out var panel)) return false;
        try { panel.Close(); } catch { }
        return true;
    }

    private void OnPanelWindowLoaded(object? sender, RoutedEventArgs e)
    {
        panelLoaded = true;
        LoadedAtUtc = DateTime.UtcNow;
        PositionPanel();
        OnPanelLoaded();
        PlayOpenAnimation();
    }

    private void OnPanelWindowActivated(object? sender, EventArgs e)
    {
        if (wasActivated) return;
        wasActivated = true;
        activatedAtUtc = DateTime.UtcNow;
    }

    private void OnPanelWindowDeactivated(object? sender, EventArgs e)
    {
        if (closeAnimationRunning || !wasActivated) return;
        if ((DateTime.UtcNow - activatedAtUtc).TotalMilliseconds < DeactivateCloseGraceMs) return;
        Close();
    }

    private void OnPanelWindowClosed(object? sender, EventArgs e)
    {
        var type = GetType();
        if (ActivePanels.TryGetValue(type, out var current) && ReferenceEquals(current, this))
            ActivePanels.Remove(type);
        LastCloseTimes[type] = DateTime.UtcNow;
        SetGlassFramesNotification(false);
    }

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || closeAnimationRunning) return;
        if (OnPanelEscape())
        {
            e.Handled = true;
            return;
        }
        Close();
        e.Handled = true;
    }

    private void PlayOpenAnimation()
    {
        var card = PanelCard;
        if (card == null) return;

        // Pin the scale origin to the opening widget's center (panel position is already
        // final at this point), so the card visibly grows out of the widget. The origin is
        // set while the card is still invisible — a late layout can't cause a visible jump.
        var anchor = ComputeSpawnAnchorOrigin();
        spawnAnchor = anchor;
        ResolveGlassAnimationTargets();
        if (TransformAnimationEnabled && cardScale != null && anchor.HasValue)
            card.RenderTransformOrigin = anchor.Value;

        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = OpenDuration, Easing = OpenEasing }
        };

        if (TransformAnimationEnabled && cardScale != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = OpenDuration, Easing = OpenEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = OpenDuration, Easing = OpenEasing }
            };

            cardScale.ScaleX = 1.0;
            cardScale.ScaleY = 1.0;
        }

        card.Opacity = 1.0;
    }

    // ---- Glass transition playback ----

    /// <summary>
    /// Resolve the glass visuals once, at Loaded: the live surface (any panel has exactly one)
    /// and the pre-rendered backdrop image, keeping the image's at-rest source so frame
    /// playback can put it back when the zoom settles.
    /// </summary>
    private void ResolveGlassAnimationTargets()
    {
        glassSurface = this.GetVisualDescendants().OfType<LiquidGlassSurface>().FirstOrDefault();
        glassBackdropImage = this.GetVisualDescendants()
            .OfType<Image>()
            .FirstOrDefault(v => v.Name == "LiquidGlassBgImage");
        glassRestSource = glassBackdropImage?.Source;
        if (glassBackdropImage != null)
            SetGlassFramesNotification(true);
    }

    private void OnCardScalePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (!panelLoaded) return;
        if (e.Property != ScaleTransform.ScaleXProperty && e.Property != ScaleTransform.ScaleYProperty) return;
        UpdateGlassForScale(cardScale?.ScaleX ?? 1.0);
    }

    protected void OnGlassFramesReady()
    {
        // A strip finished rendering (hover prewarm) or loading from disk mid-zoom: catch up.
        if (!panelLoaded || glassFrames != null) return;
        glassFramesPending = false;
        UpdateGlassForScale(cardScale?.ScaleX ?? 1.0);
    }

    /// <summary>Feed the interpolated card scale to whichever glass path is active.</summary>
    private void UpdateGlassForScale(double t)
    {
        var card = PanelCard;
        if (card == null) return;

        if (t >= 0.999)
        {
            RestoreGlassAtRest();
            return;
        }

        // Live wallpaper sampling: the surface recomputes its optics for the rect the card
        // currently covers — real glass at animation rate.
        if (glassSurface is { IsVisible: true })
        {
            glassSurface.SetAnimationFrameScale(t, AnchorRelativeToSurface(glassSurface, card));
            return;
        }

        // Pre-rendered strip: swap in the frame drawn for the closest covered scale. The
        // frames fill the card, so the card's own zoom maps each one 1:1 onto its rect.
        var image = glassBackdropImage;
        if (image == null || !image.IsVisible) return;

        if (glassFrames == null)
        {
            if (glassFramesPending) return;
            glassFramesPending = true;
            glassFrames = QueryGlassFrames();
            if (glassFrames == null) return;
        }

        PanelAnimationFrame? match = null;
        foreach (var frame in glassFrames)
        {
            if (frame.Scale >= t - 0.002)
            {
                match = frame;
                break;
            }
        }
        match ??= glassFrames[glassFrames.Count - 1];

        usingGlassFrames = true;
        image.Source = match.Value.Bitmap;
    }

    private void RestoreGlassAtRest()
    {
        glassSurface?.SetAnimationFrameScale(1.0, default);
        if (!usingGlassFrames) return;
        usingGlassFrames = false;
        if (glassBackdropImage != null && glassRestSource != null)
            glassBackdropImage.Source = glassRestSource;
    }

    /// <summary>The card-space anchor re-expressed inside the glass surface's bounds.</summary>
    private Point AnchorRelativeToSurface(LiquidGlassSurface surface, Visual card)
    {
        var anchor = spawnAnchor ?? new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        var point = anchor.ToPixels(card.Bounds.Size);
        var topLeft = surface.TranslatePoint(new Point(0, 0), card);
        var size = surface.Bounds.Size;
        if (topLeft == null || size.Width <= 0 || size.Height <= 0) return point;
        return new Point(
            (point.X - topLeft.Value.X) / size.Width,
            (point.Y - topLeft.Value.Y) / size.Height);
    }

    /// <summary>The screen the panel spawns on (primary when the spawn point is unknown).</summary>
    protected Screen? ResolveSpawnScreen()
    {
        if (SpawnScreenCenter.HasValue)
        {
            var spawn = new PixelPoint(
                (int)Math.Round(SpawnScreenCenter.Value.X),
                (int)Math.Round(SpawnScreenCenter.Value.Y));
            return Screens.ScreenFromPoint(spawn) ?? Screens.Primary;
        }
        return Screens.Primary;
    }

    /// <summary>The corner radius the panel's glass is drawn with (the spawn radius, or the panel's own).</summary>
    protected double GlassCardCornerRadius =>
        PanelCard is Border border ? border.CornerRadius.TopLeft : SpawnCornerRadius;

    /// <summary>
    /// This panel's cached glass animation frames (ascending scale), or null when none is
    /// available yet. The default asks the host's popup glass service; a panel fed by another
    /// service overrides this and <see cref="SetGlassFramesNotification"/>.
    /// </summary>
    protected virtual IReadOnlyList<PanelAnimationFrame>? QueryGlassFrames()
    {
        if (glassBackdropImage == null) return null;
        return PopupLiquidGlassService.TryGetGlassFrames(
            SpawnScreenCenter, Width, Height, GlassCardCornerRadius, ResolveSpawnScreen(), Screens.All,
            out var frames)
            ? frames
            : null;
    }

    /// <summary>(Un)subscribe the "glass frames became ready" notification.</summary>
    protected virtual void SetGlassFramesNotification(bool enabled)
    {
        if (enabled) PopupLiquidGlassService.FrameStripCompleted += OnGlassFramesReady;
        else PopupLiquidGlassService.FrameStripCompleted -= OnGlassFramesReady;
    }

    /// <summary>
    /// The widget's center expressed as a relative origin inside the panel card, so the
    /// scale transition reads as the card growing straight out of the widget (and folding
    /// back into it on close). Falls back to the card's own center when the spawn point is
    /// unknown or the card is not laid out yet. Slightly clamped so a work-area-clamped
    /// panel still folds toward the widget's side rather than from an extreme far-away point.
    /// </summary>
    private RelativePoint? ComputeSpawnAnchorOrigin()
    {
        try
        {
            var spawn = SpawnScreenCenter;
            var card = PanelCard;
            if (spawn == null || card == null) return null;

            var screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawn.Value.X),
                (int)Math.Round(spawn.Value.Y)));
            double scale = screen?.Scaling > 0 ? screen.Scaling : 1.0;

            // Spawn and Position are physical pixels; the card measures in DIPs.
            var anchor = new Point(
                (spawn.Value.X - Position.X) / scale,
                (spawn.Value.Y - Position.Y) / scale);
            var topLeft = card.TranslatePoint(new Point(0, 0), this);
            var size = card.Bounds.Size;
            if (topLeft == null || size.Width <= 0 || size.Height <= 0) return null;

            double rx = Math.Clamp((anchor.X - topLeft.Value.X) / size.Width, -0.5, 1.5);
            double ry = Math.Clamp((anchor.Y - topLeft.Value.Y) / size.Height, -0.5, 1.5);
            return new RelativePoint(rx, ry, RelativeUnit.Relative);
        }
        catch
        {
            // The anchored zoom is a nicety, never worth breaking the open transition over:
            // an exception here would leave the card stuck invisible.
            return null;
        }
    }

    /// <summary>
    /// Runs the exit transition first, then really closes. While the exit animation plays,
    /// further Close() calls are ignored so every dismissal path (button, Escape, focus loss,
    /// re-trigger) collapses into one animated close.
    /// </summary>
    /// <remarks>
    /// Window.Close is not virtual in Avalonia, so this hides it: every call site on the
    /// concrete panel types (their own handlers and <see cref="TryCloseActivePanel{T}"/>) binds
    /// to this method, while framework-initiated closes (application shutdown) keep closing
    /// immediately — which is exactly right for shutdown.
    /// </remarks>
    public new void Close() => Close(null);

    public new void Close(object? dialogResult)
    {
        if (closeAnimationRunning) return;

        var card = PanelCard;
        if (card == null || !panelLoaded)
        {
            base.Close(dialogResult);
            return;
        }

        closeAnimationRunning = true;

        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = CloseDuration, Easing = CloseEasing }
        };
        card.Opacity = 0.0;

        if (TransformAnimationEnabled && cardScale != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = CloseDuration, Easing = CloseEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = CloseDuration, Easing = CloseEasing }
            };

            cardScale.ScaleX = CloseScaleTo;
            cardScale.ScaleY = CloseScaleTo;
        }

        var timer = new DispatcherTimer { Interval = CloseTimerDuration };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { base.Close(dialogResult); } catch { }
        };
        timer.Start();
    }

    private void PositionPanel()
    {
        var spawn = SpawnScreenCenter;
        Screen? screen = null;
        if (spawn.HasValue)
        {
            screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawn.Value.X),
                (int)Math.Round(spawn.Value.Y)));
        }
        screen ??= Screens.Primary;
        if (screen == null) return;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double physWidth = Width * scale;
        double physHeight = Height * scale;

        double targetX;
        double targetY;

        if (spawn.HasValue)
        {
            targetX = spawn.Value.X - physWidth / 2.0;
            targetY = spawn.Value.Y - physHeight / 2.0;
        }
        else
        {
            targetX = screen.WorkingArea.X + (screen.WorkingArea.Width - physWidth) / 2.0;
            targetY = screen.WorkingArea.Y + (screen.WorkingArea.Height - physHeight) / 2.0;
        }

        var work = screen.WorkingArea;
        double margin = 16 * scale;
        targetX = Math.Clamp(targetX, work.X + margin, work.X + Math.Max(0, work.Width - physWidth - margin));
        targetY = Math.Clamp(targetY, work.Y + margin, work.Y + Math.Max(0, work.Height - physHeight - margin));

        Position = new PixelPoint((int)Math.Round(targetX), (int)Math.Round(targetY));
    }
}
