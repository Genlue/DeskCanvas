using Clock.Models;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using Locale = Clock.Locales.Locale;

namespace Clock.ViewModels;

public class AnalogClockViewModel : ReactiveObject, IDisposable
{
    private readonly ClockModel clockModel;
    private readonly UpdateTimer timer;
    private readonly IWidgetSession? session;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>
    /// Session-less widgets (the gallery preview, legacy layout entries without identity) pass
    /// no session and keep the historic always-on behaviour: ticking then only follows
    /// <see cref="Start"/>/<see cref="Stop"/>. With a session, the subscription is gated by
    /// the latest activity snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// </summary>
    public AnalogClockViewModel(ClockModel clockModel, IWidgetSession? session = null)
    {
        this.clockModel = clockModel;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;

        timer = clockModel.ShowSeconds ? TimerService.Timer100Ms : TimerService.Timer5Seconds;
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

    /// <summary>
    /// Single subscription state machine (the Digital pilot's shape): the shared timer
    /// subscription exists exactly while the control is running AND the session renders.
    /// Overlapping transitions cannot double-subscribe or double-unsubscribe — the count is
    /// reconciled from both inputs on every change instead of toggled from each.
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
    
    /// <summary>
    /// Shared timezone resolution (the Runtime copy): a null id is the unset timezone (local),
    /// an unknown id falls back to Utc instead of throwing (the SharedTimeZone contract).
    /// </summary>
    private TimeZoneInfo TimeZoneInfo => SharedTimeZone.Resolve(clockModel.TimeZoneId);

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