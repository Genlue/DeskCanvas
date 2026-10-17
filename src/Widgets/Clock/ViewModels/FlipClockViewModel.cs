using System;
using System.Globalization;
using System.Threading;
using Clock.Models;
using DeskCanvas.Contracts;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using ReactiveUI;

namespace Clock.ViewModels;

public class FlipClockViewModel : ReactiveObject, IDisposable
{
    private FlipClockModel model;
    private readonly UpdateTimer timer;
    private readonly IWidgetSession? session;
    private bool isRunning;
    private bool renderActive = true;
    private int tickSubscriptions;

    private string hourText = "00";
    private string minuteText = "00";
    private string secondText = "00";
    private string amPmText = string.Empty;
    private string dateBarText = string.Empty;
    private string cardDateBadgeText = string.Empty;
    private string barRightText = string.Empty;

    /// <summary>
    /// Session-less widgets (the gallery preview, legacy layout entries without identity) pass
    /// no session and keep the historic always-on behaviour: ticking then only follows
    /// <see cref="Start"/>/<see cref="Stop"/>. With a session, the subscription is gated by
    /// the latest activity snapshot's <see cref="ActivitySnapshot.RenderActive"/>.
    /// </summary>
    public FlipClockViewModel(FlipClockModel model, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        // Always subscribe to Timer1Second so minute flips happen on the exact :00 second boundary.
        timer = TimerService.Timer1Second;
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

    public void UpdateModel(FlipClockModel newModel)
    {
        model = newModel;
        UpdateTime();

        this.RaisePropertyChanged(nameof(ShowTopBar));
        this.RaisePropertyChanged(nameof(ShowBottomBar));
        this.RaisePropertyChanged(nameof(ShowThirdFlipCard));
        this.RaisePropertyChanged(nameof(CardWidth));
        this.RaisePropertyChanged(nameof(CardHeight));
        this.RaisePropertyChanged(nameof(CardSpacing));
        this.RaisePropertyChanged(nameof(DigitFontSize));
        this.RaisePropertyChanged(nameof(FlapCornerRadius));
        this.RaisePropertyChanged(nameof(BadgeFontSize));
        this.RaisePropertyChanged(nameof(HourTopLeftBadge));
        this.RaisePropertyChanged(nameof(MinuteTopRightBadge));
        this.RaisePropertyChanged(nameof(SecondTopRightBadge));
        this.RaisePropertyChanged(nameof(MinuteBottomRightBadge));
    }

    /// <summary>
    /// Shared timezone resolution (the Runtime copy): a null id is the unset timezone (local),
    /// an unknown id falls back to Utc instead of throwing (the SharedTimeZone contract).
    /// </summary>
    private TimeZoneInfo ActiveTimeZone => SharedTimeZone.Resolve(model.TimeZoneId);

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

    private void UpdateTime()
    {
        var now = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, ActiveTimeZone);
        var culture = Thread.CurrentThread.CurrentUICulture;

        var hFormat = model.Use24Hours ? "HH" : "hh";
        var nextH = now.ToString(hFormat, CultureInfo.InvariantCulture);
        var nextM = now.ToString("mm", CultureInfo.InvariantCulture);
        var nextS = now.ToString("ss", CultureInfo.InvariantCulture);
        var nextAmPm = model.Use24Hours ? string.Empty : (now.Hour >= 12 ? "PM" : "AM");

        string nextDateBar;
        string nextCardDate;
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            var ddd = now.ToString("ddd", culture);
            nextDateBar = $"{now.Month}月{now.Day}日 {ddd}";
            nextCardDate = $"{now.Month}/{now.Day} {ddd}";
        }
        else
        {
            nextDateBar = now.ToString("ddd, MMM d", culture);
            nextCardDate = now.ToString("MMM d", culture);
        }

        var nextBarRight = model.Use24Hours ? "24H" : nextAmPm;

        var amPmChanged = amPmText != nextAmPm;
        var secondChanged = secondText != nextS;
        var cardDateChanged = cardDateBadgeText != nextCardDate;

        HourText = nextH;
        MinuteText = nextM;
        SecondText = nextS;
        AmPmText = nextAmPm;
        DateBarText = nextDateBar;
        CardDateBadgeText = nextCardDate;
        BarRightText = nextBarRight;

        if (amPmChanged)
            this.RaisePropertyChanged(nameof(HourTopLeftBadge));

        if (cardDateChanged)
        {
            this.RaisePropertyChanged(nameof(MinuteTopRightBadge));
            this.RaisePropertyChanged(nameof(SecondTopRightBadge));
        }

        if (secondChanged && model.ShowSeconds && model.SecondsStyle == FlipSecondsStyle.Badge)
            this.RaisePropertyChanged(nameof(MinuteBottomRightBadge));
    }

    public string HourText
    {
        get => hourText;
        private set => this.RaiseAndSetIfChanged(ref hourText, value);
    }

    public string MinuteText
    {
        get => minuteText;
        private set => this.RaiseAndSetIfChanged(ref minuteText, value);
    }

    public string SecondText
    {
        get => secondText;
        private set => this.RaiseAndSetIfChanged(ref secondText, value);
    }

    public string AmPmText
    {
        get => amPmText;
        private set => this.RaiseAndSetIfChanged(ref amPmText, value);
    }

    public string DateBarText
    {
        get => dateBarText;
        private set => this.RaiseAndSetIfChanged(ref dateBarText, value);
    }

    public string CardDateBadgeText
    {
        get => cardDateBadgeText;
        private set => this.RaiseAndSetIfChanged(ref cardDateBadgeText, value);
    }

    public string BarRightText
    {
        get => barRightText;
        private set => this.RaiseAndSetIfChanged(ref barRightText, value);
    }

    public bool ShowTopBar => model.ShowDate && model.DatePosition == FlipDatePosition.TopBar;

    public bool ShowBottomBar => model.ShowDate && model.DatePosition == FlipDatePosition.BottomBar;

    public bool ShowThirdFlipCard => model.ShowSeconds && model.SecondsStyle == FlipSecondsStyle.FlipCard;

    private bool HasOuterBar => ShowTopBar || ShowBottomBar;

    // 2-card: 157 * 2 + 14 = 328 DIP; 3-card: 102 * 3 + 11 * 2 = 328 DIP.
    public double CardWidth => ShowThirdFlipCard ? 102.0 : 157.0;

    public double CardHeight => ShowThirdFlipCard
        ? (HasOuterBar ? 108.0 : 128.0)
        : (HasOuterBar ? 110.0 : 132.0);

    public double CardSpacing => ShowThirdFlipCard ? 11.0 : 14.0;

    public double DigitFontSize => ShowThirdFlipCard
        ? (HasOuterBar ? 54.0 : 62.0)
        : (HasOuterBar ? 68.0 : 80.0);

    public double FlapCornerRadius => ShowThirdFlipCard
        ? (HasOuterBar ? 12.0 : 13.0)
        : (HasOuterBar ? 14.0 : 15.0);

    public double BadgeFontSize => ShowThirdFlipCard ? 9.5 : 10.5;

    // When TopBar/BottomBar is visible, AM/PM is already shown on the right of the bar;
    // only show the internal top-left AM/PM badge on the Hour card when no outer bar is shown.
    public string? HourTopLeftBadge => (model.Use24Hours || HasOuterBar) ? null : AmPmText;

    public string? MinuteTopRightBadge =>
        (model.ShowDate && model.DatePosition == FlipDatePosition.CardOverlay && !ShowThirdFlipCard)
            ? CardDateBadgeText
            : null;

    public string? SecondTopRightBadge =>
        (model.ShowDate && model.DatePosition == FlipDatePosition.CardOverlay && ShowThirdFlipCard)
            ? CardDateBadgeText
            : null;

    public string? MinuteBottomRightBadge =>
        (model.ShowSeconds && model.SecondsStyle == FlipSecondsStyle.Badge)
            ? SecondText
            : null;
}
