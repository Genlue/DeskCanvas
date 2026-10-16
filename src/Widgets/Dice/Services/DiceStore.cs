using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeskCanvas.Core;
using Dice.Models;

namespace Dice.Services;

public static class DiceStore
{
    private static readonly object FileLock = new();
    private static DiceDataModel? cachedData;
    private static readonly Random Rnd = new();

    public static event Action<DiceRollRecord>? RollCompleted;
    public static event Action? HistoryCleared;

    public static string FilePath
    {
        get
        {
            var inCurrent = Path.Combine(Const.CurrentFolder, "dice.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.DiceFile;
        }
    }

    public static DiceDataModel GetData()
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
                    var data = JsonSerializer.Deserialize<DiceDataModel>(json);
                    if (data != null)
                    {
                        cachedData = data;
                        return cachedData;
                    }
                }
                catch { }
            }

            cachedData = new DiceDataModel();
            return cachedData;
        }
    }

    public static DiceRollRecord Roll(int count, DiceType type)
    {
        count = Math.Clamp(count, 1, 6);
        int maxVal = (int)type;

        var values = new List<int>(count);
        int sum = 0;
        bool allMax = true;

        for (int i = 0; i < count; i++)
        {
            int val = Rnd.Next(1, maxVal + 1);
            values.Add(val);
            sum += val;
            if (val != maxVal) allMax = false;
        }

        var record = new DiceRollRecord
        {
            Timestamp = DateTime.Now,
            Values = values,
            Sum = sum,
            Type = type,
            IsCriticalMax = allMax
        };

        var data = GetData();
        lock (FileLock)
        {
            data.History.Insert(0, record);
            if (data.History.Count > 100)
            {
                data.History.RemoveRange(100, data.History.Count - 100);
            }
            SaveInternal(data);
        }

        RollCompleted?.Invoke(record);
        return record;
    }

    public static void ClearHistory()
    {
        var data = GetData();
        lock (FileLock)
        {
            data.History.Clear();
            SaveInternal(data);
        }
        HistoryCleared?.Invoke();
    }

    private static void SaveInternal(DiceDataModel data)
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
