using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Habits.Models;
using Habits.Services;

namespace Habits.ViewModels;

public class HabitDisplayItem : INotifyPropertyChanged
{
    private HabitItem habit;
    private double todayValue;
    private bool isCompleted;
    private int currentStreak;
    private int bestStreak;

    public HabitDisplayItem(HabitItem habit, double todayValue, bool isCompleted, int currentStreak, int bestStreak)
    {
        this.habit = habit;
        this.todayValue = todayValue;
        this.isCompleted = isCompleted;
        this.currentStreak = currentStreak;
        this.bestStreak = bestStreak;
        UpdateBrush();
    }

    public HabitItem Habit
    {
        get => habit;
        set
        {
            if (habit != value)
            {
                var oldColor = habit.ColorHex;
                habit = value;
                if (!string.Equals(oldColor, habit.ColorHex, StringComparison.OrdinalIgnoreCase))
                {
                    UpdateBrush();
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Icon));
                OnPropertyChanged(nameof(IsNumeric));
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressPercentage));
            }
        }
    }

    public string Id => habit.Id;
    public string Title => habit.Title;
    public string Icon => habit.Icon;
    public bool IsNumeric => habit.Type == HabitType.Numeric;
    public double TargetValue => habit.TargetValue;
    public string Unit => habit.Unit;

    public IBrush ColorBrush { get; private set; } = HabitBrushes.DodgerBlue;

    private void UpdateBrush()
    {
        var newBrush = HabitBrushes.GetBrush(habit.ColorHex);
        if (!ReferenceEquals(ColorBrush, newBrush))
        {
            ColorBrush = newBrush;
            OnPropertyChanged(nameof(ColorBrush));
        }
    }

    public double TodayValue
    {
        get => todayValue;
        set
        {
            if (Math.Abs(todayValue - value) > 0.001)
            {
                todayValue = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(CompactSubtitle));
            }
        }
    }

    public bool IsCompleted
    {
        get => isCompleted;
        set
        {
            if (isCompleted != value)
            {
                isCompleted = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(ProgressPercentage));
                OnPropertyChanged(nameof(StatusOpacity));
                OnPropertyChanged(nameof(CompactSubtitle));
            }
        }
    }

    public int CurrentStreak
    {
        get => currentStreak;
        set
        {
            if (currentStreak != value)
            {
                currentStreak = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StreakText));
                OnPropertyChanged(nameof(CompactSubtitle));
            }
        }
    }

    public int BestStreak
    {
        get => bestStreak;
        set
        {
            if (bestStreak != value)
            {
                bestStreak = value;
                OnPropertyChanged();
            }
        }
    }

    public string StreakText => $"连击 {CurrentStreak}";

    public string CompactSubtitle => IsNumeric
        ? $"{TodayValue:0.#}/{TargetValue:0.#}{Unit}" + (CurrentStreak > 0 ? $" · 连打{CurrentStreak}天" : "")
        : (CurrentStreak > 0 ? $"连续 {CurrentStreak} 天" : (IsCompleted ? "今日已完成" : "今日未打卡"));

    public double ProgressPercentage
    {
        get
        {
            if (!IsNumeric) return IsCompleted ? 1.0 : 0.0;
            if (TargetValue <= 0) return 0;
            return Math.Min(1.0, TodayValue / TargetValue);
        }
    }

    public string ProgressText
    {
        get
        {
            if (IsNumeric)
            {
                return $"{TodayValue:0.#} / {TargetValue:0.#} {Unit}";
            }
            return IsCompleted ? "已打卡" : "未打卡";
        }
    }

    public string StepValueText => $"+{habit.StepValue:0.#}";
    public string NumericProgressShort => $"{TodayValue:0.#}/{TargetValue:0.#}{Unit}";

    public double StatusOpacity => IsCompleted ? 0.6 : 1.0;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class HabitsViewModel : INotifyPropertyChanged
{
    private HabitsModel widgetSettings;
    private HabitsDataModel dataModel;
    private readonly DateTime? testNow;

    public HabitsModel WidgetSettings
    {
        get => widgetSettings;
        set
        {
            widgetSettings = value;
            OnPropertyChanged();
            RefreshDisplay();
        }
    }

    public HabitsDataModel DataModel
    {
        get => dataModel;
        set
        {
            dataModel = value;
            OnPropertyChanged();
            RefreshDisplay();
        }
    }

    public ObservableCollection<HabitDisplayItem> DisplayHabits { get; } = [];

    private HabitDisplayItem? focusedHabit;
    public HabitDisplayItem? FocusedHabit
    {
        get => focusedHabit;
        private set
        {
            if (!ReferenceEquals(focusedHabit, value))
            {
                focusedHabit = value;
                OnPropertyChanged();
            }
        }
    }

    public bool CanCycleHabits => DisplayHabits.Count > 1;

    public void CycleFocusedHabit(bool next)
    {
        if (DisplayHabits.Count <= 1) return;
        int curIdx = DisplayHabits.IndexOf(FocusedHabit!);
        if (curIdx < 0) curIdx = 0;
        int nextIdx = next
            ? (curIdx + 1) % DisplayHabits.Count
            : (curIdx - 1 + DisplayHabits.Count) % DisplayHabits.Count;

        var newFocused = DisplayHabits[nextIdx];
        widgetSettings = widgetSettings with { FocusedHabitId = newFocused.Id };
        FocusedHabit = newFocused;

        var now = testNow ?? DateTime.Now;
        var weekList = HabitStatsCalculator.GetWeeklyStatus(newFocused.Habit, dataModel.Records, dataModel.DayResetHour, now);
        WeeklyDays.Clear();
        for (int i = 0; i < weekList.Count; i++)
        {
            WeeklyDays.Add(weekList[i]);
        }
    }

    public ObservableCollection<DayCheckStatus> WeeklyDays { get; } = [];

    public ObservableCollection<HeatmapCell> HeatmapCells { get; } = [];

    public string HeatmapMonthTitle { get; private set; } = string.Empty;
    public int TodayCompletedCount { get; private set; }
    public int TodayDueCount { get; private set; }
    public double TodayRate { get; private set; }
    public string TodaySummaryText { get; private set; } = string.Empty;

    public HabitsViewModel(HabitsModel widgetSettings, HabitsDataModel dataModel, DateTime? testNow = null)
    {
        this.widgetSettings = widgetSettings;
        this.dataModel = dataModel;
        this.testNow = testNow;
        RefreshDisplay();
    }

    public void UpdateData(HabitsDataModel newData)
    {
        dataModel = newData;
        RefreshDisplay();
    }

    public void UpdateSettings(HabitsModel newSettings)
    {
        widgetSettings = newSettings;
        RefreshDisplay();
    }

    public void RefreshDisplay()
    {
        var now = testNow ?? DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, dataModel.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        // Fast dictionary for today's records without GroupBy
        var todayRecords = new Dictionary<string, HabitRecord>(StringComparer.Ordinal);
        foreach (var r in dataModel.Records)
        {
            if (r.Date == todayStr)
            {
                todayRecords[r.HabitId] = r;
            }
        }

        // Differential update for DisplayHabits: match existing items to preserve visual tree containers
        var existingMap = new Dictionary<string, HabitDisplayItem>(DisplayHabits.Count, StringComparer.Ordinal);
        foreach (var item in DisplayHabits)
        {
            existingMap[item.Id] = item;
        }

        int completedCount = 0;
        int dueCount = 0;
        var targetHabits = dataModel.Habits;

        for (int i = 0; i < targetHabits.Count; i++)
        {
            var habit = targetHabits[i];
            bool isDue = HabitStatsCalculator.IsDueOnDate(habit, today);
            if (isDue) dueCount++;

            double val = 0;
            bool done = false;
            if (todayRecords.TryGetValue(habit.Id, out var rec))
            {
                val = rec.Value;
                done = rec.Completed;
            }

            if (isDue && done) completedCount++;

            var (currentStreak, bestStreak) = HabitStatsCalculator.CalculateStreaks(habit, dataModel.Records, dataModel.DayResetHour, now);

            if (existingMap.TryGetValue(habit.Id, out var existingItem))
            {
                // In-place update (only notifies when fields actually change)
                existingItem.Habit = habit;
                existingItem.TodayValue = val;
                existingItem.IsCompleted = done;
                existingItem.CurrentStreak = currentStreak;
                existingItem.BestStreak = bestStreak;

                int currentIndex = DisplayHabits.IndexOf(existingItem);
                if (currentIndex != i)
                {
                    DisplayHabits.Move(currentIndex, i);
                }
                existingMap.Remove(habit.Id);
            }
            else
            {
                var newItem = new HabitDisplayItem(habit, val, done, currentStreak, bestStreak);
                DisplayHabits.Insert(i, newItem);
            }
        }

        // Remove habits no longer present
        foreach (var removed in existingMap.Values)
        {
            DisplayHabits.Remove(removed);
        }

        OnPropertyChanged(nameof(CanCycleHabits));

        // Summary counters
        if (TodayCompletedCount != completedCount)
        {
            TodayCompletedCount = completedCount;
            OnPropertyChanged(nameof(TodayCompletedCount));
        }

        if (TodayDueCount != dueCount)
        {
            TodayDueCount = dueCount;
            OnPropertyChanged(nameof(TodayDueCount));
        }

        double newRate = dueCount > 0 ? (double)completedCount / dueCount : 0;
        if (Math.Abs(TodayRate - newRate) > 0.0001)
        {
            TodayRate = newRate;
            OnPropertyChanged(nameof(TodayRate));
        }

        string newSummary = dueCount > 0 ? $"{completedCount}/{dueCount}" : "0/0";
        if (TodaySummaryText != newSummary)
        {
            TodaySummaryText = newSummary;
            OnPropertyChanged(nameof(TodaySummaryText));
        }

        // Focus habit
        var targetFocusId = widgetSettings.FocusedHabitId;
        var targetItem = DisplayHabits.FirstOrDefault(h => h.Id == targetFocusId) ?? DisplayHabits.FirstOrDefault();
        FocusedHabit = targetItem;

        // Differential update WeeklyDays only when needed
        if (targetItem != null && (widgetSettings.ViewMode == HabitsViewMode.Focus || WeeklyDays.Count > 0))
        {
            var weekList = HabitStatsCalculator.GetWeeklyStatus(targetItem.Habit, dataModel.Records, dataModel.DayResetHour, now);
            if (WeeklyDays.Count == 7)
            {
                for (int i = 0; i < 7; i++)
                {
                    if (!Equals(WeeklyDays[i], weekList[i]))
                    {
                        WeeklyDays[i] = weekList[i];
                    }
                }
            }
            else
            {
                WeeklyDays.Clear();
                for (int i = 0; i < weekList.Count; i++)
                {
                    WeeklyDays.Add(weekList[i]);
                }
            }
        }
        else if (WeeklyDays.Count > 0 && widgetSettings.ViewMode != HabitsViewMode.Focus)
        {
            WeeklyDays.Clear();
        }

        // Differential update HeatmapCells only when in heatmap mode or currently displayed
        if (widgetSettings.ViewMode == HabitsViewMode.Heatmap || HeatmapCells.Count > 0)
        {
            var singleId = widgetSettings.ViewMode == HabitsViewMode.Focus && targetItem != null ? targetItem.Id : null;
            var cells = HabitStatsCalculator.GetMonthHeatmap(dataModel, today.Year, today.Month, dataModel.DayResetHour, now, singleId);

            for (int i = 0; i < cells.Count; i++)
            {
                if (i < HeatmapCells.Count)
                {
                    if (!Equals(HeatmapCells[i], cells[i]))
                    {
                        HeatmapCells[i] = cells[i];
                    }
                }
                else
                {
                    HeatmapCells.Add(cells[i]);
                }
            }
            while (HeatmapCells.Count > cells.Count)
            {
                HeatmapCells.RemoveAt(HeatmapCells.Count - 1);
            }

            var monthTitle = today.ToString("yyyy年M月", CultureInfo.CurrentUICulture);
            if (HeatmapMonthTitle != monthTitle)
            {
                HeatmapMonthTitle = monthTitle;
                OnPropertyChanged(nameof(HeatmapMonthTitle));
            }
        }
    }

    public void ToggleCheckIn(string habitId)
    {
        var habit = dataModel.Habits.FirstOrDefault(h => h.Id == habitId);
        if (habit == null) return;

        var now = testNow ?? DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, dataModel.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        int existingIdx = dataModel.Records.FindIndex(r => r.HabitId == habitId && r.Date == todayStr);
        HabitRecord? existingRecord = existingIdx >= 0 ? dataModel.Records[existingIdx] : null;

        var newRecords = new List<HabitRecord>(dataModel.Records.Count + (existingIdx < 0 ? 1 : 0));
        newRecords.AddRange(dataModel.Records);

        HabitRecord newRecord;
        if (habit.Type == HabitType.Boolean)
        {
            bool nextState = existingRecord == null || !existingRecord.Completed;
            newRecord = new HabitRecord(habitId, todayStr, nextState ? 1 : 0, nextState, null, now);
        }
        else
        {
            bool nextDone = existingRecord == null || !existingRecord.Completed;
            double nextVal = nextDone ? habit.TargetValue : 0;
            newRecord = new HabitRecord(habitId, todayStr, nextVal, nextDone, null, now);
        }

        if (existingIdx >= 0)
        {
            newRecords[existingIdx] = newRecord;
        }
        else
        {
            newRecords.Add(newRecord);
        }

        var updated = dataModel with { Records = newRecords };
        HabitsStore.Save(updated);
    }

    public void StepNumeric(string habitId, bool increment)
    {
        var habit = dataModel.Habits.FirstOrDefault(h => h.Id == habitId);
        if (habit == null || habit.Type != HabitType.Numeric) return;

        var now = testNow ?? DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, dataModel.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        int existingIdx = dataModel.Records.FindIndex(r => r.HabitId == habitId && r.Date == todayStr);
        HabitRecord? existingRecord = existingIdx >= 0 ? dataModel.Records[existingIdx] : null;

        var newRecords = new List<HabitRecord>(dataModel.Records.Count + (existingIdx < 0 ? 1 : 0));
        newRecords.AddRange(dataModel.Records);

        double curVal = existingRecord?.Value ?? 0;
        double step = habit.StepValue > 0 ? habit.StepValue : 1;
        double nextVal = increment ? curVal + step : Math.Max(0, curVal - step);
        bool completed = nextVal >= habit.TargetValue;

        var newRecord = new HabitRecord(habitId, todayStr, nextVal, completed, existingRecord?.Note, now);
        if (existingIdx >= 0)
        {
            newRecords[existingIdx] = newRecord;
        }
        else
        {
            newRecords.Add(newRecord);
        }

        var updated = dataModel with { Records = newRecords };
        HabitsStore.Save(updated);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
