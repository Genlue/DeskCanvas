using System;

namespace Habits.Models;

public record HabitRecord(
    string HabitId,
    string Date,
    double Value = 0,
    bool Completed = false,
    string? Note = null,
    DateTime LastUpdatedAt = default
);
