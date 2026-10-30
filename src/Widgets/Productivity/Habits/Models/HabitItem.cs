using System;

namespace Habits.Models;

public record HabitItem(
    string Id,
    string Title,
    string Icon = "🎯",
    string ColorHex = "#007AFF",
    HabitType Type = HabitType.Boolean,
    double TargetValue = 1,
    double StepValue = 1,
    string Unit = "次",
    HabitFrequency Frequency = HabitFrequency.Daily,
    DateTime CreatedAt = default
);
