using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Services;

/// <summary>One entry inside the <c>launcher-usage.json</c> document.</summary>
public sealed class SpotlightUsageEntryDto
{
    [JsonPropertyName("count")] public int Count { get; set; }

    /// <summary>UTC timestamp of the last launch; omitted/absent means "never launched".</summary>
    [JsonPropertyName("last")] public DateTime? Last { get; set; }

    [JsonPropertyName("pinned")] public bool Pinned { get; set; }
}

/// <summary>The <c>launcher-usage.json</c> document.</summary>
public sealed class SpotlightUsageFile
{
    [JsonPropertyName("version")] public int Version { get; set; } = SpotlightUsageStore.SchemaVersion;

    [JsonPropertyName("entries")]
    public Dictionary<string, SpotlightUsageEntryDto> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>
/// Persists the spotlight's per-entry usage (launch count, last launch, pinned 置顶) in
/// <see cref="Const.DataFolder"/>/<c>launcher-usage.json</c>. Keys are opaque strings
/// (lowercase paths or <c>"wf:&lt;id&gt;</c>") — this store deliberately knows nothing about
/// what they point at, so the launcher can change its entry model without touching storage.
/// <para>
/// Never throws: a missing or corrupt file yields an empty store (the usage history is
/// rebuilt organically), and a failed save is swallowed — losing a launch count is annoying,
/// but a crashed keystroke handler would kill the whole overlay. Writes are atomic
/// (temp file + <see cref="File.Move"/>) so a crash mid-write can never leave a half file
/// behind; launch writes are coalesced on a 500 ms timer because a single drag over the grid
/// can fire dozens of launches and none of them deserves its own disk hit.
/// </para>
/// </summary>
public sealed class SpotlightUsageStore : IDisposable
{
    /// <summary>Current document schema. Bumping it requires a migration path, not silent data loss.</summary>
    public const int SchemaVersion = 1;

    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, LauncherUsage> _entries = new(StringComparer.Ordinal);

    private Timer? _flushTimer;
    private bool _dirty;
    private bool _disposed;

    /// <summary>Raised after any mutation. May fire on any thread; consumers must marshal to their own.</summary>
    public event Action? Changed;

    /// <summary>Loads the usage file; any problem (missing/corrupt/unreadable) yields an empty store.</summary>
    public SpotlightUsageStore()
        // Const.DataFolder is the one writable, hot-updatable location shared by every
        // cross-profile store (same convention as shortcuts.json / ui-state.json).
        : this(Path.Combine(Const.DataFolder, "launcher-usage.json"))
    {
    }

    /// <summary>Injection constructor (tests, tooling). Does not touch the real data folder.</summary>
    public SpotlightUsageStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    /// <summary>Full path of the backing file.</summary>
    public string FilePath => _filePath;

    /// <summary>
    /// The usage record for a key. Never null and never throws: an unknown or blank key degrades
    /// to the default record, so callers can rank straight off the result without guarding.
    /// </summary>
    public LauncherUsage Get(string entryKey)
    {
        if (string.IsNullOrEmpty(entryKey)) return new LauncherUsage();
        lock (_gate)
        {
            return _entries.TryGetValue(entryKey, out var usage) ? usage : new LauncherUsage();
        }
    }

    /// <summary>
    /// Records one launch (count + 1, last-launched = now). The disk write is deferred 500 ms
    /// and coalesced — see the type doc. A blank key is ignored rather than stored.
    /// </summary>
    public void RecordLaunch(string entryKey)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return;

        Action? changed;
        lock (_gate)
        {
            if (_disposed) return;
            _entries.TryGetValue(entryKey, out var usage);
            usage = (usage ?? new LauncherUsage()) with
            {
                LaunchCount = Math.Max(0, usage?.LaunchCount ?? 0) + 1,
                LastLaunchedUtc = DateTime.UtcNow,
            };
            _entries[entryKey] = usage;
            changed = Changed;
            ScheduleFlush_Locked();
        }

        changed?.Invoke();
    }

    /// <summary>Sets the pinned (置顶) flag and persists immediately.</summary>
    public void SetPinned(string entryKey, bool pinned)
    {
        if (string.IsNullOrWhiteSpace(entryKey)) return;

        Action? changed;
        lock (_gate)
        {
            if (_disposed) return;
            _entries.TryGetValue(entryKey, out var usage);
            if (usage is not null && usage.Pinned == pinned) return;

            _entries[entryKey] = (usage ?? new LauncherUsage()) with { Pinned = pinned };
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
    }

    /// <summary>
    /// A defensive copy of every entry, for stale-entry cleanup and usage inspection. Mutating
    /// the store while iterating the snapshot is safe — it is a copy.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, LauncherUsage>> Snapshot()
    {
        lock (_gate)
        {
            var snapshot = new List<KeyValuePair<string, LauncherUsage>>(_entries.Count);
            foreach (var (key, usage) in _entries)
                snapshot.Add(new KeyValuePair<string, LauncherUsage>(key, usage));
            return snapshot;
        }
    }

    /// <summary>Clears every usage record and persists the empty store immediately.</summary>
    public void Clear()
    {
        Action? changed;
        lock (_gate)
        {
            if (_disposed) return;
            if (_entries.Count == 0) return;
            _entries.Clear();
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
    }

    /// <summary>
    /// Flushes any pending coalesced write and stops the flush timer. Safe to call twice; a
    /// disposed store quietly ignores all further mutations instead of throwing.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;

            // The timer callback takes the same lock, so disposing under the lock guarantees no
            // flush races with this one.
            _flushTimer?.Dispose();
            _flushTimer = null;

            if (_dirty) Save_Locked();
        }
    }

    /// <summary>
    /// Arms (or re-arms) the one-shot flush timer. Re-arming an existing timer instead of
    /// recreating one keeps the common burst case allocation-free. Caller holds <see cref="_gate"/>.
    /// </summary>
    private void ScheduleFlush_Locked()
    {
        _dirty = true;
        if (_flushTimer is null)
        {
            _flushTimer = new Timer(FlushTimerCallback, null, CoalesceDelayMs, Timeout.Infinite);
        }
        else
        {
            try
            {
                _flushTimer.Change(CoalesceDelayMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException)
            {
                // Raced with Dispose inside the timer callback: _disposed is now true, the
                // final state was flushed there, nothing to do.
            }
        }
    }

    private void FlushTimerCallback(object? state)
    {
        Action? changed;
        lock (_gate)
        {
            if (_disposed || !_dirty) return;
            changed = Changed;
            Save_Locked();
        }

        changed?.Invoke();
    }

    /// <summary>Delay before a launch burst is written to disk.</summary>
    private const int CoalesceDelayMs = 500;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Populates state from disk; every failure mode degrades to an empty store.</summary>
    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            using var document = JsonDocument.Parse(File.ReadAllText(_filePath));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (!root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number ||
                !version.TryGetInt32(out var v) || v != SchemaVersion)
                return;
            if (!root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Object)
                return;

            // Field-by-field tolerance (same policy as the other stores): one corrupt entry
            // keeps its default instead of discarding the whole history.
            foreach (var property in entries.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                var entry = property.Value;

                var count = 0;
                if (entry.TryGetProperty("count", out var countEl) && countEl.ValueKind == JsonValueKind.Number &&
                    countEl.TryGetInt32(out var c))
                    count = Math.Max(0, c); // A negative count is garbage, not a debt to repay.

                DateTime? last = null;
                if (entry.TryGetProperty("last", out var lastEl) && lastEl.ValueKind == JsonValueKind.String)
                {
                    // A bad timestamp keeps null (no recency bonus) rather than sinking the entry.
                    if (lastEl.TryGetDateTime(out var parsed)) last = parsed;
                }

                var pinned = entry.TryGetProperty("pinned", out var pinnedEl) &&
                             pinnedEl.ValueKind == JsonValueKind.True;

                // Older files may still carry "quickKey"; the feature is gone, so the unknown
                // field is simply ignored here (no migration, no data loss beyond that field).
                _entries[property.Name] = new LauncherUsage(count, last, pinned);
            }
        }
        catch (Exception ex)
        {
            // Missing/corrupt/locked file: the usage history is a cache of habit, not truth.
            // Starting empty (and overwriting on the next save) beats blocking startup.
            System.Diagnostics.Debug.WriteLine($"[SpotlightUsageStore] Load failed, starting empty: {ex.Message}");
        }
    }

    /// <summary>
    /// Serializes and atomically replaces the file. Caller holds <see cref="_gate"/>; the IO is
    /// small (a few hundred entries at most after coalescing) and keeping it inside the lock
    /// buys strict write ordering, which two-phase snapshot-then-write would not.
    /// Swallows failures on purpose: the score degrades gracefully to in-memory-only, and
    /// logging here is all we can do — the launch keystroke must never observe an exception.
    /// </summary>
    private void Save_Locked()
    {
        try
        {
            var file = new SpotlightUsageFile();
            foreach (var (key, usage) in _entries)
            {
                file.Entries[key] = new SpotlightUsageEntryDto
                {
                    Count = usage.LaunchCount,
                    Last = usage.LastLaunchedUtc,
                    Pinned = usage.Pinned,
                };
            }

            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var temp = $"{_filePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(file, SerializerOptions));
            File.Move(temp, _filePath, overwrite: true);
            _dirty = false;
        }
        catch (Exception ex)
        {
            // Deliberately not thrown: usage stats are best-effort. _dirty stays true so the
            // next flush timer tick (or Dispose) retries with fresher data anyway.
            System.Diagnostics.Debug.WriteLine($"[SpotlightUsageStore] Save failed: {ex.Message}");
        }
    }
}
