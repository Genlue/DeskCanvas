using ReactiveUI;
using DeskCanvas.Services;
using Weather.Models;
using Weather.Services;

namespace Weather.ViewModels;

public class AirQualityViewModel : ReactiveObject, IDisposable
{
    private readonly ForecastModel model;
    private readonly OpenMeteoWeatherProvider provider;

    /// <summary>Set once <see cref="Dispose"/> ran; keeps disposal (and late replies) harmless.</summary>
    private bool disposed;

    public AirQualityViewModel(ForecastModel model)
    {
        provider = new OpenMeteoWeatherProvider();
        this.model = model;
        TimerService.Timer1Hour.Subscribe(UpdateForecast);
        UpdateForecast();
    }

    private void UpdateForecast()
    {
        if (disposed) return;
        _ = UpdateForecastAsync();
    }

    private async Task UpdateForecastAsync()
    {
        var forecast = await provider.GetAirQualityAsync(model.Latitude, model.Longitude);
        // A reply that arrives after disposal (the view was unloaded mid-request) must not
        // touch this view model any more.
        if (disposed) return;
        if (forecast?.Current == null) return;
        Metric = new(0, 100, forecast.Current.AirQualityIndex, WeatherIcon.Wind.Value);
    }
    
    private MetricViewModel metric = new(0, 100, 0, null);
    public MetricViewModel Metric
    {
        get => metric;
        private set => this.RaiseAndSetIfChanged(ref metric, value);
    }
    
    /// <summary>
    /// Detach from the process-lifetime hourly timer and dispose the provider's
    /// <see cref="HttpClient"/>, so an abandoned air-quality view (each settings save
    /// re-creates the view, each Gallery visit creates a preview) stops updating and
    /// releases its connections. The view must rebuild a fresh view model before using
    /// this one again. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        TimerService.Timer1Hour.Unsubscribe(UpdateForecast);
        provider.Dispose();
        GC.SuppressFinalize(this);
    }
}