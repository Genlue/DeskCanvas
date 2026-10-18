using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Weather.ViewModels;

namespace Weather.Views.Controls;

/// <summary>
/// 4x2 (medium) forecast: current conditions + a scrolling hourly strip.
/// The fixed-pixel design is calibrated for a ~340x170 card; a LayoutTransform
/// scales it up proportionally on larger grids (cell sizes differ hugely
/// between the virtual and manual grids) while keeping the scroll behavior.
/// Cards shorter than <see cref="CompactHeight"/> switch to the compact type
/// scale so the hourly strip keeps a full-viewport height.
/// </summary>
public partial class ForecastWide : UserControl
{
    private const double DesignWidth = 340;
    private const double DesignHeight = 170;

    /// <summary>
    /// Below this height the design-size header would eat so much of the card that the
    /// hourly strip no longer fits its icon: switch the header to the compact type scale
    /// (36px temperature, 12px details) and shrink the hourly icon.
    /// </summary>
    private const double CompactHeight = 150;
    private readonly Forecast? owner;

    public ForecastWide(ForecastViewModel viewModel, Forecast? owner = null)
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
            Math.Min(e.NewSize.Width / DesignWidth, e.NewSize.Height / DesignHeight),
            1.0, 2.5);
        Scaler.LayoutTransform = scale > 1.01 ? new ScaleTransform(scale, scale) : null;

        // 只有「矮于设计稿」的卡片需要紧凑档；放大档（scale > 1）的内容始终按
        // 340x170 排版，不会出现逐小时条被挤扁的问题。
        Root.Classes.Set("compact", e.NewSize.Height < CompactHeight);
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
