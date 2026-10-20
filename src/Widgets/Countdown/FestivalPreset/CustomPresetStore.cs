// 预设单独落一个 presets.json：events.json 已经发布过，往事件文件里塞模板字段
// 会让旧版本读到意外的数据形状；预设丢失的代价只是少几个快捷入口，风险更低。
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Countdown.Services;
using DeskCanvas.Core;

namespace Countdown.FestivalPreset;

public static class CustomPresetStore
{
    private static readonly object FileLock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>与 countdown.json 同目录，跟随当前配置档案。</summary>
    public static string FilePath
    {
        get
        {
            var directory = Path.GetDirectoryName(CountdownStore.FilePath);
            if (string.IsNullOrEmpty(directory))
            {
                directory = Const.ActiveProfileFolder;
            }

            return Path.Combine(directory, "presets.json");
        }
    }

    public static List<CustomPreset> Get()
    {
        lock (FileLock)
        {
            return ReadUnlocked();
        }
    }

    /// <summary>同 id 覆盖，否则追加；顺序即 chip 的展示顺序。</summary>
    public static void Add(CustomPreset preset)
    {
        lock (FileLock)
        {
            var presets = ReadUnlocked();
            int index = presets.FindIndex(item => item.Id == preset.Id);
            if (index >= 0)
            {
                presets[index] = preset;
            }
            else
            {
                presets.Add(preset);
            }

            WriteUnlocked(presets);
        }
    }

    public static bool Delete(string id)
    {
        lock (FileLock)
        {
            var presets = ReadUnlocked();
            if (presets.RemoveAll(item => item.Id == id) == 0)
            {
                return false;
            }

            WriteUnlocked(presets);
            return true;
        }
    }

    private static List<CustomPreset> ReadUnlocked()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<CustomPreset>>(json, JsonOptions) ?? [];
        }
        catch
        {
            // 文件损坏时退回空列表，不让弹窗打不开。
            return [];
        }
    }

    private static void WriteUnlocked(List<CustomPreset> presets)
    {
        try
        {
            var path = FilePath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(presets, JsonOptions));
        }
        catch
        {
            // 存储失败不阻断当前编辑流程，和 CountdownStore 的处理保持一致。
        }
    }
}
