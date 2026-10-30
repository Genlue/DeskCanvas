using System.Text.Json;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using Weather.Models.Forecast;
using Weather.Models.Geocoding;

namespace Weather.Services;

public class OpenMeteoWeatherProvider : IDisposable
{
    private readonly OpenMeteoSharedProvider shared;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string owner = $"weather-{Guid.NewGuid():N}";
    private bool disposed;

    public OpenMeteoWeatherProvider() : this(SharedDataServices.Weather) { }
    public OpenMeteoWeatherProvider(OpenMeteoSharedProvider shared) => this.shared = shared ?? throw new ArgumentNullException(nameof(shared));

    public Task<ForecastResponse?> GetForecastAsync(double latitude, double longitude, string temperatureUnit) =>
        GetResponseAsync<ForecastResponse>(new(new WeatherRequest(latitude, longitude, temperatureUnit)));

    public Task<AirQualityResponse?> GetAirQualityAsync(double latitude, double longitude) =>
        GetResponseAsync<AirQualityResponse>(new(new WeatherRequest(latitude, longitude), WeatherResource.AirQuality));

    private async Task<T?> GetResponseAsync<T>(OpenMeteoRequest request, CancellationToken cancellationToken = default)
    {
        if (disposed) return default;
        try
        {
            using var linked = cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken) : null;
            using var lease = await shared.AcquireAsync(owner, request, linked?.Token ?? lifetime.Token).ConfigureAwait(false);
            return disposed ? default : JsonSerializer.Deserialize<T>(lease.Snapshot.Value!);
        }
        catch (OperationCanceledException) { return default; }
        catch (Exception e)
        {
            await LogCrashAsync(e).ConfigureAwait(false);
            return default;
        }
    }

    public Task<List<City>?> GetCitiesAsync(string cityName) => GetCitiesAsync(cityName, CancellationToken.None);

    public async Task<List<City>?> GetCitiesAsync(string cityName, CancellationToken cancellationToken)
    {
        var language = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
        var result = await GetResponseAsync<GeocodingResponse>(new(default, WeatherResource.Geocoding, cityName, language), cancellationToken).ConfigureAwait(false);
        return result?.Cities;
    }

    /// <summary>Release this owner's waits; shared fetches continue for other owners.</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        lifetime.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Write a failed request to the diagnostic file. Ordinary owner cancellation
    /// and teardown are not logged.
    /// </summary>
    private async Task LogCrashAsync(Exception e)
    {
        if (disposed) return;

        try
        {
            await File.WriteAllTextAsync("weather_crash_log.txt", $"{e.Message}{Environment.NewLine}{e.StackTrace}");
        }
        catch
        {
            // A diagnostic write must never take the caller down.
        }
    }
}
