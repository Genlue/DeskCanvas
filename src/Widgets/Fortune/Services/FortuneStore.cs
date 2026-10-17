using System;
using System.IO;
using System.Text.Json;
using DeskCanvas.Core;
using Fortune.Models;

namespace Fortune.Services;

public static class FortuneStore
{
    private static readonly object FileLock = new();
    private static FortuneDataModel? cachedData;

    public static event Action<DailyFortuneRecord>? FortuneDrawn;

    static FortuneStore()
    {
        Const.ActiveProfileChanged += _ =>
        {
            lock (FileLock)
            {
                cachedData = null;
            }
        };
    }

    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "fortune.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.FortuneFile;
        }
    }

    public static FortuneDataModel GetData()
    {
        if (cachedData != null) return cachedData;

        lock (FileLock)
        {
            if (cachedData != null) return cachedData;

            var path = FilePath;
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var data = JsonSerializer.Deserialize<FortuneDataModel>(json);
                    if (data != null)
                    {
                        cachedData = data;
                        ValidateToday(cachedData);
                        return cachedData;
                    }
                }
                catch { }
            }

            cachedData = new FortuneDataModel();
            return cachedData;
        }
    }

    private static void ValidateToday(FortuneDataModel data)
    {
        string todayStr = DateTime.Today.ToString("yyyy-MM-dd");
        if (data.TodayRecord != null && data.TodayRecord.Date != todayStr)
        {
            // It's a new day! The previous day's fortune becomes historical only.
            data.TodayRecord = null;
        }
    }

    public static DailyFortuneRecord? GetTodayFortune()
    {
        var data = GetData();
        ValidateToday(data);
        return data.TodayRecord;
    }

    public static DailyFortuneRecord DrawToday(FortunePoolType poolType)
    {
        var data = GetData();
        ValidateToday(data);

        var item = FortuneRepository.GetRandomFortune(poolType);
        var record = new DailyFortuneRecord
        {
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
            DrawnTime = DateTime.Now,
            Fortune = item,
            IsFreeDraw = false
        };

        lock (FileLock)
        {
            data.TodayRecord = record;
            data.History.Insert(0, record);
            if (data.History.Count > 30)
            {
                data.History.RemoveRange(30, data.History.Count - 30);
            }
            SaveInternal(data);
        }

        FortuneDrawn?.Invoke(record);
        return record;
    }

    public static DailyFortuneRecord FreeDraw(FortunePoolType poolType)
    {
        var data = GetData();
        var item = FortuneRepository.GetRandomFortune(poolType);
        var record = new DailyFortuneRecord
        {
            Date = DateTime.Today.ToString("yyyy-MM-dd"),
            DrawnTime = DateTime.Now,
            Fortune = item,
            IsFreeDraw = true
        };

        lock (FileLock)
        {
            data.History.Insert(0, record);
            if (data.History.Count > 30)
            {
                data.History.RemoveRange(30, data.History.Count - 30);
            }
            SaveInternal(data);
        }

        FortuneDrawn?.Invoke(record);
        return record;
    }

    public static void ResetToday()
    {
        var data = GetData();
        lock (FileLock)
        {
            data.TodayRecord = null;
            SaveInternal(data);
        }
    }

    private static void SaveInternal(FortuneDataModel data)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
        catch { }
    }
}
