using System;
using System.Globalization;
using System.Threading;
using Clock.Models;
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
    private bool isRunning;
    private TimeZoneInfo? cachedTimeZone;

    public event Action? SecondTicked;

    public TickDialClockViewModel(ClockModel clockModel)
    {
        this.clockModel = clockModel;
        timer = ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;
        Start();
    }

    public void Start()
    {
        if (isRunning) return;
        isRunning = true;
        timer.Subscribe(UpdateTime);
        UpdateTime();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        timer.Unsubscribe(UpdateTime);
    }

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

    private TimeZoneInfo TimeZoneInfo => cachedTimeZone ??= (clockModel.TimeZoneId != null
        ? SafeFindTimeZone(clockModel.TimeZoneId)
        : TimeZoneInfo.Local);

    private static TimeZoneInfo SafeFindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
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
