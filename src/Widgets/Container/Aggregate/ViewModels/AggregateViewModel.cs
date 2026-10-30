using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FixedWidgets.Models;
using FixedWidgets.Services;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;

namespace FixedWidgets.ViewModels;

public class AggregateViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// The weekday name is formatted on every one-second tick, so the culture is resolved
    /// once here: <see cref="CultureInfo.GetCultureInfo(string)"/> hands back a cached,
    /// read-only instance instead of building (and unifying) a new one per tick.
    /// </summary>
    private static readonly CultureInfo ChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static bool IsChineseCulture =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private readonly OpenMeteoWeatherService weatherService = new();
    private readonly IWidgetSession? session;
    private AggregateModel model;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late replies) harmless.</summary>
    private bool disposed;

    private string cityName = IsChineseCulture ? "北京" : "Beijing";
    private string temperatureText = "24°";
    private string conditionText = IsChineseCulture ? "晴朗" : "Clear";
    private string highLowText = "↑26°  ↓18°";
    private StreamGeometry? weatherIcon = OpenMeteoWeatherService.GetWeatherIcon(0);
    private string timeText = "00:00";
    private string dateText = "";
    private string monthYearText = "";
    private IReadOnlyList<string> weekHeaders = IsChineseCulture
        ? ["一", "二", "三", "四", "五", "六", "日"]
        : ["M", "T", "W", "T", "F", "S", "S"];
    private IReadOnlyList<DayCellViewModel> dayCells = [];
    private IBrush? customAccentBrush;

    public string CityName
    {
        get => cityName;
        set => this.RaiseAndSetIfChanged(ref cityName, value);
    }

    public string TemperatureText
    {
        get => temperatureText;
        set => this.RaiseAndSetIfChanged(ref temperatureText, value);
    }

    public string ConditionText
    {
        get => conditionText;
        set => this.RaiseAndSetIfChanged(ref conditionText, value);
    }

    public string HighLowText
    {
        get => highLowText;
        set => this.RaiseAndSetIfChanged(ref highLowText, value);
    }

    public StreamGeometry? WeatherIcon
    {
        get => weatherIcon;
        set => this.RaiseAndSetIfChanged(ref weatherIcon, value);
    }

    public string TimeText
    {
        get => timeText;
        set => this.RaiseAndSetIfChanged(ref timeText, value);
    }

    public string DateText
    {
        get => dateText;
        set => this.RaiseAndSetIfChanged(ref dateText, value);
    }

    public string MonthYearText
    {
        get => monthYearText;
        set => this.RaiseAndSetIfChanged(ref monthYearText, value);
    }

    public IReadOnlyList<string> WeekHeaders
    {
        get => weekHeaders;
        set => this.RaiseAndSetIfChanged(ref weekHeaders, value);
    }

    public IReadOnlyList<DayCellViewModel> DayCells
    {
        get => dayCells;
        set => this.RaiseAndSetIfChanged(ref dayCells, value);
    }

    public IBrush? CustomAccentBrush
    {
        get => customAccentBrush;
        set => this.RaiseAndSetIfChanged(ref customAccentBrush, value);
    }

    private double padding = 14.0;
    public double Padding
    {
        get => padding;
        set
        {
            this.RaiseAndSetIfChanged(ref padding, value);
            this.RaisePropertyChanged(nameof(LeftMargin));
            this.RaisePropertyChanged(nameof(RightMargin));
            this.RaisePropertyChanged(nameof(DividerMargin));
            this.RaisePropertyChanged(nameof(CityMargin));
            this.RaisePropertyChanged(nameof(TimeMargin));
            this.RaisePropertyChanged(nameof(WeatherMargin));
        }
    }

    public Thickness LeftMargin => new(padding, padding, padding, padding);
    public Thickness RightMargin => new(padding, padding, padding, padding);
    public Thickness DividerMargin => new(0, padding, 0, padding);
    public Thickness CityMargin => LeftMargin;
    public Thickness TimeMargin => LeftMargin;
    public Thickness WeatherMargin => LeftMargin;

    private bool hollowTodayNumber = true;
    public bool HollowTodayNumber
    {
        get => hollowTodayNumber;
        set => this.RaiseAndSetIfChanged(ref hollowTodayNumber, value);
    }

    private IBrush? todayDotBrush;
    public IBrush? TodayDotBrush
    {
        get => todayDotBrush;
        set => this.RaiseAndSetIfChanged(ref todayDotBrush, value);
    }

    public void UpdateTodayBrush()
    {
        if (model.TodayColorMode != "Custom")
        {
            SetTodayDotBrush(ResolveAccentBrush());
            return;
        }

        // Per-screen: the variant the marker's window renders with (the view is the resource
        // host). The Application variant is only the uniform fallback for a host-less view
        // model — the pre-per-screen behaviour — never an OR-ed second vote.
        var dark = ResolveEffectiveVariant(
            (resourceHost as StyledElement)?.ActualThemeVariant,
            Application.Current?.ActualThemeVariant) == ThemeVariant.Dark;
        var hex = dark ? model.TodayColorDark : model.TodayColorLight;

        IBrush? brush = null;
        if (!string.IsNullOrWhiteSpace(hex))
        {
            try { brush = new SolidColorBrush(Color.Parse(hex)); }
            catch { brush = null; }
        }

        SetTodayDotBrush(brush ?? ResolveAccentBrush());
    }

    // ---------- Per-screen theme seam (pure, testable) ----------

    /// <summary>
    /// Pure seam: the effective variant for a themed readout. The host window's variant wins
    /// (per-screen policy); a null host variant (control not attached / uniform host) falls
    /// back to the uniform Application variant, then to the platform default. Same shape as
    /// the Batteries readout seam.
    /// </summary>
    public static ThemeVariant ResolveEffectiveVariant(ThemeVariant? hostVariant, ThemeVariant? uniformVariant) =>
        hostVariant ?? uniformVariant ?? ThemeVariant.Default;

    /// <summary>
    /// Assigns the marker brush only when the colour really changed: the accent resource is
    /// replaced on every theme re-apply, and a blind assignment would re-render the calendar
    /// (and allocate a brush) for an unchanged colour.
    /// </summary>
    private void SetTodayDotBrush(IBrush? brush)
    {
        if (TodayDotBrush is ISolidColorBrush current && brush is ISolidColorBrush next)
        {
            if (current.Color == next.Color) return;
        }
        else if (TodayDotBrush == null && brush == null)
        {
            return;
        }

        TodayDotBrush = brush;
    }

    private IBrush ResolveAccentBrush()
    {
        var variant = (resourceHost as StyledElement)?.ActualThemeVariant
            ?? Application.Current?.ActualThemeVariant
            ?? ThemeVariant.Default;

        // 优先从传入的 resourceHost 解析
        if (resourceHost != null)
        {
            if (resourceHost is Visual vis)
            {
                if (ThemeScopeResolver.TryFindResource<IBrush>(vis, "CalendarTodayBrush", variant, out var vcb) && vcb != null)
                    return vcb;
                if (ThemeScopeResolver.TryFindResource<Color>(vis, "SystemAccentColor", variant, out var vacc))
                    return new SolidColorBrush(vacc);
            }

            if (resourceHost.TryGetResource("CalendarTodayBrush", variant, out var calBrush) && calBrush is IBrush cb)
                return cb;
            if (resourceHost.TryGetResource("SystemAccentColor", variant, out var value) && value is Color accent)
                return new SolidColorBrush(accent);
        }

        // 回退至 Application.Current
        if (Application.Current != null)
        {
            // Variant-explicit lookup: the variant-less overload resolves the LIGHT theme
            // dictionary even while the app renders dark — in 单色-黑白 that turned the
            // today dot into the dark-mode background color (same trap as
            // MarkdownRenderer.ResolveThemed documents).
            if (((IResourceHost)Application.Current).TryGetResource("CalendarTodayBrush", Application.Current.ActualThemeVariant, out var calBrush) && calBrush is IBrush cb)
                return cb;
            if (Application.Current.TryFindResource("SystemAccentColor", out var value) && value is Color accent)
                return new SolidColorBrush(accent);
        }

        return new SolidColorBrush(Color.Parse("#FF3B30"));
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        UpdateTodayBrush();
    }

    /// <summary>The resource host the view model is scoped to.</summary>
    private IResourceHost? resourceHost;

    /// <summary>The process-lifetime or window-lifetime resource host the accent subscription belongs to.</summary>
    private IResourceHost? accentHost;

    private void OnAccentResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        UpdateTodayBrush();
    }

    public void BindResourceHost(IResourceHost host)
    {
        if (ReferenceEquals(resourceHost, host)) return;

        if (resourceHost is StyledElement oldSe)
            oldSe.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        if (accentHost != null)
            accentHost.ResourcesChanged -= OnAccentResourcesChanged;

        resourceHost = host;
        if (resourceHost is StyledElement newSe)
            newSe.ActualThemeVariantChanged += OnActualThemeVariantChanged;

        accentHost = resourceHost ?? (Application.Current as IResourceHost);
        if (accentHost != null)
        {
            accentHost.ResourcesChanged += OnAccentResourcesChanged;
        }

        UpdateTodayBrush();
    }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: both timer subscriptions (the one-second clock and the hourly weather/calendar
    /// refresh) are then gated on the session's activity. Null keeps the legacy always-on
    /// behaviour (preview, legacy layout entries) — those hosts never push activity either.
    /// </summary>
    public AggregateViewModel(AggregateModel model, IResourceHost? host = null, IWidgetSession? session = null)
    {
        this.model = model;
        this.resourceHost = host;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;
        ApplyModel(model);

        UpdateTickSubscription();

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnActualThemeVariantChanged;

        if (resourceHost is StyledElement se)
            se.ActualThemeVariantChanged += OnActualThemeVariantChanged;

        // A colour picked in 外观 replaces the accent resource in place; follow it so the
        // marker does not keep the colour it was created with.
        accentHost = resourceHost ?? (Application.Current as IResourceHost);
        if (accentHost != null)
        {
            accentHost.ResourcesChanged += OnAccentResourcesChanged;
        }

        UpdateClock();
        UpdateCalendar();
        _ = UpdateWeather();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): both refresh loops follow the snapshot's RenderActive.
    /// Session-less view models ignore the push entirely (the host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null) return;
        renderActive = activity.RenderActive;
        UpdateTickSubscription();
    }

    /// <summary>Check seam: the live subscription count over the shared timers (0 or 2 —
    /// the one-second and the hourly timer subscribe and unsubscribe together).</summary>
    public int TickSubscriptionCountForChecks => tickSubscriptions;

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    /// <summary>Swap the model and refresh everything it drives (the view's refresh path).</summary>
    public void UpdateModel(AggregateModel newModel)
    {
        model = newModel;
        ApplyModel(newModel);
        UpdateClock();
        UpdateCalendar();
        _ = UpdateWeather();
    }

    private void ApplyModel(AggregateModel m)
    {
        bool isZh = IsChineseCulture;
        CityName = !isZh && m.City == "北京" ? "Beijing" : m.City;
        Padding = m.Padding > 0 ? m.Padding : 14.0;
        HollowTodayNumber = m.HollowTodayNumber;
        UpdateTodayBrush();
        if (!string.IsNullOrWhiteSpace(m.CustomAccentColor) && Color.TryParse(m.CustomAccentColor, out var col))
        {
            CustomAccentBrush = new SolidColorBrush(col);
        }
        else
        {
            CustomAccentBrush = null;
        }

        // Set week headers according to FirstDayOfWeek
        if (m.FirstDayOfWeek == DayOfWeek.Sunday)
        {
            WeekHeaders = isZh
                ? ["日", "一", "二", "三", "四", "五", "六"]
                : ["S", "M", "T", "W", "T", "F", "S"];
        }
        else
        {
            WeekHeaders = isZh
                ? ["一", "二", "三", "四", "五", "六", "日"]
                : ["M", "T", "W", "T", "F", "S", "S"];
        }
    }

    private void OnSecondTick()
    {
        UpdateClock();
    }

    private void OnHourTick()
    {
        UpdateCalendar();
        _ = UpdateWeather();
    }

    public void UpdateClock()
    {
        var now = DateTime.Now;

        // Time format
        if (model.Is24Hour)
        {
            TimeText = model.ShowSeconds ? now.ToString("HH:mm:ss") : now.ToString("HH:mm");
        }
        else
        {
            string tt = now.ToString("hh:mm", CultureInfo.InvariantCulture);
            string ampm = now.Hour >= 12 ? "PM" : "AM";
            TimeText = model.ShowSeconds ? $"{tt}:{now:ss} {ampm}" : $"{tt} {ampm}";
        }

        if (IsChineseCulture)
        {
            // Chinese Date & Weekday: e.g. "9月12日 星期六"
            string weekday = now.ToString("dddd", ChineseCulture);
            DateText = $"{now.Month}月{now.Day}日 {weekday}";
        }
        else
        {
            DateText = now.ToString("ddd, MMM d", CultureInfo.CurrentUICulture);
        }
    }

    public void UpdateCalendar()
    {
        var now = DateTime.Now;
        MonthYearText = IsChineseCulture
            ? $"{now.Year}年 {now.Month}月"
            : now.ToString("MMMM yyyy", CultureInfo.CurrentUICulture);

        var firstDayOfMonth = new DateTime(now.Year, now.Month, 1);
        int daysInMonth = DateTime.DaysInMonth(now.Year, now.Month);

        // Determine offset based on FirstDayOfWeek
        int startDayOffset = ((int)firstDayOfMonth.DayOfWeek - (int)model.FirstDayOfWeek + 7) % 7;

        var cells = new List<DayCellViewModel>(42);

        // Previous month padding days
        var prevMonthDate = firstDayOfMonth.AddMonths(-1);
        int daysInPrevMonth = DateTime.DaysInMonth(prevMonthDate.Year, prevMonthDate.Month);

        for (int i = startDayOffset - 1; i >= 0; i--)
        {
            int dayNum = daysInPrevMonth - i;
            var dt = new DateTime(prevMonthDate.Year, prevMonthDate.Month, dayNum);
            bool isWeekend = dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday;
            cells.Add(new DayCellViewModel(dayNum, dayNum.ToString(), false, false, isWeekend, dt));
        }

        // Current month days
        for (int day = 1; day <= daysInMonth; day++)
        {
            var dt = new DateTime(now.Year, now.Month, day);
            bool isToday = day == now.Day && now.Month == DateTime.Today.Month && now.Year == DateTime.Today.Year;
            bool isWeekend = dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday;
            cells.Add(new DayCellViewModel(day, day.ToString(), true, isToday, isWeekend, dt));
        }

        // Next month padding days to fill 35 or 42 grid cells (5 or 6 rows)
        int targetTotalCells = cells.Count > 35 ? 42 : 35;
        var nextMonthDate = firstDayOfMonth.AddMonths(1);
        int nextDay = 1;
        while (cells.Count < targetTotalCells)
        {
            var dt = new DateTime(nextMonthDate.Year, nextMonthDate.Month, nextDay);
            bool isWeekend = dt.DayOfWeek == DayOfWeek.Saturday || dt.DayOfWeek == DayOfWeek.Sunday;
            cells.Add(new DayCellViewModel(nextDay, nextDay.ToString(), false, false, isWeekend, dt));
            nextDay++;
        }

        DayCells = cells;
    }

    /// <summary>
    /// Refresh the weather block. Called from the constructor, from model updates and from the
    /// hourly tick (never as an event handler), so it returns a task the callers may discard —
    /// and it catches everything itself: an unhandled exception from an <c>async void</c>
    /// method would otherwise reach the dispatcher and take the whole process down.
    /// </summary>
    public async Task UpdateWeather()
    {
        try
        {
            var info = await weatherService.GetWeatherAsync(model.Latitude, model.Longitude, model.TemperatureUnit);

            // The reply is applied on the UI thread only. Normally the await above already
            // resumes there (it is the caller's context), but the service runs its own awaits
            // ConfigureAwait(false), so a caller that started this from a background thread would
            // land on the pool — and neither bound properties nor the icon geometry (see
            // ApplyWeather) may be touched from there.
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    try { ApplyWeather(info); }
                    catch { /* Same contract as below: keep what is on screen. */ }
                });
                return;
            }

            ApplyWeather(info);
        }
        catch
        {
            // Offline or a service error: keep the values that are already on screen.
        }
    }

    /// <summary>
    /// Apply one weather reply. UI thread only — it writes bound properties and parses
    /// <see cref="WeatherIcon"/>, and an Avalonia geometry belongs to the thread that created it:
    /// parsing it anywhere else makes the bound <c>Path</c> throw from its measure pass, inside
    /// the render pass, which takes the process down (the 全部组件 gallery crash).
    /// </summary>
    private void ApplyWeather(WeatherInfo? info)
    {
        // A reply that arrives after disposal (the view was unloaded mid-request) must not
        // touch this view model any more, and must not revive it through its bindings.
        if (disposed) return;

        if (info == null)
        {
            if (TemperatureText == "--°")
            {
                TemperatureText = "24°";
                ConditionText = IsChineseCulture ? "晴朗" : "Clear";
                HighLowText = "↑26°  ↓18°";
                WeatherIcon = OpenMeteoWeatherService.GetWeatherIcon(0);
            }
            return;
        }

        TemperatureText = $"{Math.Round(info.Temperature):0}°";
        ConditionText = info.ConditionText;
        HighLowText = $"↑{Math.Round(info.MaxTemp):0}°  ↓{Math.Round(info.MinTemp):0}°";
        WeatherIcon = OpenMeteoWeatherService.GetWeatherIcon(info.WeatherCode);
    }

    /// <summary>
    /// Release timer subscriptions and this weather owner's in-flight waits.
    /// The view rebuilds a fresh model before reuse. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        if (resourceHost is StyledElement se)
            se.ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        if (accentHost != null)
            accentHost.ResourcesChanged -= OnAccentResourcesChanged;
        UpdateTickSubscription();
        weatherService.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single subscription state machine over both shared timers: they subscribe and
    /// unsubscribe together, exactly while the session renders AND the view model is not
    /// disposed. Session-less view models render always (<paramref name="renderActive"/>
    /// starts true and nothing flips it). Overlapping transitions cannot double-subscribe or
    /// double-unsubscribe — the count is reconciled from both inputs on every change instead
    /// of toggled from each.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = renderActive && !disposed;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions = 2;
            TimerService.Timer1Second.Subscribe(OnSecondTick);
            TimerService.Timer1Hour.Subscribe(OnHourTick);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions = 0;
            TimerService.Timer1Second.Unsubscribe(OnSecondTick);
            TimerService.Timer1Hour.Unsubscribe(OnHourTick);
        }
    }
}
