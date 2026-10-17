using Avalonia.Controls;
using Avalonia.Media;
using ReactiveUI;
using DeskCanvas.Contracts;
using DeskCanvas.Services;
using Weather.Models;
using Weather.Models.Forecast;
using Weather.Services;

namespace Weather.ViewModels;

public class ForecastViewModel : ReactiveObject, IDisposable
{
    private readonly ForecastModel model;
    private readonly OpenMeteoWeatherProvider provider;
    private readonly IWidgetSession? session;
    private bool renderActive = true;
    private int tickSubscriptions;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late replies) harmless.</summary>
    private bool disposed;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the hourly refresh subscription is then gated on the session's activity. Null
    /// keeps the legacy always-on behaviour (preview, legacy layout entries) — those hosts
    /// never push activity either.
    /// </summary>
    public ForecastViewModel(ForecastModel model, IWidgetSession? session = null)
    {
        provider = new OpenMeteoWeatherProvider();
        this.model = model;
        this.session = session;
        renderActive = session?.Activity.RenderActive ?? true;

        UpdateTickSubscription();
        UpdateForecast();
    }

    /// <summary>
    /// Host activity push (the <see cref="DeskCanvas.Runtime.IWidgetActivityConsumer"/> path,
    /// invoked on the UI thread): the hourly refresh follows the snapshot's RenderActive.
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

    /// <summary>Check seam: the latest session gate value.</summary>
    public bool RenderActiveForChecks => renderActive;

    private void UpdateForecast()
    {
        if (disposed) return;
        _ = UpdateForecastAsync();
    }

    private async Task UpdateForecastAsync()
    {
        var forecast = await provider.GetForecastAsync(model.Latitude, model.Longitude, model.TemperatureUnit);
        // A reply that arrives after disposal (the view was unloaded mid-request) must not
        // touch this view model any more, and must not revive it through its bindings.
        if (disposed || forecast is null) return;

        ApplyForecast(forecast);
    }

    private static readonly string[] ChineseWeekdays = ["周日", "周一", "周二", "周三", "周四", "周五", "周六"];

    private static string FormatDayOfWeek(DayOfWeek dayOfWeek)
    {
        var culture = Thread.CurrentThread.CurrentUICulture;
        if (string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase))
            return ChineseWeekdays[(int)dayOfWeek];
        return culture.DateTimeFormat.AbbreviatedDayNames[(int)dayOfWeek];
    }

    private static IReadOnlyList<DailyForecastViewModel> BuildInitialFourDays()
    {
        var today = DateTime.Today;
        var list = new List<DailyForecastViewModel>(4);
        for (int i = 1; i <= 4; i++)
        {
            var dayName = FormatDayOfWeek(today.AddDays(i).DayOfWeek);
            list.Add(new DailyForecastViewModel(dayName, WeatherIcon.Clear.Value, "--°", "--°", []));
        }
        return list;
    }

    private void ApplyForecast(ForecastResponse forecast)
    {
        var currentHour = DateTime.Now.Hour;

        CurrentTemperature = $"{forecast.Current.Temperature:0}°";
        CurrentIcon = forecast.Hourly.IsDay[currentHour] > 0 ? WeatherIconProvider.GetIcon(forecast.Current.WeatherCode) : WeatherIcon.Night.Value;
        CurrentCondition = GetCondition(forecast.Current.WeatherCode);
        CurrentMinMax = $"{forecast.Daily.Max[0]:0}°  {forecast.Daily.Min[0]:0}°";
        CurrentMin = $"{forecast.Daily.Min[0]:0}";
        CurrentMax = $"{forecast.Daily.Max[0]:0}";
        CurrentMinDegree = $"{forecast.Daily.Min[0]:0}°";
        CurrentMaxDegree = $"{forecast.Daily.Max[0]:0}°";
        TemperatureMetric = new(forecast.Daily.Min.Min(), forecast.Daily.Max.Max(), forecast.Current.Temperature, null);
        UVIndex = new(0, 10, forecast.Hourly.UVIndex[currentHour], WeatherIcon.Clear.Value);
        Pressure = new(960, 1050, forecast.Current.Pressure, WeatherIcon.Pressure.Value);
        SunsetSunrise = GetSunsetSunrise(forecast);
        HourlyForecast = Enumerable
            .Range(currentHour, 24)
            .Select(hour => GetHourlyForecast(forecast, hour % 24));
        var dailyList = Enumerable
            .Range(0, forecast.Daily.Min.Count)
            .Select(day => GetDailyForecast(forecast, day))
            .ToList();
        DailyForecast = dailyList;
        UpcomingFourDaysForecast = dailyList.Skip(1).Take(4).ToList();
    }

    private SunriseSunsetViewModel GetSunsetSunrise(ForecastResponse forecast)
    {
        var sunrise = forecast.Daily.Sunrise[0];
        var sunset = forecast.Daily.Sunset[0];
        var sunriseTomorrow = forecast.Daily.Sunrise[1];
        var now = DateTime.Now;

        if (now < sunrise)
            return new(WeatherIcon.Sunrise.Value, sunrise.ToString("HH:mm"));
        if (now < sunset)
            return new(WeatherIcon.Sunset.Value, sunset.ToString("HH:mm"));
        return new(WeatherIcon.Sunrise.Value, sunriseTomorrow.ToString("HH:mm"));
    }

    private DailyForecastViewModel GetDailyForecast(ForecastResponse forecast, int day)
    {
        var dayOfWeekName = FormatDayOfWeek(forecast.Daily.Time[day].DayOfWeek);
        var code = forecast.Daily.WeatherCode[day];
        var icon = WeatherIconProvider.GetIcon(code);
        var min = forecast.Daily.Min[day];
        var max = forecast.Daily.Max[day];

        var totalMin = forecast.Daily.Min.Min();
        var totalMax = forecast.Daily.Max.Max();
        
        List<GridLength> graph =
        [
            new GridLength(min - totalMin, GridUnitType.Star),
            new GridLength(max - min, GridUnitType.Star),
            new GridLength(totalMax - max, GridUnitType.Star),
        ];
        
        return new DailyForecastViewModel(dayOfWeekName, icon, $"{min:0}°", $"{max:0}°", graph);
    }

    private HourlyForecastViewModel GetHourlyForecast(ForecastResponse forecast, int hour)
    {
        var time = hour.ToString();
        var code = forecast.Hourly.WeatherCode[hour];
        var icon = forecast.Hourly.IsDay[hour] > 0 ? WeatherIconProvider.GetIcon(code) : WeatherIcon.Night.Value;
        var temperature = forecast.Hourly.Temperature[hour];

        return new HourlyForecastViewModel(time, icon, $"{temperature:0}°");
    }
    
    private string? GetCondition(WeatherCode code) => Locales.Locale.ResourceManager.GetString($"Weather_Code_{(int)code}");

    private StreamGeometry currentIcon = WeatherIcon.Clear.Value;
    public StreamGeometry CurrentIcon
    {
        get => currentIcon;
        private set => this.RaiseAndSetIfChanged(ref currentIcon, value);
    }

    private IEnumerable<HourlyForecastViewModel> hourlyForecast = [];
    public IEnumerable<HourlyForecastViewModel> HourlyForecast
    {
        get => hourlyForecast;
        private set => this.RaiseAndSetIfChanged(ref hourlyForecast, value);
    }

    private IEnumerable<DailyForecastViewModel> dailyForecast = [];
    public IEnumerable<DailyForecastViewModel> DailyForecast
    {
        get => dailyForecast;
        private set => this.RaiseAndSetIfChanged(ref dailyForecast, value);
    }

    private IEnumerable<DailyForecastViewModel> upcomingFourDaysForecast = BuildInitialFourDays();
    public IEnumerable<DailyForecastViewModel> UpcomingFourDaysForecast
    {
        get => upcomingFourDaysForecast;
        private set => this.RaiseAndSetIfChanged(ref upcomingFourDaysForecast, value);
    }
    
    public string CityName => model.Name;

    private string? currentTemperature = "--°";
    public string? CurrentTemperature
    {
        get => currentTemperature;
        private set => this.RaiseAndSetIfChanged(ref currentTemperature, value);
    }
    
    private string? currentCondition = "--------";
    public string? CurrentCondition
    {
        get => currentCondition;
        private set => this.RaiseAndSetIfChanged(ref currentCondition, value);
    }
    
    private string? currentMinMax = "--°  --°";
    public string? CurrentMinMax
    {
        get => currentMinMax;
        private set => this.RaiseAndSetIfChanged(ref currentMinMax, value);
    }
        
    private string? currentMin = "--";
    public string? CurrentMin
    {
        get => currentMin;
        private set => this.RaiseAndSetIfChanged(ref currentMin, value);
    }
    
    private string? currentMax = "--";
    public string? CurrentMax
    {
        get => currentMax;
        private set => this.RaiseAndSetIfChanged(ref currentMax, value);
    }

    private string? currentMinDegree = "--°";
    public string? CurrentMinDegree
    {
        get => currentMinDegree;
        private set => this.RaiseAndSetIfChanged(ref currentMinDegree, value);
    }

    private string? currentMaxDegree = "--°";
    public string? CurrentMaxDegree
    {
        get => currentMaxDegree;
        private set => this.RaiseAndSetIfChanged(ref currentMaxDegree, value);
    }

    private MetricViewModel temperatureMetric = new(0, 1, 0, null);
    public MetricViewModel TemperatureMetric
    {
        get => temperatureMetric;
        private set => this.RaiseAndSetIfChanged(ref temperatureMetric, value);
    }
    
    private MetricViewModel uvIndex = new(0, 10, 0, WeatherIcon.Clear.Value);
    public MetricViewModel UVIndex
    {
        get => uvIndex;
        private set => this.RaiseAndSetIfChanged(ref uvIndex, value);
    }

    private MetricViewModel pressure = new(960, 1050, 960, WeatherIcon.Pressure.Value);
    public MetricViewModel Pressure
    {
        get => pressure;
        private set => this.RaiseAndSetIfChanged(ref pressure, value);
    }
    
    private SunriseSunsetViewModel sunsetSunrise = new(new(), "--:--");
    public SunriseSunsetViewModel SunsetSunrise
    {
        get => sunsetSunrise;
        private set => this.RaiseAndSetIfChanged(ref sunsetSunrise, value);
    }

    /// <summary>
    /// Detach from the process-lifetime hourly timer and release this owner's
    /// in-flight provider waits. The view rebuilds a fresh owner before reuse.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        UpdateTickSubscription();
        provider.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single subscription state machine: the shared timer subscription exists exactly while
    /// the session renders AND the view model is not disposed. Session-less view models render
    /// always (<paramref name="renderActive"/> starts true and nothing flips it). Overlapping
    /// transitions cannot double-subscribe or double-unsubscribe — the count is reconciled
    /// from both inputs on every change instead of toggled from each.
    /// </summary>
    private void UpdateTickSubscription()
    {
        var wanted = renderActive && !disposed;
        if (wanted && tickSubscriptions == 0)
        {
            tickSubscriptions++;
            TimerService.Timer1Hour.Subscribe(UpdateForecast);
        }
        else if (!wanted && tickSubscriptions > 0)
        {
            tickSubscriptions--;
            TimerService.Timer1Hour.Unsubscribe(UpdateForecast);
        }
    }
}