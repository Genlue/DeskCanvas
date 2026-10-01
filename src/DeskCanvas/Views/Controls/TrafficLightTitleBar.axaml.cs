using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// Custom macOS-style traffic-light title bar (close / minimize / maximize / zoom)
/// for the settings window. Replicates the IconForge "红绿灯" design:
/// 36px bar, 12px circles with 1px border and 8px gap (center distance 20px, coordinates (20,18), (40,18), (60,18)).
/// Group hover: all three semantic glyphs (#4D0000, #995700, #006400) fade in simultaneously (0.12s) when hovering over the group.
/// Press feedback: scale(0.96) and brightness(0.85).
/// Alt key: switches the green button to a centered "+" zoom icon.
/// Window state: maximized shows inward restore triangles, unmaximized shows outward diagonal triangles.
/// Inactive window: solid middle gray (#DFDFDF in light mode, #3C3C3C in dark mode) with glyphs strictly hidden.
/// </summary>
public partial class TrafficLightTitleBar : UserControl
{
    private Window? window;
    private IDisposable? windowStateSubscription;
    private double size = AppSettings.DefaultTitleBarSize;
    private bool isAltPressed;

    public TrafficLightTitleBar()
    {
        InitializeComponent();
        LightsHitbox.PointerEntered += OnLightsPointerEntered;
        LightsHitbox.PointerExited += OnLightsPointerExited;
        ActualThemeVariantChanged += (_, _) => UpdateThemeState();
        ApplyGeometry();
        UpdateThemeState();
    }

    private void OnLightsPointerEntered(object? sender, PointerEventArgs e)
    {
        if (!Root.Classes.Contains("unfocused"))
            Root.Classes.Add("hovered");
    }

    private void OnLightsPointerExited(object? sender, PointerEventArgs e)
    {
        Root.Classes.Remove("hovered");
    }

    /// <summary>
    /// Traffic light diameter in DIPs. Scales the circles, the gap, the left
    /// offset and the glyphs proportionally (12px = macOS standard).
    /// </summary>
    public double Size
    {
        get => size;
        set
        {
            size = Math.Clamp(value, 12, 24);
            ApplyGeometry();
        }
    }

    /// <summary>
    /// Proportionally scale the whole traffic-light group from the 12px base
    /// geometry: diameter, corner radius, gap (8px @ 12px), left offset (14px @ 12px)
    /// and glyph sizes. The 36px bar and colors stay unchanged.
    /// </summary>
    private void ApplyGeometry()
    {
        var k = size / 12.0;
        foreach (var button in new[] { CloseButton, MinimizeButton, MaximizeButton })
        {
            button.Width = button.Height = size;
            button.CornerRadius = new CornerRadius(size / 2.0);
        }
        Lights.Spacing = size * (8.0 / 12.0);
        LightsHitbox.Margin = new Thickness(size * (14.0 / 12.0), 0, 0, 0);
        CloseIcon.Width = CloseIcon.Height = size;
        MinimizeIcon.Width = MinimizeIcon.Height = size;
        MaximizePanel.Width = MaximizePanel.Height = size;
        MaximizeIcon.Width = MaximizeIcon.Height = size;
        RestoreIcon.Width = RestoreIcon.Height = size;
        ZoomIcon.Width = ZoomIcon.Height = size;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (e.Root is not Window w) return;

        window = w;
        window.Activated += OnWindowActivated;
        window.Deactivated += OnWindowDeactivated;
        window.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        window.AddHandler(InputElement.KeyUpEvent, OnWindowKeyUp, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        windowStateSubscription = window.GetObservable(Window.WindowStateProperty)
            .Subscribe(_ => UpdateMaximizeState());
        UpdateThemeState();
        UpdateMaximizeState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (window == null) return;

        window.Activated -= OnWindowActivated;
        window.Deactivated -= OnWindowDeactivated;
        window.RemoveHandler(InputElement.KeyDownEvent, OnWindowKeyDown);
        window.RemoveHandler(InputElement.KeyUpEvent, OnWindowKeyUp);
        windowStateSubscription?.Dispose();
        windowStateSubscription = null;
        window = null;
    }

    private void UpdateThemeState()
    {
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        if (isDark)
        {
            Root.Classes.Add("dark");
            Root.Classes.Remove("light");
        }
        else
        {
            Root.Classes.Add("light");
            Root.Classes.Remove("dark");
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e)
    {
        Root.Classes.Remove("unfocused");
        UpdateThemeState();
        UpdateMaximizeState();
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        isAltPressed = false;
        Root.Classes.Add("unfocused");
        Root.Classes.Remove("hovered");
        UpdateMaximizeState();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt || (e.KeyModifiers & KeyModifiers.Alt) != 0)
        {
            if (!isAltPressed)
            {
                isAltPressed = true;
                UpdateMaximizeState();
            }
        }
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftAlt or Key.RightAlt || (e.KeyModifiers & KeyModifiers.Alt) == 0)
        {
            if (isAltPressed)
            {
                isAltPressed = false;
                UpdateMaximizeState();
            }
        }
    }

    private void OnClose(object? sender, RoutedEventArgs e) => window?.Close();

    private void OnMinimize(object? sender, RoutedEventArgs e) => window!.WindowState = WindowState.Minimized;

    private void OnMaximize(object? sender, RoutedEventArgs e) => ToggleMaximize();

    /// <summary>
    /// Drag the window when pressing the bar (outside the buttons); double
    /// click toggles maximize/restore (native behavior of system title bars).
    /// </summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (window == null) return;

        var point = e.GetCurrentPoint(this);
        if (point.Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed) return;

        // Clicks on traffic-light buttons must not start a drag.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>() != null) return;

        if (e.ClickCount >= 2)
        {
            ToggleMaximize();
            return;
        }

        window.BeginMoveDrag(e);
    }

    private void ToggleMaximize()
    {
        if (window == null) return;
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    /// <summary>
    /// Update the green button state:
    /// - Alt pressed: ZoomIcon (+)
    /// - Maximized: RestoreIcon (inward triangles)
    /// - Normal: MaximizeIcon (outward triangles)
    /// </summary>
    private void UpdateMaximizeState()
    {
        var maximized = window?.WindowState == WindowState.Maximized;
        if (isAltPressed)
        {
            ZoomIcon.IsVisible = true;
            MaximizeIcon.IsVisible = false;
            RestoreIcon.IsVisible = false;
            ToolTip.SetTip(MaximizeButton, Locale.TrafficLight_Zoom);
        }
        else
        {
            ZoomIcon.IsVisible = false;
            MaximizeIcon.IsVisible = !maximized;
            RestoreIcon.IsVisible = maximized;
            ToolTip.SetTip(MaximizeButton, maximized ? Locale.TrafficLight_Restore : Locale.TrafficLight_Maximize);
        }
    }
}
