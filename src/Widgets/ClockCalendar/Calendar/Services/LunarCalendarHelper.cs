using System;
using System.Globalization;

namespace Calendar.Services;

/// <summary>
/// Formats the Chinese lunisolar date shown by the 日期 widget.
/// <para>
/// Calendar and Countdown are independent widget assemblies, so this deliberately mirrors the
/// small formatter in Countdown instead of taking a plugin-to-plugin reference (which would also
/// leak Countdown's dependencies into Calendar's load context).
/// </para>
/// </summary>
public static class LunarCalendarHelper
{
    private static readonly ChineseLunisolarCalendar Calendar = new();

    private static readonly string[] MonthNames =
    [
        "", "正月", "二月", "三月", "四月", "五月", "六月",
        "七月", "八月", "九月", "十月", "冬月", "腊月"
    ];

    private static readonly string[] DayNames =
    [
        "", "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];

    /// <summary>Returns e.g. "腊月十六" for a solar date.</summary>
    public static string FormatLunarDate(DateTime solar)
    {
        var lunarYear = Calendar.GetYear(solar);
        var calendarMonth = Calendar.GetMonth(solar);
        var leapMonth = Calendar.GetLeapMonth(lunarYear);

        var isLeap = false;
        var lunarMonth = calendarMonth;
        if (leapMonth > 0)
        {
            if (calendarMonth == leapMonth)
            {
                isLeap = true;
                lunarMonth = calendarMonth - 1;
            }
            else if (calendarMonth > leapMonth)
            {
                lunarMonth = calendarMonth - 1;
            }
        }

        return FormatLunarMonthDay(lunarMonth, Calendar.GetDayOfMonth(solar), isLeap);
    }

    /// <summary>Formats an already-resolved lunar month/day pair.</summary>
    public static string FormatLunarMonthDay(int lunarMonth, int lunarDay, bool isLeap = false)
    {
        var month = lunarMonth is >= 1 and <= 12 ? MonthNames[lunarMonth] : $"{lunarMonth}月";
        if (isLeap) month = "闰" + month;

        var day = lunarDay is >= 1 and <= 30 ? DayNames[lunarDay] : $"{lunarDay}日";
        return month + day;
    }
}