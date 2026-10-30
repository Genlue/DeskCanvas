using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Weather.ViewModels;

namespace Weather.Views.Controls;

/// <summary>
/// 4x4 (large) forecast: current conditions + hourly + 7-day columns.
/// Fixed-pixel design calibrated for a ~340x340 card; scaled up via a
/// LayoutTransform on larger grids so the same layout fills big cells.
/// </summary>
public partial class ForecastLarge : UserControl
{
    private const double DesignSize = 340;
    private readonly Forecast? owner;

    public ForecastLarge(ForecastViewModel viewModel, Forecast? owner = null)
    {
        this.owner = owner;
        DataContext = viewModel;
        SizeChanged += OnSizeChanged;
        Unloaded += (_, _) => SizeChanged -= OnSizeChanged;
        InitializeComponent();
        // Bubble with handledEventsToo: mouse-wheel events are bubble-routed, so a
        // tunnel handler would never fire; we intercept after the ScrollViewer and
        // translate the vertical wheel delta into horizontal offset ourselves.
        AddHandler(PointerWheelChangedEvent, OnHourlyWheel, RoutingStrategies.Bubble, true);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var scale = Math.Clamp(
            Math.Min(e.NewSize.Width, e.NewSize.Height) / DesignSize,
            1.0, 2.5);
        Scaler.LayoutTransform = scale > 1.01 ? new ScaleTransform(scale, scale) : null;
    }

    /// <summary>
    /// Mouse wheel over the horizontally scrolling hourly strip: wheel down (page
    /// scroll down) scrolls right, wheel up scrolls left.
    /// </summary>
    private void OnHourlyWheel(object? sender, PointerWheelEventArgs e)
    {
        var maxX = HourlyScroller.Extent.Width - HourlyScroller.Viewport.Width;
        if (maxX <= 0) return;

        // Bounds is in the parent's coordinate space; compare with the pointer
        // position expressed in the ScrollViewer's own local space.
        var pointer = e.GetPosition(HourlyScroller);
        if (pointer.X < 0 || pointer.Y < 0 ||
            pointer.X > HourlyScroller.Bounds.Width || pointer.Y > HourlyScroller.Bounds.Height) return;

        var delta = e.Delta.Y;
        if (delta == 0) return;

        // Wheel down (negative delta) scrolls right; wheel up scrolls left,
        // matching classic web-page scrolling.
        HourlyScroller.Offset = new Vector(
            Math.Clamp(HourlyScroller.Offset.X - delta * 60, 0, maxX),
            HourlyScroller.Offset.Y);
        e.Handled = true;
    }

    public void OpenPopup(object? sender, RoutedEventArgs e)
    {
        owner?.OpenPopupWindow();
    }
}
