using System.Management;
using Avalonia.Media;
using Monitor.Models;
using Monitor.ViewModels;

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
    
    /// <summary>
    /// Shared per-metric snapshot cache. The widgets tick once a second, and every view used to
    /// run its own WMI round — a single dashboard fired four queries per tick, and a second view
    /// of the same metric (another screen, a stack child beside a standalone card) doubled that.
    /// WMI queries are the expensive part (Win32_NetworkAdapter alone can take hundreds of
    /// milliseconds), so one snapshot per type is collected and shared:
    /// <list type="bullet">
    /// <item>a result younger than <see cref="SnapshotTtlMs"/> is handed to every asker;</item>
    /// <item>an <b>in-flight</b> query is also shared — a slow round no longer stacks another
    /// one on top (single-flight), it is simply awaited by everyone.</item>
    /// </list>
    /// Values stay per-tick fresh: the window is shorter than the update interval.
    /// </summary>
    private const int SnapshotTtlMs = 900;

    private static readonly object snapshotGate = new();
    private static readonly Dictionary<MetricType, (Task<double?> Value, long At)> snapshots = new();

    public static Task<double?> GetMetricValue(MetricType type)
    {
        lock (snapshotGate)
        {
            var now = Environment.TickCount64;
            if (snapshots.TryGetValue(type, out var cached))
            {
                if (!cached.Value.IsCompleted) return cached.Value;          // single flight
                if (now - cached.At < SnapshotTtlMs) return cached.Value;    // still this tick's snapshot
            }

            var task = Task.Run(() => Read(type));
            snapshots[type] = (task, now);
            return task;
        }
    }

    /// <summary>One collection round. A failing query yields "no value" rather than a faulted task.</summary>
    private static double? Read(MetricType type)
    {
        try
        {
            return type switch
            {
                MetricType.CpuUsage => GetCpuUsage(),
                MetricType.RamUsage => GetRamUsage(),
                MetricType.DiskUsage => GetDiskUsage(),
                MetricType.NetworkUsage => GetNetworkUsage(),
                MetricType.BatteryLevel => GetBatteryLevel(),
                _ => null
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? GetCpuUsage()
    {
        if (!OperatingSystem.IsWindows()) return null;
      
        using var searcher = new ManagementObjectSearcher("select * from Win32_PerfFormattedData_PerfOS_Processor where Name='_Total'");
        // Both the result collection and every ManagementObject in it hold unmanaged WMI
        // handles; the early returns below would otherwise hand them to the finalizer queue
        // on every single tick. The value is read before the object is disposed.
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                return Convert.ToDouble(obj["PercentProcessorTime"]) / 100;
            }
        }

        return null;
    }

    private static double? GetRamUsage()
    {
        if (!OperatingSystem.IsWindows()) return null;

        using var searcher = new ManagementObjectSearcher("select * from Win32_OperatingSystem");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                var totalVisibleMemory = Convert.ToDouble(obj["TotalVisibleMemorySize"]);
                var freePhysicalMemory = Convert.ToDouble(obj["FreePhysicalMemory"]);

                if (!(totalVisibleMemory > 0)) continue;

                var usedMemory = totalVisibleMemory - freePhysicalMemory;
                return usedMemory / totalVisibleMemory;
            }
        }

        return null;
    }

    private static double? GetDiskUsage()
    {
        if (!OperatingSystem.IsWindows()) return null;
        
        using var searcher = new ManagementObjectSearcher("select * from Win32_PerfFormattedData_PerfDisk_LogicalDisk where Name='_Total'");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                return Convert.ToDouble(obj["PercentDiskTime"]) / 100;
            }
        }

        return null;
    }

    private static double? GetNetworkUsage()
    {
        if (!OperatingSystem.IsWindows()) return null;
        
        double maxSpeed = 0;
        double totalBytesSent = 0;
        double totalBytesReceived = 0;

        using (var searcher = new ManagementObjectSearcher("select * from Win32_PerfFormattedData_Tcpip_NetworkInterface"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    totalBytesSent += Convert.ToDouble(obj["BytesSentPerSec"]);
                    totalBytesReceived += Convert.ToDouble(obj["BytesReceivedPerSec"]);
                }
            }
        }
        
        using (var searcher = new ManagementObjectSearcher("select * from Win32_NetworkAdapter where NetEnabled=true"))
        using (var results = searcher.Get())
        {
            foreach (ManagementObject obj in results)
            {
                using (obj)
                {
                    maxSpeed = Convert.ToDouble(obj["Speed"]);
                    break;
                }
            }
        }

        if (maxSpeed > 0)
        {
            var totalBitsPerSec = (totalBytesSent + totalBytesReceived) * 8;
            var networkUsage = totalBitsPerSec / maxSpeed;

            return networkUsage;
        }
     
        return null;
    }
    
    private static double? GetBatteryLevel()
    {
        if (!OperatingSystem.IsWindows()) return null;

        using var searcher = new ManagementObjectSearcher("select * from Win32_Battery");
        using var results = searcher.Get();
        foreach (ManagementObject obj in results)
        {
            using (obj)
            {
                return Convert.ToInt32(obj["EstimatedChargeRemaining"]) / 100d;
            }
        }

        return null;
    }

    // The following code implements the battery charging status readout, but it's a bit difficult to integrate it well with the battery section

    /*
    public static bool IsBatteryCharing()
    {
        if (!OperatingSystem.IsWindows()) return false;

        using var searcher = new ManagementObjectSearcher("select * from Win32_Battery");
        foreach (var o in searcher.Get())
        {
            var obj = (ManagementObject)o;
            if (Convert.ToInt32(obj["BatteryStatus"]) == 2){
                return true;
            }
            else
            {
                return false;
            }
        }

        return false;
    }
    */
}