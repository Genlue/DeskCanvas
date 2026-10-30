using System;
using System.Globalization;
using System.Threading;
using Clock.Models;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using ReactiveUI;

namespace Clock.ViewModels;

/// <summary>
/// ViewModel for 表盘-3 (New Dial 3 / Tick-ring Digital Clock).
/// Exposes 24h/12h digital time, date/weekday, Chinese lunar date, and current second
/// for driving the outer 60-tick ring's sweeping comet illumination.
/// </summary>
public class TickDialClockViewModel : ReactiveObject, IDisposable
{
    private static readonly ChineseLunisolarCalendar LunisolarCalendar = new();

    private static readonly string[] LunarMonths =
        ["正月", "二月", "三月", "四月", "五月", "六月", "七月", "八月", "九月", "十月", "冬月", "腊月"];

    private static readonly string[] LunarDays =
    [
        "初一", "初二", "初三", "初四", "初五", "初六", "初七", "初八", "初九", "初十",
        "十一", "十二", "十三", "十四", "十五", "十六", "十七", "十八", "十九", "二十",
        "廿一", "廿二", "廿三", "廿四", "廿五", "廿六", "廿七", "廿八", "廿九", "三十"
    ];

    private readonly ClockModel clockModel;
    private readonly UpdateTimer timer;
    private readonly IWidgetSession? session;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    public event Action? SecondTicked;

    /// <summary>
    /// Session-less widgets (the gallery preview, legacy layout entries without identity) pass
    /// no session and keep the historic always-on behaviour: ticking then only follows
    /// <see cref="Start"/>/<see cref="Stop"/>. With a session, the subscription is gated by
    /// the latest activity snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// </summary>
    public TickDialClockViewModel(ClockModel clockModel, IWidgetSession? session = null)
    {
        this.clockModel = clockModel;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        timer = ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;
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

    /// <summary>Check seam: whether the shared timer subscription is currently live.</summary>
    public bool IsTickingForChecks => tickSubscriptions > 0;

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private void UpdateTime()
    {
        Time = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo);

        this.RaisePropertyChanged(nameof(TimeText));
        this.RaisePropertyChanged(nameof(DateText));
        this.RaisePropertyChanged(nameof(LunarText));
        this.RaisePropertyChanged(nameof(CurrentSecond));

        SecondTicked?.Invoke();
    }

    /// <summary>
    /// Shared timezone resolution (the Runtime copy): a null id is the unset timezone (local),
    /// an unknown id falls back to Utc instead of throwing (the SharedTimeZone contract).
    /// </summary>
    private TimeZoneInfo TimeZoneInfo => SharedTimeZone.Resolve(clockModel.TimeZoneId);

    /// <summary>
    /// Single subscription state machine (the Digital pilot's shape): the shared timer
    /// subscription exists exactly while the control is running AND the session renders.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = isRunning && renderActive;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            timer.Subscribe(UpdateTime);
            UpdateTime();
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            timer.Unsubscribe(UpdateTime);
        }
    }

    private DateTime time;
    public DateTime Time
    {
        get => time;
        private set => this.RaiseAndSetIfChanged(ref time, value);
    }

    public int CurrentSecond => Time.Second;

    public bool ShowSeconds => clockModel.ShowSecondsTrail ?? true;

    public bool ShowDate => clockModel.ShowDateAndLunar ?? true;

    public string TimeText => Time.ToString(clockModel.Use24Hours ? "HH:mm" : "h:mm");

    public string DateText => Time.ToString("M/d ddd", Thread.CurrentThread.CurrentUICulture);

    public string LunarText => GetLunarDateString(Time);

    public static string GetLunarDateString(DateTime dt)
    {
        try
        {
            if (dt.Year < 1901 || dt.Year > 2100) return string.Empty;

            var currentCulture = Thread.CurrentThread.CurrentUICulture;
            if (!currentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
                return dt.ToString("MMMM yyyy", currentCulture);

            int year = LunisolarCalendar.GetYear(dt);
            int month = LunisolarCalendar.GetMonth(dt);
            int day = LunisolarCalendar.GetDayOfMonth(dt);
            int leapMonth = LunisolarCalendar.GetLeapMonth(year);

            bool isLeap = leapMonth > 0 && month == leapMonth;
            if (leapMonth > 0 && month >= leapMonth)
            {
                if (month == leapMonth)
                    isLeap = true;
                else
                    month--;
            }

            if (month < 1 || month > 12 || day < 1 || day > 30)
                return string.Empty;

            string monthStr = (isLeap ? "闰" : "") + LunarMonths[month - 1];
            string dayStr = LunarDays[day - 1];
            return $"{monthStr}{dayStr}";
        }
        catch
        {
            return string.Empty;
        }
    }
}
