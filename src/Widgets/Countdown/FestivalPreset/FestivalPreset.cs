// 节日预设只保存“哪一天”的规则，不保存某一年的公历日期：农历、浮动节日和除夕
// 每年都会变，把 2026 年的日期写死，到了 2027 年就会指错日子。
using System;
using Countdown.Locales;
using Countdown.Models;

namespace Countdown.FestivalPreset;

public enum FestivalDateKind
{
    SolarFixed = 0,
    LunarFixed = 1,
    LunarNewYearsEve = 2,
    SolarNthWeekday = 3,
    SolarTermApprox = 4
}

public sealed record FestivalPreset(
    string Id,
    string TitleKey,
    FestivalDateKind Kind,
    string Category,
    string ColorTag,
    int Month = 0,
    int Day = 0,
    int Ordinal = 0,
    DayOfWeek Weekday = DayOfWeek.Sunday,
    int LunarMonth = 0,
    int LunarDay = 0)
{
    public bool IsLunar => Kind is FestivalDateKind.LunarFixed or FestivalDateKind.LunarNewYearsEve;

    /// <summary>
    /// 节日全部按年重复；解析器负责把“今年已过”顺延到明年。
    /// <para>
    /// 例外：浮动节日（某月第 N 个星期 X）无法用现有的「按月/日重复」表达——存成
    /// Yearly 后，明年的同一个月/日会偏离真正的节日（最多差 6 天）。这类节日按
    /// 单次事件保存，用户下次点预设会拿到正确的下一个日期。
    /// </para>
    /// </summary>
    public RepeatCycle Repeat => Kind == FestivalDateKind.SolarNthWeekday
        ? RepeatCycle.None
        : RepeatCycle.Yearly;

    /// <summary>
    /// 标题延迟到使用时才查资源，语言切换后无需重建目录。
    /// </summary>
    public string Title => Locale.ResourceManager.GetString(TitleKey, Locale.Culture) ?? TitleKey;
}
