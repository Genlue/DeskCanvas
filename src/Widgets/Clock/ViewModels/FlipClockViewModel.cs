using System;
using System.Globalization;
using System.Threading;
using Clock.Models;
using DeskCanvas.Services;
using ReactiveUI;

namespace Clock.ViewModels;

public class FlipClockViewModel : ReactiveObject, IDisposable
{
    private FlipClockModel model;
    private readonly UpdateTimer timer;
    private bool isRunning;
    private TimeZoneInfo? cachedTimeZone;

    private string hourText = "00";
    private string minuteText = "00";
    private string secondText = "00";
    private string amPmText = string.Empty;
    private string dateBarText = string.Empty;
    private string cardDateBadgeText = string.Empty;
    private string barRightText = string.Empty;

    public FlipClockViewModel(FlipClockModel model)
    {
        this.model = model;
        // Always subscribe to Timer1Second so minute flips happen on the exact :00 second boundary.
        timer = TimerService.Timer1Second;
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

    public void UpdateModel(FlipClockModel newModel)
    {
        model = newModel;
        cachedTimeZone = null;
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

    private TimeZoneInfo ActiveTimeZone => cachedTimeZone ??= (model.TimeZoneId != null
        ? SafeFindTimeZone(model.TimeZoneId)
        : TimeZoneInfo.Local);

    private static TimeZoneInfo SafeFindTimeZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch { return TimeZoneInfo.Local; }
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
