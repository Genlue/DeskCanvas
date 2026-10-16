using System.Collections.Generic;

namespace Habits.Models;

public record HabitsDataModel(
    List<HabitItem> Habits,
    List<HabitRecord> Records,
    int DayResetHour = 0
);
