using Clock.Models;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

namespace Clock.ViewModels;

public class DigitalClockViewModel : ReactiveObject, IDisposable
{
    private readonly ClockModel clockModel;
    private readonly UpdateTimer timer;
    private readonly IWidgetSession? session;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;
    private TimeZoneInfo? cachedTimeZone;

    /// <summary>
    /// Session-less widgets (the gallery preview, legacy layout entries without identity) pass
    /// no session and keep the historic always-on behaviour: ticking then only follows
    /// <see cref="Start"/>/<see cref="Stop"/>. With a session, the subscription is gated by
    /// the latest activity snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// </summary>
    public DigitalClockViewModel(ClockModel clockModel, IWidgetSession? session = null)
    {
        this.clockModel = clockModel;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        timer = clockModel.ShowSeconds ? TimerService.Timer1Second : TimerService.Timer1Minute;
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
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the control is loaded AND the session renders. Overlapping transitions (a covered
    /// screen while unloaded) cannot double-subscribe or double-unsubscribe — the count is
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
        Time = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo);

        this.RaisePropertyChanged(nameof(TimeText));
    }

    private TimeZoneInfo TimeZoneInfo => cachedTimeZone ??= SharedTimeZone.Resolve(clockModel.TimeZoneId);

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
