namespace DeskCanvas.Runtime;

public enum SystemMetric { CpuUsage, RamUsage, DiskUsage, NetworkUsage, BatteryLevel }
public sealed record SystemMetricValue(double? Ratio = null);

/// <summary>Platform-neutral sharing boundary; the host supplies native collection.</summary>
public sealed class SystemMetricsSharedProvider
{
    private readonly SharedDataProvider<SystemMetric, SystemMetricValue> data;

    public SystemMetricsSharedProvider(Func<SystemMetric, CancellationToken, Task<SystemMetricValue>> read, TimeSpan? ttl = null) =>
        data = new(read, ttl ?? TimeSpan.FromMilliseconds(900), "system-metrics");

    public SharedDataStatistics Statistics => data.Statistics;

    public Task<SharedDataProvider<SystemMetric, SystemMetricValue>.Lease> AcquireAsync(string owner, SystemMetric metric, CancellationToken cancellationToken = default) =>
        data.AcquireAsync(owner, metric, cancellationToken);
}
