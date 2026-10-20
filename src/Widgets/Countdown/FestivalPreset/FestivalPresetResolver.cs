// 解析器是纯函数：同一个预设 + 同一个“今天”永远得到同一天，方便单测，
// 也避免把 DateTime.Now 偷偷带进目录数据里。
using System;
using Countdown.Services;

namespace Countdown.FestivalPreset;

public static class FestivalPresetResolver
{
    /// <summary>
    /// 返回 <paramref name="today"/> 当天或之后最近的一次节日（含今天）。
    /// </summary>
    public static DateTime ResolveNextOccurrence(FestivalPreset preset, DateTime today)
    {
        var date = today.Date;

        return preset.Kind switch
        {
            FestivalDateKind.SolarFixed => NextSolarFixed(preset.Month, preset.Day, date),
            FestivalDateKind.LunarFixed => LunarCalendarHelper.GetNextLunarOccurrence(
                preset.LunarMonth, preset.LunarDay, isLeap: false, date),
            FestivalDateKind.LunarNewYearsEve => NextLunarNewYearsEve(date),
            FestivalDateKind.SolarNthWeekday => NextNthWeekday(
                preset.Month, preset.Ordinal, preset.Weekday, date),
            FestivalDateKind.SolarTermApprox => NextQingming(date),
            _ => date
        };
    }

    private static DateTime NextSolarFixed(int month, int day, DateTime today)
    {
        var date = new DateTime(today.Year, month, Math.Min(day, DateTime.DaysInMonth(today.Year, month)));
        if (date < today)
        {
            int year = today.Year + 1;
            date = new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)));
        }

        return date;
    }

    /// <summary>
    /// 除夕是农历年的最后一天，不能写死成“腊月三十”：腊月只有 29 天的年份，
    /// 除夕就是腊月廿九。用次年正月初一往前推一天，两种情况都自然成立。
    /// </summary>
    private static DateTime NextLunarNewYearsEve(DateTime today)
    {
        // 除夕总落在公历 1/2 月，往前一年开始找就足够覆盖“今年已经过完”的情况。
        for (int lunarYear = today.Year - 1; lunarYear <= today.Year + 2; lunarYear++)
        {
            var nextNewYear = LunarCalendarHelper.LunarToSolar(lunarYear + 1, 1, 1, isLeap: false);
            if (nextNewYear is null) continue;

            var eve = nextNewYear.Value.Date.AddDays(-1);
            if (eve >= today) return eve;
        }

        // 超出农历支持范围时的兜底；正常年份不会走到这里。
        return today;
    }

    private static DateTime NextNthWeekday(int month, int ordinal, DayOfWeek weekday, DateTime today)
    {
        var date = NthWeekdayOfMonth(today.Year, month, ordinal, weekday);
        if (date < today)
        {
            date = NthWeekdayOfMonth(today.Year + 1, month, ordinal, weekday);
        }

        return date;
    }

    private static DateTime NthWeekdayOfMonth(int year, int month, int ordinal, DayOfWeek weekday)
    {
        var first = new DateTime(year, month, 1);
        int offset = ((int)weekday - (int)first.DayOfWeek + 7) % 7;
        return first.AddDays(offset + (ordinal - 1) * 7);
    }

    /// <summary>
    /// 清明按公历 4 月 4/5 日近似，而不是天文节气计算：闰年及闰年次年取 4 日，
    /// 其余年份取 5 日。该经验规则在 2008–2099 年与日历一致，更早/更晚的年份
    /// 可能差一天，这是有意为之的近似。
    /// </summary>
    private static DateTime NextQingming(DateTime today)
    {
        var date = new DateTime(today.Year, 4, QingmingDay(today.Year));
        if (date < today)
        {
            date = new DateTime(today.Year + 1, 4, QingmingDay(today.Year + 1));
        }

        return date;
    }

    private static int QingmingDay(int year) => year % 4 is 2 or 3 ? 5 : 4;
}
