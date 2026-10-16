using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using DeskCanvas.Core.Models.Attributes;
using Habits.Models;
using Habits.Services;
using Habits.ViewModels;

namespace HabitsChecks;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine($"  [ok]   {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("  Habits Widget Automated Verification Suite");
        Console.WriteLine("==================================================");

        // 1. Effective Date Math (Night Owl logic)
        Console.WriteLine("\n[1] Testing Night Owl Effective Date Math...");
        var timeMidnight = new DateTime(2026, 10, 17, 1, 30, 0);
        var effective0 = HabitStatsCalculator.GetEffectiveDate(timeMidnight, dayResetHour: 0);
        Check(effective0 == new DateTime(2026, 10, 17), "ResetHour=0: 01:30 is Oct 17");

        var effective4 = HabitStatsCalculator.GetEffectiveDate(timeMidnight, dayResetHour: 4);
        Check(effective4 == new DateTime(2026, 10, 16), "ResetHour=4: 01:30 is still Oct 16 (Night Owl preserved)");

        var time4am = new DateTime(2026, 10, 17, 4, 0, 0);
        var effective4am = HabitStatsCalculator.GetEffectiveDate(time4am, dayResetHour: 4);
        Check(effective4am == new DateTime(2026, 10, 17), "ResetHour=4: 04:00 crosses into Oct 17");

        // 2. Consecutive Streaks Math
        Console.WriteLine("\n[2] Testing Streaks Math...");
        var habitDaily = new HabitItem("h_test", "Test Daily", "🎯", "#007AFF", HabitType.Boolean, Frequency: HabitFrequency.Daily);
        var today = new DateTime(2026, 10, 16);

        var records = new List<HabitRecord>
        {
            new("h_test", "2026-10-12", 1, true),
            new("h_test", "2026-10-13", 1, true),
            new("h_test", "2026-10-14", 1, true),
            new("h_test", "2026-10-15", 1, true),
            new("h_test", "2026-10-16", 1, true)
        };

        var (cStreak1, bStreak1) = HabitStatsCalculator.CalculateStreaks(habitDaily, records, 0, today);
        Check(cStreak1 == 5, $"Daily 5-day continuous streak: got {cStreak1}, expected 5");
        Check(bStreak1 == 5, $"Daily 5-day best streak: got {bStreak1}, expected 5");

        // Today not completed yet, but yesterday was -> streak is 4 (not broken)
        records.RemoveAll(r => r.Date == "2026-10-16");
        var (cStreak2, _) = HabitStatsCalculator.CalculateStreaks(habitDaily, records, 0, today);
        Check(cStreak2 == 4, $"Today not done yet, preserved yesterday streak: got {cStreak2}, expected 4");

        // Yesterday missed -> streak is 0
        records.RemoveAll(r => r.Date == "2026-10-15");
        var (cStreak3, bStreak3) = HabitStatsCalculator.CalculateStreaks(habitDaily, records, 0, today);
        Check(cStreak3 == 0, $"Yesterday missed, streak reset: got {cStreak3}, expected 0");
        Check(bStreak3 == 3, $"Historical best streak preserved: got {bStreak3}, expected 3");

        // Weekdays habit: skipping weekends does not break streak
        var habitWeekdays = new HabitItem("h_wd", "Work Habit", "💼", "#34C759", HabitType.Boolean, Frequency: HabitFrequency.Weekdays);
        // Friday Oct 9, Mon Oct 12, Tue Oct 13, Wed Oct 14, Thu Oct 15, Fri Oct 16
        var wdRecords = new List<HabitRecord>
        {
            new("h_wd", "2026-10-09", 1, true), // Friday
            new("h_wd", "2026-10-12", 1, true), // Monday
            new("h_wd", "2026-10-13", 1, true),
            new("h_wd", "2026-10-14", 1, true),
            new("h_wd", "2026-10-15", 1, true),
            new("h_wd", "2026-10-16", 1, true)
        };
        var (wdStreak, _) = HabitStatsCalculator.CalculateStreaks(habitWeekdays, wdRecords, 0, today);
        Check(wdStreak == 6, $"Weekdays habit bridges over Sat-Sun correctly: got {wdStreak}, expected 6");

        // 3. Weekly Status Mon-Sun
        Console.WriteLine("\n[3] Testing Weekly Status Generation...");
        var weekStatus = HabitStatsCalculator.GetWeeklyStatus(habitDaily, wdRecords, 0, today);
        Check(weekStatus.Count == 7, $"Weekly status has 7 days: got {weekStatus.Count}");
        Check(weekStatus[0].Label == "一" && weekStatus[6].Label == "日", "Week starts on Monday and ends on Sunday");
        Check(weekStatus.Any(d => d.IsToday && d.DateString == "2026-10-16"), "Today (Oct 16) correctly flagged");

        // 4. Heatmap Matrix
        Console.WriteLine("\n[4] Testing Heatmap Generation...");
        var testData = new HabitsDataModel(
            [habitDaily],
            [
                new("h_test", "2026-10-01", 1, true),
                new("h_test", "2026-10-02", 1, true),
                new("h_test", "2026-10-03", 1, true)
            ]
        );
        var heatmapCells = HabitStatsCalculator.GetMonthHeatmap(testData, 2026, 10, 0, today);
        Check(heatmapCells.Count % 7 == 0, $"Heatmap grid is divisible by 7: got {heatmapCells.Count}");
        var cellOct1 = heatmapCells.FirstOrDefault(c => c.DateString == "2026-10-01");
        Check(cellOct1 != null && cellOct1.Level == 4, $"Oct 1 cell completed at 100% (Level 4): got {cellOct1?.Level}");

        // 5. JSON Data Model Roundtrip
        Console.WriteLine("\n[5] Testing JSON Serialization Roundtrip...");
        var json = JsonSerializer.Serialize(testData);
        var deserialized = JsonSerializer.Deserialize<HabitsDataModel>(json);
        Check(deserialized != null && deserialized.Habits.Count == 1 && deserialized.Records.Count == 3,
            "HabitsDataModel serializes and deserializes accurately");

        // 6. Assembly Attributes
        Console.WriteLine("\n[6] Testing Widget Assembly Metadata...");
        var asm = typeof(Habits.Views.HabitsView).Assembly;
        var widgetInfo = asm.GetCustomAttribute<WidgetInfoAttribute>();
        var locale = asm.GetCustomAttribute<LocaleAttribute>();
        Check(widgetInfo != null, "Assembly carries WidgetInfoAttribute");
        Check(locale != null, "Assembly carries LocaleAttribute");
        Check(widgetInfo?.Title == "Habits_Title", $"WidgetInfo Title is {widgetInfo?.Title}");

        // 7. HabitBrushes Caching & Immutability
        Console.WriteLine("\n[7] Testing HabitBrushes Caching & Immutability...");
        var b1 = HabitBrushes.GetBrush("#007AFF");
        var b2 = HabitBrushes.GetBrush("#007AFF");
        Check(ReferenceEquals(b1, b2), "ColorCache returns identical IBrush reference for same hex string");
        var h0 = HabitBrushes.GetHeatmapBrush(0);
        var h4 = HabitBrushes.GetHeatmapBrush(4);
        Check(h0 != null && h4 != null && !ReferenceEquals(h0, h4), "Heatmap brushes return valid cached level brushes");
        Check(cellOct1?.BackgroundBrush != null, "HeatmapCell exposes pre-cached BackgroundBrush directly");

        // 8. ViewModel Differential / In-Place Updates
        Console.WriteLine("\n[8] Testing ViewModel Differential Updates...");
        var vmSettings = new HabitsModel(HabitsViewMode.Focus, "h_test");
        var vm = new HabitsViewModel(vmSettings, testData, today);
        Check(vm.DisplayHabits.Count == 1, "ViewModel initialized with 1 display habit");
        var firstItem = vm.DisplayHabits[0];
        var weeklyRef = vm.WeeklyDays;
        var heatmapRef = vm.HeatmapCells;

        // Simulate check-in toggle and update
        var updatedData = testData with
        {
            Records =
            [
                ..testData.Records,
                new HabitRecord("h_test", "2026-10-16", 1, true)
            ]
        };
        vm.UpdateData(updatedData);

        Check(ReferenceEquals(vm.DisplayHabits[0], firstItem), "DisplayHabits updated in-place (same item instance, no collection rebuild)");
        Check(firstItem.IsCompleted == true, "DisplayHabits[0].IsCompleted updated in-place");
        Check(ReferenceEquals(vm.WeeklyDays, weeklyRef), "WeeklyDays collection instance preserved (no reallocation)");
        Check(ReferenceEquals(vm.HeatmapCells, heatmapRef), "HeatmapCells collection instance preserved (no reallocation)");

        // 9. Date Key Parsing & Allocation-free formatting
        Console.WriteLine("\n[9] Testing Date Key Math & String Formatting...");
        var dtTest = new DateTime(2026, 10, 16);
        var dtStr = HabitStatsCalculator.ToDateString(dtTest);
        Check(dtStr == "2026-10-16", $"Fast ToDateString matched: {dtStr}");
        var dtKey = HabitStatsCalculator.ToDateKey(dtTest);
        var parsedKey = HabitStatsCalculator.ParseDateKey(dtStr);
        Check(dtKey == 20261016 && parsedKey == dtKey, $"DateKey math matched: {dtKey} == {parsedKey}");

        Console.WriteLine("\n==================================================");
        Console.WriteLine($"  Summary: {passed} passed, {failed} failed");
        Console.WriteLine("==================================================");

        return failed == 0 ? 0 : 1;
    }
}
