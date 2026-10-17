using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeskCanvas.Core;
using Wheel.Models;

namespace Wheel.Services;

public static class WheelStore
{
    private static readonly object FileLock = new();
    private static WheelDataModel? cachedData;

    public static event Action? TemplatesChanged;
    public static event Action<WheelDecisionRecord>? DecisionRecorded;

    static WheelStore()
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
            var inCurrent = Path.Combine(Const.CurrentFolder, "wheel.json");
            if (File.Exists(inCurrent)) return inCurrent;
            return Const.WheelFile;
        }
    }

    public static readonly string[] Palette =
    [
        "#5A9F68", "#E8A87C", "#6B8EAE", "#C98BB9",
        "#D6A756", "#7FB8A4", "#B57EDC", "#E88C7D",
        "#5FA8A0", "#C9A227", "#8E9FCD", "#D98BA3"
    ];

    public static WheelDataModel GetData()
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
                    var data = JsonSerializer.Deserialize<WheelDataModel>(json);
                    if (data != null && data.Templates.Count > 0)
                    {
                        cachedData = data;
                        return cachedData;
                    }
                }
                catch { }
            }

            cachedData = CreateDefaultData();
            SaveInternal(cachedData);
            return cachedData;
        }
    }

    private static WheelDataModel CreateDefaultData()
    {
        var model = new WheelDataModel();

        // 1. 今天吃什么
        model.Templates.Add(new WheelTemplate
        {
            Id = "food",
            Title = "今天吃什么",
            Options =
            [
                new WheelOption { Text = "火锅", Weight = 1, ColorHex = Palette[0] },
                new WheelOption { Text = "麻辣烫", Weight = 1, ColorHex = Palette[1] },
                new WheelOption { Text = "麦当劳", Weight = 1, ColorHex = Palette[2] },
                new WheelOption { Text = "烤肉", Weight = 1, ColorHex = Palette[3] },
                new WheelOption { Text = "炒菜米饭", Weight = 1, ColorHex = Palette[4] },
                new WheelOption { Text = "轻食减脂", Weight = 1, ColorHex = Palette[5] },
                new WheelOption { Text = "兰州拉面", Weight = 1, ColorHex = Palette[6] },
                new WheelOption { Text = "叫外卖", Weight = 1, ColorHex = Palette[7] }
            ]
        });

        // 2. 谁去拿外卖
        model.Templates.Add(new WheelTemplate
        {
            Id = "delivery",
            Title = "谁去拿外卖",
            Options =
            [
                new WheelOption { Text = "我去拿", Weight = 1, ColorHex = Palette[0] },
                new WheelOption { Text = "你去拿", Weight = 1, ColorHex = Palette[2] },
                new WheelOption { Text = "他去拿", Weight = 1, ColorHex = Palette[4] },
                new WheelOption { Text = "一起去", Weight = 1, ColorHex = Palette[5] },
                new WheelOption { Text = "猜拳再定", Weight = 1, ColorHex = Palette[6] }
            ]
        });

        // 3. 做还是不做
        model.Templates.Add(new WheelTemplate
        {
            Id = "action",
            Title = "做还是不做",
            Options =
            [
                new WheelOption { Text = "立即行动", Weight = 1, ColorHex = Palette[7] },
                new WheelOption { Text = "放手一搏", Weight = 1, ColorHex = Palette[2] },
                new WheelOption { Text = "暂缓观望", Weight = 1, ColorHex = Palette[5] },
                new WheelOption { Text = "明天再说", Weight = 1, ColorHex = Palette[0] }
            ]
        });

        // 4. 周末去哪玩
        model.Templates.Add(new WheelTemplate
        {
            Id = "weekend",
            Title = "周末去哪玩",
            Options =
            [
                new WheelOption { Text = "宅家刷剧", Weight = 1, ColorHex = Palette[1] },
                new WheelOption { Text = "户外露营", Weight = 1, ColorHex = Palette[4] },
                new WheelOption { Text = "逛街看电影", Weight = 1, ColorHex = Palette[0] },
                new WheelOption { Text = "咖啡馆看书", Weight = 1, ColorHex = Palette[5] },
                new WheelOption { Text = "运动健身", Weight = 1, ColorHex = Palette[6] }
            ]
        });

        return model;
    }

    public static WheelTemplate GetTemplate(string? id)
    {
        var data = GetData();
        var tmpl = data.Templates.FirstOrDefault(t => t.Id == id);
        return tmpl ?? data.Templates.FirstOrDefault() ?? CreateDefaultData().Templates[0];
    }

    public static void SaveTemplate(WheelTemplate template)
    {
        var data = GetData();
        lock (FileLock)
        {
            var idx = data.Templates.FindIndex(t => t.Id == template.Id);
            if (idx >= 0)
            {
                data.Templates[idx] = template;
            }
            else
            {
                data.Templates.Add(template);
            }
            SaveInternal(data);
        }
        TemplatesChanged?.Invoke();
    }

    public static void DeleteTemplate(string id)
    {
        var data = GetData();
        lock (FileLock)
        {
            data.Templates.RemoveAll(t => t.Id == id);
            if (data.Templates.Count == 0)
            {
                data.Templates.AddRange(CreateDefaultData().Templates);
            }
            SaveInternal(data);
        }
        TemplatesChanged?.Invoke();
    }

    public static void RecordDecision(string templateTitle, string selectedOption)
    {
        var data = GetData();
        var rec = new WheelDecisionRecord
        {
            Timestamp = DateTime.Now,
            TemplateTitle = templateTitle,
            SelectedOption = selectedOption
        };

        lock (FileLock)
        {
            data.History.Insert(0, rec);
            if (data.History.Count > 50)
            {
                data.History.RemoveRange(50, data.History.Count - 50);
            }
            SaveInternal(data);
        }

        DecisionRecorded?.Invoke(rec);
    }

    public static void ClearHistory()
    {
        var data = GetData();
        lock (FileLock)
        {
            data.History.Clear();
            SaveInternal(data);
        }
    }

    private static void SaveInternal(WheelDataModel data)
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
