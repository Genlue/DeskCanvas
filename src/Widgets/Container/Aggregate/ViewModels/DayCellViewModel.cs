using System;

namespace FixedWidgets.ViewModels;

public record DayCellViewModel(
    int DayNumber,
    string DayText,
    bool IsCurrentMonth,
    bool IsToday,
    bool IsWeekend,
    DateTime Date);
