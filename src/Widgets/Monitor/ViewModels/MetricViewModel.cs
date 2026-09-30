using Avalonia.Media;

namespace Monitor.ViewModels;

public record MetricViewModel(double Value, StreamGeometry? Icon = null)
{
    // 半径 50、StrokeThickness 8 → 周长 2π×50 ≈ 314.159；StrokeDashArray 的单位是描边宽度，
    // 故整圈 ≈ 314.159/8 ≈ 39.27 单位。改 Metric.axaml 的描边粗度时必须同步改这里。
    private const double FullCircleDash = 39.27;

    public double StrokeDashOffset => FullCircleDash * (1 - Value);
    public string Text => $"{Value:P0}";
};