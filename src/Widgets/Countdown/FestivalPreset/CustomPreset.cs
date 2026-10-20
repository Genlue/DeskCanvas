// 自定义预设是用户自己的“事件草稿”：只保留重建编辑器所需的字段，
// 不写入 events.json，避免把事件数据和快捷模板混在一起。
using System;
using Countdown.Models;

namespace Countdown.FestivalPreset;

public sealed record CustomPreset(
    string Id,
    string Title,
    DateTime TargetDate,
    bool IsLunar = false,
    int LunarMonth = 1,
    int LunarDay = 1,
    bool IsLeapMonth = false,
    RepeatCycle Repeat = RepeatCycle.Yearly,
    CountDirection Direction = CountDirection.Auto,
    string Category = "General",
    string ColorTag = "#FF5A5F")
{
    public static CustomPreset FromItem(CountdownItem item) => new(
        Id: item.Id,
        Title: item.Title,
        TargetDate: item.TargetDate,
        IsLunar: item.IsLunar,
        LunarMonth: item.LunarMonth,
        LunarDay: item.LunarDay,
        IsLeapMonth: item.IsLeapMonth,
        Repeat: item.Repeat,
        Direction: item.Direction,
        Category: item.Category,
        ColorTag: item.ColorTag ?? "#FF5A5F");

    public CountdownItem ToCountdownItem() => new(
        Id: Id,
        Title: Title,
        TargetDate: TargetDate,
        IsLunar: IsLunar,
        LunarMonth: LunarMonth,
        LunarDay: LunarDay,
        IsLeapMonth: IsLeapMonth,
        Repeat: Repeat,
        Direction: Direction,
        Category: Category,
        ColorTag: ColorTag);
}
