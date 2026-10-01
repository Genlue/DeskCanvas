using Clock.Models;
using ReactiveUI;
using DeskCanvas.Services;

namespace Clock.ViewModels;

public class DigitalClockViewModel : ReactiveObject, IDisposable
{
    private readonly ClockModel clockModel;
    private readonly UpdateTimer timer;
    private bool isRunning;
    private TimeZoneInfo? cachedTimeZone;

    public DigitalClockViewModel(ClockModel clockModel)
    {
        this.clockModel = clockModel;
        timer = clockModel.ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;
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

    private string HH => clockModel.Use24Hours ? "HH" : "hh";
    private string SS => clockModel.ShowSeconds ? ":ss" : "";
    private string AM => clockModel.Use24Hours ? "" : " tt";

    public string TimeText => Time.ToString($"{HH}:mm{SS}{AM}");
    public string DateText => Time.ToString("D", Thread.CurrentThread.CurrentUICulture); 
    public bool ShowDate => clockModel.ShowDate;
}