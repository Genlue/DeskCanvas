using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia.Threading;
using DeskCanvas.Core;
using Habits.Models;

namespace Habits.Services;

/// <summary>
/// Unified single data source for all habits widgets across all screens.
/// Persists habits and check-in records to <c>habits.json</c> in the data folder.
/// Synchronizes check-ins, additions, edits, and deletions live across all active instances.
/// </summary>
public static class HabitsStore
{
    private static readonly object FileLock = new();
    private static HabitsDataModel? cachedModel;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    // JsonSerializerOptions builds an expensive metadata cache — reuse static instances
    private static readonly JsonSerializerOptions SaveOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions LoadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Shared JsonSerializerOptions for serializing/deserializing HabitsModel and HabitsDataModel.
    /// </summary>
    public static JsonSerializerOptions SharedOptions => LoadOptions;

    /// <summary>
    /// Raised whenever unified habits data changes (by any widget or external file edit).
    /// </summary>
    public static event Action<HabitsDataModel, object?>? ModelChanged;

    /// <summary>
    /// Raised when persisting the model to disk fails (disk full, lock, ACL, ...).
    /// </summary>
    public static event Action<string>? SaveFailed;

    /// <summary>
    /// The file path where unified habits data is stored.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, Const.HabitsFileName);
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.HabitsFile;
        }
    }

    static HabitsStore()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// Gets the current unified habits model.
    /// If not loaded, loads from <c>habits.json</c> or creates defaults.
    /// Failure policy: when the file exists but cannot be parsed, the in-memory default is used
    /// and the file is not rewritten to prevent destroying recoverable user data.
    /// </summary>
    public static HabitsDataModel Get()
    {
        if (cachedModel != null) return cachedModel;

        lock (FileLock)
        {
            if (cachedModel != null) return cachedModel;

            var path = FilePath;
            var fileExists = File.Exists(path);

            cachedModel = LoadFromFile();
            if (cachedModel != null) return cachedModel;

            cachedModel = CreateDefault();
            if (!fileExists)
            {
                SaveInternal(cachedModel);
            }
            return cachedModel;
        }
    }

    /// <summary>
    /// Updates the unified habits model and saves to disk.
    /// </summary>
    public static void Save(HabitsDataModel model, object? sender = null)
    {
        lock (FileLock)
        {
            cachedModel = model;
            SaveInternal(model);
        }

        NotifyModelChanged(model, sender);
    }

    private static void NotifyModelChanged(HabitsDataModel model, object? sender)
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

    private static void SaveInternal(HabitsDataModel model)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(model, SaveOptions);

            // Atomic replace: write to .tmp beside the target, then move.
            // Plain WriteAllText truncates first — a crash mid-write leaves a torn file.
            var tmp = path + ".tmp";
            lastWriteTime = DateTime.UtcNow;
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HabitsStore] Save failed: {ex.Message}");
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

    private static HabitsDataModel? LoadFromFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<HabitsDataModel>(json, LoadOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HabitsStore] Load failed: {ex.Message}");
            return null;
        }
    }

    private static HabitsDataModel CreateDefault()
    {
        var now = DateTime.Now;
        var habits = new List<HabitItem>
        {
            new("h_water", "每日饮水", "💧", "#007AFF", HabitType.Numeric, 2000, 250, "ml", HabitFrequency.Daily, now),
            new("h_read", "深度阅读", "📖", "#34C759", HabitType.Numeric, 30, 10, "分钟", HabitFrequency.Daily, now),
            new("h_sleep", "早睡打卡", "🌙", "#AF52DE", HabitType.Boolean, 1, 1, "次", HabitFrequency.Daily, now),
            new("h_sport", "运动健身", "🏃", "#FF9500", HabitType.Boolean, 1, 1, "次", HabitFrequency.Daily, now)
        };

        return new HabitsDataModel(habits, [], DayResetHour: 0);
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
            fileWatcher.Renamed += OnFileRenamed;
            fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HabitsStore] Watcher init failed: {ex.Message}");
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

    private static void OnFileRenamed(object sender, RenamedEventArgs e) => OnFileChanged(sender, e);

    /// <summary>
    /// Test helper to reset the in-memory cache.
    /// </summary>
    public static void ResetForTesting(HabitsDataModel? model = null)
    {
        lock (FileLock)
        {
            cachedModel = model;
        }
    }
}
