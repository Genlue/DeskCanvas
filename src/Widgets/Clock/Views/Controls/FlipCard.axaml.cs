using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Clock.Views.Controls;

/// <summary>
/// A single mechanical split-flap card (e.g., <c>HH</c>, <c>MM</c>, or <c>SS</c>) that renders
/// upper and lower half-flaps with a 60fps 2-phase 3D gravity flip transition when <see cref="Value"/> changes.
/// </summary>
public partial class FlipCard : UserControl
{
    private const double Phase1DurationMs = 200.0;
    private const double Phase2DurationMs = 230.0;
    private const double TotalDurationMs = Phase1DurationMs + Phase2DurationMs;

    public static readonly StyledProperty<string> ValueProperty =
        AvaloniaProperty.Register<FlipCard, string>(nameof(Value), "00");

    public static readonly StyledProperty<double> CardWidthProperty =
        AvaloniaProperty.Register<FlipCard, double>(nameof(CardWidth), 156.0);

    public static readonly StyledProperty<double> CardHeightProperty =
        AvaloniaProperty.Register<FlipCard, double>(nameof(CardHeight), 110.0);

    public static readonly StyledProperty<double> DigitFontSizeProperty =
        AvaloniaProperty.Register<FlipCard, double>(nameof(DigitFontSize), 68.0);

    public static readonly StyledProperty<double> FlapCornerRadiusProperty =
        AvaloniaProperty.Register<FlipCard, double>(nameof(FlapCornerRadius), 14.0);

    public static readonly StyledProperty<double> BadgeFontSizeProperty =
        AvaloniaProperty.Register<FlipCard, double>(nameof(BadgeFontSize), 10.5);

    public static readonly StyledProperty<string?> TopLeftBadgeProperty =
        AvaloniaProperty.Register<FlipCard, string?>(nameof(TopLeftBadge));

    public static readonly StyledProperty<string?> TopRightBadgeProperty =
        AvaloniaProperty.Register<FlipCard, string?>(nameof(TopRightBadge));

    public static readonly StyledProperty<string?> BottomRightBadgeProperty =
        AvaloniaProperty.Register<FlipCard, string?>(nameof(BottomRightBadge));

    private readonly ScaleTransform animTopScale = new(1.0, 1.0);
    private readonly ScaleTransform animBottomScale = new(1.0, 0.0);
    private readonly Stopwatch flipStopwatch = new();
    private DispatcherTimer? flipTimer;
    private string currentDisplayedValue = string.Empty;
    private string targetDisplayedValue = string.Empty;

    public FlipCard()
    {
        InitializeComponent();
        AnimTopFlap.RenderTransform = animTopScale;
        AnimBottomFlap.RenderTransform = animBottomScale;

        ApplyLayoutMetrics();
        ApplyBadges();

        DetachedFromVisualTree += (_, _) => StopAnimationAndSnap();

        // A first binding push equal to the ValueProperty default ("00" — e.g. the minute card
        // created during the :00 minute) raises no PropertyChanged, so the static flaps would
        // stay empty. Sync once from the current Value on attach to guarantee initial rendering.
        AttachedToVisualTree += (_, _) =>
        {
            if (string.IsNullOrEmpty(currentDisplayedValue) && !string.IsNullOrEmpty(Value))
            {
                SetInstant(Value);
            }
        };
    }

    public string Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double CardWidth
    {
        get => GetValue(CardWidthProperty);
        set => SetValue(CardWidthProperty, value);
    }

    public double CardHeight
    {
        get => GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    public double DigitFontSize
    {
        get => GetValue(DigitFontSizeProperty);
        set => SetValue(DigitFontSizeProperty, value);
    }

    public double FlapCornerRadius
    {
        get => GetValue(FlapCornerRadiusProperty);
        set => SetValue(FlapCornerRadiusProperty, value);
    }

    public double BadgeFontSize
    {
        get => GetValue(BadgeFontSizeProperty);
        set => SetValue(BadgeFontSizeProperty, value);
    }

    public string? TopLeftBadge
    {
        get => GetValue(TopLeftBadgeProperty);
        set => SetValue(TopLeftBadgeProperty, value);
    }

    public string? TopRightBadge
    {
        get => GetValue(TopRightBadgeProperty);
        set => SetValue(TopRightBadgeProperty, value);
    }

    public string? BottomRightBadge
    {
        get => GetValue(BottomRightBadgeProperty);
        set => SetValue(BottomRightBadgeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ValueProperty)
        {
            var next = change.GetNewValue<string>() ?? "00";
            OnValueChanged(next);
        }
        else if (change.Property == CardWidthProperty ||
                 change.Property == CardHeightProperty ||
                 change.Property == DigitFontSizeProperty ||
                 change.Property == FlapCornerRadiusProperty ||
                 change.Property == BadgeFontSizeProperty)
        {
            if (RootGrid != null)
            {
                ApplyLayoutMetrics();
            }
        }
        else if (change.Property == TopLeftBadgeProperty ||
                 change.Property == TopRightBadgeProperty ||
                 change.Property == BottomRightBadgeProperty)
        {
            if (RootGrid != null)
            {
                ApplyBadges();
            }
        }
    }

    private void ApplyLayoutMetrics()
    {
        var w = CardWidth;
        var h = CardHeight;
        var halfH = h / 2.0;
        var r = FlapCornerRadius;
        var fontSize = DigitFontSize;
        var badgeSize = BadgeFontSize;

        var topRadius = new CornerRadius(r, r, 0, 0);
        var bottomRadius = new CornerRadius(0, 0, r, r);
        var outerRadius = new CornerRadius(r);

        RootGrid.Width = w;
        RootGrid.Height = h;

        StaticTopFlap.Width = w;
        StaticTopFlap.Height = halfH;
        StaticTopFlap.CornerRadius = topRadius;
        StaticTopInnerGrid.Width = w;
        StaticTopInnerGrid.Height = h;
        StaticTopText.FontSize = fontSize;

        StaticBottomFlap.Width = w;
        StaticBottomFlap.Height = halfH;
        StaticBottomFlap.CornerRadius = bottomRadius;
        StaticBottomInnerGrid.Width = w;
        StaticBottomInnerGrid.Height = h;
        StaticBottomText.FontSize = fontSize;

        AnimTopFlap.Width = w;
        AnimTopFlap.Height = halfH;
        AnimTopFlap.CornerRadius = topRadius;
        AnimTopInnerGrid.Width = w;
        AnimTopInnerGrid.Height = h;
        AnimTopText.FontSize = fontSize;

        AnimBottomFlap.Width = w;
        AnimBottomFlap.Height = halfH;
        AnimBottomFlap.CornerRadius = bottomRadius;
        AnimBottomInnerGrid.Width = w;
        AnimBottomInnerGrid.Height = h;
        AnimBottomText.FontSize = fontSize;

        OuterRimBorder.CornerRadius = outerRadius;

        TopLeftBadgeText.FontSize = badgeSize;
        TopRightBadgeText.FontSize = badgeSize;
        BottomRightBadgeText.FontSize = badgeSize;
    }

    private void ApplyBadges()
    {
        TopLeftBadgeText.Text = TopLeftBadge;
        TopLeftBadgeText.IsVisible = !string.IsNullOrEmpty(TopLeftBadge);

        TopRightBadgeText.Text = TopRightBadge;
        TopRightBadgeText.IsVisible = !string.IsNullOrEmpty(TopRightBadge);

        BottomRightBadgeText.Text = BottomRightBadge;
        BottomRightBadgeText.IsVisible = !string.IsNullOrEmpty(BottomRightBadge);
    }

    private void OnValueChanged(string nextValue)
    {
        if (string.IsNullOrEmpty(currentDisplayedValue) || !IsLoaded)
        {
            SetInstant(nextValue);
            return;
        }

        if (flipTimer is { IsEnabled: true })
        {
            if (targetDisplayedValue == nextValue)
                return;
            StopAnimationAndSnap();
        }
        else if (currentDisplayedValue == nextValue)
        {
            return;
        }

        StartFlipAnimation(currentDisplayedValue, nextValue);
    }

    private void SetInstant(string val)
    {
        flipTimer?.Stop();
        currentDisplayedValue = val;
        targetDisplayedValue = val;

        StaticTopText.Text = val;
        StaticBottomText.Text = val;
        AnimTopText.Text = val;
        AnimBottomText.Text = val;

        AnimTopFlap.IsVisible = false;
        AnimBottomFlap.IsVisible = false;
        StaticTopShade.Opacity = 0;
        StaticBottomShade.Opacity = 0;
        AnimTopShade.Opacity = 0;
        AnimBottomShade.Opacity = 0;
    }

    private void StopAnimationAndSnap()
    {
        flipTimer?.Stop();
        if (!string.IsNullOrEmpty(targetDisplayedValue))
        {
            SetInstant(targetDisplayedValue);
        }
    }

    private void StartFlipAnimation(string fromVal, string toVal)
    {
        currentDisplayedValue = fromVal;
        targetDisplayedValue = toVal;

        StaticTopText.Text = toVal;
        StaticBottomText.Text = fromVal;
        AnimTopText.Text = fromVal;
        AnimBottomText.Text = toVal;

        animTopScale.ScaleY = 1.0;
        animBottomScale.ScaleY = 0.0;

        AnimTopFlap.IsVisible = true;
        AnimBottomFlap.IsVisible = false;

        StaticTopShade.Opacity = 0.26;
        StaticBottomShade.Opacity = 0.0;
        AnimTopShade.Opacity = 0.0;
        AnimBottomShade.Opacity = 0.24;

        flipStopwatch.Restart();
        flipTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, OnFlipTick);
        if (!flipTimer.IsEnabled)
        {
            flipTimer.Start();
        }
    }

    private void OnFlipTick(object? sender, EventArgs e)
    {
        var elapsed = flipStopwatch.Elapsed.TotalMilliseconds;

        if (elapsed < Phase1DurationMs)
        {
            var p = Math.Clamp(elapsed / Phase1DurationMs, 0.0, 1.0);
            // Gravity acceleration (EaseInQuad)
            var eased = p * p;
            var angle = eased * (Math.PI / 2.0);
            animTopScale.ScaleY = Math.Max(0.01, Math.Cos(angle));

            AnimTopShade.Opacity = eased * 0.40;
            StaticTopShade.Opacity = (1.0 - eased) * 0.26;
            StaticBottomShade.Opacity = 0.0;
        }
        else if (elapsed < TotalDurationMs)
        {
            if (AnimTopFlap.IsVisible)
            {
                AnimTopFlap.IsVisible = false;
                AnimBottomFlap.IsVisible = true;
                StaticTopShade.Opacity = 0.0;
            }

            var p = Math.Clamp((elapsed - Phase1DurationMs) / Phase2DurationMs, 0.0, 1.0);
            // Mechanical landing settle (EaseOutCubic)
            var inv = 1.0 - p;
            var eased = 1.0 - (inv * inv * inv);
            var angle = (1.0 - eased) * (Math.PI / 2.0);
            animBottomScale.ScaleY = Math.Max(0.01, Math.Cos(angle));

            AnimBottomShade.Opacity = (1.0 - eased) * 0.24;
            StaticBottomShade.Opacity = eased * 0.34;
        }
        else
        {
            StopAnimationAndSnap();
        }
    }
}
