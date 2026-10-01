using System.Globalization;
using Calendar.Models;
using ReactiveUI;
using DeskCanvas.Services;

namespace Calendar.ViewModels;

public class MonthCalendarViewModel : ReactiveObject, IDisposable
{
    private readonly MonthCalendarModel monthCalendarModel;
    private DateTime currentDate;
    private bool isRunning;
    
    public MonthCalendarViewModel(MonthCalendarModel monthCalendarModel)
    {
        this.monthCalendarModel = monthCalendarModel;
        Start();
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        TimerService.Timer5Minutes.Subscribe(UpdateTime);
        UpdateTime();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        TimerService.Timer5Minutes.Unsubscribe(UpdateTime);
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
    
    private void UpdateTime()
    {
        var now = DateTime.Now;
        var format = Thread.CurrentThread.CurrentUICulture.DateTimeFormat;

        if (currentDate.Date == now.Date) return;

        currentDate = now;
        Month = format.GetMonthName(now.Month).ToUpper();
        TodayWeekday = format.GetAbbreviatedDayName(now.DayOfWeek).ToUpper();
        TodayNumber = now.Day.ToString();
        var weekDays = GetWeekDays(format).ToList();
        Days = weekDays
            .Concat(GetEmptyDays(now))
            .Concat(GetDaysOfMonth(now))
            .ToList();
        WeekHeaders = weekDays.Select(day => day.Day).ToList();
        CurrentWeek = BuildCurrentWeek(now);
    }

    /// <summary>
    /// The seven real days of the week containing today (starting at the user's
    /// first day of week) — the compact month-strip for the S/M tiers.
    /// </summary>
    private List<DayViewModel> BuildCurrentWeek(DateTime now)
    {
        var diff = ((int)now.DayOfWeek - (int)monthCalendarModel.FirstDayOfWeek + 7) % 7;
        var weekStart = now.Date.AddDays(-diff);
        return Enumerable
            .Range(0, 7)
            .Select(i => weekStart.AddDays(i))
            .Select(day => new DayViewModel(
                day.Day.ToString(),
                IsWeekend(day.DayOfWeek),
                day.Date == now.Date))
            .ToList();
    }
    
    private string? month;
    public string? Month 
    {
        get => month;
        private set => this.RaiseAndSetIfChanged(ref month, value);
    }

    private string? todayWeekday;
    public string? TodayWeekday
    {
        get => todayWeekday;
        private set => this.RaiseAndSetIfChanged(ref todayWeekday, value);
    }

    private string? todayNumber;
    public string? TodayNumber
    {
        get => todayNumber;
        private set => this.RaiseAndSetIfChanged(ref todayNumber, value);
    }

    private List<string?>? weekHeaders;
    public List<string?>? WeekHeaders
    {
        get => weekHeaders;
        private set => this.RaiseAndSetIfChanged(ref weekHeaders, value);
    }

    private List<DayViewModel>? currentWeek;
    public List<DayViewModel>? CurrentWeek
    {
        get => currentWeek;
        private set => this.RaiseAndSetIfChanged(ref currentWeek, value);
    }

    private List<DayViewModel>? days;
    public List<DayViewModel>? Days
    {
        get => days;
        private set => this.RaiseAndSetIfChanged(ref days, value);
    }
    
    private static IEnumerable<DayViewModel> GetDaysOfMonth(DateTime now)
    {
        return Enumerable
            .Range(1, DateTime.DaysInMonth(now.Year, now.Month))
            .Select(day => new DayViewModel(
                day.ToString(),
                IsWeekend(new DateTime(now.Year, now.Month, day).DayOfWeek),
                day == now.Day));
    }

    private IEnumerable<DayViewModel> GetEmptyDays(DateTime now)
    {
        // Cells before the 1st of the month: how far the 1st is from the first
        // day of the week (columns run firstDayOfWeek..). The old
        // "(startOfMonthDayOfWeek + firstDayOfWeek + 5) % 7" only held when
        // firstDayOfWeek == Monday, so any other start day shifted every date
        // one column off (e.g. Sunday-start showed a Friday the 4th under
        // Wednesday).
        var startOfMonthDayOfWeek = (int) new DateTime(now.Year, now.Month, 1).DayOfWeek;
        var firstDayOfWeek = (int) monthCalendarModel.FirstDayOfWeek;
        
        var count = (startOfMonthDayOfWeek - firstDayOfWeek + 7) % 7;
        
        return Enumerable.Range(0, count).Select(_ => new DayViewModel());
    }

    private IEnumerable<DayViewModel> GetWeekDays(DateTimeFormatInfo format)
    {
        var offset = (int)monthCalendarModel.FirstDayOfWeek;
        
        return Enumerable.Range(0, 7)
            .Select(i => (DayOfWeek)((i + offset) % 7))
            .Select(day => new DayViewModel(
                format.GetShortestDayName(day),
                IsWeekend(day)));
    }
    
    private static bool IsWeekend(DayOfWeek dayOfWeek) => dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
}