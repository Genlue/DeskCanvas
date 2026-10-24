using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskCanvas.Core;

namespace DeskCanvas.Services;

/// <summary>A query the user actually searched with (a result was opened from it).</summary>
public sealed record SpotlightSearchHistoryEntry(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("timestamp")] DateTime Timestamp
);

/// <summary>
/// Thread-safe persistent store of recent search queries, surfaced by the 单空格
/// search-history list. Persisted to <see cref="Const.DataFolder"/>/<c>search-history.json</c>.
/// <para>
/// Same tolerance policy as the other spotlight stores: a missing or corrupt file starts
/// empty, and a failed save is swallowed — losing one history row must never break the
/// keystroke path. Only queries the user <b>used</b> (opened a result from) are recorded,
/// so the list reads as "searches that went somewhere", not every half-typed word.
/// </para>
/// </summary>
public sealed class SpotlightSearchHistoryStore : IDisposable
{
    private const int MaxEntries = 50;
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly List<SpotlightSearchHistoryEntry> _items = [];
    private bool _disposed;

    public SpotlightSearchHistoryStore()
        : this(Path.Combine(Const.DataFolder, "search-history.json"))
    {
    }

    public SpotlightSearchHistoryStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<SpotlightSearchHistoryEntry> GetRecent(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<SpotlightSearchHistoryEntry>();
        lock (_gate)
        {
            return _items.Take(maxCount).ToList();
        }
    }

    /// <summary>
    /// Records one used query. Blank/whitespace queries are ignored (the 单/双空格 views are
    /// not searches); a repeat moves the query to the top instead of duplicating it.
    /// </summary>
    public void Record(string? query)
    {
        var text = query?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        lock (_gate)
        {
            if (_disposed) return;
            _items.RemoveAll(existing =>
                string.Equals(existing.Query, text, StringComparison.Ordinal));
            _items.Insert(0, new SpotlightSearchHistoryEntry(text, DateTime.UtcNow));
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
                var loaded = JsonSerializer.Deserialize<List<SpotlightSearchHistoryEntry>>(json);
                if (loaded != null)
                {
                    foreach (var entry in loaded)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Query))
                        {
                            _items.Add(entry);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SpotlightSearchHistoryStore] Load failed, starting empty: {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[SpotlightSearchHistoryStore] Save failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
