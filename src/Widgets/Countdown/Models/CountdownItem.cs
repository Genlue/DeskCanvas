using System;

namespace Countdown.Models;

public record CountdownItem(
    string Id,
    string Title,
    DateTime TargetDate,
    DateTime? StartDate = null,
    bool IsLunar = false,
    int LunarMonth = 1,
    int LunarDay = 1,
    bool IsLeapMonth = false,
    RepeatCycle Repeat = RepeatCycle.None,
    CountDirection Direction = CountDirection.Auto,
    string Category = "General",
    string? ColorTag = null,
    bool IsPinned = false
);
