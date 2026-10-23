using System;

namespace Calendar.Models;

/// <summary>
/// A single schedule event item.
/// </summary>
public record ScheduleEventModel(
    string Id,
    string Title,
    DateTime StartTime,
    DateTime EndTime,
    bool IsAllDay = false,
    string Color = "#007AFF",
    string? Category = "工作",
    string? Location = null,
    string? Notes = null,
    bool IsCompleted = false
);
