using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Calendar.Models;
using Calendar.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Calendar.Views;

public partial class SchedulePanelWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private DateTime currentDate;
    private string? editingEventId;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public SchedulePanelWindow() : this(null, null, null) { }

    public SchedulePanelWindow(Point? screenCenter = null, string? targetEventId = null, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;
        currentDate = DateTime.Today;

        InitializeComponent();
        InitializePanel(cornerRadius);

        if (!string.IsNullOrEmpty(targetEventId))
        {
            var targetEvent = ScheduleStore.Get().Events?.FirstOrDefault(e => e.Id == targetEventId);
            if (targetEvent != null)
            {
                currentDate = targetEvent.StartTime.Date;
                editingEventId = targetEvent.Id;
            }
        }

        RefreshView();

        ScheduleStore.ModelChanged += OnStoreModelChanged;
        Closed += OnWindowClosed;

        if (!string.IsNullOrEmpty(editingEventId))
        {
            PopulateFormForEdit(editingEventId);
        }
        else
        {
            ResetForm();
        }
    }

    public static void ShowPanel(Point? screenCenter = null, Window? owner = null, string? targetEventId = null, double? cornerRadius = null)
    {
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "calendar.schedule.panel",
            PanelScope.PerOwner,
            () => new SchedulePanelWindow(screenCenter, targetEventId, cornerRadius)));
    }

    private void OnStoreModelChanged(ScheduleModel newModel, object? sender)
    {
        RefreshView();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ScheduleStore.ModelChanged -= OnStoreModelChanged;
    }

    private void RefreshView()
    {
        CurrentDateText.Text = currentDate.Date == DateTime.Today
            ? $"{currentDate:M月d日} · 今天"
            : $"{currentDate:M月d日 dddd}";

        var events = ScheduleStore.GetEventsForDate(currentDate);
        EventsItemsControl.ItemsSource = events;
        ListCountText.Text = $"该日共 {events.Count} 项安排";
        EmptyNotice.IsVisible = events.Count == 0;
    }

    private void OnPrevDayClicked(object? sender, RoutedEventArgs e)
    {
        currentDate = currentDate.AddDays(-1);
        RefreshView();
    }

    private void OnNextDayClicked(object? sender, RoutedEventArgs e)
    {
        currentDate = currentDate.AddDays(1);
        RefreshView();
    }

    private void OnTodayClicked(object? sender, RoutedEventArgs e)
    {
        currentDate = DateTime.Today;
        RefreshView();
    }

    private void OnAllDayCheckChanged(object? sender, RoutedEventArgs e)
    {
        var isAllDay = IsAllDayCheckBox.IsChecked == true;
        TimeInputsGrid.IsVisible = !isAllDay;
        QuickPresetsRow.IsVisible = !isAllDay;
    }

    private void OnTimeInputLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && TryParseTime(tb.Text, out var time))
        {
            tb.Text = $"{time.Hours:D2}:{time.Minutes:D2}";
        }
    }

    private void OnAdd30MinClicked(object? sender, RoutedEventArgs e)
    {
        if (TryParseTime(StartTimeInput.Text, out var start))
        {
            var end = start.Add(TimeSpan.FromMinutes(30));
            EndTimeInput.Text = $"{end.Hours:D2}:{end.Minutes:D2}";
        }
    }

    private void OnAdd1HourClicked(object? sender, RoutedEventArgs e)
    {
        if (TryParseTime(StartTimeInput.Text, out var start))
        {
            var end = start.Add(TimeSpan.FromHours(1));
            EndTimeInput.Text = $"{end.Hours:D2}:{end.Minutes:D2}";
        }
    }

    private void OnAdd2HourClicked(object? sender, RoutedEventArgs e)
    {
        if (TryParseTime(StartTimeInput.Text, out var start))
        {
            var end = start.Add(TimeSpan.FromHours(2));
            EndTimeInput.Text = $"{end.Hours:D2}:{end.Minutes:D2}";
        }
    }

    private void OnPresetAfternoonClicked(object? sender, RoutedEventArgs e)
    {
        StartTimeInput.Text = "14:00";
        EndTimeInput.Text = "15:00";
    }

    private void OnEditEventClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string eventId })
        {
            PopulateFormForEdit(eventId);
        }
    }

    private void PopulateFormForEdit(string eventId)
    {
        var ev = ScheduleStore.Get().Events?.FirstOrDefault(e => e.Id == eventId);
        if (ev == null) return;

        editingEventId = ev.Id;
        FormTitleText.Text = "编辑日程";
        EventTitleInput.Text = ev.Title;
        IsAllDayCheckBox.IsChecked = ev.IsAllDay;
        StartTimeInput.Text = $"{ev.StartTime:HH:mm}";
        EndTimeInput.Text = $"{ev.EndTime:HH:mm}";
        TimeInputsGrid.IsVisible = !ev.IsAllDay;
        QuickPresetsRow.IsVisible = !ev.IsAllDay;

        EventLocationInput.Text = ev.Location ?? "";
        EventNotesInput.Text = ev.Notes ?? "";

        if (ev.Category == "会议" || ev.Color == "#FF9500") ColorOrange.IsChecked = true;
        else if (ev.Category == "生活" || ev.Color == "#34C759") ColorGreen.IsChecked = true;
        else if (ev.Category == "重要" || ev.Color == "#FF3B30") ColorRed.IsChecked = true;
        else ColorBlue.IsChecked = true;

        CancelEditBtn.IsVisible = true;
    }

    private void OnCancelEditClicked(object? sender, RoutedEventArgs e)
    {
        ResetForm();
    }

    private void OnDeleteEventClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string eventId })
        {
            ScheduleStore.DeleteEvent(eventId, this);
            if (editingEventId == eventId)
            {
                ResetForm();
            }
        }
    }

    private void OnSaveEventClicked(object? sender, RoutedEventArgs e)
    {
        var title = EventTitleInput.Text?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var isAllDay = IsAllDayCheckBox.IsChecked == true;
        DateTime startTime;
        DateTime endTime;

        if (isAllDay)
        {
            startTime = currentDate.Date;
            endTime = currentDate.Date.AddDays(1).AddSeconds(-1);
        }
        else
        {
            if (!TryParseTime(StartTimeInput.Text, out var startOffset))
                startOffset = new TimeSpan(9, 0, 0);

            if (!TryParseTime(EndTimeInput.Text, out var endOffset))
                endOffset = startOffset.Add(TimeSpan.FromHours(1));

            if (endOffset < startOffset) endOffset = startOffset.Add(TimeSpan.FromHours(1));

            startTime = currentDate.Date.Add(startOffset);
            endTime = currentDate.Date.Add(endOffset);
        }

        var (category, color) = GetSelectedCategoryAndColor();
        var location = EventLocationInput.Text?.Trim();
        var notes = EventNotesInput.Text?.Trim();

        var ev = new ScheduleEventModel(
            editingEventId ?? Guid.NewGuid().ToString("N"),
            title,
            startTime,
            endTime,
            isAllDay,
            color,
            category,
            string.IsNullOrWhiteSpace(location) ? null : location,
            string.IsNullOrWhiteSpace(notes) ? null : notes
        );

        ScheduleStore.AddOrUpdateEvent(ev, this);
        ResetForm();
    }

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        text = text.Trim().Replace("：", ":");

        // Format: HH:mm or H:mm
        if (TimeSpan.TryParse(text, out time)) return true;

        if (DateTime.TryParseExact(text, new[] { "H:mm", "HH:mm", "H:m", "HH:m" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            time = dt.TimeOfDay;
            return true;
        }

        // Shorthand formats: "9" -> 09:00, "14" -> 14:00, "1430" -> 14:30
        if (int.TryParse(text, out var num))
        {
            if (num >= 0 && num <= 24)
            {
                time = TimeSpan.FromHours(num == 24 ? 0 : num);
                return true;
            }
            if (num >= 100 && num <= 2359)
            {
                var hours = num / 100;
                var mins = num % 100;
                if (hours < 24 && mins < 60)
                {
                    time = new TimeSpan(hours, mins, 0);
                    return true;
                }
            }
        }

        return false;
    }

    private (string Category, string Color) GetSelectedCategoryAndColor()
    {
        if (ColorOrange.IsChecked == true) return ("会议", "#FF9500");
        if (ColorGreen.IsChecked == true) return ("生活", "#34C759");
        if (ColorRed.IsChecked == true) return ("重要", "#FF3B30");
        return ("工作", "#007AFF");
    }

    private void ResetForm()
    {
        editingEventId = null;
        FormTitleText.Text = "新建日程";
        EventTitleInput.Text = "";

        var nowHour = DateTime.Now.Hour;
        var startH = Math.Min(22, nowHour + 1);
        StartTimeInput.Text = $"{startH:D2}:00";
        EndTimeInput.Text = $"{Math.Min(23, startH + 1):D2}:00";

        IsAllDayCheckBox.IsChecked = false;
        TimeInputsGrid.IsVisible = true;
        QuickPresetsRow.IsVisible = true;

        EventLocationInput.Text = "";
        EventNotesInput.Text = "";
        ColorBlue.IsChecked = true;
        CancelEditBtn.IsVisible = false;
    }
}
