using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using uWidgets.Services;

namespace uWidgets.Views;

/// <summary>
/// Shared template for the widgets' secondary panel windows (weather forecast, reminders,
/// big folder, clipboard). Everything a panel needs to behave identically lives here:
///   - spawn position: centered on the opening widget (clamped to the screen's work area),
///   - open transition: fade + rise + scale-in on the Material 3 "emphasized decelerate" curve,
///   - close transition: fade + drop + scale-out on the "emphasized accelerate" curve,
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

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(280);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(170);
    // Transitions start on the first rendered frame after the value change; pad the close
    // timer so base.Close() never fires while the exit transition is still running.
    private static readonly TimeSpan CloseTimerDuration = CloseDuration + TimeSpan.FromMilliseconds(40);

    private const double OpenScaleFrom = 0.94;
    private const double OpenRiseFrom = 14.0;
    private const double CloseScaleTo = 0.97;
    private const double CloseDropTo = 8.0;
    private const int ReopenCooldownMs = 250;

    private ScaleTransform? cardScale;
    private TranslateTransform? cardRise;
    private bool panelLoaded;
    private bool closeAnimationRunning;
    private bool wasActivated;
    private DateTime activatedAtUtc;

    /// <summary>The card visual that carries the open/close transition (the panel's root border).</summary>
    protected abstract Visual? PanelCard { get; }

    /// <summary>Physical-pixel center of the widget the panel spawns from; null centers on the primary work area.</summary>
    protected abstract Point? SpawnScreenCenter { get; }

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
    /// Called by derived constructors right after InitializeComponent: hides the card, wires
    /// the panel behavior, and registers the window as its type's active panel.
    /// </summary>
    protected void InitializePanel()
    {
        var card = PanelCard;
        if (card != null)
        {
            card.Opacity = 0.0;
            if (TransformAnimationEnabled)
            {
                cardScale = card.RenderTransform as ScaleTransform ?? new ScaleTransform();
                cardRise = new TranslateTransform();
                // RenderTransformOrigin (0.5,0.5 in every panel's AXAML) applies to the whole
                // group, so the card still scales around its center while the rise is a plain
                // pixel offset.
                card.RenderTransform = new TransformGroup { Children = { cardRise, cardScale } };
                cardScale.ScaleX = OpenScaleFrom;
                cardScale.ScaleY = OpenScaleFrom;
                cardRise.Y = OpenRiseFrom;
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

        card.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = OpenDuration, Easing = OpenEasing }
        };

        if (TransformAnimationEnabled && cardScale != null && cardRise != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = OpenDuration, Easing = OpenEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = OpenDuration, Easing = OpenEasing }
            };
            cardRise.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = OpenDuration, Easing = OpenEasing }
            };

            cardScale.ScaleX = 1.0;
            cardScale.ScaleY = 1.0;
            cardRise.Y = 0.0;
        }

        card.Opacity = 1.0;
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

        if (TransformAnimationEnabled && cardScale != null && cardRise != null)
        {
            cardScale.Transitions = new Transitions
            {
                new DoubleTransition { Property = ScaleTransform.ScaleXProperty, Duration = CloseDuration, Easing = CloseEasing },
                new DoubleTransition { Property = ScaleTransform.ScaleYProperty, Duration = CloseDuration, Easing = CloseEasing }
            };
            cardRise.Transitions = new Transitions
            {
                new DoubleTransition { Property = TranslateTransform.YProperty, Duration = CloseDuration, Easing = CloseEasing }
            };

            cardScale.ScaleX = CloseScaleTo;
            cardScale.ScaleY = CloseScaleTo;
            cardRise.Y = CloseDropTo;
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
