using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Threading;
using Calendar.Locales;
using Calendar.Models;
using Calendar.Services;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;

namespace Calendar.ViewModels;

public class ScheduleItemViewModel : INotifyPropertyChanged
{
    private readonly ScheduleEventModel model;
    private IBrush? monochromeBrush;

    public ScheduleItemViewModel(ScheduleEventModel model)
    {
        this.model = model;
        UpdateLiveStatus();
    }

    public ScheduleEventModel Model => model;
    public string Id => model.Id;
    public string Title => model.Title;
    public DateTime StartTime => model.StartTime;
    public DateTime EndTime => model.EndTime;
    public bool IsAllDay => model.IsAllDay;
    public string? Category => model.Category;
    public string? Location => model.Location;
    public string? Notes => model.Notes;
    public bool IsCompleted => model.IsCompleted;

    public IBrush ColorBrush => monochromeBrush ?? ParseColorBrush(model.Color);

    public void SetMonochromeBrush(IBrush? brush)
    {
        if (ReferenceEquals(monochromeBrush, brush)) return;
        monochromeBrush = brush;
        OnPropertyChanged(nameof(ColorBrush));
    }

    private static IBrush ParseColorBrush(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var color))
            return new SolidColorBrush(color);
        return new SolidColorBrush(Color.Parse("#007AFF"));
    }

    public string TimeRangeText
    {
        get
        {
            if (IsAllDay) return Locale.Calendar_Schedule_AllDay;
            return $"{StartTime:HH:mm} - {EndTime:HH:mm}";
        }
    }

    public string SmallSummaryText
    {
        get
        {
            if (IsAllDay)
            {
                return string.IsNullOrWhiteSpace(Location)
                    ? Locale.Calendar_Schedule_AllDay
                    : $"{Locale.Calendar_Schedule_AllDay} · {Location}";
            }
            if (!string.IsNullOrWhiteSpace(Location))
            {
                return $"{StartTime:HH:mm} · {Location}";
            }
            return $"{StartTime:HH:mm} - {EndTime:HH:mm}";
        }
    }

    public string StartTimeText => IsAllDay ? Locale.Calendar_Schedule_AllDay : $"{StartTime:HH:mm}";
    public string EndTimeText => IsAllDay ? "" : $"{EndTime:HH:mm}";

    private bool isNow;
    public bool IsNow
    {
        get => isNow;
        private set
        {
            if (isNow != value)
            {
                isNow = value;
                OnPropertyChanged();
            }
        }
    }

    public void UpdateLiveStatus()
    {
        var now = DateTime.Now;
        IsNow = !IsCompleted && !IsAllDay && now >= StartTime && now <= EndTime;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class ScheduleDayViewModel : INotifyPropertyChanged
{
    public DateTime Date { get; set; }
    public string Day => Date.Day.ToString();
    public bool IsToday => Date.Date == DateTime.Today;
    public bool IsCurrentMonth { get; set; } = true;
    public bool IsWeekend => Date.DayOfWeek == DayOfWeek.Saturday || Date.DayOfWeek == DayOfWeek.Sunday;
    public bool HasEvents { get; set; }
    private IBrush? baseDotBrush;
    private IBrush? monochromeBrush;
    public IBrush? DotBrush => monochromeBrush ?? baseDotBrush;

    public void SetDotBrush(IBrush? brush) => baseDotBrush = brush;

    public void SetMonochromeBrush(IBrush? brush)
    {
        if (ReferenceEquals(monochromeBrush, brush)) return;
        monochromeBrush = brush;
        OnPropertyChanged(nameof(DotBrush));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

public class ScheduleViewModel : INotifyPropertyChanged
{
    private readonly IWidgetSession? session;
    private DispatcherTimer? timer;
    private ScheduleModel model;
    private DateTime selectedDate;

    public ScheduleViewModel(ScheduleModel model, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
        selectedDate = DateTime.Today;
        UpcomingEvents = [];
        SmallEvents = [];
        SelectedDateEvents = [];
        Days = [];

        Update(model);
    }

    private IBrush? monochromeBrush;

    public ScheduleModel Model => model;

    public void SetMonochromeBrush(IBrush? brush)
    {
        if (ReferenceEquals(monochromeBrush, brush)) return;
        monochromeBrush = brush;
        foreach (var item in UpcomingEvents) item.SetMonochromeBrush(brush);
        foreach (var item in SelectedDateEvents) item.SetMonochromeBrush(brush);
        foreach (var day in Days) day.SetMonochromeBrush(brush);
    }

    public ObservableCollection<ScheduleItemViewModel> UpcomingEvents { get; }
    public ObservableCollection<ScheduleItemViewModel> SmallEvents { get; }
    public ObservableCollection<ScheduleItemViewModel> SelectedDateEvents { get; }
    public ObservableCollection<ScheduleDayViewModel> Days { get; }

    public DateTime SelectedDate
    {
        get => selectedDate;
        set
        {
            if (selectedDate.Date != value.Date)
            {
                selectedDate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedDateTitle));
                RefreshSelectedDateEvents();
            }
        }
    }

    public string SelectedDateTitle
    {
        get
        {
            if (selectedDate.Date == DateTime.Today)
                return $"{selectedDate:M月d日} · 今日日程";
            return $"{selectedDate:M月d日} · 日程安排";
        }
    }

    // Header date properties
    public string DayOfWeek
    {
        get
        {
            var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            if (!isZh) return DateTime.Today.ToString("ddd", CultureInfo.CurrentUICulture).ToUpperInvariant();
            return DateTime.Today.DayOfWeek switch
            {
                System.DayOfWeek.Monday => "周一",
                System.DayOfWeek.Tuesday => "周二",
                System.DayOfWeek.Wednesday => "周三",
                System.DayOfWeek.Thursday => "周四",
                System.DayOfWeek.Friday => "周五",
                System.DayOfWeek.Saturday => "周六",
                _ => "周日"
            };
        }
    }

    public string DayOfWeekFull
    {
        get
        {
            var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            if (!isZh) return DateTime.Today.ToString("dddd", CultureInfo.CurrentUICulture);
            return DateTime.Today.DayOfWeek switch
            {
                System.DayOfWeek.Monday => "星期一",
                System.DayOfWeek.Tuesday => "星期二",
                System.DayOfWeek.Wednesday => "星期三",
                System.DayOfWeek.Thursday => "星期四",
                System.DayOfWeek.Friday => "星期五",
                System.DayOfWeek.Saturday => "星期六",
                _ => "星期日"
            };
        }
    }

    public string Day => DateTime.Today.Day.ToString();
    public string Month => DateTime.Today.ToString("M月");
    public string Year => DateTime.Today.Year.ToString();
    public string MonthYear => DateTime.Today.ToString("yyyy年 M月");

    public string WeekNumberText
    {
        get
        {
            var cal = CultureInfo.CurrentCulture.Calendar;
            var week = cal.GetWeekOfYear(DateTime.Today, CalendarWeekRule.FirstFourDayWeek, System.DayOfWeek.Monday);
            return $"第 {week} 周";
        }
    }

    public int EventsCount => UpcomingEvents.Count;

    public string EventsCountText => EventsCount > 0
        ? string.Format(Locale.Calendar_Schedule_ItemsCount, EventsCount)
        : Locale.Calendar_Schedule_NoEvents;

    public bool HasEvents => UpcomingEvents.Count > 0;
    public bool HasMoreEvents => UpcomingEvents.Count > 1;
    public bool HasMoreThanTwoEvents => UpcomingEvents.Count > 2;
    public string MoreThanTwoEventsText => $"+{UpcomingEvents.Count - 2}";

    public ScheduleItemViewModel? NextEvent => UpcomingEvents.FirstOrDefault();

    public string RemainingEventsCountText
    {
        get
        {
            if (UpcomingEvents.Count <= 1) return "";
            var secondEvent = UpcomingEvents[1];
            return string.Format(Locale.Calendar_Schedule_UpcomingMore, UpcomingEvents.Count - 1);
        }
    }

    public void Update(ScheduleModel newModel)
    {
        model = newModel;
        RefreshTodayEvents();
        RefreshSelectedDateEvents();
        BuildMiniMonthGrid();

        OnPropertyChanged(nameof(DayOfWeek));
        OnPropertyChanged(nameof(DayOfWeekFull));
        OnPropertyChanged(nameof(Day));
        OnPropertyChanged(nameof(Month));
        OnPropertyChanged(nameof(Year));
        OnPropertyChanged(nameof(MonthYear));
        OnPropertyChanged(nameof(WeekNumberText));
        OnPropertyChanged(nameof(EventsCount));
        OnPropertyChanged(nameof(EventsCountText));
        OnPropertyChanged(nameof(HasEvents));
        OnPropertyChanged(nameof(HasMoreEvents));
        OnPropertyChanged(nameof(HasMoreThanTwoEvents));
        OnPropertyChanged(nameof(MoreThanTwoEventsText));
        OnPropertyChanged(nameof(NextEvent));
        OnPropertyChanged(nameof(RemainingEventsCountText));
        OnPropertyChanged(nameof(SmallEvents));
    }

    private void RefreshTodayEvents()
    {
        var today = DateTime.Today;
        var now = DateTime.Now;

        var rawEvents = (model.Events ?? [])
            .Where(e => e.StartTime.Date <= today && e.EndTime.Date >= today)
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.StartTime)
            .Select(e => new ScheduleItemViewModel(e))
            .ToList();

        foreach (var item in rawEvents) item.SetMonochromeBrush(monochromeBrush);
        UpcomingEvents.Clear();
        SmallEvents.Clear();
        foreach (var ev in rawEvents)
        {
            UpcomingEvents.Add(ev);
        }
        foreach (var ev in UpcomingEvents.Take(2))
        {
            SmallEvents.Add(ev);
        }
    }

    private void RefreshSelectedDateEvents()
    {
        var targetDate = selectedDate.Date;
        var rawEvents = (model.Events ?? [])
            .Where(e => e.StartTime.Date <= targetDate && e.EndTime.Date >= targetDate)
            .OrderBy(e => e.IsAllDay ? 0 : 1)
            .ThenBy(e => e.StartTime)
            .Select(e => new ScheduleItemViewModel(e))
            .ToList();

        foreach (var item in rawEvents) item.SetMonochromeBrush(monochromeBrush);
        SelectedDateEvents.Clear();
        foreach (var ev in rawEvents)
        {
            SelectedDateEvents.Add(ev);
        }
    }

    private void BuildMiniMonthGrid()
    {
        Days.Clear();
        var now = DateTime.Today;
        var firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
        var daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

        // Find Monday of the current week to show a 7-day compact bar or month grid
        var diff = (7 + (int)now.DayOfWeek - (int)System.DayOfWeek.Monday) % 7;
        var monday = now.AddDays(-diff);

        for (int i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            var hasEvents = (model.Events ?? []).Any(e => e.StartTime.Date <= date && e.EndTime.Date >= date);
            var firstEvent = (model.Events ?? []).FirstOrDefault(e => e.StartTime.Date <= date && e.EndTime.Date >= date);

            IBrush? dotBrush = null;
            if (firstEvent != null)
            {
                try
                {
                    if (Color.TryParse(firstEvent.Color, out var c))
                        dotBrush = new SolidColorBrush(c);
                }
                catch { }
            }

            var day = new ScheduleDayViewModel
            {
                Date = date,
                IsCurrentMonth = date.Month == now.Month,
                HasEvents = hasEvents
            };
            day.SetDotBrush(dotBrush ?? new SolidColorBrush(Color.Parse("#FF3B30")));
            day.SetMonochromeBrush(monochromeBrush);
            Days.Add(day);
        }
    }

    public void Start()
    {
        if (timer == null)
        {
            timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            timer.Tick += (_, _) => OnTimerTick();
        }
        timer.Start();
    }

    public void Stop()
    {
        timer?.Stop();
    }

    private void OnTimerTick()
    {
        foreach (var ev in UpcomingEvents)
        {
            ev.UpdateLiveStatus();
        }
        OnPropertyChanged(nameof(NextEvent));
    }

    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (activity.RenderActive)
            Start();
        else
            Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}
