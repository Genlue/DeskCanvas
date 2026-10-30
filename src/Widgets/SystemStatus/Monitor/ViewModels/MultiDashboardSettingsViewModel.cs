using System.Text.Json;
using Monitor.Locales;
using Monitor.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;

namespace Monitor.ViewModels;

public class MultiDashboardSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider) : ReactiveObject
{
    private MultiDashboardModel model = widgetLayoutProvider.Get().GetModel<MultiDashboardModel>()
        ?? new MultiDashboardModel((MetricType[])MultiDashboardModel.DefaultMetrics.Clone());

    public MetricTypeViewModel[] AllTypes =>
    [
        new MetricTypeViewModel(MetricType.CpuUsage, Locale.Monitor_Metric_0),
        new MetricTypeViewModel(MetricType.RamUsage, Locale.Monitor_Metric_1),
        new MetricTypeViewModel(MetricType.DiskUsage, Locale.Monitor_Metric_2),
        new MetricTypeViewModel(MetricType.NetworkUsage, Locale.Monitor_Metric_3),
        new MetricTypeViewModel(MetricType.BatteryLevel, Locale.Monitor_Metric_4)
    ];

    private MetricType[] Metrics =>
        model.Metrics is { Length: 4 } metrics
            ? metrics
            : (MetricType[])MultiDashboardModel.DefaultMetrics.Clone();

    public MetricTypeViewModel Item0
    {
        get => Resolve(0);
        set => SetMetric(0, value);
    }

    public MetricTypeViewModel Item1
    {
        get => Resolve(1);
        set => SetMetric(1, value);
    }

    public MetricTypeViewModel Item2
    {
        get => Resolve(2);
        set => SetMetric(2, value);
    }

    public MetricTypeViewModel Item3
    {
        get => Resolve(3);
        set => SetMetric(3, value);
    }

    private MetricTypeViewModel Resolve(int index) =>
        AllTypes.FirstOrDefault(x => x.Type == Metrics[index]) ?? AllTypes[0];

    private void SetMetric(int index, MetricTypeViewModel? selected)
    {
        if (selected == null) return;
        var type = selected.Type;
        var metrics = (MetricType[])Metrics.Clone();
        metrics[index] = type;
        model = model with { Metrics = metrics };

        var widgetSettings = widgetLayoutProvider.Get();
        widgetLayoutProvider.Save(widgetSettings with { Settings = JsonSerializer.SerializeToElement(model) });
    }
}
