namespace Timetable.Models;

/// <summary>
/// Shape of the imported JSON file (the format documented in the built-in guide and
/// produced by the AI prompt). Mutable DTOs with case-insensitive matching, so a small
/// deviation from the documented casing still imports.
/// </summary>
public sealed class TimetableDocument
{
    public int Version { get; set; } = 1;
    public string? Name { get; set; }
    public string? StartDate { get; set; }
    public int TotalWeeks { get; set; }
    public List<DocumentPeriod>? Periods { get; set; }
    public List<DocumentCourse>? Courses { get; set; }
}

public sealed class DocumentPeriod
{
    public string? Label { get; set; }
    public string? Start { get; set; }
    public string? End { get; set; }
}

public sealed class DocumentCourse
{
    public string? Name { get; set; }
    public string? Teacher { get; set; }
    public string? Room { get; set; }
    public string? Color { get; set; }

    /// <summary>Free-form note (备注): anything written on the course block beyond name/teacher/room.</summary>
    public string? Note { get; set; }

    public List<DocumentEntry>? Entries { get; set; }
}

public sealed class DocumentEntry
{
    public int Day { get; set; }
    public List<int>? Periods { get; set; }
    public string? Weeks { get; set; }
}
