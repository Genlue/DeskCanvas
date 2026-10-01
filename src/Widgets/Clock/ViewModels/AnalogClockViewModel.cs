using Clock.Models;
using ReactiveUI;
using DeskCanvas.Services;
using Locale = Clock.Locales.Locale;

namespace Clock.ViewModels;

public class AnalogClockViewModel : ReactiveObject, IDisposable
{
    private readonly ClockModel clockModel;
    private readonly UpdateTimer timer;
    private bool isRunning;
    private TimeZoneInfo? cachedTimeZone;

    public AnalogClockViewModel(ClockModel clockModel)
    {
        this.clockModel = clockModel;

        timer = clockModel.ShowSeconds ? TimerService.Timer100Ms : TimerService.Timer5Seconds;
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
        var time = Time;

        HourHand = new ClockHandViewModel(GetHoursAngle(time), 190, false);
        MinuteHand = new ClockHandViewModel(GetMinutesAngle(time), 365, false);
        SecondHand = new ClockHandViewModel(GetSecondsAngle(time), 460, true, clockModel.ShowSeconds);
        this.RaisePropertyChanged(nameof(DigitalTimeText));
    }

    /// <summary>
    /// 24-hour digital readout for this clock's time zone (used inside the dial
    /// and under the dial on wide layouts).
    /// </summary>
    public string DigitalTimeText => Time.ToString("HH:mm");
    
    private TimeZoneInfo TimeZoneInfo => cachedTimeZone ??= (clockModel.TimeZoneId != null
        ? SafeFindTimeZone(clockModel.TimeZoneId)
        : TimeZoneInfo.Local);

    private static TimeZoneInfo SafeFindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
    }

    private DateTime Time => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo);
        
    public string CityAbbreviation => TimeZoneInfo.Id == TimeZoneInfo.Local.Id 
        ? "" 
        : TimeZoneInfo.DisplayName.Split(") ").Last()[..3].ToUpper();

    private string CityFullName => TimeZoneInfo.DisplayName.Split(") ").Last().Split(", ").First().Split("(").First();

    private string? customCityName;

    /// <summary>
    /// Optional user-provided display name; when set it replaces the city name
    /// derived from the selected time zone (World Clock settings).
    /// </summary>
    public string? CustomCityName
    {
        get => customCityName;
        set
        {
            customCityName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            this.RaisePropertyChanged(nameof(CityName));
        }
    }

    /// <summary>
    /// Display city name: only the user-entered custom name — never the
    /// time-zone-derived city (leave blank in settings to show no name).
    /// </summary>
    public string CityName => customCityName ?? string.Empty;
    
    public string Date => (Time.Date - DateTime.Now.Date).Days switch
    {
        > 0 => Locale.Clock_Tomorrow,
        < 0 => Locale.Clock_Yesterday,
        _ => Locale.Clock_Today
    };

    public TimeSpan TimeZoneDiffInternal(DateTime utcNow) => TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo) - TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo.Local);
    
    public string TimeZoneDiff => TimeZoneDiffInternal(DateTime.UtcNow).Hours switch
    {
        > 0 => $"+{TimeZoneDiffInternal(DateTime.UtcNow):hh\\:mm}",
        < 0 => $"-{TimeZoneDiffInternal(DateTime.UtcNow):hh\\:mm}",
        _ => "00:00"
    };

    private bool showCityName;
    public bool ShowCityName
    {
        get => showCityName;
        set
        {
            ShowCityAbbreviation = !value;
            this.RaiseAndSetIfChanged(ref showCityName, value);
        }
    }

    private bool showCityAbbreviation;
    public bool ShowCityAbbreviation
    {
        get => showCityAbbreviation;
        set => this.RaiseAndSetIfChanged(ref showCityAbbreviation, value);
    }
        
    private ClockHandViewModel? hourHand;
    public ClockHandViewModel? HourHand
    {
        get => hourHand;
        private set => this.RaiseAndSetIfChanged(ref hourHand, value);
    }

    private ClockHandViewModel? minuteHand;
    public ClockHandViewModel? MinuteHand
    {
        get => minuteHand;
        private set => this.RaiseAndSetIfChanged(ref minuteHand, value);
    }

    private ClockHandViewModel? secondHand;
    public ClockHandViewModel? SecondHand
    {
        get => secondHand;
        private set => this.RaiseAndSetIfChanged(ref secondHand, value);
    }

    private static double GetSecondsAngle(DateTime time) => (time.Second + time.Millisecond / 1000.0) * 6;
    private static double GetMinutesAngle(DateTime time) => (time.Minute + time.Second / 60.0) * 6;
    private static double GetHoursAngle(DateTime time) => (time.Hour % 12 + time.Minute / 60.0) * 30;
}