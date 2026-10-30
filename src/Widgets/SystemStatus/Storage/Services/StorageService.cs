using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

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

    /// <summary>
    /// Shared drive-snapshot source for every enumeration entrypoint. Defaults to the host process
    /// singleton so concurrent widget instances, view polls and settings pages share one
    /// DriveInfo enumeration per TTL window. Tests replace it with a controlled provider; the
    /// fallback stays lazy so an injected source never loads the host assembly.
    /// </summary>
    public static StorageDrivesSharedProvider DriveSource
    {
        get => driveSource ?? SharedDataServices.Storage;
        set => driveSource = value;
    }
    private static StorageDrivesSharedProvider? driveSource;

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

        IReadOnlyList<SharedDriveSnapshot>? snapshots;
        try
        {
            // One shared DriveInfo round serves every consumer; projection below stays equivalent
            // to the legacy per-drive enumeration.
            using var lease = DriveSource.AcquireAsync("storage-facade").GetAwaiter().GetResult();
            snapshots = lease.Snapshot.Value;
        }
        catch
        {
            return list;
        }

        if (snapshots == null)
            return list;

        foreach (var drive in snapshots)
        {
            var type = drive.DriveType;
            if (type != DriveType.Fixed &&
                !(includeRemovable && type == DriveType.Removable) &&
                !(includeNetwork && type == DriveType.Network))
            {
                continue;
            }

            var total = drive.TotalBytes;
            if (total <= 0)
                continue;

            var free = Math.Clamp(drive.FreeBytes, 0L, total);
            var used = Math.Clamp(total - free, 0L, total);
            var fraction = Math.Clamp((double)used / total, 0.0, 1.0);
            var percent = (int)Math.Round(fraction * 100.0);

            var rootPath = drive.Name;
            var driveLetter = rootPath.TrimEnd('\\', '/');
            var isSystem = !string.IsNullOrEmpty(systemRoot) &&
                           string.Equals(rootPath.TrimEnd('\\'), systemRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            var fallbackLabel = isSystem
                ? (isZh ? "系统" : "System")
                : type switch
                {
                    DriveType.Removable => isZh ? "可移动磁盘" : "Removable Disk",
                    DriveType.Network => isZh ? "网络磁盘" : "Network Drive",
                    _ => isZh ? "本地磁盘" : "Local Disk"
                };

            var effectiveLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? fallbackLabel : drive.VolumeLabel;
            var displayTitle = $"{driveLetter} {effectiveLabel}";

            list.Add(new DriveStorageInfo(
                RootPath: rootPath,
                DriveLetter: driveLetter,
                VolumeLabel: effectiveLabel,
                DisplayTitle: displayTitle,
                ShortTitle: driveLetter,
                DriveType: type,
                DriveFormat: string.IsNullOrWhiteSpace(drive.DriveFormat) ? "NTFS" : drive.DriveFormat,
                IsSystemDrive: isSystem,
                TotalBytes: total,
                FreeBytes: free,
                UsedBytes: used,
                UsageFraction: fraction,
                UsagePercent: percent
            ));
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
