using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskCanvas.Core;

namespace DeskCanvas.Services;

/// <summary>
/// One user-defined 分类 of the launcher: a named cluster of catalogue entries, shown as a capsule
/// in the app grid's category bar (macOS Launchpad's folders, expressed as a filter).
/// <para>
/// <see cref="Items"/> holds <see cref="LauncherEntry.Key"/> values (lowercase shortcut paths), not
/// display names: a category must survive a rename on disk, and the catalogue is the only place
/// that knows what a key currently points at. An entry belongs to <b>at most one</b> category — the
/// bar is a partition, so "which category is this app in?" always has a single answer.
/// </para>
/// </summary>
public sealed record LauncherAppCategory(string Id, string Name, IReadOnlyList<string> Items)
{
    /// <summary>True when the category owns <paramref name="entryKey"/> (keys are case-insensitive).</summary>
    public bool Contains(string entryKey) =>
        !string.IsNullOrEmpty(entryKey) &&
        Items.Any(item => string.Equals(item, entryKey, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One category inside the <c>launcher-categories.json</c> document.</summary>
public sealed class SpotlightAppCategoryDto
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";

    [JsonPropertyName("items")] public List<string> Items { get; set; } = [];
}

/// <summary>The <c>launcher-categories.json</c> document.</summary>
public sealed class SpotlightAppCategoryFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = SpotlightAppCategoryStore.SchemaVersion;

    [JsonPropertyName("categories")] public List<SpotlightAppCategoryDto> Categories { get; set; } = [];
}

/// <summary>
/// Persists the launcher's user-defined app categories in
/// <see cref="Const.DataFolder"/>/<c>launcher-categories.json</c>.
/// <para>
/// A separate store rather than a field on <c>LauncherSettings</c>: categories are launcher
/// <b>state</b> (like 置顶 / usage), they are edited from the grid itself, and keeping them out of
/// the settings document means a corrupt category list can never take the whole appearance config
/// down with it. Same contract as <see cref="SpotlightUsageStore"/>: never throws, a missing or
/// unreadable file yields an empty list, and every write is atomic (temp file + move) so a crash
/// mid-write cannot leave half a file behind.
/// </para>
/// </summary>
public sealed class SpotlightAppCategoryStore
{
    /// <summary>Current document schema. Bumping it requires a migration path, not silent data loss.</summary>
    public const int SchemaVersion = 1;

    /// <summary>Longest accepted category name, in characters.</summary>
    public const int MaxNameLength = 24;

    private readonly string filePath;
    private readonly object gate = new();
    private readonly List<LauncherAppCategory> categories = [];

    /// <summary>Raised after any mutation. May fire on any thread; consumers must marshal to their own.</summary>
    public event Action? Changed;

    /// <summary>Loads the real store from the data folder.</summary>
    public SpotlightAppCategoryStore()
        : this(Path.Combine(Const.DataFolder, "launcher-categories.json"))
    {
    }

    /// <summary>Injection constructor (tests, tooling). Does not touch the real data folder.</summary>
    public SpotlightAppCategoryStore(string filePath)
    {
        this.filePath = filePath;
        Load();
    }

    /// <summary>Full path of the backing file.</summary>
    public string FilePath => filePath;

    /// <summary>A defensive copy of the categories, in bar order. Never null.</summary>
    public IReadOnlyList<LauncherAppCategory> Categories
    {
        get { lock (gate) return categories.ToArray(); }
    }

    /// <summary>The category that owns <paramref name="entryKey"/>, or null when it is unfiled.</summary>
    public LauncherAppCategory? CategoryOf(string entryKey)
    {
        if (string.IsNullOrEmpty(entryKey)) return null;
        lock (gate) return categories.FirstOrDefault(category => category.Contains(entryKey));
    }

    /// <summary>
    /// Append a new empty category and return it. A blank or over-long name is normalized instead
    /// of rejected (the caller may be mid-typing); the name is only ever shown, never keyed on.
    /// </summary>
    public LauncherAppCategory Create(string? name)
    {
        LauncherAppCategory created;
        Action? changed;
        lock (gate)
        {
            created = new LauncherAppCategory(NewId(), NormalizeName(name), []);
            categories.Add(created);
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
        return created;
    }

    /// <summary>Rename a category. Returns false when the id is unknown or the name normalizes away.</summary>
    public bool Rename(string id, string? name)
    {
        if (string.IsNullOrEmpty(id)) return false;
        var normalized = NormalizeName(name);

        Action? changed;
        lock (gate)
        {
            var index = categories.FindIndex(category => category.Id == id);
            if (index < 0) return false;
            if (categories[index].Name == normalized) return true;

            categories[index] = categories[index] with { Name = normalized };
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
        return true;
    }

    /// <summary>Delete a category (its entries fall back to 全部). Returns false when the id is unknown.</summary>
    public bool Delete(string id)
    {
        if (string.IsNullOrEmpty(id)) return false;

        Action? changed;
        lock (gate)
        {
            if (categories.RemoveAll(category => category.Id == id) == 0) return false;
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
        return true;
    }

    /// <summary>
    /// File an entry into <paramref name="categoryId"/>, or unfile it when the id is null/unknown.
    /// An entry is only ever in one category, so this removes it from wherever it was first.
    /// </summary>
    public bool Assign(string entryKey, string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return false;

        Action? changed;
        lock (gate)
        {
            var target = string.IsNullOrEmpty(categoryId)
                ? -1
                : categories.FindIndex(category => category.Id == categoryId);

            var moved = false;
            for (var i = 0; i < categories.Count; i++)
            {
                var category = categories[i];
                var has = category.Contains(entryKey);
                if (i == target)
                {
                    if (has) continue;
                    categories[i] = category with { Items = [.. category.Items, entryKey] };
                    moved = true;
                }
                else if (has)
                {
                    categories[i] = category with
                    {
                        Items = category.Items
                            .Where(item => !string.Equals(item, entryKey, StringComparison.OrdinalIgnoreCase))
                            .ToArray(),
                    };
                    moved = true;
                }
            }

            if (!moved) return false;
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
        return true;
    }

    /// <summary>Drop keys that no longer exist in the catalogue (a deleted shortcut).</summary>
    public bool Prune(IReadOnlyCollection<string> liveKeys)
    {
        if (liveKeys.Count == 0) return false;
        var live = new HashSet<string>(liveKeys, StringComparer.OrdinalIgnoreCase);

        Action? changed;
        lock (gate)
        {
            var changedAny = false;
            for (var i = 0; i < categories.Count; i++)
            {
                var kept = categories[i].Items.Where(live.Contains).ToArray();
                if (kept.Length == categories[i].Items.Count) continue;
                categories[i] = categories[i] with { Items = kept };
                changedAny = true;
            }

            if (!changedAny) return false;
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
        return true;
    }

    private static string NewId() => Guid.NewGuid().ToString("N")[..12];

    private static string NormalizeName(string? name)
    {
        var trimmed = (name ?? string.Empty).Trim();
        if (trimmed.Length > MaxNameLength) trimmed = trimmed[..MaxNameLength];
        return trimmed;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Populates state from disk; every failure mode degrades to an empty store.</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(filePath)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != SchemaVersion)
                return;
            if (!root.TryGetProperty("categories", out var list) || list.ValueKind != JsonValueKind.Array)
                return;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var element in list.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object) continue;

                var id = element.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(id) || !seenIds.Add(id)) continue;

                var name = element.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? NormalizeName(nameEl.GetString())
                    : "";
                if (name.Length == 0) continue;

                var items = new List<string>();
                if (element.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in itemsEl.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String) continue;
                        var key = item.GetString();
                        // An entry can only live in one category: the first category that claims
                        // it keeps it, exactly like Assign() maintains.
                        if (string.IsNullOrWhiteSpace(key) || !seenKeys.Add(key)) continue;
                        items.Add(key);
                    }
                }

                categories.Add(new LauncherAppCategory(id, name, items));
            }
        }
        catch (Exception ex)
        {
            // Missing/corrupt/locked file: an empty bar is recoverable (the user just files the
            // apps again), while blocking the overlay's startup is not.
            System.Diagnostics.Debug.WriteLine($"[SpotlightAppCategoryStore] Load failed, starting empty: {ex.Message}");
        }
    }

    /// <summary>Serializes and atomically replaces the file. Caller holds <see cref="gate"/>.</summary>
    private void Save_Locked()
    {
        try
        {
            var file = new SpotlightAppCategoryFile();
            foreach (var category in categories)
            {
                file.Categories.Add(new SpotlightAppCategoryDto
                {
                    Id = category.Id,
                    Name = category.Name,
                    Items = [.. category.Items],
                });
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = $"{filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Best effort: the in-memory list stays authoritative for this session.
            System.Diagnostics.Debug.WriteLine($"[SpotlightAppCategoryStore] Save failed: {ex.Message}");
        }
    }
}
