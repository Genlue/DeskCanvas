using System;
using Calendar.Services;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace Calendar.ViewModels;

public class DateCalendarViewModel : ReactiveObject, IDisposable
{
    private readonly IWidgetSession? session;
    private readonly bool showLunar;
    private DateTime currentDate;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the timer subscription is then gated on the session's activity. Null keeps
    /// the legacy always-on behaviour (preview, legacy layout entries) — those hosts never
    /// push activity either.
    /// <paramref name="showLunar"/> is read from <see cref="Calendar.Models.DateModel"/> once:
    /// changing the setting recreates the widget content, so it does not need notifications.
    /// </summary>
    public DateCalendarViewModel(IWidgetSession? session = null, bool showLunar = true)
    {
        this.session = session;
        this.showLunar = showLunar;
        renderActive = session?.Activity.RenderActive ?? true;
        Start();
    }

    /// <summary>The 显示农历 setting, consumed by the date face binding.</summary>
    public bool ShowLunar => showLunar;

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
    /// screen while unloaded — the view is only stopped, never disposed, so it can be
    /// re-attached later) cannot double-subscribe or double-unsubscribe — the count is
    /// reconciled from both inputs on every change instead of toggled from each.
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
        var weekday = format.GetAbbreviatedDayName(now.DayOfWeek).Replace(".", "");
        var month = format.GetAbbreviatedMonthName(now.Month).Replace(".", "");
        DayOfWeek = weekday;
        Month = month;
        Header = $"{weekday} {month}";
        Day = now.Day.ToString();
        LunarDate = LunarCalendarHelper.FormatLunarDate(now);
    }
    
    private string? month;
    public string? Month 
    {
        get => month;
        private set => this.RaiseAndSetIfChanged(ref month, value);
    }

    private string? dayOfWeek;
    public string? DayOfWeek
    {
        get => dayOfWeek;
        private set => this.RaiseAndSetIfChanged(ref dayOfWeek, value);
    }

    private string? header;
    /// <summary>Compact header line used by the 日期 face: "周二 2月".</summary>
    public string? Header
    {
        get => header;
        private set => this.RaiseAndSetIfChanged(ref header, value);
    }

    private string? lunarDate;
    /// <summary>Chinese lunisolar date such as "腊月十六".</summary>
    public string? LunarDate
    {
        get => lunarDate;
        private set => this.RaiseAndSetIfChanged(ref lunarDate, value);
    }

    private string? day;
    public string? Day
    {
        get => day;
        private set => this.RaiseAndSetIfChanged(ref day, value);
    }
}