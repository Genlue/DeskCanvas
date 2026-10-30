using System.Globalization;
using Calendar.Models;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace Calendar.ViewModels;

public class MonthCalendarViewModel : ReactiveObject, IDisposable
{
    private readonly MonthCalendarModel monthCalendarModel;
    private readonly IWidgetSession? session;
    private DateTime currentDate;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the timer subscription is then gated on the session's activity. Null keeps
    /// the legacy always-on behaviour (preview, legacy layout entries) — those hosts never
    /// push activity either.
    /// </summary>
    public MonthCalendarViewModel(MonthCalendarModel monthCalendarModel, IWidgetSession? session = null)
    {
        this.monthCalendarModel = monthCalendarModel;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        Start();
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        UpdateTickSubscription();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        UpdateTickSubscription();
    }

    /// <summary>
    /// Host activity push (the <see cref="IWidgetActivityConsumer"/> path, invoked on the UI
    /// thread): ticking follows the snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// Session-less view models ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();
    }

    /// <summary>Check seam: the live subscription count over the shared timer (0 or 1).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the control is loaded AND the session renders. Overlapping transitions (a covered
    /// screen while unloaded — the settings window caches pages and the Gallery keeps a live
    /// preview, so the view model is only stopped, never disposed) cannot double-subscribe or
    /// double-unsubscribe — the count is reconciled from both inputs on every change instead
    /// of toggled from each.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = isRunning && renderActive;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            TimerService.Timer5Minutes.Subscribe(UpdateTime);
            UpdateTime();
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer5Minutes.Unsubscribe(UpdateTime);
        }
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