using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Storage.Services;

public static class StorageService
{
    public record DriveStorageInfo(
        string RootPath,
        string DriveLetter,
        string VolumeLabel,
        string DisplayTitle,
        string ShortTitle,
        DriveType DriveType,
        string DriveFormat,
        bool IsSystemDrive,
        long TotalBytes,
        long FreeBytes,
        long UsedBytes,
        double UsageFraction,
        int UsagePercent
    );

    public static List<DriveStorageInfo> GetDrives(
        bool includeRemovable = true,
        bool includeNetwork = false,
        IReadOnlyCollection<string>? hiddenDrives = null)
    {
        var hiddenSet = hiddenDrives != null && hiddenDrives.Count > 0
            ? new HashSet<string>(hiddenDrives.Select(NormalizeDriveKey), StringComparer.OrdinalIgnoreCase)
            : null;

        var all = GetAllCandidateDrives(includeRemovable: true, includeNetwork: true);
        var result = new List<DriveStorageInfo>(all.Count);

        foreach (var drive in all)
        {
            if (drive.DriveType == DriveType.Removable && !includeRemovable)
                continue;
            if (drive.DriveType == DriveType.Network && !includeNetwork)
                continue;
            if (hiddenSet != null &&
                (hiddenSet.Contains(NormalizeDriveKey(drive.RootPath)) ||
                 hiddenSet.Contains(NormalizeDriveKey(drive.DriveLetter))))
            {
                continue;
            }

            result.Add(drive);
        }

        return result;
    }

    public static List<DriveStorageInfo> GetAllCandidateDrives(
        bool includeRemovable = true,
        bool includeNetwork = true)
    {
        var list = new List<DriveStorageInfo>();
        string systemRoot = string.Empty;
        try
        {
            systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? string.Empty;
        }
        catch { }

        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch
        {
            return list;
        }

        foreach (var drive in drives)
        {
            try
            {
                var type = drive.DriveType;
                if (type != DriveType.Fixed &&
                    !(includeRemovable && type == DriveType.Removable) &&
                    !(includeNetwork && type == DriveType.Network))
                {
                    continue;
                }

                if (!drive.IsReady)
                    continue;

                var total = drive.TotalSize;
                if (total <= 0)
                    continue;

                var free = Math.Clamp(drive.AvailableFreeSpace, 0L, total);
                var used = Math.Clamp(total - free, 0L, total);
                var fraction = Math.Clamp((double)used / total, 0.0, 1.0);
                var percent = (int)Math.Round(fraction * 100.0);

                var rootPath = drive.Name;
                var driveLetter = rootPath.TrimEnd('\\', '/');
                var isSystem = !string.IsNullOrEmpty(systemRoot) &&
                               string.Equals(rootPath.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

                string label = string.Empty;
                try { label = drive.VolumeLabel?.Trim() ?? string.Empty; } catch { }

                string format = string.Empty;
                try { format = drive.DriveFormat?.Trim() ?? string.Empty; } catch { }

                var fallbackLabel = isSystem
                    ? "系统"
                    : type switch
                    {
                        DriveType.Removable => "可移动磁盘",
                        DriveType.Network => "网络磁盘",
                        _ => "本地磁盘"
                    };

                var effectiveLabel = string.IsNullOrWhiteSpace(label) ? fallbackLabel : label;
                var displayTitle = $"{driveLetter} {effectiveLabel}";

                list.Add(new DriveStorageInfo(
                    RootPath: rootPath,
                    DriveLetter: driveLetter,
                    VolumeLabel: effectiveLabel,
                    DisplayTitle: displayTitle,
                    ShortTitle: driveLetter,
                    DriveType: type,
                    DriveFormat: string.IsNullOrWhiteSpace(format) ? "NTFS" : format,
                    IsSystemDrive: isSystem,
                    TotalBytes: total,
                    FreeBytes: free,
                    UsedBytes: used,
                    UsageFraction: fraction,
                    UsagePercent: percent
                ));
            }
            catch
            {
                // Skip inaccessible or disconnected drives without breaking enumeration
            }
        }

        return list
            .OrderBy(d => d.DriveLetter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string NormalizeDriveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return string.Empty;
        return key.Trim().TrimEnd('\\', '/').ToUpperInvariant();
    }

    public static string FormatBytes(long bytes, bool compact = false)
    {
        if (bytes < 0) bytes = 0;
        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;
        const double tb = gb * 1024.0;

        if (bytes >= tb)
        {
            var val = bytes / tb;
            return compact ? $"{val:0.##}T" : $"{val:0.##} TB";
        }

        if (bytes >= gb)
        {
            var val = bytes / gb;
            var formatted = val >= 100 ? $"{val:0}" : $"{val:0.#}";
            return compact ? $"{formatted}G" : $"{formatted} GB";
        }

        if (bytes >= mb)
        {
            var val = bytes / mb;
            return compact ? $"{val:0}M" : $"{val:0} MB";
        }

        var kbVal = bytes / kb;
        return compact ? $"{kbVal:0}K" : $"{kbVal:0} KB";
    }

    public static void OpenDriveInExplorer(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath)) return;
        try
        {
            if (!Directory.Exists(rootPath)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = rootPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Storage] Failed to open drive {rootPath}: {ex.Message}");
        }
    }
}
