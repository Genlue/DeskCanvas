namespace Habits.Models;

public record HabitsModel(
    HabitsViewMode ViewMode = HabitsViewMode.List,
    string? FocusedHabitId = null,
    bool ShowStreak = true,
    bool ShowTargetProgress = true
);
