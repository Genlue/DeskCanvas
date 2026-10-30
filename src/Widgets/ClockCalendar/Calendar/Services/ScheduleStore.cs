using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using Calendar.Models;
using DeskCanvas.Core;

namespace Calendar.Services;

/// <summary>
/// Unified single data source for all calendar schedule widgets across all screens.
/// Persists events to <c>schedule.json</c> in the active profile directory.
/// Synchronizes additions, edits, and deletions live across all active instances.
/// </summary>
public static class ScheduleStore
{
    private static readonly object FileLock = new();
    private static ScheduleModel? cachedModel;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    /// <summary>
    /// Raised whenever the unified schedule data changes (by any widget or external file edit).
    /// <c>sender</c> is the widget instance or source that triggered the change.
    /// </summary>
    public static event Action<ScheduleModel, object?>? ModelChanged;

    /// <summary>
    /// The file path where unified schedule events are stored.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "schedule.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.ScheduleFile;
        }
    }

    static ScheduleStore()
    {
        InitializeWatcher();
        Const.ActiveProfileChanged += _ =>
        {
            lock (FileLock)
            {
                cachedModel = null;
                InitializeWatcher();
            }
            try
            {
                var updated = Get();
                NotifyModelChanged(updated, null);
            }
            catch { }
        };
    }

    /// <summary>
    /// Gets the current unified schedule model.
    /// </summary>
    public static ScheduleModel Get()
    {
        if (cachedModel != null) return cachedModel;

        lock (FileLock)
        {
            if (cachedModel != null) return cachedModel;

            cachedModel = LoadFromFile() ?? CreateDefault();
            SaveInternal(cachedModel);
            return cachedModel;
        }
    }

    /// <summary>
    /// Updates the unified schedule model and saves it to <c>schedule.json</c>.
    /// Broadcasts the update to all active widget instances across all screens.
    /// </summary>
    public static void Save(ScheduleModel model, object? sender = null)
    {
        lock (FileLock)
        {
            cachedModel = model;
            SaveInternal(model);
        }

        NotifyModelChanged(model, sender);
    }

    public static void AddOrUpdateEvent(ScheduleEventModel ev, object? sender = null)
    {
        var model = Get();
        var list = new List<ScheduleEventModel>(model.Events ?? []);
        var index = list.FindIndex(e => e.Id == ev.Id);
        if (index >= 0)
        {
            list[index] = ev;
        }
        else
        {
            list.Add(ev);
        }
        Save(model with { Events = list }, sender);
    }

    public static void DeleteEvent(string id, object? sender = null)
    {
        var model = Get();
        var list = new List<ScheduleEventModel>(model.Events ?? []);
        list.RemoveAll(e => e.Id == id);
        Save(model with { Events = list }, sender);
    }

    public static void ToggleComplete(string id, object? sender = null)
    {
        var model = Get();
        var list = new List<ScheduleEventModel>(model.Events ?? []);
        var index = list.FindIndex(e => e.Id == id);
        if (index >= 0)
        {
            var cur = list[index];
            list[index] = cur with { IsCompleted = !cur.IsCompleted };
            Save(model with { Events = list }, sender);
        }
    }

    public static List<ScheduleEventModel> GetEventsForDate(DateTime date)
    {
        var targetDate = date.Date;
        var model = Get();
        return (model.Events ?? [])
            .Where(e => e.StartTime.Date <= targetDate && e.EndTime.Date >= targetDate)
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.StartTime)
            .ToList();
    }

    private static void NotifyModelChanged(ScheduleModel model, object? sender)
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
        catch { }
    }

    private static ScheduleModel? LoadFromFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<ScheduleModel>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveInternal(ScheduleModel model)
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(model, JsonOptions);
            var tempFile = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempFile, json);
            File.Move(tempFile, path, overwrite: true);
            lastWriteTime = File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScheduleStore] SaveInternal failed: {ex.Message}");
        }
    }

    private static ScheduleModel CreateDefault()
    {
        var categories = new List<ScheduleCategoryModel>
        {
            new("work", "工作", "#007AFF"),
            new("meeting", "会议", "#FF9500"),
            new("personal", "生活", "#34C759"),
            new("urgent", "重要", "#FF3B30"),
            new("study", "学习", "#AF52DE")
        };

        // Do not pre-fill events: newly added widgets start cleanly with empty schedule
        return new ScheduleModel(1, categories, []);
    }

    private static void InitializeWatcher()
    {
        try
        {
            fileWatcher?.Dispose();
            fileWatcher = null;

            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) return;

            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            fileWatcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName
            };

            fileWatcher.Changed += OnFileChanged;
            fileWatcher.Created += OnFileChanged;
            fileWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return;

            var currentWrite = File.GetLastWriteTimeUtc(path);
            if ((currentWrite - lastWriteTime).TotalMilliseconds < 400) return;
            lastWriteTime = currentWrite;

            lock (FileLock)
            {
                cachedModel = LoadFromFile() ?? cachedModel;
            }

            if (cachedModel != null)
            {
                NotifyModelChanged(cachedModel, null);
            }
        }
        catch { }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
}
