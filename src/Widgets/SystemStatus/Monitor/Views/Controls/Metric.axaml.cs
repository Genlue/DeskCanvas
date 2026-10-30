using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;

namespace Monitor.Views.Controls;

public partial class Metric : Viewbox
{
    private bool showCenterValue;

    /// <summary>
    /// Single-cell (S tier) mode: the metric icon makes the ring cluttered at a
    /// ~50px diameter — replace it with the percentage in the center instead.
    /// </summary>
    public bool ShowCenterValue
    {
        get => showCenterValue;
        set
        {
            showCenterValue = value;
            CenterValue.IsVisible = value;
            Icon.IsVisible = !value;
        }
    }

    public static readonly StyledProperty<Avalonia.Media.IBrush?> IndicatorBrushProperty =
        AvaloniaProperty.Register<Metric, Avalonia.Media.IBrush?>(nameof(IndicatorBrush));

    public Avalonia.Media.IBrush? IndicatorBrush
    {
        get => GetValue(IndicatorBrushProperty);
        set => SetValue(IndicatorBrushProperty, value);
    }

    public static string GetBrushKey(Monitor.Models.MetricType type) => type switch
    {
        Monitor.Models.MetricType.CpuUsage => "MonitorCpuBrush",
        Monitor.Models.MetricType.RamUsage => "MonitorRamBrush",
        Monitor.Models.MetricType.DiskUsage => "MonitorDiskBrush",
        Monitor.Models.MetricType.NetworkUsage => "MonitorNetBrush",
        Monitor.Models.MetricType.BatteryLevel => "MonitorBatteryBrush",
        _ => "SystemControlForegroundBaseHighBrush"
    };

    public void ApplyMetricType(Monitor.Models.MetricType type)
    {
        var key = GetBrushKey(type);
        this.Bind(IndicatorBrushProperty, this.GetResourceObservable(key).ToBinding());
    }

    public Metric()
    {
        InitializeComponent();
        // StrokeDashOffset animation causing memory leaks
        // https://github.com/AvaloniaUI/Avalonia/issues/16973
        // 
        // ProgressBar.Transitions = new Transitions
        // {
        //     new DoubleTransition { Property = Shape.StrokeDashOffsetProperty, Duration = TimeSpan.FromMilliseconds(300) }
        // };
    }
}
