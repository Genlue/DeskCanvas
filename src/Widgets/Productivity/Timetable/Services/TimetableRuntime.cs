using System.Globalization;
using Timetable.Models;

namespace Timetable.Services;

/// <summary>
/// Read-only index over a model's courses, built once per data change: one slot list per
/// weekday, sorted by first period. Slot counts are bounded by the import limits
/// (≤ 2000), so the whole runtime is a small, allocation-free-at-render structure.
/// </summary>
public sealed class TimetableRuntime
{
    public sealed record Slot(TimetableCourse Course, TimetableEntry Entry, WeekSet Weeks, int FirstPeriod, int LastPeriod);

    public static readonly TimetableRuntime Empty = Build(new TimetableModel());

    /// <summary>Slots per weekday; index 1..7 (Monday..Sunday), index 0 unused.</summary>
    public List<Slot>[] ByDay { get; }

    /// <summary>Highest period index used by any entry (0 when the model has no data).</summary>
    public int MaxPeriod { get; }

    public int EntryCount { get; }

    public bool HasAny => EntryCount > 0;

    private TimetableRuntime(List<Slot>[] byDay, int maxPeriod, int entryCount)
    {
        ByDay = byDay;
        MaxPeriod = maxPeriod;
        EntryCount = entryCount;
    }

    public static TimetableRuntime Build(TimetableModel model)
    {
        var byDay = new List<Slot>[8];
        for (var day = 1; day <= 7; day++) byDay[day] = [];

        var maxPeriod = 0;
        var entryCount = 0;

        if (model.Courses != null)
        {
            foreach (var course in model.Courses)
            {
                if (course.Entries == null) continue;
                foreach (var entry in course.Entries)
                {
                    if (entry.Day is < 1 or > 7 || entry.Periods is not { Count: > 0 }) continue;

                    var first = entry.Periods[0];
                    var last = entry.Periods[^1];
                    var weeks = WeekExpression.TryParse(entry.Weeks, out var set, out _) ? set : WeekSet.AllWeeks;

                    byDay[entry.Day].Add(new Slot(course, entry, weeks, first, last));
                    if (last > maxPeriod) maxPeriod = last;
                    entryCount++;
                }
            }
        }

        for (var day = 1; day <= 7; day++)
        {
            byDay[day].Sort(static (a, b) =>
            {
                var byFirst = a.FirstPeriod.CompareTo(b.FirstPeriod);
                return byFirst != 0 ? byFirst : a.LastPeriod.CompareTo(b.LastPeriod);
            });
        }

        return new TimetableRuntime(byDay, maxPeriod, entryCount);
    }

    /// <summary>A slot is active in the given week when the week is unknown (0) or the rule matches.</summary>
    public static bool IsActiveThisWeek(Slot slot, int week) => week < 1 || slot.Weeks.IsActive(week);
}

/// <summary>Time-of-day helpers: term week maths and bell-time parsing (single source for both views).</summary>
public static class TimetableClock
{
    /// <summary>The current 1-based week, or 0 when it cannot be determined.</summary>
    public static int ComputeWeek(TimetableModel model, DateTime now)
    {
        if (model.CurrentWeekOverride > 0) return model.CurrentWeekOverride;

        if (model.StartDate is { } startText &&
            DateTime.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
        {
            var days = (now.Date - start.Date).Days;
            if (days < 0) return 0;
            return days / 7 + 1;
        }

        return 0;
    }

    /// <summary>Bell bounds of a period ("HH:mm"), when the model declares them.</summary>
    public static (TimeOnly? Start, TimeOnly? End) PeriodBounds(TimetableModel model, int period)
    {
        if (model.Periods is not { } periods || period < 1 || period > periods.Count) return (null, null);
        var p = periods[period - 1];
        return (Parse(p.Start), Parse(p.End));
    }

    /// <summary>True when every declared period (up to the used maximum) carries usable bell times.</summary>
    public static bool HasBellTimes(TimetableModel model, int maxPeriod)
    {
        if (model.Periods is not { Count: > 0 } periods) return false;
        if (maxPeriod < 1) return false;
        var usable = 0;
        foreach (var period in periods)
        {
            if (Parse(period.Start) != null && Parse(period.End) != null) usable++;
        }
        return usable > 0;
    }

    public static TimeOnly? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;
    }

    /// <summary>Display label for a period: the model's own label, else the localized "第 N 节" default.</summary>
    public static string PeriodLabel(TimetableModel model, int period)
    {
        if (model.Periods is { } periods && period >= 1 && period <= periods.Count &&
            periods[period - 1].Label is { Length: > 0 } label)
            return label;
        return Locales.Locale.Timetable_PeriodFull.Replace("{0}", period.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Short label for compact grids (a custom label is kept only when it is short).</summary>
    public static string PeriodShortLabel(TimetableModel model, int period)
    {
        if (model.Periods is { } periods && period >= 1 && period <= periods.Count &&
            periods[period - 1].Label is { Length: > 0 and <= 4 } label)
            return label;
        return Locales.Locale.Timetable_Period.Replace("{0}", period.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Short weekday header ("一"…"日" or the culture's shortest day name).</summary>
    public static string DayShortName(int day)
    {
        var format = CultureInfo.CurrentUICulture.DateTimeFormat;
        return format.GetShortestDayName((DayOfWeek)(day % 7));
    }

    public static string DayName(int day) => CultureInfo.CurrentUICulture.DateTimeFormat.GetDayName((DayOfWeek)(day % 7));

    /// <summary>
    /// The culture's <b>abbreviated</b> weekday name ("周一" / "Mon") — the form the relative
    /// labels compose with. <see cref="DayShortName"/> is the shortest one ("一" / "Mo"), which is
    /// right for a grid header but reads as a fragment inside "下{0}".
    /// </summary>
    public static string DayShortFullName(int day) =>
        CultureInfo.CurrentUICulture.DateTimeFormat.GetAbbreviatedDayName((DayOfWeek)(day % 7));

    /// <summary>
    /// The week a lookahead day belongs to, given the week of the day the scan starts from.
    /// The timetable week runs Monday→Sunday, so a target day with a <b>lower</b> index than
    /// today lies in the next calendar week: on Saturday the coming Monday is next week's
    /// Monday. Evaluating it with the current week instead (the old behaviour) let 单双周
    /// courses from the wrong week through, so the header said "下周一" while the list showed
    /// this week's Monday. An unknown week (0) has no arithmetic to do and stays 0, which keeps
    /// <see cref="TimetableRuntime.IsActiveThisWeek"/> unfiltered.
    /// </summary>
    public static int WeekOfDay(int currentWeek, int currentDay, int targetDay) =>
        currentWeek < 1 ? currentWeek : currentWeek + (targetDay < currentDay ? 1 : 0);

    /// <summary>
    /// Relative label for a lookahead day: 今天 / 明天 for the two immediate days, the
    /// next-week form across the Sunday→Monday boundary, and — because the widget headers are
    /// width-constrained and "本周" adds nothing mid-week — the plain weekday name for the rest
    /// of the current week. Pass <paramref name="qualifySameWeek"/> to get the explicit 本周X
    /// form instead (a caller that shows the label without the surrounding card needs it).
    /// <para>
    /// The qualified forms compose with the <b>short</b> day name and a bare prefix in the
    /// resource ("下{0}" + "周一"), so the result is the colloquial "下周一" rather than
    /// "下周星期一" — the full name already carries its own 周/星期 word.
    /// </para>
    /// </summary>
    public static string RelativeDayLabel(int currentDay, int targetDay, bool qualifySameWeek = false)
    {
        if (targetDay == currentDay) return Locales.Locale.Timetable_Today;

        // Tomorrow wins over the week arithmetic: Sunday → Monday is the next calendar week but
        // still reads as "明天", which is what the widget has always shown for offset 1.
        var offset = ((targetDay - currentDay) % 7 + 7) % 7;
        if (offset == 1) return Locales.Locale.Timetable_Tomorrow;

        if (targetDay < currentDay)
            return Locales.Locale.Timetable_NextWeekDay.Replace("{0}", DayShortFullName(targetDay));
        return qualifySameWeek
            ? Locales.Locale.Timetable_ThisWeekDay.Replace("{0}", DayShortFullName(targetDay))
            : DayName(targetDay);
    }
}
