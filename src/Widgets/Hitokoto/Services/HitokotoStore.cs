using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using DeskCanvas.Core;
using Hitokoto.Models;

namespace Hitokoto.Services;

/// <summary>
/// Persisted local store for Hitokoto widget: current quote cache, local favorites,
/// recent history (up to 50 quotes), and liked sentence UUIDs.
/// <para>
/// The serialized shape stays the same plain document (current / favorites / history / likedUuids),
/// but the two questions asked on every render — "is this quote liked / favorited" — are answered
/// from in-memory sets instead of linear scans, favorites are capped, and the file rewrite is
/// debounced so a burst of likes or bookmarks lands on disk once instead of once per action.
/// </para>
/// </summary>
public sealed class HitokotoStore
{
    public const int MaxHistoryCount = 50;

    /// <summary>
    /// Soft cap for local favorites. Favorites are a hand-curated list and the cap only exists to
    /// stop unbounded growth; when it is exceeded the oldest entry (the tail — newest sits at
    /// index 0) is dropped.
    /// </summary>
    public const int MaxFavoriteCount = 500;

    /// <summary>How long a mutation waits before the whole file is rewritten; later mutations reset it.</summary>
    private const int SaveDebounceMs = 400;

    public static HitokotoStore Instance { get; } = new();

    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };

    private readonly object syncLock = new();
    private readonly string filePath;
    private HitokotoStoreData data;

    // Lookup indexes over the serialized lists. Rebuilt on load, maintained on every mutation —
    // the point is that rendering never walks the lists.
    private readonly HashSet<string> likedSet = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> favoriteSet = new(StringComparer.OrdinalIgnoreCase);

    // Debounced writer: armed by the first mutation, reset by every following one.
    private Timer? saveTimer;

    public event EventHandler? Changed;

    /// <summary>Raised when the user selects a specific quote from the secondary panel to display on the widget.</summary>
    public event EventHandler<HitokotoItem>? QuoteSelected;

    private sealed class HitokotoStoreData
    {
        public HitokotoItem? Current { get; set; }
        public List<HitokotoItem> Favorites { get; set; } = [];
        public List<HitokotoItem> History { get; set; } = [];
        public List<string> LikedUuids { get; set; } = [];
    }

    private HitokotoStore()
    {
        filePath = Path.Combine(Const.DataFolder, "hitokoto.json");
        data = LoadFromDisk();
        RebuildIndexes();
    }

    public HitokotoItem GetCurrentOrFallback(HitokotoModel model)
    {
        lock (syncLock)
        {
            if (data.Current != null && !string.IsNullOrWhiteSpace(data.Current.Hitokoto))
            {
                return Enrich(data.Current);
            }

            var fallback = HitokotoService.OfflineFallbackQuotes[0] with { FetchedAt = DateTime.Now };
            data.Current = fallback;
            return Enrich(fallback);
        }
    }

    public IReadOnlyList<HitokotoItem> GetFavorites()
    {
        lock (syncLock)
        {
            return data.Favorites.Select(Enrich).ToList();
        }
    }

    public IReadOnlyList<HitokotoItem> GetHistory()
    {
        lock (syncLock)
        {
            return data.History.Select(Enrich).ToList();
        }
    }

    public bool IsFavorite(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        lock (syncLock)
        {
            return favoriteSet.Contains(uuid);
        }
    }

    public bool IsLiked(string? uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        lock (syncLock)
        {
            return likedSet.Contains(uuid);
        }
    }

    public void RecordQuote(HitokotoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Hitokoto)) return;
        lock (syncLock)
        {
            var enriched = Enrich(item);
            data.Current = enriched;

            data.History.RemoveAll(x =>
                (!string.IsNullOrWhiteSpace(item.Uuid) && string.Equals(x.Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase))
                || string.Equals(x.Hitokoto, item.Hitokoto, StringComparison.Ordinal));
            data.History.Insert(0, enriched);
            if (data.History.Count > MaxHistoryCount)
            {
                data.History.RemoveRange(MaxHistoryCount, data.History.Count - MaxHistoryCount);
            }

            // Keep favorite entry's like count in sync if present
            var favIdx = data.Favorites.FindIndex(x => string.Equals(x.Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase));
            if (favIdx >= 0)
            {
                data.Favorites[favIdx] = enriched;
            }

            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool ToggleFavorite(HitokotoItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Hitokoto)) return false;
        bool nowFavorite;
        lock (syncLock)
        {
            var idx = data.Favorites.FindIndex(x =>
                (!string.IsNullOrWhiteSpace(item.Uuid) && string.Equals(x.Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase))
                || string.Equals(x.Hitokoto, item.Hitokoto, StringComparison.Ordinal));
            if (idx >= 0)
            {
                var removed = data.Favorites[idx];
                data.Favorites.RemoveAt(idx);
                if (!string.IsNullOrWhiteSpace(removed.Uuid))
                    favoriteSet.Remove(removed.Uuid);
                nowFavorite = false;
            }
            else
            {
                data.Favorites.Insert(0, Enrich(item));
                if (!string.IsNullOrWhiteSpace(item.Uuid))
                    favoriteSet.Add(item.Uuid);
                TrimFavorites();
                nowFavorite = true;
            }
            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return nowFavorite;
    }

    public HitokotoItem MarkLiked(HitokotoItem item)
    {
        HitokotoItem updated;
        lock (syncLock)
        {
            var alreadyLiked = !string.IsNullOrWhiteSpace(item.Uuid) && likedSet.Contains(item.Uuid);

            if (!alreadyLiked && !string.IsNullOrWhiteSpace(item.Uuid))
            {
                data.LikedUuids.Add(item.Uuid);
                likedSet.Add(item.Uuid);
            }

            var newLikes = alreadyLiked ? item.Likes : item.Likes + 1;
            updated = item with { Likes = newLikes, IsLiked = true };

            if (data.Current != null && string.Equals(data.Current.Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase))
                data.Current = updated;

            for (var i = 0; i < data.History.Count; i++)
            {
                if (string.Equals(data.History[i].Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase))
                    data.History[i] = updated;
            }

            for (var i = 0; i < data.Favorites.Count; i++)
            {
                if (string.Equals(data.Favorites[i].Uuid, item.Uuid, StringComparison.OrdinalIgnoreCase))
                    data.Favorites[i] = updated;
            }

            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return updated;
    }

    public void SelectQuote(HitokotoItem item)
    {
        RecordQuote(item);
        QuoteSelected?.Invoke(this, Enrich(item));
    }

    public void ClearHistory()
    {
        lock (syncLock)
        {
            data.History.Clear();
            ScheduleSave();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private HitokotoItem Enrich(HitokotoItem item)
    {
        var liked = !string.IsNullOrWhiteSpace(item.Uuid) && likedSet.Contains(item.Uuid);
        var likes = liked && item.Likes == 0 ? 1 : item.Likes;
        return item with { IsLiked = liked, Likes = likes };
    }

    /// <summary>Drop the oldest favorites beyond <see cref="MaxFavoriteCount"/> (index 0 is newest).</summary>
    private void TrimFavorites()
    {
        while (data.Favorites.Count > MaxFavoriteCount)
        {
            var overflow = data.Favorites[^1];
            data.Favorites.RemoveAt(data.Favorites.Count - 1);
            if (!string.IsNullOrWhiteSpace(overflow.Uuid))
                favoriteSet.Remove(overflow.Uuid);
        }
    }

    /// <summary>Rebuild the lookup sets and enforce the caps on data that just came from disk.</summary>
    private void RebuildIndexes()
    {
        likedSet.Clear();
        foreach (var uuid in data.LikedUuids)
        {
            if (!string.IsNullOrWhiteSpace(uuid))
                likedSet.Add(uuid);
        }

        favoriteSet.Clear();
        foreach (var favorite in data.Favorites)
        {
            if (!string.IsNullOrWhiteSpace(favorite.Uuid))
                favoriteSet.Add(favorite.Uuid);
        }
        TrimFavorites();

        if (data.History.Count > MaxHistoryCount)
            data.History.RemoveRange(MaxHistoryCount, data.History.Count - MaxHistoryCount);
    }

    private HitokotoStoreData LoadFromDisk()
    {
        HitokotoStoreData? loaded = null;
        try
        {
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                loaded = JsonSerializer.Deserialize<HitokotoStoreData>(json);
            }
        }
        catch
        {
            // A corrupt file falls back to a fresh store rather than failing the widget.
        }

        if (loaded == null)
            return new HitokotoStoreData();

        // A hand-edited file can carry explicit nulls for the collections; treat them as absent.
        loaded.Favorites ??= [];
        loaded.History ??= [];
        loaded.LikedUuids ??= [];
        return loaded;
    }

    /// <summary>
    /// Arm (or re-arm) the debounced writer. Called under <see cref="syncLock"/>; the timer
    /// callback serializes the current snapshot under the same lock.
    /// </summary>
    private void ScheduleSave()
    {
        saveTimer ??= new Timer(_ => SaveToDisk(), null, Timeout.Infinite, Timeout.Infinite);
        saveTimer.Change(SaveDebounceMs, Timeout.Infinite);
    }

    private void SaveToDisk()
    {
        try
        {
            string json;
            lock (syncLock)
            {
                json = JsonSerializer.Serialize(data, SaveOptions);
            }

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmpPath = filePath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, filePath, overwrite: true);
        }
        catch
        {
            // Ignore transient IO failures
        }
    }
}
