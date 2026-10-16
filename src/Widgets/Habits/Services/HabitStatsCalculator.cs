using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Media;
using Habits.Models;

namespace Habits.Services;

public record DayCheckStatus(
    DayOfWeek DayOfWeek,
    string Label,
    DateTime Date,
    string DateString,
    bool IsToday,
    bool IsDue,
    bool IsCompleted,
    double Value,
    double TargetValue,
    IBrush? ColorBrush = null
)
{
    public bool IsTodayAndNotDone => IsToday && !IsCompleted;
    public bool IsMissedOrFuture => !IsCompleted && !IsToday;
}

public record HeatmapCell(
    DateTime Date,
    string DateString,
    int Level,           // 0: none, 1: 25%, 2: 50%, 3: 75%, 4: 100%
    double Percentage,   // 0.0 ~ 1.0
    int CompletedCount,
    int TotalDueCount,
    bool IsCurrentMonth,
    bool IsToday
)
{
    public IBrush BackgroundBrush => HabitBrushes.GetHeatmapBrush(Level);

    public string BackgroundHex => Level switch
    {
        4 => "#FF34C759",
        3 => "#A534C759",
        2 => "#7034C759",
        1 => "#4034C759",
        _ => "#12808080"
    };

    public double CellOpacity => IsCurrentMonth ? 1.0 : 0.25;
    public double BorderThicknessValue => IsToday ? 1.5 : 0;
}

public static class HabitStatsCalculator
{
    public static DateTime GetEffectiveDate(DateTime now, int dayResetHour)
    {
        if (dayResetHour > 0 && now.Hour < dayResetHour)
        {
            return now.Date.AddDays(-1);
        }
        return now.Date;
    }

    /// <summary>
    /// Fast allocation-free yyyy-MM-dd formatting without regex, parser, or culture overhead.
    /// </summary>
    public static string ToDateString(DateTime date)
    {
        return string.Create(10, date, static (span, d) =>
        {
            int y = d.Year;
            int m = d.Month;
            int day = d.Day;

            span[0] = (char)('0' + (y / 1000));
            span[1] = (char)('0' + ((y / 100) % 10));
            span[2] = (char)('0' + ((y / 10) % 10));
            span[3] = (char)('0' + (y % 10));
            span[4] = '-';
            span[5] = (char)('0' + (m / 10));
            span[6] = (char)('0' + (m % 10));
            span[7] = '-';
            span[8] = (char)('0' + (day / 10));
            span[9] = (char)('0' + (day % 10));
        });
    }

    public static int ToDateKey(DateTime dt) => dt.Year * 10000 + dt.Month * 100 + dt.Day;

    public static int ParseDateKey(string? dateStr)
    {
        if (dateStr == null || dateStr.Length < 10) return 0;
        int y = (dateStr[0] - '0') * 1000 + (dateStr[1] - '0') * 100 + (dateStr[2] - '0') * 10 + (dateStr[3] - '0');
        int m = (dateStr[5] - '0') * 10 + (dateStr[6] - '0');
        int d = (dateStr[8] - '0') * 10 + (dateStr[9] - '0');
        return y * 10000 + m * 100 + d;
    }

    public static bool IsDueOnDate(HabitItem habit, DateTime date)
    {
        return habit.Frequency switch
        {
            HabitFrequency.Daily => true,
            HabitFrequency.Weekdays => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday),
            HabitFrequency.Weekends => date.DayOfWeek is (DayOfWeek.Saturday or DayOfWeek.Sunday),
            _ => true
        };
    }

    public static (int CurrentStreak, int BestStreak) CalculateStreaks(
        HabitItem habit,
        IEnumerable<HabitRecord> records,
        int dayResetHour,
        DateTime? now = null)
    {
        var effectiveToday = GetEffectiveDate(now ?? DateTime.Now, dayResetHour);

        // Single pass over records: track completed dates as integer keys (yyyyMMdd)
        // and find earliest record date. Avoids GroupBy/ToDictionary/Select/Min allocations.
        var completedDateKeys = new HashSet<int>();
        DateTime minRecordDate = DateTime.MaxValue;

        foreach (var r in records)
        {
            if (r.HabitId == habit.Id)
            {
                int key = ParseDateKey(r.Date);
                if (key != 0)
                {
                    if (r.Completed)
                    {
                        completedDateKeys.Add(key);
                    }
                    else
                    {
                        completedDateKeys.Remove(key);
                    }

                    if (DateTime.TryParse(r.Date, CultureInfo.InvariantCulture, out var dt) && dt < minRecordDate)
                    {
                        minRecordDate = dt;
                    }
                }
            }
        }

        // 1. Calculate Current Streak
        int currentStreak = 0;
        DateTime checkDate = effectiveToday;

        bool todayDue = IsDueOnDate(habit, checkDate);
        bool todayDone = completedDateKeys.Contains(ToDateKey(checkDate));

        if (todayDue && todayDone)
        {
            currentStreak = 1;
            checkDate = checkDate.AddDays(-1);
        }
        else if (!todayDue)
        {
            // Today not due, search previous due day
            checkDate = checkDate.AddDays(-1);
        }
        else
        {
            // Today is due but not done yet — streak is not broken yet if yesterday was completed!
            checkDate = checkDate.AddDays(-1);
        }

        // Trace backwards
        while (true)
        {
            if (IsDueOnDate(habit, checkDate))
            {
                if (completedDateKeys.Contains(ToDateKey(checkDate)))
                {
                    currentStreak++;
                    checkDate = checkDate.AddDays(-1);
                }
                else
                {
                    break;
                }
            }
            else
            {
                // Day not due, doesn't break streak
                checkDate = checkDate.AddDays(-1);
            }

            // Cap at 10 years to prevent infinite loop
            if ((effectiveToday - checkDate).TotalDays > 3650) break;
        }

        // 2. Calculate Best Streak across all history
        int bestStreak = currentStreak;
        int runningStreak = 0;

        // Find earliest record or creation date
        DateTime startDate = habit.CreatedAt != default ? habit.CreatedAt.Date : effectiveToday.AddMonths(-6);
        if (minRecordDate != DateTime.MaxValue && minRecordDate < startDate)
        {
            startDate = minRecordDate;
        }

        for (var date = startDate; date <= effectiveToday; date = date.AddDays(1))
        {
            if (!IsDueOnDate(habit, date)) continue;

            if (completedDateKeys.Contains(ToDateKey(date)))
            {
                runningStreak++;
                if (runningStreak > bestStreak) bestStreak = runningStreak;
            }
            else
            {
                runningStreak = 0;
            }
        }

        return (currentStreak, bestStreak);
    }

    public static List<DayCheckStatus> GetWeeklyStatus(
        HabitItem habit,
        IEnumerable<HabitRecord> records,
        int dayResetHour,
        DateTime? now = null)
    {
        var effectiveToday = GetEffectiveDate(now ?? DateTime.Now, dayResetHour);

        // Single pass dictionary avoiding GroupBy overhead
        var recordDict = new Dictionary<string, HabitRecord>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.HabitId == habit.Id)
            {
                recordDict[r.Date] = r;
            }
        }

        // Find Monday of current week
        int diff = (7 + (effectiveToday.DayOfWeek - DayOfWeek.Monday)) % 7;
        var monday = effectiveToday.AddDays(-diff);

        var habitBrush = HabitBrushes.GetBrush(habit.ColorHex);
        var list = new List<DayCheckStatus>(7);
        for (int i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            var dateStr = ToDateString(date);
            bool isToday = date == effectiveToday;
            bool isDue = IsDueOnDate(habit, date);

            bool completed = false;
            double value = 0;
            if (recordDict.TryGetValue(dateStr, out var rec))
            {
                completed = rec.Completed;
                value = rec.Value;
            }

            string label = i switch
            {
                0 => "一",
                1 => "二",
                2 => "三",
                3 => "四",
                4 => "五",
                5 => "六",
                _ => "日"
            };

            list.Add(new DayCheckStatus(
                date.DayOfWeek,
                label,
                date,
                dateStr,
                isToday,
                isDue,
                completed,
                value,
                habit.TargetValue,
                habitBrush
            ));
        }

        return list;
    }

    public static List<HeatmapCell> GetMonthHeatmap(
        HabitsDataModel data,
        int year,
        int month,
        int dayResetHour,
        DateTime? now = null,
        string? singleHabitId = null)
    {
        var effectiveToday = GetEffectiveDate(now ?? DateTime.Now, dayResetHour);
        var firstOfMonth = new DateTime(year, month, 1);
        int daysInMonth = DateTime.DaysInMonth(year, month);
        var lastOfMonth = new DateTime(year, month, daysInMonth);

        // Pad to start from Monday
        int startOffset = (7 + (firstOfMonth.DayOfWeek - DayOfWeek.Monday)) % 7;
        var startDate = firstOfMonth.AddDays(-startOffset);

        // Pad to end on Sunday
        int endOffset = (7 - (int)lastOfMonth.DayOfWeek) % 7;
        if (lastOfMonth.DayOfWeek == DayOfWeek.Sunday) endOffset = 0;
        else endOffset = 7 - (int)lastOfMonth.DayOfWeek;
        var endDate = lastOfMonth.AddDays(endOffset);

        var habits = data.Habits;
        if (singleHabitId != null)
        {
            var filtered = new List<HabitItem>();
            for (int i = 0; i < habits.Count; i++)
            {
                if (habits[i].Id == singleHabitId)
                {
                    filtered.Add(habits[i]);
                    break;
                }
            }
            habits = filtered;
        }

        // Fast compound lookup map: (Date, HabitId) -> Completed
        // Eliminates nested GroupBy lists and LastOrDefault LINQ inside the date loop.
        var completedMap = new Dictionary<(string Date, string HabitId), bool>();
        foreach (var r in data.Records)
        {
            if (singleHabitId == null || r.HabitId == singleHabitId)
            {
                completedMap[(r.Date, r.HabitId)] = r.Completed;
            }
        }

        int totalDays = (int)(endDate - startDate).TotalDays + 1;
        var cells = new List<HeatmapCell>(totalDays);

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            var dateStr = ToDateString(date);
            bool isCurrentMonth = date.Month == month;
            bool isToday = date == effectiveToday;

            int totalDue = 0;
            int completedCount = 0;

            for (int h = 0; h < habits.Count; h++)
            {
                var habit = habits[h];
                if (!IsDueOnDate(habit, date)) continue;

                totalDue++;
                if (completedMap.TryGetValue((dateStr, habit.Id), out var done) && done)
                {
                    completedCount++;
                }
            }

            double pct = totalDue > 0 ? (double)completedCount / totalDue : 0;
            int level = 0;
            if (completedCount > 0)
            {
                if (pct >= 1.0) level = 4;
                else if (pct >= 0.75) level = 3;
                else if (pct >= 0.5) level = 2;
                else level = 1;
            }

            cells.Add(new HeatmapCell(
                date,
                dateStr,
                level,
                pct,
                completedCount,
                totalDue,
                isCurrentMonth,
                isToday
            ));
        }

        return cells;
    }
}
