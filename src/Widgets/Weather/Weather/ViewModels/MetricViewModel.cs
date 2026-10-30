using Avalonia.Collections;
using Avalonia.Media;

namespace Weather.ViewModels;

public record MetricViewModel(double Min, double Max, double Value, StreamGeometry? Icon)
{
    // 量规几何：EllipseGeometry 半径 50、StrokeThickness 8。
    // StrokeDashArray 的数值单位是**描边宽度**（不是像素），所以整圈长度 = 2π×50/8 ≈ 39.27 单位。
    // 量规弧长取 242°（起点旋转 149°，即缺口 118° 居中于正下方），242/360 × 39.27 ≈ 26.398 单位。
    // ⚠️ 改 Metric.axaml 的 StrokeThickness 必须同步改这两个常量，否则弧长与缺口位置都会偏 ——
    //    缺口一旦不再居中于正下方，进度环左右就不对称（右侧比左侧短）。
    private const double GaugeDash = 26.398;

    public int? DisplayMin => Icon != null ? null : (int)Math.Round(Min);
    public int? DisplayMax => Icon != null ? null : (int)Math.Round(Max);
    public int? DisplayValue => (int)Math.Round(Value);
    public int FontSize => DisplayValue.ToString()?.Length > 2
        ? 50 * 2 / DisplayValue.ToString()?.Length ?? 2
        : 50;

    public double Progress => (Max - Min) <= 0 ? 0 : Math.Clamp((Value - Min) / (Max - Min), 0, 1);

    public bool IsProgressVisible => Progress > 0.001;

    public AvaloniaList<double> StrokeDashArray =>
        new AvaloniaList<double> { Math.Max(0.0001, Progress * GaugeDash), 100 };

    public double StrokeDashOffset => 0;
}