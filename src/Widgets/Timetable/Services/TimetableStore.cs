using System;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using DeskCanvas.Core;
using Timetable.Models;

namespace Timetable.Services;

/// <summary>
/// Unified single data source for all timetable/schedule widgets across all screens.
/// Persists schedule data to <c>timetable.json</c> in the data folder.
/// Synchronizes additions, imports, settings, and edits live across all active instances.
/// </summary>
public static class TimetableStore
{
    private static readonly object FileLock = new();
    private static TimetableModel? cachedModel;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    // JsonSerializerOptions builds an expensive metadata cache — never allocate one per save/load.
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LoadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Raised whenever the unified timetable data changes (by any widget or external file edit).
    /// <c>sender</c> is the widget instance or source that triggered the change.
    /// </summary>
    public static event Action<TimetableModel, object?>? ModelChanged;

    /// <summary>
    /// Raised when persisting the model to disk fails (disk full, lock, ACL, …).
    /// The message is human-readable; surfaces belong in UI (e.g. the settings page status line),
    /// because a silent save failure is exactly how the two historical data copies diverged.
    /// </summary>
    public static event Action<string>? SaveFailed;

    /// <summary>
    /// The file path where unified timetable data is stored.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "timetable.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.TimetableFile;
        }
    }

    static TimetableStore()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// Gets the current unified timetable model.
    /// If not loaded, loads from <c>timetable.json</c> (migrating from legacy <c>layout.json</c> if needed).
    /// <para>
    /// Failure policy: when the file exists but cannot be parsed, the in-memory default is used
    /// and the file is <b>not</b> rewritten — overwriting it with defaults here destroyed the
    /// user's last recoverable copy once a file went bad. The file is only (re)written when it
    /// was missing (fresh install / first run) or when a legacy migration contributed data.
    /// </para>
    /// </summary>
    public static TimetableModel Get()
    {
        if (cachedModel != null) return cachedModel;

        lock (FileLock)
        {
            if (cachedModel != null) return cachedModel;

            cachedModel = LoadFromFile();
            if (cachedModel != null) return cachedModel;

            var fileExists = File.Exists(FilePath);
            var migrated = MigrateFromLayout();
            cachedModel = migrated ?? CreateDefault();

            // Adopted legacy data (or a fresh install): persist it. An existing but
            // unreadable file with nothing to migrate is left untouched on purpose.
            if (!fileExists || migrated != null) SaveInternal(cachedModel);
            return cachedModel;
        }
    }

    /// <summary>
    /// Updates the unified timetable model and saves it to <c>timetable.json</c>.
    /// Broadcasts the update to all active widget instances across all screens.
    /// </summary>
    public static void Save(TimetableModel model, object? sender = null)
    {
        var normalized = model.Normalize();
        lock (FileLock)
        {
            cachedModel = normalized;
            SaveInternal(normalized);
        }

        NotifyModelChanged(normalized, sender);
    }

    private static void NotifyModelChanged(TimetableModel model, object? sender)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                ModelChanged?.Invoke(model, sender);
            }
            else
            {
                Dispatcher.UIThread.Post(() => ModelChanged?.Invoke(model, sender));
            }
        }
        catch
        {
            ModelChanged?.Invoke(model, sender);
        }
    }

    private static void SaveInternal(TimetableModel model)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = SaveOptions;
            var json = JsonSerializer.Serialize(model, options);

            // Atomic replace: write beside the target, then move. A plain WriteAllText
            // truncates first — a crash (or a watcher reload) mid-write used to leave a
            // torn file behind, and the next load silently fell back to defaults.
            var tmp = path + ".tmp";
            lastWriteTime = DateTime.UtcNow;
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TimetableStore] Failed to save {FilePath}: {ex.Message}");
            RaiseSaveFailed(ex.Message);
        }
    }

    private static void RaiseSaveFailed(string message)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                SaveFailed?.Invoke(message);
            }
            else
            {
                Dispatcher.UIThread.Post(() => SaveFailed?.Invoke(message));
            }
        }
        catch
        {
            SaveFailed?.Invoke(message);
        }
    }

    private static TimetableModel? LoadFromFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var model = JsonSerializer.Deserialize<TimetableModel>(json, LoadOptions);
            return model?.Normalize();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TimetableStore] Failed to read {FilePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Seamless migration: if timetable.json does not exist yet, inspect layout.json
    /// to carry over existing user schedule from any previously placed Timetable widget.
    /// </summary>
    private static TimetableModel? MigrateFromLayout()
    {
        try
        {
            if (!File.Exists(Const.LayoutFile)) return null;

            var json = File.ReadAllText(Const.LayoutFile);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("Screens", out var screensElem) && screensElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var screenElem in screensElem.EnumerateArray())
                {
                    if (screenElem.TryGetProperty("Layout", out var layoutElem) && layoutElem.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var widgetElem in layoutElem.EnumerateArray())
                        {
                            if (widgetElem.TryGetProperty("Type", out var typeElem) && typeElem.GetString() == "Timetable"
                                && widgetElem.TryGetProperty("Settings", out var settingsElem) && settingsElem.ValueKind == JsonValueKind.Object)
                            {
                                var model = JsonSerializer.Deserialize<TimetableModel>(settingsElem.GetRawText(), new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });
                                if (model != null && model.HasData)
                                {
                                    return model.Normalize();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TimetableStore] Migration check failed: {ex.Message}");
        }

        return null;
    }

    private static TimetableModel CreateDefault()
    {
        return new TimetableModel();
    }

    private static void InitializeWatcher()
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            fileWatcher.Changed += OnFileChanged;
            fileWatcher.Created += OnFileChanged;
            fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TimetableStore] Watcher init failed: {ex.Message}");
        }
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore changes triggered by ourselves within 1.5 seconds
        if ((DateTime.UtcNow - lastWriteTime).TotalMilliseconds < 1500)
            return;

        // Debounce external write bursts
        Dispatcher.UIThread.Post(() =>
        {
            lock (FileLock)
            {
                var reloaded = LoadFromFile();
                if (reloaded == null || Equals(reloaded, cachedModel)) return;
                cachedModel = reloaded;
                NotifyModelChanged(cachedModel, null);
            }
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Test helper to reset the in-memory cache.
    /// </summary>
    public static void ResetForTesting(TimetableModel? model = null)
    {
        lock (FileLock)
        {
            cachedModel = model?.Normalize();
        }
    }
}
