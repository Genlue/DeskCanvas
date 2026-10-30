using System;
using Countdown.Locales;
using Countdown.Models;

namespace Countdown.Services;

public record CountdownComputedResult(
    DateTime EffectiveTargetDate,
    bool IsCountup,
    int Days,
    int Hours,
    int Minutes,
    int Seconds,
    double Progress,
    string MainDisplayNumber,
    string UnitLabel,
    string SubtitleLabel,
    string FormattedDate
);

public static class CountdownCalculationService
{
    private static readonly string[] CachedDayStrings = InitializeCachedDayStrings();

    private static string[] InitializeCachedDayStrings()
    {
        var arr = new string[1000];
        for (int i = 0; i < arr.Length; i++)
        {
            arr[i] = i.ToString();
        }
        return arr;
    }

    public static string FormatDayNumber(int days)
    {
        if (days >= 0 && days < CachedDayStrings.Length)
        {
            return CachedDayStrings[days];
        }
        return days.ToString();
    }

    public static DateTime ResolveEffectiveTargetDate(CountdownItem item, DateTime now)
    {
        if (item.IsLunar)
        {
            if (item.Repeat == RepeatCycle.Yearly)
            {
                return LunarCalendarHelper.GetNextLunarOccurrence(item.LunarMonth, item.LunarDay, item.IsLeapMonth, now);
            }

            int lunarYear = now.Year;
            var solar = LunarCalendarHelper.LunarToSolar(lunarYear, item.LunarMonth, item.LunarDay, item.IsLeapMonth);
            return solar ?? item.TargetDate;
        }

        if (item.Repeat == RepeatCycle.Yearly)
        {
            int year = now.Year;
            int month = item.TargetDate.Month;
            int day = Math.Min(item.TargetDate.Day, DateTime.DaysInMonth(year, month));
            var thisYearDate = new DateTime(year, month, day, item.TargetDate.Hour, item.TargetDate.Minute, item.TargetDate.Second);

            if (thisYearDate < now)
            {
                year++;
                day = Math.Min(item.TargetDate.Day, DateTime.DaysInMonth(year, month));
                return new DateTime(year, month, day, item.TargetDate.Hour, item.TargetDate.Minute, item.TargetDate.Second);
            }
            return thisYearDate;
        }

        if (item.Repeat == RepeatCycle.Monthly)
        {
            int year = now.Year;
            int month = now.Month;
            int day = Math.Min(item.TargetDate.Day, DateTime.DaysInMonth(year, month));
            var thisMonthDate = new DateTime(year, month, day, item.TargetDate.Hour, item.TargetDate.Minute, item.TargetDate.Second);

            if (thisMonthDate < now)
            {
                month++;
                if (month > 12)
                {
                    month = 1;
                    year++;
                }
                day = Math.Min(item.TargetDate.Day, DateTime.DaysInMonth(year, month));
                return new DateTime(year, month, day, item.TargetDate.Hour, item.TargetDate.Minute, item.TargetDate.Second);
            }
            return thisMonthDate;
        }

        if (item.Repeat == RepeatCycle.Weekly)
        {
            var targetDayOfWeek = item.TargetDate.DayOfWeek;
            int daysToAdd = ((int)targetDayOfWeek - (int)now.DayOfWeek + 7) % 7;
            var nextDate = now.Date.AddDays(daysToAdd).Add(item.TargetDate.TimeOfDay);
            if (nextDate < now)
            {
                nextDate = nextDate.AddDays(7);
            }
            return nextDate;
        }

        return item.TargetDate;
    }

    public static CountdownComputedResult Compute(
        CountdownItem item,
        DateTime now,
        CountdownGranularity granularity,
        CountdownComputedResult? previous = null)
    {
        DateTime effectiveTarget = ResolveEffectiveTargetDate(item, now);
        TimeSpan diff = effectiveTarget - now;

        bool isCountup = false;
        if (item.Direction == CountDirection.Countup)
        {
            isCountup = true;
        }
        else if (item.Direction == CountDirection.Countdown)
        {
            isCountup = false;
        }
        else // Auto
        {
            isCountup = diff.TotalSeconds < 0 && item.Repeat == RepeatCycle.None;
        }

        int days;
        int hours = 0;
        int minutes = 0;
        int seconds = 0;
        string mainDisplay;
        string unitLabel;
        string subtitleLabel;
        double progress = 0.0;
        bool showDetailedTime = false;

        if (isCountup)
        {
            TimeSpan elapsed = now - item.TargetDate;
            if (elapsed.TotalSeconds < 0) elapsed = TimeSpan.Zero;
            days = (int)Math.Floor(elapsed.TotalDays);
            hours = elapsed.Hours;
            minutes = elapsed.Minutes;
            seconds = elapsed.Seconds;

            if (previous != null && previous.IsCountup && previous.Days == days && previous.MainDisplayNumber != null)
            {
                mainDisplay = previous.MainDisplayNumber;
            }
            else
            {
                mainDisplay = FormatDayNumber(days);
            }

            unitLabel = Locale.Countdown_DaysPassed;

            if (previous != null && previous.IsCountup && previous.SubtitleLabel != null)
            {
                subtitleLabel = previous.SubtitleLabel;
            }
            else
            {
                subtitleLabel = string.Format(Locale.Countdown_CountupSubtitle, item.TargetDate.ToString("yyyy-MM-dd"));
            }

            progress = 1.0;
        }
        else
        {
            int calendarDays = (effectiveTarget.Date - now.Date).Days;
            days = Math.Max(0, calendarDays);
            if (diff.TotalSeconds > 0)
            {
                hours = diff.Hours;
                minutes = diff.Minutes;
                seconds = diff.Seconds;
            }

            if (granularity == CountdownGranularity.AlwaysDetailed)
            {
                showDetailedTime = true;
            }
            else if (granularity == CountdownGranularity.Auto24Hours && diff.TotalHours < 24 && diff.TotalSeconds > 0)
            {
                showDetailedTime = true;
            }

            if (diff.TotalSeconds <= 0 && calendarDays <= 0)
            {
                mainDisplay = "0";
                unitLabel = Locale.Countdown_Today;
                subtitleLabel = Locale.Countdown_EventArrived;
            }
            else if (showDetailedTime && diff.TotalHours < 24)
            {
                if (previous != null && previous.Hours == hours && previous.Minutes == minutes && previous.Seconds == seconds && previous.MainDisplayNumber != null)
                {
                    mainDisplay = previous.MainDisplayNumber;
                }
                else
                {
                    mainDisplay = $"{diff.Hours:D2}:{diff.Minutes:D2}:{diff.Seconds:D2}";
                }

                unitLabel = Locale.Countdown_HoursMinutesSeconds;

                if (previous != null && previous.EffectiveTargetDate == effectiveTarget && previous.SubtitleLabel != null && previous.SubtitleLabel.Length == 16)
                {
                    subtitleLabel = previous.SubtitleLabel;
                }
                else
                {
                    subtitleLabel = effectiveTarget.ToString("yyyy-MM-dd HH:mm");
                }
            }
            else
            {
                if (previous != null && !previous.IsCountup && previous.Days == days && previous.MainDisplayNumber != null)
                {
                    mainDisplay = previous.MainDisplayNumber;
                }
                else
                {
                    mainDisplay = FormatDayNumber(days);
                }

                unitLabel = days == 0 ? Locale.Countdown_Today : Locale.Countdown_DaysRemaining;

                if (previous != null && previous.EffectiveTargetDate == effectiveTarget && previous.SubtitleLabel != null && previous.SubtitleLabel.Length == 10)
                {
                    subtitleLabel = previous.SubtitleLabel;
                }
                else
                {
                    subtitleLabel = effectiveTarget.ToString("yyyy-MM-dd");
                }
            }

            // Progress calculation
            DateTime start = item.StartDate ?? (item.Repeat switch
            {
                RepeatCycle.Yearly => effectiveTarget.AddYears(-1),
                RepeatCycle.Monthly => effectiveTarget.AddMonths(-1),
                RepeatCycle.Weekly => effectiveTarget.AddDays(-7),
                _ => effectiveTarget.AddDays(-30)
            });

            if (effectiveTarget > start)
            {
                progress = Math.Clamp((now - start).TotalSeconds / (effectiveTarget - start).TotalSeconds, 0.0, 1.0);
                if (previous != null && Math.Abs(previous.Progress - progress) < 0.0005)
                {
                    progress = previous.Progress;
                }
            }
            else
            {
                progress = 1.0;
            }
        }

        string formattedDate;
        if (previous != null && previous.EffectiveTargetDate == effectiveTarget && previous.FormattedDate != null)
        {
            formattedDate = previous.FormattedDate;
        }
        else if (item.IsLunar)
        {
            string lunarStr = LunarCalendarHelper.FormatLunarMonthDay(item.LunarMonth, item.LunarDay, item.IsLeapMonth);
            formattedDate = $"{lunarStr} ({effectiveTarget:MM-dd})";
        }
        else
        {
            formattedDate = effectiveTarget.ToString("yyyy-MM-dd");
        }

        bool timePartsMatch = (!isCountup && !showDetailedTime) || (previous != null &&
            previous.Hours == hours &&
            previous.Minutes == minutes &&
            previous.Seconds == seconds);

        if (previous != null &&
            previous.EffectiveTargetDate == effectiveTarget &&
            previous.IsCountup == isCountup &&
            previous.Days == days &&
            timePartsMatch &&
            Math.Abs(previous.Progress - progress) < 1e-6 &&
            previous.MainDisplayNumber == mainDisplay &&
            previous.UnitLabel == unitLabel &&
            previous.SubtitleLabel == subtitleLabel &&
            previous.FormattedDate == formattedDate)
        {
            return previous;
        }

        return new CountdownComputedResult(
            effectiveTarget,
            isCountup,
            days,
            hours,
            minutes,
            seconds,
            progress,
            mainDisplay,
            unitLabel,
            subtitleLabel,
            formattedDate
        );
    }
}
