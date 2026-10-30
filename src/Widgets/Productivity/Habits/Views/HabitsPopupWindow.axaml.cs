using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Habits.Locales;
using Habits.Models;
using Habits.Services;

namespace Habits.Views;

public partial class HabitsPopupWindow : SecondaryPanelWindow
{
    private HabitsDataModel currentData;
    private readonly Point? spawnScreenCenter;
    private readonly Action<HabitsDataModel>? onDataChanged;

    private DateTime calendarMonth;
    private DateTime selectedDate;
    private HabitItem? editingHabit;

    private static readonly string[] FormFreqOptions =
    [
        Locale.Habits_Freq_Daily,
        Locale.Habits_Freq_Weekdays,
        Locale.Habits_Freq_Weekends
    ];

    private static readonly string[] PresetColors =
    [
        "#007AFF", "#34C759", "#FF9500", "#AF52DE", "#FF2D55", "#5856D6"
    ];

    // 矢量图标路径（24×24 视窗，Material 实底图形）。规范 §4.4：禁止 emoji 充当功能图标。
    private const string EditIconGeometry =
        "M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04c.39-.39.39-1.02 0-1.41l-2.34-2.34a.9959.9959 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z";

    private const string DeleteIconGeometry =
        "M6 19c0 1.1.9 2 2 2h8c1.1 0 2-.9 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z";

    /// <summary>
    /// 主题跟随的矢量操作图标：24×24 设计画布经 Viewbox 统一缩放到 16×16（墨迹约 12px）。
    /// Path 用 Stretch=None 保留原始坐标——Stretch=Uniform 会把几何边界拉到控件左上角，
    /// 对宽高比不接近 1 的字形（如铅笔、垃圾桶）会让墨迹偏上/偏左。
    /// Fill 绑定窗口作用域 SystemControlForegroundBaseHighBrush（彩色/单色、明暗全作用域存在）。
    /// </summary>
    private Control CreateActionIcon(string geometryData)
    {
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse(geometryData),
            Stretch = Stretch.None,
            Width = 24,
            Height = 24,
            Opacity = 0.75,
        };
        path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, this.GetResourceObservable("SystemControlForegroundBaseHighBrush").ToBinding());

        return new Viewbox
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Child = path,
        };
    }

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public HabitsPopupWindow() : this(HabitsStore.Get(), null, null) { }

    public HabitsPopupWindow(
        HabitsDataModel data,
        Point? screenCenter = null,
        Action<HabitsDataModel>? onDataChanged = null,
        double? cornerRadius = null)
    {
        currentData = data;
        spawnScreenCenter = screenCenter;
        this.onDataChanged = onDataChanged;

        var now = DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, data.DayResetHour);
        calendarMonth = new DateTime(today.Year, today.Month, 1);
        selectedDate = today;

        InitializeComponent();
        InitializePanel(cornerRadius);

        InitFormFrequency();
        RefreshTodayTab();
        RefreshCalendarTab();
        RefreshStatsTab();

        HabitsStore.ModelChanged += OnStoreModelChanged;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(
        HabitsDataModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<HabitsDataModel>? onDataChanged = null,
        double? cornerRadius = null)
    {
        // PerOwner identity (the weather panel precedent): every habits widget owns its own
        // panel. The service handles the cooldown, the toggle-close and the pinned show — the
        // decision is deliberately ignored, mirroring the old bare early returns.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "habits.popup",
            PanelScope.PerOwner,
            () => new HabitsPopupWindow(model, screenCenter, onDataChanged, cornerRadius)));
    }

    private void OnStoreModelChanged(HabitsDataModel newData, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        currentData = newData;
        RefreshTodayTab();
        RefreshCalendarTab();
        RefreshStatsTab();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        HabitsStore.ModelChanged -= OnStoreModelChanged;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        HabitsStore.ModelChanged -= OnStoreModelChanged;
        Closed -= OnWindowClosed;
    }

    private void SaveData(HabitsDataModel newData)
    {
        currentData = newData;
        HabitsStore.Save(newData, sender: this);
        onDataChanged?.Invoke(newData);
        RefreshTodayTab();
        RefreshCalendarTab();
        RefreshStatsTab();
    }

    #region Tab Navigation

    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (TodayTabPanel == null || CalendarTabPanel == null || StatsTabPanel == null) return;

        TodayTabPanel.IsVisible = TabTodayBtn.IsChecked == true;
        CalendarTabPanel.IsVisible = TabCalendarBtn.IsChecked == true;
        StatsTabPanel.IsVisible = TabStatsBtn.IsChecked == true;

        if (CalendarTabPanel.IsVisible) RefreshCalendarTab();
        if (StatsTabPanel.IsVisible) RefreshStatsTab();
    }

    #endregion

    #region Tab 1: Today & Management

    private void RefreshTodayTab()
    {
        HabitsManagementList.Children.Clear();

        var now = DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, currentData.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        var todayRecords = new Dictionary<string, HabitRecord>(StringComparer.Ordinal);
        foreach (var r in currentData.Records)
        {
            if (r.Date == todayStr)
            {
                todayRecords[r.HabitId] = r;
            }
        }

        int completedCount = 0;
        int dueCount = 0;

        foreach (var habit in currentData.Habits)
        {
            bool isDue = HabitStatsCalculator.IsDueOnDate(habit, today);
            if (isDue) dueCount++;

            bool completed = false;
            double val = 0;
            if (todayRecords.TryGetValue(habit.Id, out var rec))
            {
                completed = rec.Completed;
                val = rec.Value;
            }
            if (isDue && completed) completedCount++;

            var (currentStreak, _) = HabitStatsCalculator.CalculateStreaks(habit, currentData.Records, currentData.DayResetHour, now);

            // Card item
            var border = new Border
            {
                Background = HabitBrushes.CardBackground,
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10, 8),
                Margin = new Thickness(0, 2, 0, 4)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto, Auto")
            };

            // Icon
            var iconBorder = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                Background = HabitBrushes.IconBackground,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = habit.Icon,
                    FontSize = 18,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                }
            };
            Grid.SetColumn(iconBorder, 0);
            grid.Children.Add(iconBorder);

            // Title & Progress
            var infoStack = new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Spacing = 2
            };
            var titleRow = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 6
            };
            titleRow.Children.Add(new TextBlock
            {
                Text = habit.Title,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            titleRow.Children.Add(new TextBlock
            {
                Text = $"连击 {currentStreak}",
                FontSize = 11,
                Opacity = 0.7,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            infoStack.Children.Add(titleRow);

            string progressDesc = habit.Type == HabitType.Numeric
                ? $"{val:0.#} / {habit.TargetValue:0.#} {habit.Unit}"
                : (completed ? "今日已完成" : "今日未打卡");
            infoStack.Children.Add(new TextBlock
            {
                Text = progressDesc,
                FontSize = 11,
                Opacity = 0.55
            });
            Grid.SetColumn(infoStack, 1);
            grid.Children.Add(infoStack);

            // Actions: Numeric steppers or Check button
            var actionStack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            if (habit.Type == HabitType.Numeric)
            {
                var minusBtn = new Button
                {
                    Classes = { "pill-btn" },
                    Content = "-",
                    Width = 24,
                    Padding = new Thickness(0)
                };
                minusBtn.Click += (_, _) => StepHabit(habit.Id, false);
                actionStack.Children.Add(minusBtn);

                var plusBtn = new Button
                {
                    Classes = { "pill-btn" },
                    Content = "+",
                    Width = 24,
                    Padding = new Thickness(0)
                };
                plusBtn.Click += (_, _) => StepHabit(habit.Id, true);
                actionStack.Children.Add(plusBtn);
            }

            var punchBtn = new Button
            {
                Classes = { "pill-btn" },
                Content = completed ? "已打卡" : "打卡",
                Background = completed
                    ? HabitBrushes.GetBrush(habit.ColorHex)
                    : HabitBrushes.ButtonBackground
            };
            if (completed)
            {
                // 完成态：白字压在习惯色实底上，保证对比度；未完成态继承主题前景
                punchBtn.Foreground = HabitBrushes.White;
            }
            punchBtn.Click += (_, _) => ToggleHabit(habit.Id);
            actionStack.Children.Add(punchBtn);

            Grid.SetColumn(actionStack, 2);
            grid.Children.Add(actionStack);

            // More / Edit / Delete buttons
            var editStack = new StackPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 2,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };

            var editBtn = new Button
            {
                Classes = { "dc-icon-button", "compact", "habit-action" },
                Content = CreateActionIcon(EditIconGeometry)
            };
            editBtn.Click += (_, _) => OpenEditForm(habit);
            editStack.Children.Add(editBtn);

            var deleteBtn = new Button
            {
                Classes = { "dc-icon-button", "compact", "habit-action" },
                Content = CreateActionIcon(DeleteIconGeometry)
            };
            deleteBtn.Click += (_, _) => DeleteHabit(habit.Id);
            editStack.Children.Add(deleteBtn);

            Grid.SetColumn(editStack, 3);
            grid.Children.Add(editStack);

            border.Child = grid;
            HabitsManagementList.Children.Add(border);
        }

        SubtitleText.Text = $"今日已完成 {completedCount}/{dueCount} 项";
    }

    private void ToggleHabit(string habitId)
    {
        var habit = currentData.Habits.FirstOrDefault(h => h.Id == habitId);
        if (habit == null) return;

        var now = DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, currentData.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        int existingIdx = currentData.Records.FindIndex(r => r.HabitId == habitId && r.Date == todayStr);
        HabitRecord? existing = existingIdx >= 0 ? currentData.Records[existingIdx] : null;

        var newRecords = new List<HabitRecord>(currentData.Records.Count + (existingIdx < 0 ? 1 : 0));
        newRecords.AddRange(currentData.Records);

        bool nextDone = existing == null || !existing.Completed;
        double nextVal = habit.Type == HabitType.Boolean
            ? (nextDone ? 1 : 0)
            : (nextDone ? habit.TargetValue : 0);

        var newRec = new HabitRecord(habitId, todayStr, nextVal, nextDone, null, now);
        if (existingIdx >= 0)
        {
            newRecords[existingIdx] = newRec;
        }
        else
        {
            newRecords.Add(newRec);
        }

        SaveData(currentData with { Records = newRecords });
    }

    private void StepHabit(string habitId, bool increment)
    {
        var habit = currentData.Habits.FirstOrDefault(h => h.Id == habitId);
        if (habit == null) return;

        var now = DateTime.Now;
        var today = HabitStatsCalculator.GetEffectiveDate(now, currentData.DayResetHour);
        var todayStr = HabitStatsCalculator.ToDateString(today);

        int existingIdx = currentData.Records.FindIndex(r => r.HabitId == habitId && r.Date == todayStr);
        HabitRecord? existing = existingIdx >= 0 ? currentData.Records[existingIdx] : null;

        var newRecords = new List<HabitRecord>(currentData.Records.Count + (existingIdx < 0 ? 1 : 0));
        newRecords.AddRange(currentData.Records);

        double curVal = existing?.Value ?? 0;
        double step = habit.StepValue > 0 ? habit.StepValue : 1;
        double nextVal = increment ? curVal + step : Math.Max(0, curVal - step);
        bool completed = nextVal >= habit.TargetValue;

        var newRec = new HabitRecord(habitId, todayStr, nextVal, completed, existing?.Note, now);
        if (existingIdx >= 0)
        {
            newRecords[existingIdx] = newRec;
        }
        else
        {
            newRecords.Add(newRec);
        }

        SaveData(currentData with { Records = newRecords });
    }

    private void DeleteHabit(string habitId)
    {
        var newHabits = new List<HabitItem>(currentData.Habits.Count);
        foreach (var h in currentData.Habits)
        {
            if (h.Id != habitId) newHabits.Add(h);
        }

        var newRecords = new List<HabitRecord>(currentData.Records.Count);
        foreach (var r in currentData.Records)
        {
            if (r.HabitId != habitId) newRecords.Add(r);
        }

        SaveData(currentData with { Habits = newHabits, Records = newRecords });
    }

    #endregion

    #region Add / Edit Form

    private void InitFormFrequency()
    {
        FormFreqCombo.ItemsSource = FormFreqOptions;
        FormFreqCombo.SelectedIndex = 0;
    }

    private void OnAddHabitClicked(object? sender, RoutedEventArgs e)
    {
        editingHabit = null;
        FormTitleText.Text = Locale.Habits_AddHabit;
        FormTitleBox.Text = string.Empty;
        FormIconBox.Text = "🎯";
        TypeBooleanRadio.IsChecked = true;
        FormTargetBox.Text = "1";
        FormStepBox.Text = "1";
        FormUnitBox.Text = "次";
        FormFreqCombo.SelectedIndex = 0;
        FormPanel.IsVisible = true;
    }

    private void OpenEditForm(HabitItem habit)
    {
        editingHabit = habit;
        FormTitleText.Text = Locale.Habits_EditHabit;
        FormTitleBox.Text = habit.Title;
        FormIconBox.Text = habit.Icon;
        if (habit.Type == HabitType.Numeric) TypeNumericRadio.IsChecked = true;
        else TypeBooleanRadio.IsChecked = true;
        FormTargetBox.Text = habit.TargetValue.ToString(CultureInfo.InvariantCulture);
        FormStepBox.Text = habit.StepValue.ToString(CultureInfo.InvariantCulture);
        FormUnitBox.Text = habit.Unit;
        FormFreqCombo.SelectedIndex = (int)habit.Frequency;
        FormPanel.IsVisible = true;
    }

    private void OnFormTypeChanged(object? sender, RoutedEventArgs e)
    {
        if (NumericConfigGrid == null) return;
        NumericConfigGrid.IsVisible = TypeNumericRadio.IsChecked == true;
    }

    private void OnFormCancelClicked(object? sender, RoutedEventArgs e)
    {
        FormPanel.IsVisible = false;
        editingHabit = null;
    }

    private void OnFormSaveClicked(object? sender, RoutedEventArgs e)
    {
        var title = FormTitleBox.Text?.Trim();
        if (string.IsNullOrEmpty(title)) return;

        var icon = string.IsNullOrWhiteSpace(FormIconBox.Text) ? "🎯" : FormIconBox.Text.Trim();
        var isNumeric = TypeNumericRadio.IsChecked == true;
        double.TryParse(FormTargetBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var target);
        double.TryParse(FormStepBox.Text, NumberStyles.Any, CultureInfo.InvariantCulture, out var step);
        if (target <= 0) target = 1;
        if (step <= 0) step = 1;
        var unit = string.IsNullOrWhiteSpace(FormUnitBox.Text) ? (isNumeric ? "次" : "") : FormUnitBox.Text.Trim();
        var freq = (HabitFrequency)Math.Clamp(FormFreqCombo.SelectedIndex, 0, 2);

        var habits = new List<HabitItem>(currentData.Habits);
        if (editingHabit == null)
        {
            // Add new
            string newId = "h_" + Guid.NewGuid().ToString("N")[..8];
            string color = PresetColors[habits.Count % PresetColors.Length];
            habits.Add(new HabitItem(
                newId,
                title,
                icon,
                color,
                isNumeric ? HabitType.Numeric : HabitType.Boolean,
                target,
                step,
                unit,
                freq,
                DateTime.Now
            ));
        }
        else
        {
            // Edit existing
            int idx = habits.FindIndex(h => h.Id == editingHabit.Id);
            if (idx >= 0)
            {
                habits[idx] = editingHabit with
                {
                    Title = title,
                    Icon = icon,
                    Type = isNumeric ? HabitType.Numeric : HabitType.Boolean,
                    TargetValue = target,
                    StepValue = step,
                    Unit = unit,
                    Frequency = freq
                };
            }
        }

        SaveData(currentData with { Habits = habits });
        FormPanel.IsVisible = false;
        editingHabit = null;
    }

    #endregion

    #region Tab 2: Calendar & Backfill

    private void OnPrevMonthClicked(object? sender, RoutedEventArgs e)
    {
        calendarMonth = calendarMonth.AddMonths(-1);
        RefreshCalendarTab();
    }

    private void OnNextMonthClicked(object? sender, RoutedEventArgs e)
    {
        calendarMonth = calendarMonth.AddMonths(1);
        RefreshCalendarTab();
    }

    private void RefreshCalendarTab()
    {
        CalendarMonthText.Text = calendarMonth.ToString("yyyy年 M月", CultureInfo.CurrentUICulture);
        CalendarDaysGrid.Children.Clear();

        var firstDay = new DateTime(calendarMonth.Year, calendarMonth.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(calendarMonth.Year, calendarMonth.Month);
        int offset = (7 + (firstDay.DayOfWeek - DayOfWeek.Monday)) % 7;

        // Empty padding cells
        for (int i = 0; i < offset; i++)
        {
            CalendarDaysGrid.Children.Add(new Border { Height = 34 });
        }

        // Fast completed count lookup by date string without GroupBy
        var completedCountsByDate = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in currentData.Records)
        {
            if (r.Completed)
            {
                completedCountsByDate.TryGetValue(r.Date, out int count);
                completedCountsByDate[r.Date] = count + 1;
            }
        }

        var today = HabitStatsCalculator.GetEffectiveDate(DateTime.Now, currentData.DayResetHour);

        for (int day = 1; day <= daysInMonth; day++)
        {
            var date = new DateTime(calendarMonth.Year, calendarMonth.Month, day);
            var dateStr = HabitStatsCalculator.ToDateString(date);

            completedCountsByDate.TryGetValue(dateStr, out int doneCount);

            bool isToday = date == today;
            bool isSelected = date == selectedDate;

            var dayBtn = new Button
            {
                Classes = { "pill-btn" },
                Height = 32,
                Margin = new Thickness(2),
                Padding = new Thickness(0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch,
                Background = isSelected
                    ? HabitBrushes.CalendarSelected
                    : (doneCount > 0 ? HabitBrushes.CalendarDone : HabitBrushes.Transparent),
                BorderBrush = isToday ? HabitBrushes.DodgerBlue : HabitBrushes.Transparent,
                BorderThickness = isToday ? new Thickness(1.5) : new Thickness(0)
            };

            var stack = new StackPanel
            {
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Spacing = 1
            };
            stack.Children.Add(new TextBlock
            {
                Text = day.ToString(),
                FontSize = 11,
                FontWeight = isToday ? FontWeight.Bold : FontWeight.Normal,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
            });

            if (doneCount > 0)
            {
                stack.Children.Add(new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = HabitBrushes.ForestGreen,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                });
            }

            dayBtn.Content = stack;
            var capturedDate = date;
            dayBtn.Click += (_, _) =>
            {
                selectedDate = capturedDate;
                RefreshCalendarTab();
            };

            CalendarDaysGrid.Children.Add(dayBtn);
        }

        RefreshSelectedDateDetail();
    }

    private void RefreshSelectedDateDetail()
    {
        SelectedDateTitleText.Text = selectedDate.ToString("yyyy年M月d日 (ddd) 打卡详情与补卡", CultureInfo.CurrentUICulture);
        SelectedDateHabitsPanel.Children.Clear();

        var dateStr = HabitStatsCalculator.ToDateString(selectedDate);
        var dayRecords = new Dictionary<string, HabitRecord>(StringComparer.Ordinal);
        foreach (var r in currentData.Records)
        {
            if (r.Date == dateStr)
            {
                dayRecords[r.HabitId] = r;
            }
        }

        foreach (var habit in currentData.Habits)
        {
            bool isDue = HabitStatsCalculator.IsDueOnDate(habit, selectedDate);
            bool completed = dayRecords.TryGetValue(habit.Id, out var rec) && rec.Completed;

            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto"),
                Margin = new Thickness(0, 2)
            };

            var titleBlock = new TextBlock
            {
                Text = $"{habit.Icon} {habit.Title}",
                FontSize = 12,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(titleBlock, 0);
            row.Children.Add(titleBlock);

            var statusBlock = new TextBlock
            {
                Text = completed ? "已打卡" : (isDue ? "待打卡" : "无目标"),
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(statusBlock, 1);
            row.Children.Add(statusBlock);

            var toggleBtn = new Button
            {
                Classes = { "pill-btn" },
                Content = completed ? "撤销" : "补卡",
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            toggleBtn.Click += (_, _) => ToggleBackfill(habit.Id, dateStr, completed, habit.TargetValue);
            Grid.SetColumn(toggleBtn, 2);
            row.Children.Add(toggleBtn);

            SelectedDateHabitsPanel.Children.Add(row);
        }
    }

    private void ToggleBackfill(string habitId, string dateStr, bool wasCompleted, double targetValue)
    {
        var newRecords = new List<HabitRecord>(currentData.Records.Count + 1);
        foreach (var r in currentData.Records)
        {
            if (!(r.HabitId == habitId && r.Date == dateStr))
            {
                newRecords.Add(r);
            }
        }

        if (!wasCompleted)
        {
            newRecords.Add(new HabitRecord(habitId, dateStr, targetValue, true, "补卡", DateTime.Now));
        }

        SaveData(currentData with { Records = newRecords });
    }

    #endregion

    #region Tab 3: Statistics

    private void RefreshStatsTab()
    {
        int maxCurrentStreak = 0;
        int maxBestStreak = 0;
        var now = DateTime.Now;

        foreach (var habit in currentData.Habits)
        {
            var (cStreak, bStreak) = HabitStatsCalculator.CalculateStreaks(habit, currentData.Records, currentData.DayResetHour, now);
            if (cStreak > maxCurrentStreak) maxCurrentStreak = cStreak;
            if (bStreak > maxBestStreak) maxBestStreak = bStreak;
        }

        int totalPunches = 0;
        foreach (var r in currentData.Records)
        {
            if (r.Completed) totalPunches++;
        }

        // Current month rate
        var today = HabitStatsCalculator.GetEffectiveDate(now, currentData.DayResetHour);
        var cells = HabitStatsCalculator.GetMonthHeatmap(currentData, today.Year, today.Month, currentData.DayResetHour, now);
        int dueSum = 0;
        int doneSum = 0;
        for (int i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c.IsCurrentMonth)
            {
                dueSum += c.TotalDueCount;
                doneSum += c.CompletedCount;
            }
        }
        double monthlyRate = dueSum > 0 ? (double)doneSum / dueSum : 0;

        StatCurrentStreakText.Text = $"{maxCurrentStreak} 天";
        StatBestStreakText.Text = $"{maxBestStreak} 天";
        StatTotalCheckInsText.Text = $"{totalPunches} 次";
        StatCompletionRateText.Text = $"{monthlyRate * 100:0.#}%";

        StatsHabitsBreakdown.Children.Clear();
        foreach (var habit in currentData.Habits)
        {
            var (cStreak, bStreak) = HabitStatsCalculator.CalculateStreaks(habit, currentData.Records, currentData.DayResetHour, now);
            int habitPunches = 0;
            foreach (var r in currentData.Records)
            {
                if (r.HabitId == habit.Id && r.Completed) habitPunches++;
            }

            var border = new Border
            {
                Background = HabitBrushes.StatsCardBackground,
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6),
                Margin = new Thickness(0, 2)
            };

            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("Auto, *, Auto")
            };

            var title = new TextBlock
            {
                Text = $"{habit.Icon} {habit.Title}",
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(title, 0);
            grid.Children.Add(title);

            var stats = new TextBlock
            {
                Text = $"连击: {cStreak}天 · 最高: {bStreak}天 · 累计: {habitPunches}次",
                FontSize = 11,
                Opacity = 0.6,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetColumn(stats, 2);
            grid.Children.Add(stats);

            border.Child = grid;
            StatsHabitsBreakdown.Children.Add(border);
        }
    }

    #endregion
}
