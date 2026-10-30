using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Services;

namespace Weather.Views.Controls;

public partial class Metric : UserControl
{
    public Metric()
    {
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
        InitializeComponent();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = Math.Min(DesiredSize.Width, DesiredSize.Height);
        var margin = size >= 150 ? size * 0.1 : 6;
        Margin = new Thickness(margin, margin);

        // 1×1 (Cell) tier: the range labels and the accent icon at the bottom of
        // the canvas crowd the value — show only the ring + centered number.
        var compact = SizeTiers.ResolveTier(this, e.NewSize) == WidgetTier.Cell;
        MinText.IsVisible = !compact;
        MaxText.IsVisible = !compact;
        MetricIcon.IsVisible = !compact;
        ValueText.LineHeight = compact ? 110 : 100;
    }
}