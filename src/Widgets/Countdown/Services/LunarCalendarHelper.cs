using System;
using System.Globalization;

namespace Countdown.Services;

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

    public static void SolarToLunar(DateTime solar, out int lunarYear, out int lunarMonth, out int lunarDay, out bool isLeap)
    {
        lunarYear = Calendar.GetYear(solar);
        int calMonth = Calendar.GetMonth(solar);
        int leapMonth = Calendar.GetLeapMonth(lunarYear);
        isLeap = false;
        lunarMonth = calMonth;

        if (leapMonth > 0)
        {
            if (calMonth == leapMonth)
            {
                isLeap = true;
                lunarMonth = calMonth - 1;
            }
            else if (calMonth > leapMonth)
            {
                lunarMonth = calMonth - 1;
            }
        }

        lunarDay = Calendar.GetDayOfMonth(solar);
    }

    public static DateTime? LunarToSolar(int lunarYear, int lunarMonth, int lunarDay, bool isLeap)
    {
        try
        {
            if (lunarYear < Calendar.MinSupportedDateTime.Year || lunarYear > Calendar.MaxSupportedDateTime.Year)
                return null;

            int leapMonth = Calendar.GetLeapMonth(lunarYear);
            int calMonth = lunarMonth;

            if (leapMonth > 0)
            {
                if (isLeap)
                {
                    if (lunarMonth + 1 != leapMonth)
                        return null; // Not a valid leap month in this year
                    calMonth = leapMonth;
                }
                else if (lunarMonth >= leapMonth)
                {
                    calMonth = lunarMonth + 1;
                }
            }
            else if (isLeap)
            {
                return null;
            }

            int daysInMonth = Calendar.GetDaysInMonth(lunarYear, calMonth);
            int validDay = Math.Min(lunarDay, daysInMonth);

            return Calendar.ToDateTime(lunarYear, calMonth, validDay, 0, 0, 0, 0);
        }
        catch
        {
            return null;
        }
    }

    public static DateTime GetNextLunarOccurrence(int lunarMonth, int lunarDay, bool isLeap, DateTime afterDate)
    {
        int currentSolarYear = afterDate.Year;
        for (int y = currentSolarYear - 1; y <= currentSolarYear + 3; y++)
        {
            var date = LunarToSolar(y, lunarMonth, lunarDay, isLeap);
            if (date.HasValue && date.Value.Date >= afterDate.Date)
            {
                return date.Value;
            }
        }

        // Fallback: next solar year
        return afterDate.AddYears(1);
    }

    public static string FormatLunarDate(DateTime solar)
    {
        SolarToLunar(solar, out _, out int m, out int d, out bool isLeap);
        return FormatLunarMonthDay(m, d, isLeap);
    }

    public static string FormatLunarMonthDay(int lunarMonth, int lunarDay, bool isLeap = false)
    {
        string mStr = (lunarMonth >= 1 && lunarMonth <= 12) ? MonthNames[lunarMonth] : $"{lunarMonth}月";
        if (isLeap) mStr = "闰" + mStr;
        string dStr = (lunarDay >= 1 && lunarDay <= 30) ? DayNames[lunarDay] : $"{lunarDay}日";
        return $"{mStr}{dStr}";
    }
}
