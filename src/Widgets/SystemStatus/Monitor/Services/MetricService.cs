using Avalonia.Media;
using Monitor.Models;
using Monitor.ViewModels;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

namespace Monitor.Services;

public static class MetricService
{
    public static StreamGeometry GetMetricIcon(MetricType type)
    {
        return type switch
        {
            MetricType.CpuUsage => MetricIcon.CpuUsage.Value,
            MetricType.RamUsage => MetricIcon.RamUsage.Value,
            MetricType.DiskUsage => MetricIcon.DiskUsage.Value,
            MetricType.NetworkUsage => MetricIcon.NetworkUsage.Value,
            MetricType.BatteryLevel => MetricIcon.BatteryLevel.Value,
            _ => new StreamGeometry()
        };
    }
    
    public static Task<double?> GetMetricValue(MetricType type) => GetMetricValue(type, "monitor-legacy");

    public static async Task<double?> GetMetricValue(MetricType type, string owner,
        CancellationToken cancellationToken = default, SystemMetricsSharedProvider? provider = null)
    {
        var metric = type switch
        {
            MetricType.CpuUsage => SystemMetric.CpuUsage,
            MetricType.RamUsage => SystemMetric.RamUsage,
            MetricType.DiskUsage => SystemMetric.DiskUsage,
            MetricType.NetworkUsage => SystemMetric.NetworkUsage,
            MetricType.BatteryLevel => SystemMetric.BatteryLevel,
            _ => (SystemMetric?)null
        };
        if (metric is null) return null;
        using var lease = await (provider ?? SharedDataServices.SystemMetrics).AcquireAsync(owner, metric.Value, cancellationToken).ConfigureAwait(false);
        return lease.Snapshot.Value?.Ratio;
    }
}
