namespace Calendar.Models;

/// <param name="FirstDayOfWeek">The day the week (and the month grid) starts on.</param>
/// <param name="TodayColorMode">Today-marker color: "Accent" (follow the system accent)
/// or "Custom" (use the per-theme colors below).</param>
/// <param name="TodayColorLight">Custom today-marker color used in the light theme (#RRGGBB).</param>
/// <param name="TodayColorDark">Custom today-marker color used in the dark theme (#RRGGBB).</param>
/// <param name="HollowTodayNumber">Draw today's number as a hole punched out of the marker disc
/// (镂空) instead of painting it on top of the disc. Default on.</param>
public record MonthCalendarModel(
    DayOfWeek FirstDayOfWeek,
    string TodayColorMode = "Accent",
    string? TodayColorLight = null,
    string? TodayColorDark = null,
    bool HollowTodayNumber = true);
