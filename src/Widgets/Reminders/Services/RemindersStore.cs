using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using Reminders.Locales;
using Reminders.Models;
using DeskCanvas.Core;

namespace Reminders.Services;

/// <summary>
/// Unified single data source for all checklist/reminders widgets across all screens.
/// Persists checklist items to <c>reminders.json</c> in the software directory.
/// Synchronizes additions, completions, edits, and deletions live across all active instances.
/// </summary>
public static class RemindersStore
{
    private static readonly object FileLock = new();
    private static RemindersListModel? cachedModel;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    /// <summary>
    /// Raised whenever the unified reminders data changes (by any widget or external file edit).
    /// <c>sender</c> is the widget instance or source that triggered the change.
    /// </summary>
    public static event Action<RemindersListModel, object?>? ModelChanged;

    /// <summary>
    /// The file path where unified reminders are stored.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "reminders.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.RemindersFile;
        }
    }

    static RemindersStore()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// Gets the current unified reminders model.
    /// If not loaded, loads from <c>reminders.json</c> (migrating from legacy <c>layout.json</c> if needed).
    /// </summary>
    public static RemindersListModel Get()
    {
        if (cachedModel != null) return cachedModel;

        lock (FileLock)
        {
            if (cachedModel != null) return cachedModel;

            cachedModel = LoadFromFile() ?? MigrateFromLayout() ?? CreateDefault();
            SaveInternal(cachedModel);
            return cachedModel;
        }
    }

    /// <summary>
    /// Updates the unified reminders model and saves it to <c>reminders.json</c>.
    /// Broadcasts the update to all active widget instances across all screens.
    /// </summary>
    public static void Save(RemindersListModel model, object? sender = null)
    {
        lock (FileLock)
        {
            cachedModel = model;
            SaveInternal(model);
        }

        NotifyModelChanged(model, sender);
    }

    private static void NotifyModelChanged(RemindersListModel model, object? sender)
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

    private static void SaveInternal(RemindersListModel model)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(model, options);
            lastWriteTime = DateTime.UtcNow;
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemindersStore] Failed to save {FilePath}: {ex.Message}");
        }
    }

    private static RemindersListModel? LoadFromFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var model = JsonSerializer.Deserialize<RemindersListModel>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return model != null ? model with { Reminders = model.Reminders ?? [] } : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemindersStore] Failed to read {FilePath}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Seamless migration: if reminders.json does not exist yet, inspect layout.json
    /// to carry over existing user reminders from any previously placed Reminders widget.
    /// </summary>
    private static RemindersListModel? MigrateFromLayout()
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
                            if (widgetElem.TryGetProperty("Type", out var typeElem) && typeElem.GetString() == "Reminders"
                                && widgetElem.TryGetProperty("Settings", out var settingsElem) && settingsElem.ValueKind == JsonValueKind.Object)
                            {
                                var model = JsonSerializer.Deserialize<RemindersListModel>(settingsElem.GetRawText(), new JsonSerializerOptions
                                {
                                    PropertyNameCaseInsensitive = true
                                });
                                if (model != null && (model.Reminders is { Count: > 0 } || !string.IsNullOrWhiteSpace(model.ListName)))
                                {
                                    return model with { Reminders = model.Reminders ?? [] };
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RemindersStore] Migration check failed: {ex.Message}");
        }

        return null;
    }

    private static RemindersListModel CreateDefault()
    {
        return new RemindersListModel(Locale.Reminders_List_Title ?? "待办清单", []);
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
            System.Diagnostics.Debug.WriteLine($"[RemindersStore] Watcher init failed: {ex.Message}");
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
    public static void ResetForTesting(RemindersListModel? model = null)
    {
        lock (FileLock)
        {
            cachedModel = model;
        }
    }
}
