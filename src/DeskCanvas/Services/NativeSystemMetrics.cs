using System;
using System.Management;
using DeskCanvas.Runtime;

namespace DeskCanvas.Services;

/// <summary>Windows collection boundary. All callers share the Runtime provider above it.</summary>
internal static class NativeSystemMetrics
{
    public static SystemMetricValue Read(SystemMetric metric)
    {
        if (!OperatingSystem.IsWindows()) return new();
        try
        {
            return metric switch
            {
                SystemMetric.CpuUsage => new(GetCpuUsage()),
                SystemMetric.RamUsage => new(GetRamUsage()),
                SystemMetric.DiskUsage => new(GetDiskUsage()),
                SystemMetric.NetworkUsage => new(GetNetworkUsage()),
                SystemMetric.BatteryLevel => new(GetBatteryLevel()),
                _ => new()
            };
        }
        catch { return new(); }
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

}
