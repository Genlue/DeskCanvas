using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Progress.Models;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Progress.ViewModels;

public class ProgressViewModel : ReactiveObject, IDisposable
{
    private ProgressModel model;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly IWidgetSession? session;
    private readonly Visual? themeAnchor;
    private readonly UpdateTimer timer;
    private bool isRunning;
    private bool renderActive;
    private int tickSubscriptions;

    /// <summary>Set once <see cref="Dispose"/> ran; a disposed view model must not restart.</summary>
    private bool disposed;

    public ProgressViewModel(
        ProgressModel model,
        IAppSettingsProvider? appSettingsProvider = null,
        IWidgetSession? session = null,
        Visual? themeAnchor = null)
    {
        this.model = model;
        this.appSettingsProvider = appSettingsProvider;
        this.session = session;
        this.themeAnchor = themeAnchor;
        renderActive = session?.Activity.RenderActive ?? true;
        timer = TimerService.Timer5Seconds;

        if (this.appSettingsProvider != null)
        {
            this.appSettingsProvider.DataChanged += OnAppSettingsChanged;
        }

        UpdateProgress();
    }

    private void OnAppSettingsChanged(object sender, DeskCanvas.Core.Models.Settings.AppSettings? oldData, DeskCanvas.Core.Models.Settings.AppSettings newData)
    {
        if (model.FollowAccentColor)
        {
            UpdateBrushes();
        }
    }

    public void UpdateModel(ProgressModel newModel)
    {
        model = newModel;
        UpdateProgress();
    }

    public void Start()
    {
        if (isRunning || disposed) return;
        isRunning = true;
        UpdateTickSubscription();
        // Always refresh once on activation, even when the session keeps the timer gated off:
        // the derived state must not depend on whether a tick is about to arrive.
        UpdateProgress();
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

    /// <summary>Check seam: the last activity gate value the view model reconciled against.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>
    /// Stop the five-second tick and detach from the process-lifetime app-settings event,
    /// which otherwise keeps every abandoned view model (each settings save re-creates the
    /// view) alive until the process ends. The view must build a fresh view model before
    /// using this one again. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        if (appSettingsProvider != null)
        {
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;
        }
        GC.SuppressFinalize(this);
    }

    private void OnTimerTick()
    {
        UpdateProgress();
    }

    /// <summary>
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the control is running AND the session renders. Session-less view models render always
    /// (<paramref name="renderActive"/> starts true and nothing flips it). Overlapping
    /// transitions (a covered screen while unloaded) cannot double-subscribe or
    /// double-unsubscribe — the count is reconciled from both inputs on every change instead
    /// of toggled from each.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = isRunning && renderActive;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            timer.Subscribe(OnTimerTick);
            UpdateProgress();
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            timer.Unsubscribe(OnTimerTick);
        }
    }

    private string titleText = "";
    public string TitleText
    {
        get => titleText;
        private set => this.RaiseAndSetIfChanged(ref titleText, value);
    }

    private string countText = "";
    public string CountText
    {
        get => countText;
        private set => this.RaiseAndSetIfChanged(ref countText, value);
    }

    private string percentageText = "";
    public string PercentageText
    {
        get => percentageText;
        private set => this.RaiseAndSetIfChanged(ref percentageText, value);
    }

    private int totalDots = 365;
    public int TotalDots
    {
        get => totalDots;
        private set => this.RaiseAndSetIfChanged(ref totalDots, value);
    }

    private int passedDots = 0;
    public int PassedDots
    {
        get => passedDots;
        private set => this.RaiseAndSetIfChanged(ref passedDots, value);
    }

    private bool hasCurrentDot = true;
    public bool HasCurrentDot
    {
        get => hasCurrentDot;
        private set => this.RaiseAndSetIfChanged(ref hasCurrentDot, value);
    }

    private DotShape dotShape = DotShape.Circle;
    public DotShape DotShape
    {
        get => dotShape;
        private set => this.RaiseAndSetIfChanged(ref dotShape, value);
    }

    private int preferredColumns = 0;
    public int PreferredColumns
    {
        get => preferredColumns;
        private set => this.RaiseAndSetIfChanged(ref preferredColumns, value);
    }

    private bool showHeader = true;
    public bool ShowHeader
    {
        get => showHeader;
        private set => this.RaiseAndSetIfChanged(ref showHeader, value);
    }

    private bool showPercentage = true;
    public bool ShowPercentage
    {
        get => showPercentage;
        private set => this.RaiseAndSetIfChanged(ref showPercentage, value);
    }

    private bool showCount = true;
    public bool ShowCount
    {
        get => showCount;
        private set => this.RaiseAndSetIfChanged(ref showCount, value);
    }

    private IBrush? passedBrush;
    public IBrush? PassedBrush
    {
        get => passedBrush;
        private set => this.RaiseAndSetIfChanged(ref passedBrush, value);
    }

    private IBrush? currentBrush;
    public IBrush? CurrentBrush
    {
        get => currentBrush;
        private set => this.RaiseAndSetIfChanged(ref currentBrush, value);
    }

    private IBrush? remainingBrush;
    public IBrush? RemainingBrush
    {
        get => remainingBrush;
        private set => this.RaiseAndSetIfChanged(ref remainingBrush, value);
    }

    private Func<int, string>? dotTooltipFunc;
    public Func<int, string>? DotTooltipFunc
    {
        get => dotTooltipFunc;
        private set => this.RaiseAndSetIfChanged(ref dotTooltipFunc, value);
    }

    private void UpdateBrushes()
    {
        Color passedColor = Color.Parse("#BDB2FF"); // Soft lavender fallback
        var theme = appSettingsProvider?.Get()?.Theme;
        if (model.FollowAccentColor)
        {
            if (theme != null && theme.IsColorful)
            {
                passedColor = Color.Parse("#30D158"); // Apple Fitness / Activity vibrant green
            }
            else
            {
                var accentHex = theme?.AccentColor;
                if (!string.IsNullOrEmpty(accentHex) && Color.TryParse(accentHex, out var parsed))
                {
                    passedColor = parsed;
                }
                else
                {
                    passedColor = Color.Parse("#7B61FF");
                }
            }
        }
        else if (!string.IsNullOrEmpty(model.PassedColor) && Color.TryParse(model.PassedColor, out var parsedPassed))
        {
            passedColor = parsedPassed;
        }
        PassedBrush = new SolidColorBrush(passedColor);

        Color curColor = Color.Parse("#FF2A85"); // Vibrant magenta for current highlight
        if (model.FollowAccentColor && theme != null && theme.IsColorful)
        {
            curColor = Color.Parse("#FF375F"); // Apple Fitness vibrant red
        }
        else if (!string.IsNullOrEmpty(model.CurrentColor) && Color.TryParse(model.CurrentColor, out var parsedCur))
        {
            curColor = parsedCur;
        }
        CurrentBrush = new SolidColorBrush(curColor);

        // Per-screen: the variant the anchor's window renders with. A missing theme flag
        // (follow-the-system / AutoTheme) and an unresolvable anchor both fall through to
        // ResolveProgressThemeVariant's uniform fallback — the pre-per-screen behaviour.
        bool isDark = theme?.DarkMode switch
        {
            true => true,
            false => false,
            null => ResolveProgressThemeVariant(themeAnchor) == ThemeVariant.Dark
        };

        Color remColor = isDark
            ? Color.FromArgb(50, 255, 255, 255) // Soft light dots for dark background
            : Color.FromArgb(38, 0, 0, 0);       // Soft dark dots for light background (Apple Gray 5)
        if (!string.IsNullOrEmpty(model.RemainingColor) && Color.TryParse(model.RemainingColor, out var parsedRem))
        {
            remColor = parsedRem;
        }
        RemainingBrush = new SolidColorBrush(remColor);
    }

    // ---------- Per-screen theme seam (pure, testable) ----------

    /// <summary>
    /// Check seam: replaces the live visual→variant mapping so headless checks can exercise
    /// the per-screen path without a window, a screen walk or host services. Production code
    /// never touches this; the fixture sets and clears it around each assertion.
    /// </summary>
    internal static Func<Visual, ThemeVariant?>? WindowVariantResolverForChecks;

    /// <summary>
    /// The theme variant the given visual renders with, per screen: the effective theme's
    /// explicit DarkMode flag when set (the focus-link feature themes screens independently),
    /// otherwise the visual's live window variant — the same reading the uniform path took.
    /// A visual that cannot resolve a theme or a window (null anchor, 组件库 preview,
    /// headless context) falls back to the uniform application variant, which is exactly the
    /// global reading this replaced.
    /// </summary>
    internal static ThemeVariant ResolveProgressThemeVariant(Visual? visual)
    {
        if (visual != null)
        {
            if (WindowVariantResolverForChecks?.Invoke(visual) is { } injected)
            {
                return injected;
            }

            var theme = FocusThemeCoordinator.ResolveVisualTheme(visual);
            if (theme?.DarkMode is { } dark)
            {
                return dark ? ThemeVariant.Dark : ThemeVariant.Light;
            }

            if (visual.GetVisualRoot() is Control { ActualThemeVariant: { } root }
                && root != ThemeVariant.Default)
            {
                return root;
            }
        }

        // Uniform fallback: no window to resolve against — the old global application reading.
        return Application.Current?.ActualThemeVariant ?? ThemeVariant.Light;
    }

    public void UpdateProgress()
    {
        var now = DateTime.Now;
        ShowHeader = model.ShowHeader;
        ShowPercentage = model.ShowPercentage;
        ShowCount = model.ShowCount;
        DotShape = model.DotShape;

        UpdateBrushes();

        switch (model.Mode)
        {
            case ProgressMode.Year:
                UpdateYearProgress(now);
                break;
            case ProgressMode.Month:
                UpdateMonthProgress(now);
                break;
            case ProgressMode.Week:
                UpdateWeekProgress(now);
                break;
            case ProgressMode.Day:
                UpdateDayProgress(now);
                break;
            case ProgressMode.Life:
                UpdateLifeProgress(now);
                break;
        }
    }

    private static bool IsZh => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private void UpdateYearProgress(DateTime now)
    {
        int daysInYear = DateTime.IsLeapYear(now.Year) ? 366 : 365;
        int currentDay = now.DayOfYear; // 1-based
        int passed = currentDay - 1;

        TotalDots = daysInYear;
        PassedDots = passed;
        HasCurrentDot = true;
        PreferredColumns = 0; // Auto-balance columns

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : now.Year.ToString();

        CountText = IsZh ? $"{currentDay} / {daysInYear}" : $"{currentDay} / {daysInYear} d";
        PercentageText = $"{((double)currentDay / daysInYear * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int dayNum = index + 1;
            var dt = new DateTime(now.Year, 1, 1).AddDays(index);
            if (IsZh)
            {
                string status = index < passed ? "已过" : (index == passed ? "今天" : "未到");
                return $"第 {dayNum} 天 · {dt:M月d日} ({status})";
            }
            else
            {
                string status = index < passed ? "Passed" : (index == passed ? "Today" : "Upcoming");
                return $"Day {dayNum} · {dt.ToString("MMM d", CultureInfo.CurrentUICulture)} ({status})";
            }
        };
    }

    private void UpdateMonthProgress(DateTime now)
    {
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);
        int currentDay = now.Day; // 1-based
        int passed = currentDay - 1;

        TotalDots = daysInMonth;
        PassedDots = passed;
        HasCurrentDot = true;
        PreferredColumns = 7; // Calendar-style 7 columns

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : (IsZh ? $"{now.Year}年{now.Month}月" : now.ToString("MMM yyyy", CultureInfo.CurrentUICulture));

        CountText = IsZh ? $"{currentDay} / {daysInMonth}" : $"{currentDay} / {daysInMonth} d";
        PercentageText = $"{((double)currentDay / daysInMonth * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int dayNum = index + 1;
            var dt = new DateTime(now.Year, now.Month, dayNum);
            if (IsZh)
            {
                string status = index < passed ? "已过" : (index == passed ? "今天" : "未到");
                return $"{dt:M月d日} · 星期{GetDayOfWeekName(dt.DayOfWeek)} ({status})";
            }
            else
            {
                string status = index < passed ? "Passed" : (index == passed ? "Today" : "Upcoming");
                return $"{dt.ToString("MMM d", CultureInfo.CurrentUICulture)} · {GetDayOfWeekName(dt.DayOfWeek)} ({status})";
            }
        };
    }

    private void UpdateWeekProgress(DateTime now)
    {
        // Monday is 0, Sunday is 6
        int dayOfWeekIndex = (int)now.DayOfWeek == 0 ? 6 : (int)now.DayOfWeek - 1;
        int currentDayNum = dayOfWeekIndex + 1;

        TotalDots = 7;
        PassedDots = dayOfWeekIndex;
        HasCurrentDot = true;
        PreferredColumns = 7;

        var cal = CultureInfo.CurrentUICulture.Calendar;
        int weekNum = cal.GetWeekOfYear(now, CalendarWeekRule.FirstDay, DayOfWeek.Monday);

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : (IsZh ? $"第 {weekNum} 周" : $"Week {weekNum}");

        CountText = IsZh ? $"{currentDayNum} / 7" : $"{currentDayNum} / 7 d";
        PercentageText = $"{((double)currentDayNum / 7.0 * 100.0):F1}%";

        var weekStart = now.Date.AddDays(-dayOfWeekIndex);
        DotTooltipFunc = index =>
        {
            var dt = weekStart.AddDays(index);
            if (IsZh)
            {
                string status = index < dayOfWeekIndex ? "已过" : (index == dayOfWeekIndex ? "今天" : "未到");
                return $"星期{GetDayOfWeekName((DayOfWeek)((index + 1) % 7))} · {dt:M月d日} ({status})";
            }
            else
            {
                string status = index < dayOfWeekIndex ? "Passed" : (index == dayOfWeekIndex ? "Today" : "Upcoming");
                return $"{GetDayOfWeekName((DayOfWeek)((index + 1) % 7))} · {dt.ToString("MMM d", CultureInfo.CurrentUICulture)} ({status})";
            }
        };
    }

    private void UpdateDayProgress(DateTime now)
    {
        int total;
        int stepMinutes;
        int preferredCol;

        switch (model.DayGranularity)
        {
            case DayGranularity.Minutes5:
                total = 288;
                stepMinutes = 5;
                preferredCol = 18;
                break;
            case DayGranularity.Minutes10:
                total = 144;
                stepMinutes = 10;
                preferredCol = 12; // 12x12
                break;
            case DayGranularity.Minutes15:
                total = 96;
                stepMinutes = 15;
                preferredCol = 12;
                break;
            case DayGranularity.Minutes30:
                total = 48;
                stepMinutes = 30;
                preferredCol = 8;
                break;
            case DayGranularity.Hour1:
            default:
                total = 24;
                stepMinutes = 60;
                preferredCol = 6;
                break;
        }

        int currentMinutes = now.Hour * 60 + now.Minute;
        int currentSlot = Math.Min(currentMinutes / stepMinutes, total - 1);

        TotalDots = total;
        PassedDots = currentSlot;
        HasCurrentDot = true;
        PreferredColumns = preferredCol;

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : (IsZh ? $"{now:M月d日} 周{GetDayOfWeekName(now.DayOfWeek)}" : $"{now.ToString("MMM d", CultureInfo.CurrentUICulture)} {GetDayOfWeekName(now.DayOfWeek)}");

        CountText = $"{currentSlot + 1} / {total}";
        PercentageText = $"{((double)(currentSlot + 1) / total * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int startMin = index * stepMinutes;
            int endMin = (index + 1) * stepMinutes;
            string startStr = $"{startMin / 60:D2}:{startMin % 60:D2}";
            string endStr = $"{endMin / 60:D2}:{endMin % 60:D2}";
            string status = index < currentSlot ? (IsZh ? "已过" : "Passed") : (index == currentSlot ? (IsZh ? "当前" : "Now") : (IsZh ? "未到" : "Upcoming"));
            return $"{startStr} ~ {endStr} ({status})";
        };
    }

    private void UpdateLifeProgress(DateTime now)
    {
        int totalMonths = Math.Max(12, model.LifeExpectancyYears * 12);
        int passedMonths = 0;

        if (!string.IsNullOrEmpty(model.BirthDate) && DateTime.TryParse(model.BirthDate, out var birth))
        {
            passedMonths = ((now.Year - birth.Year) * 12) + now.Month - birth.Month;
            if (passedMonths < 0) passedMonths = 0;
            if (passedMonths > totalMonths) passedMonths = totalMonths;
        }
        else
        {
            // Default 25 years if birthdate not configured
            passedMonths = 25 * 12;
        }

        TotalDots = totalMonths;
        PassedDots = passedMonths;
        HasCurrentDot = true;
        PreferredColumns = 30; // 30 months per row, or auto

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : (IsZh ? "人生进度" : "Life Progress");

        CountText = IsZh ? $"{passedMonths} / {totalMonths} 月" : $"{passedMonths} / {totalMonths} mo";
        PercentageText = $"{((double)passedMonths / totalMonths * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int ageYears = index / 12;
            int ageMonths = index % 12;
            if (IsZh)
            {
                string status = index < passedMonths ? "已度过" : (index == passedMonths ? "此刻" : "未来");
                return $"{ageYears} 岁 {ageMonths + 1} 个月 ({status})";
            }
            else
            {
                string status = index < passedMonths ? "Passed" : (index == passedMonths ? "Now" : "Upcoming");
                return $"{ageYears}y {ageMonths + 1}m ({status})";
            }
        };
    }

    private static string GetDayOfWeekName(DayOfWeek day) => IsZh
        ? day switch
        {
            DayOfWeek.Monday => "一",
            DayOfWeek.Tuesday => "二",
            DayOfWeek.Wednesday => "三",
            DayOfWeek.Thursday => "四",
            DayOfWeek.Friday => "五",
            DayOfWeek.Saturday => "六",
            DayOfWeek.Sunday => "日",
            _ => ""
        }
        : day switch
        {
            DayOfWeek.Monday => "Mon",
            DayOfWeek.Tuesday => "Tue",
            DayOfWeek.Wednesday => "Wed",
            DayOfWeek.Thursday => "Thu",
            DayOfWeek.Friday => "Fri",
            DayOfWeek.Saturday => "Sat",
            DayOfWeek.Sunday => "Sun",
            _ => ""
        };
}
