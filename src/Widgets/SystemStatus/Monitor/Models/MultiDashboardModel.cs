namespace Monitor.Models;

public record MultiDashboardModel(MetricType[] Metrics)
{
    public static readonly MetricType[] DefaultMetrics =
    [
        MetricType.CpuUsage,
        MetricType.RamUsage,
        MetricType.DiskUsage,
        MetricType.NetworkUsage
    ];
}
