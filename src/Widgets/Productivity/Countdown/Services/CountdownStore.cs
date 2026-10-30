using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia.Threading;
using Countdown.Locales;
using Countdown.Models;
using DeskCanvas.Core;

namespace Countdown.Services;

public static class CountdownStore
{
    private static readonly object FileLock = new();
    private static List<CountdownItem>? cachedEvents;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static event Action<List<CountdownItem>, object?>? ModelChanged;

    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "countdown.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.CountdownFile;
        }
    }

    static CountdownStore()
    {
        InitializeWatcher();
        Const.ActiveProfileChanged += _ =>
        {
            lock (FileLock)
            {
                cachedEvents = null;
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

    public static List<CountdownItem> Get()
    {
        if (cachedEvents != null) return cachedEvents;

        lock (FileLock)
        {
            if (cachedEvents != null) return cachedEvents;

            var path = FilePath;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var list = JsonSerializer.Deserialize<List<CountdownItem>>(json, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        cachedEvents = list;
                        return cachedEvents;
                    }
                }
                catch { }
            }

            cachedEvents = CreateDefaultEvents();
            SaveInternal(cachedEvents);
            return cachedEvents;
        }
    }

    public static void Save(List<CountdownItem> events, object? sender = null)
    {
        lock (FileLock)
        {
            cachedEvents = events;
            SaveInternal(events);
        }

        NotifyModelChanged(events, sender);
    }

    public static void NotifyModelChanged(List<CountdownItem> events, object? sender)
    {
        if (ModelChanged == null) return;

        void InvokeSubscribers()
        {
            var invocationList = ModelChanged?.GetInvocationList();
            if (invocationList == null) return;

            foreach (var d in invocationList)
            {
                // Prune disposed view models to prevent memory leaks from static roots
                if (d.Target is Countdown.ViewModels.CountdownViewModel { IsDisposed: true })
                {
                    ModelChanged -= (Action<List<CountdownItem>, object?>)d;
                    continue;
                }

                try
                {
                    ((Action<List<CountdownItem>, object?>)d).Invoke(events, sender);
                }
                catch
                {
                    // Ignore exceptions from individual subscribers
                }
            }
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            InvokeSubscribers();
        }
        else
        {
            Dispatcher.UIThread.Post(InvokeSubscribers);
        }
    }

    public static void AddOrUpdate(CountdownItem evt, object? sender = null)
    {
        var list = Get().ToList();
        int index = list.FindIndex(e => e.Id == evt.Id);
        if (index >= 0)
        {
            list[index] = evt;
        }
        else
        {
            list.Add(evt);
        }
        Save(list, sender);
    }

    public static void Delete(string id, object? sender = null)
    {
        var list = Get().Where(e => e.Id != id).ToList();
        Save(list, sender);
    }

    public static void TogglePin(string id, object? sender = null)
    {
        var list = Get().Select(e => e.Id == id ? e with { IsPinned = !e.IsPinned } : e).ToList();
        Save(list, sender);
    }

    private static void SaveInternal(List<CountdownItem> events)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (fileWatcher != null)
            {
                fileWatcher.EnableRaisingEvents = false;
            }

            try
            {
                var json = JsonSerializer.Serialize(events, JsonOptions);
                File.WriteAllText(path, json);
                lastWriteTime = File.GetLastWriteTimeUtc(path);
            }
            finally
            {
                if (fileWatcher != null)
                {
                    fileWatcher.EnableRaisingEvents = true;
                }
            }
        }
        catch { }
    }

    private static List<CountdownItem> CreateDefaultEvents()
    {
        var now = DateTime.Now;
        var nextYear = new DateTime(now.Year + 1, 1, 1);

        return
        [
            new CountdownItem(
                Id: Guid.NewGuid().ToString("N"),
                Title: Locale.Countdown_Default_NewYear,
                TargetDate: nextYear,
                StartDate: new DateTime(now.Year, 1, 1),
                Repeat: RepeatCycle.Yearly,
                Category: "Holiday",
                ColorTag: "#FF5A5F",
                IsPinned: true
            ),
            new CountdownItem(
                Id: Guid.NewGuid().ToString("N"),
                Title: Locale.Countdown_Default_SpringFestival,
                TargetDate: nextYear,
                IsLunar: true,
                LunarMonth: 1,
                LunarDay: 1,
                Repeat: RepeatCycle.Yearly,
                Category: "Holiday",
                ColorTag: "#FF9F0A"
            ),
            new CountdownItem(
                Id: Guid.NewGuid().ToString("N"),
                Title: Locale.Countdown_Default_Weekend,
                TargetDate: now.Date.AddDays(((int)DayOfWeek.Saturday - (int)now.DayOfWeek + 7) % 7),
                Repeat: RepeatCycle.Weekly,
                Category: "Life",
                ColorTag: "#0A84FF"
            )
        ];
    }

    private static void InitializeWatcher()
    {
        try
        {
            if (fileWatcher != null)
            {
                fileWatcher.EnableRaisingEvents = false;
                fileWatcher.Changed -= OnFileChanged;
                fileWatcher.Dispose();
                fileWatcher = null;
            }

            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
            };

            fileWatcher.Changed += OnFileChanged;
            fileWatcher.EnableRaisingEvents = true;
        }
        catch { }
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            var writeTime = File.GetLastWriteTimeUtc(e.FullPath);
            if (Math.Abs((writeTime - lastWriteTime).TotalMilliseconds) < 1000)
                return;

            lastWriteTime = writeTime;

            Dispatcher.UIThread.Post(() =>
            {
                lock (FileLock)
                {
                    cachedEvents = null;
                }
                var updated = Get();
                NotifyModelChanged(updated, null);
            }, DispatcherPriority.Background);
        }
        catch { }
    }
}
