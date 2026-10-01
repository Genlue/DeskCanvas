using System;
using System.Globalization;
using Avalonia.Media;
using Progress.Models;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;

namespace Progress.ViewModels;

public class ProgressViewModel : ReactiveObject, IDisposable
{
    private ProgressModel model;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly UpdateTimer timer;
    private bool isRunning;

    /// <summary>Set once <see cref="Dispose"/> ran; a disposed view model must not restart.</summary>
    private bool disposed;

    public ProgressViewModel(ProgressModel model, IAppSettingsProvider? appSettingsProvider = null)
    {
        this.model = model;
        this.appSettingsProvider = appSettingsProvider;
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
        timer.Subscribe(OnTimerTick);
        UpdateProgress();
    }

    public void Stop()
    {
        if (!isRunning) return;
        isRunning = false;
        timer.Unsubscribe(OnTimerTick);
    }

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

        bool isDark = theme?.DarkMode switch
        {
            true => true,
            false => false,
            null => Avalonia.Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark
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

        CountText = $"{currentDay} / {daysInYear}";
        PercentageText = $"{((double)currentDay / daysInYear * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int dayNum = index + 1;
            var dt = new DateTime(now.Year, 1, 1).AddDays(index);
            string status = index < passed ? "已过" : (index == passed ? "今天" : "未到");
            return $"第 {dayNum} 天 · {dt:M月d日} ({status})";
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
            : $"{now.Year}年{now.Month}月";

        CountText = $"{currentDay} / {daysInMonth}";
        PercentageText = $"{((double)currentDay / daysInMonth * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int dayNum = index + 1;
            var dt = new DateTime(now.Year, now.Month, dayNum);
            string status = index < passed ? "已过" : (index == passed ? "今天" : "未到");
            return $"{dt:M月d日} · 星期{GetDayOfWeekName(dt.DayOfWeek)} ({status})";
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

        var cal = CultureInfo.CurrentCulture.Calendar;
        int weekNum = cal.GetWeekOfYear(now, CalendarWeekRule.FirstDay, DayOfWeek.Monday);

        TitleText = !string.IsNullOrWhiteSpace(model.CustomTitle)
            ? model.CustomTitle
            : $"第 {weekNum} 周";

        CountText = $"{currentDayNum} / 7";
        PercentageText = $"{((double)currentDayNum / 7.0 * 100.0):F1}%";

        var weekStart = now.Date.AddDays(-dayOfWeekIndex);
        DotTooltipFunc = index =>
        {
            var dt = weekStart.AddDays(index);
            string status = index < dayOfWeekIndex ? "已过" : (index == dayOfWeekIndex ? "今天" : "未到");
            return $"星期{GetDayOfWeekName((DayOfWeek)((index + 1) % 7))} · {dt:M月d日} ({status})";
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
            : $"{now:M月d日} 周{GetDayOfWeekName(now.DayOfWeek)}";

        CountText = $"{currentSlot + 1} / {total}";
        PercentageText = $"{((double)(currentSlot + 1) / total * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int startMin = index * stepMinutes;
            int endMin = (index + 1) * stepMinutes;
            string startStr = $"{startMin / 60:D2}:{startMin % 60:D2}";
            string endStr = $"{endMin / 60:D2}:{endMin % 60:D2}";
            string status = index < currentSlot ? "已过" : (index == currentSlot ? "当前" : "未到");
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
            : "人生进度";

        CountText = $"{passedMonths} / {totalMonths} 月";
        PercentageText = $"{((double)passedMonths / totalMonths * 100.0):F1}%";

        DotTooltipFunc = index =>
        {
            int ageYears = index / 12;
            int ageMonths = index % 12;
            string status = index < passedMonths ? "已度过" : (index == passedMonths ? "此刻" : "未来");
            return $"{ageYears} 岁 {ageMonths + 1} 个月 ({status})";
        };
    }

    private static string GetDayOfWeekName(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "一",
        DayOfWeek.Tuesday => "二",
        DayOfWeek.Wednesday => "三",
        DayOfWeek.Thursday => "四",
        DayOfWeek.Friday => "五",
        DayOfWeek.Saturday => "六",
        DayOfWeek.Sunday => "日",
        _ => ""
    };
}
