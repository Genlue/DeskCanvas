using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DeskCanvas.Core;
using Map.Models;

namespace Map.Services;

/// <summary>
/// Service responsible for fetching, caching, and managing map tiles.
/// Features a 2-tier cache: in-memory LRU bitmap cache and local persistent disk cache.
/// </summary>
public class MapTileService : IDisposable
{
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    static MapTileService()
    {
        HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/122.0.0.0 Safari/537.36 DeskCanvas/1.9.3");
    }

    private const int MaxMemoryCacheTiles = 400;
    private const int TargetCacheTilesAfterEviction = 320;

    /// <summary>Disk cache budget and the level it is pruned back to. Tiles are ~10-60 KB PNGs;
    /// without a cap the cache grew for the lifetime of the machine — every panned zoom level
    /// of every provider kept its files forever.</summary>
    private const long MaxDiskCacheBytes = 512L * 1024 * 1024;
    private const long TargetDiskCacheBytesAfterPrune = 384L * 1024 * 1024;

    /// <summary>Process-wide prune throttle: a new service instance is built for every MapView
    /// (including each Gallery visit), and enumerating the whole cache that often is pointless.</summary>
    private static long lastDiskPruneAt;

    private readonly ConcurrentDictionary<string, Bitmap> memoryCache = new();
    private readonly ConcurrentQueue<string> evictionQueue = new();
    private readonly ConcurrentDictionary<string, byte> activeRequests = new();
    private readonly string cacheBaseDir;
    private bool isDisposed;

    public event Action? TileLoaded;

    public MapTileService()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        cacheBaseDir = Path.Combine(localAppData, Const.AppName, "Cache", "Map");
        try
        {
            if (!Directory.Exists(cacheBaseDir))
                Directory.CreateDirectory(cacheBaseDir);
        }
        catch
        {
            // Ignore directory creation issues; will fallback to memory-only
        }

        PruneDiskCacheIfNeeded();
    }

    /// <summary>
    /// Gets a tile from memory or disk, or schedules an asynchronous background download.
    /// Returns null immediately if the tile is still fetching.
    /// </summary>
    public Bitmap? GetTile(MapProvider provider, int z, int x, int y, string? baiduApiKey = null)
    {
        if (isDisposed) return null;

        if (z < MapModel.MinZoom || z > MapModel.MaxZoom) return null;
        var maxTile = 1 << z;
        if (y < 0 || y >= maxTile) return null;
        x = ((x % maxTile) + maxTile) % maxTile;

        var key = $"{provider}_{z}_{x}_{y}";

        // 1. In-memory cache hit
        if (memoryCache.TryGetValue(key, out var cachedBitmap))
            return cachedBitmap;

        // 2. Disk cache hit
        var diskPath = GetDiskCachePath(provider, z, x, y);
        if (File.Exists(diskPath))
        {
            try
            {
                using var stream = File.OpenRead(diskPath);
                var bmp = new Bitmap(stream);
                AddToMemoryCache(key, bmp);
                return bmp;
            }
            catch
            {
                // Corrupt file, re-download
                try { File.Delete(diskPath); } catch { }
            }
        }

        // 3. Queue download
        if (activeRequests.TryAdd(key, 0))
        {
            Task.Run(async () =>
            {
                try
                {
                    await DownloadTileAsync(provider, z, x, y, key, diskPath, baiduApiKey);
                }
                finally
                {
                    activeRequests.TryRemove(key, out _);
                }
            });
        }

        return null;
    }

    private void AddToMemoryCache(string key, Bitmap bitmap)
    {
        if (memoryCache.TryAdd(key, bitmap))
        {
            evictionQueue.Enqueue(key);
            TrimMemoryCacheIfNeeded();
        }
    }

    private void TrimMemoryCacheIfNeeded()
    {
        if (memoryCache.Count <= MaxMemoryCacheTiles) return;

        // Evict oldest keys down to TargetCacheTilesAfterEviction
        while (memoryCache.Count > TargetCacheTilesAfterEviction && evictionQueue.TryDequeue(out var oldKey))
        {
            memoryCache.TryRemove(oldKey, out _);
            // CRITICAL: We intentionally do NOT call bmp.Dispose() here.
            // The UI render thread may concurrently be executing context.DrawImage(bmp, ...).
            // Dereferenced bitmaps are safely reclaimed and finalized by the .NET GC
            // once rendering completes, eliminating AccessViolationExceptions.
        }
    }

    private async Task DownloadTileAsync(MapProvider provider, int z, int x, int y, string key, string diskPath, string? baiduApiKey)
    {
        var url = GetTileUrl(provider, z, x, y, baiduApiKey);
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var bytes = await HttpClient.GetByteArrayAsync(url);
            if (bytes == null || bytes.Length < 100 || isDisposed) return;

            // Save to disk
            try
            {
                var dir = Path.GetDirectoryName(diskPath);
                if (dir != null && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                await File.WriteAllBytesAsync(diskPath, bytes);
            }
            catch
            {
                // Ignore disk write failure
            }

            // Decode to Avalonia Bitmap
            using var ms = new MemoryStream(bytes);
            var bitmap = new Bitmap(ms);

            AddToMemoryCache(key, bitmap);

            // Notify UI
            Dispatcher.UIThread.Post(() =>
            {
                if (!isDisposed)
                    TileLoaded?.Invoke();
            });
        }
        catch
        {
            // Silently fail for transient network glitches
        }
    }

    private string GetDiskCachePath(MapProvider provider, int z, int x, int y)
    {
        return Path.Combine(cacheBaseDir, provider.ToString(), z.ToString(), $"{x}_{y}.png");
    }

    /// <summary>
    /// Keeps the disk cache inside <see cref="MaxDiskCacheBytes"/> by deleting the oldest tiles
    /// first. Runs off the UI thread and at most once per 6 h per process, so a Gallery visit or a
    /// widget rebuild never pays for a directory walk; a tile deleted mid-read simply fails its
    /// decode in <see cref="GetTile"/> and is re-downloaded.
    /// </summary>
    private void PruneDiskCacheIfNeeded()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref lastDiskPruneAt);
        if (now - last < TimeSpan.FromHours(6).TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref lastDiskPruneAt, now, last) != last) return;

        Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(cacheBaseDir)) return;

                long total = 0;
                var files = new List<(string Path, long Size, DateTime Written)>();
                foreach (var path in Directory.EnumerateFiles(cacheBaseDir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(path);
                        if (info.Exists)
                        {
                            total += info.Length;
                            files.Add((path, info.Length, info.LastWriteTimeUtc));
                        }
                    }
                    catch { }
                }

                if (total <= MaxDiskCacheBytes) return;

                files.Sort((a, b) => a.Written.CompareTo(b.Written));   // oldest first
                foreach (var (path, size, _) in files)
                {
                    if (total <= TargetDiskCacheBytesAfterPrune) break;
                    try
                    {
                        File.Delete(path);
                        total -= size;
                    }
                    catch { }
                }
            }
            catch
            {
                // Pruning is best effort; the cache keeps working unpruned.
            }
        });
    }

    private static string GetTileUrl(MapProvider provider, int z, int x, int y, string? baiduApiKey)
    {
        if (z < MapModel.MinZoom || z > MapModel.MaxZoom) return string.Empty;
        var maxTile = 1 << z;
        if (y < 0 || y >= maxTile) return string.Empty;

        // Wrap tile x if it overflows the 2^z boundary
        x = ((x % maxTile) + maxTile) % maxTile;

        // Subdomain index rotation (prevent integer overflow with Math.Abs on int.MinValue)
        var subInt = (int)(Math.Abs((long)x + y) % 4);
        var subChar = (char)('a' + subInt);

        return provider switch
        {
            MapProvider.Amap =>
                $"https://webrd0{subInt + 1}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}",

            MapProvider.AmapSatellite =>
                $"https://webst0{subInt + 1}.is.autonavi.com/appmaptile?style=6&x={x}&y={y}&z={z}",

            MapProvider.Google =>
                $"https://mt{subInt}.google.com/vt/lyrs=m&hl=zh-CN&x={x}&y={y}&z={z}",

            MapProvider.GoogleSatellite =>
                $"https://mt{subInt}.google.com/vt/lyrs=s&hl=zh-CN&x={x}&y={y}&z={z}",

            MapProvider.GoogleHybrid =>
                $"https://mt{subInt}.google.com/vt/lyrs=y&hl=zh-CN&x={x}&y={y}&z={z}",

            MapProvider.AppleLight =>
                $"https://{subChar}.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png",

            MapProvider.AppleDark =>
                $"https://{subChar}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png",

            MapProvider.OpenStreetMap =>
                $"https://tile.openstreetmap.org/{z}/{x}/{y}.png",

            MapProvider.Baidu =>
                !string.IsNullOrWhiteSpace(baiduApiKey)
                    ? $"https://api.map.baidu.com/staticimage/v2?ak={baiduApiKey}&center={x},{y}&width=256&height=256&zoom={z}"
                    : $"https://webrd0{subInt + 1}.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}",

            _ => $"https://webrd01.is.autonavi.com/appmaptile?lang=zh_cn&size=1&scale=1&style=8&x={x}&y={y}&z={z}"
        };
    }

    public void ClearMemoryCache()
    {
        memoryCache.Clear();
        while (evictionQueue.TryDequeue(out _)) { }
    }

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;
        ClearMemoryCache();
    }
}
