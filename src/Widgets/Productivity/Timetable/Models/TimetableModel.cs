using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Timetable.Models;

/// <summary>
/// Hard limits that keep an imported schedule — which is untrusted user/AI input —
/// bounded in memory and render cost. The importer enforces them before anything
/// reaches the persisted model.
/// </summary>
public static class TimetableLimits
{
    /// <summary>Maximum number of courses kept in the model.</summary>
    public const int MaxCourses = 500;

    /// <summary>Maximum number of period rows (节次) per day.</summary>
    public const int MaxPeriods = 30;

    /// <summary>Maximum total number of course slots (entries) across all courses.</summary>
    public const int MaxSlots = 2000;

    /// <summary>Maximum number of weeks a term may declare (also the week-mask width).</summary>
    public const int MaxWeeks = 60;

    /// <summary>Import files larger than this are rejected before parsing.</summary>
    public const long MaxImportFileBytes = 2 * 1024 * 1024;

    /// <summary>Rendering guards for user-visible text.</summary>
    public const int MaxNameLength = 40;

    /// <summary>Rendering guards for teacher / room / colour text.</summary>
    public const int MaxShortTextLength = 24;

    /// <summary>Free-form course note (备注): kept one short sentence for tooltips and the review list.</summary>
    public const int MaxNoteLength = 80;
}

/// <summary>A single bell period (节次): label plus optional start/end time ("HH:mm").</summary>
public record TimetablePeriod(string? Label = null, string? Start = null, string? End = null);

/// <summary>One weekly meeting of a course: day (1=Monday … 7=Sunday), periods (1-based) and a week rule.</summary>
public record TimetableEntry(int Day = 0, List<int>? Periods = null, string? Weeks = null);

/// <summary>A course block, as read off a timetable image: identity plus one or more weekly meetings.</summary>
public record TimetableCourse(
    string? Name = null,
    string? Teacher = null,
    string? Room = null,
    string? Color = null,
    List<TimetableEntry>? Entries = null,
    string? Note = null);

/// <summary>
/// The widget's persisted model (stored inside the widget layout's Settings JSON).
/// Display flags live here too, so the importer only replaces the data part.
/// </summary>
public record TimetableModel(
    int Version = 1,
    string? Name = null,
    string? StartDate = null,
    int TotalWeeks = 0,
    int CurrentWeekOverride = 0,
    List<TimetablePeriod>? Periods = null,
    List<TimetableCourse>? Courses = null,
    bool ShowWeekend = true,
    bool ShowRoom = true,
    bool ShowTeacher = false)
{
    public const int CurrentVersion = 1;

    public int CourseCount => Courses?.Count ?? 0;

    public int SlotCount
    {
        get
        {
            if (Courses == null) return 0;
            var total = 0;
            foreach (var course in Courses) total += course.Entries?.Count ?? 0;
            return total;
        }
    }

    public bool HasData => CourseCount > 0;

    private static readonly Regex HexColorRegex =
        new("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$", RegexOptions.Compiled);

    /// <summary>
    /// Repairs a model loaded from disk (or built by the importer): clamps counts,
    /// drops unusable items and returns fresh lists, so no caller-visible aliasing
    /// survives and every consumer can assume the invariants.
    /// <para>
    /// Every drop is logged — normalization is the one place where persisted data can
    /// silently vanish, and a silent drop is indistinguishable from data loss to the user.
    /// </para>
    /// </summary>
    public TimetableModel Normalize()
    {
        var periods = new List<TimetablePeriod>();
        if (Periods != null)
        {
            foreach (var period in Periods)
            {
                if (periods.Count >= TimetableLimits.MaxPeriods)
                {
                    LogDrop($"periods beyond the limit of {TimetableLimits.MaxPeriods} ignored " +
                            $"({Periods.Count} declared)");
                    break;
                }
                periods.Add(new TimetablePeriod(
                    Clip(period.Label, TimetableLimits.MaxShortTextLength),
                    NormalizeTime(period.Start),
                    NormalizeTime(period.End)));
            }
        }

        var courses = new List<TimetableCourse>();
        var slotTotal = 0;
        if (Courses != null)
        {
            foreach (var course in Courses)
            {
                if (courses.Count >= TimetableLimits.MaxCourses)
                {
                    LogDrop($"courses beyond the limit of {TimetableLimits.MaxCourses} ignored " +
                            $"({Courses.Count} declared)");
                    break;
                }
                var name = Clip(course.Name, TimetableLimits.MaxNameLength);
                if (string.IsNullOrEmpty(name))
                {
                    LogDrop("course without a usable name dropped");
                    continue;
                }

                var entries = new List<TimetableEntry>();
                if (course.Entries != null)
                {
                    foreach (var entry in course.Entries)
                    {
                        if (slotTotal >= TimetableLimits.MaxSlots)
                        {
                            LogDrop($"《{name}》slots beyond the limit of {TimetableLimits.MaxSlots} ignored");
                            break;
                        }
                        if (entry.Day is < 1 or > 7)
                        {
                            LogDrop($"《{name}》entry with invalid day {entry.Day} dropped");
                            continue;
                        }

                        var ps = new List<int>();
                        if (entry.Periods != null)
                        {
                            foreach (var p in entry.Periods)
                            {
                                if (p is < 1 or > TimetableLimits.MaxPeriods)
                                {
                                    LogDrop($"《{name}》entry day {entry.Day}: invalid period {p} ignored");
                                    continue;
                                }
                                if (!ps.Contains(p)) ps.Add(p);
                            }
                        }
                        if (ps.Count == 0)
                        {
                            LogDrop($"《{name}》entry day {entry.Day} without usable periods dropped");
                            continue;
                        }
                        ps.Sort();

                        entries.Add(new TimetableEntry(entry.Day, ps, Clip(entry.Weeks, TimetableLimits.MaxShortTextLength)));
                        slotTotal++;
                    }
                }
                if (entries.Count == 0)
                {
                    LogDrop($"《{name}》dropped: no usable time slots remain");
                    continue;
                }

                courses.Add(new TimetableCourse(
                    name,
                    Clip(course.Teacher, TimetableLimits.MaxShortTextLength),
                    Clip(course.Room, TimetableLimits.MaxShortTextLength),
                    course.Color is { } color && HexColorRegex.IsMatch(color) ? color : null,
                    entries,
                    Clip(course.Note, TimetableLimits.MaxNoteLength)));
            }
        }

        return new TimetableModel(
            CurrentVersion,
            Clip(Name, TimetableLimits.MaxNameLength),
            NormalizeDate(StartDate),
            Math.Clamp(TotalWeeks, 0, TimetableLimits.MaxWeeks),
            Math.Clamp(CurrentWeekOverride, 0, TimetableLimits.MaxWeeks),
            periods,
            courses,
            ShowWeekend,
            ShowRoom,
            ShowTeacher);
    }

    /// <summary>Normalization drops are data-maintenance events, not noise — always leave a trace.</summary>
    private static void LogDrop(string message) =>
        Debug.WriteLine($"[TimetableModel.Normalize] {message}");

    private static string? Clip(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    /// <summary>Normalizes "H:mm", "HH:mm" or "HH:mm:ss" to "HH:mm"; returns null for anything else.</summary>
    internal static string? NormalizeTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return TimeOnly.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
            ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Keeps the date only when it parses; stores it back as ISO "yyyy-MM-dd".</summary>
    internal static string? NormalizeDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;
    }
}
