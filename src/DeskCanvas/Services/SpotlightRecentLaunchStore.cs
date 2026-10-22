using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskCanvas.Core;
using DeskCanvas.ViewModels;

namespace DeskCanvas.Services;

/// <summary>
/// A persistent record of an item opened through Spotlight search results.
/// </summary>
public sealed record SpotlightRecentLaunch(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("display")] string Display,
    [property: JsonPropertyName("sub")] string? Sub,
    [property: JsonPropertyName("category")] string? Category,
    [property: JsonPropertyName("target")] string? Target,
    [property: JsonPropertyName("iconPath")] string? IconPath,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp,
    [property: JsonPropertyName("kind")] SpotlightItemKind Kind
);

/// <summary>
/// Thread-safe persistent store for recently opened items from search results.
/// Persisted to &lt;data folder&gt;/recent-search-launches.json.
/// </summary>
public sealed class SpotlightRecentLaunchStore : IDisposable
{
    private const int MaxEntries = 100;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly List<SpotlightRecentLaunch> _items = [];
    private bool _disposed;

    public SpotlightRecentLaunchStore()
        : this(Path.Combine(Const.DataFolder, "recent-search-launches.json"))
    {
    }

    public SpotlightRecentLaunchStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SpotlightRecentLaunch> GetRecent(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<SpotlightRecentLaunch>();
        lock (_gate)
        {
            return _items.Take(maxCount).ToList();
        }
    }

    public void RecordLaunch(SpotlightItem item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Id)) return;
        if (item.Kind == SpotlightItemKind.Header) return;

        string? target = item.Kind switch
        {
            SpotlightItemKind.App => item.Entry?.Target ?? item.Entry?.ShortcutPath,
            SpotlightItemKind.File => item.FilePath,
            SpotlightItemKind.Web => item.Web?.Url,
            SpotlightItemKind.WebResult => item.LinkUrl,
            _ => item.FilePath ?? item.Id
        };

        string? iconPath = item.Entry?.ShortcutPath ?? item.FilePath;

        var launch = new SpotlightRecentLaunch(
            Id: item.Id,
            Display: item.Name,
            Sub: item.Subtitle,
            Category: item.GroupTitle ?? item.Kind.ToString(),
            Target: target,
            IconPath: iconPath,
            Timestamp: DateTime.UtcNow,
            Kind: item.Kind
        );

        lock (_gate)
        {
            // Deduplicate: bump recent item to the top if launched again
            _items.RemoveAll(existing =>
                string.Equals(existing.Id, launch.Id, StringComparison.Ordinal)
                || (!string.IsNullOrEmpty(target) && string.Equals(existing.Target, target, StringComparison.OrdinalIgnoreCase)));

            _items.Insert(0, launch);

            if (_items.Count > MaxEntries)
            {
                _items.RemoveRange(MaxEntries, _items.Count - MaxEntries);
            }

            Save();
        }
    }

    private void Load()
    {
        lock (_gate)
        {
            _items.Clear();
            if (!File.Exists(_filePath)) return;

            try
            {
                var json = File.ReadAllText(_filePath);
                var loaded = JsonSerializer.Deserialize<List<SpotlightRecentLaunch>>(json);
                if (loaded != null)
                {
                    _items.AddRange(loaded);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpotlightRecentLaunchStore] Load failed, starting empty: {ex.Message}");
            }
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SpotlightRecentLaunchStore] Save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
