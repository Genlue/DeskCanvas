namespace DeskCanvas.Runtime;

/// <summary>
/// Platform-neutral drive facts captured with pure BCL <see cref="DriveInfo"/> enumeration. Byte
/// counts are raw BCL values (InvariantCulture semantics); presentation data such as fallback
/// labels, display titles and usage percentages is derived by consumers. Only Fixed, Removable and
/// Network drives that are ready with a positive size enter the snapshot, mirroring the legacy
/// per-drive exception protection including guarded VolumeLabel/DriveFormat access.
/// </summary>
public sealed record SharedDriveSnapshot(
    string Name,
    DriveType DriveType,
    long TotalBytes,
    long FreeBytes,
    string VolumeLabel,
    string DriveFormat)
{
    /// <summary>Enumerates the drives once per shared round. Never throws.</summary>
    public static IReadOnlyList<SharedDriveSnapshot> CaptureAll()
    {
        var list = new List<SharedDriveSnapshot>();
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
                if (type != DriveType.Fixed && type != DriveType.Removable && type != DriveType.Network)
                    continue;

                if (!drive.IsReady)
                    continue;

                var total = drive.TotalSize;
                if (total <= 0)
                    continue;

                var free = Math.Clamp(drive.AvailableFreeSpace, 0L, total);

                string label = string.Empty;
                try { label = drive.VolumeLabel?.Trim() ?? string.Empty; } catch { }

                string format = string.Empty;
                try { format = drive.DriveFormat?.Trim() ?? string.Empty; } catch { }

                list.Add(new SharedDriveSnapshot(drive.Name, type, total, free, label, format));
            }
            catch
            {
                // Skip inaccessible or disconnected drives without breaking enumeration
            }
        }

        return list;
    }
}

/// <summary>One shared DriveInfo enumeration per TTL window for every Storage consumer.</summary>
public sealed class StorageDrivesSharedProvider
{
    private readonly SharedDataProvider<int, IReadOnlyList<SharedDriveSnapshot>> data;

    public StorageDrivesSharedProvider(Func<int, CancellationToken, Task<IReadOnlyList<SharedDriveSnapshot>>> fetch, TimeSpan? ttl = null) =>
        data = new(fetch, ttl ?? TimeSpan.FromSeconds(10), "storage-drives");

    public SharedDataStatistics Statistics => data.Statistics;

    public Task<SharedDataProvider<int, IReadOnlyList<SharedDriveSnapshot>>.Lease> AcquireAsync(string owner, CancellationToken cancellationToken = default) =>
        data.AcquireAsync(owner, 0, cancellationToken);
}
