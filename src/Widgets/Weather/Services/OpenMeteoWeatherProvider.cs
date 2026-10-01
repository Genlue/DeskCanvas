using System.Globalization;
using System.Text.Json;
using DeskCanvas.Core.Services;
using Weather.Models.Forecast;
using Weather.Models.Geocoding;

namespace Weather.Services;

public class OpenMeteoWeatherProvider : IDisposable
{
    // Follows the configurable proxy setting (default: direct connection, bypassing
    // the system proxy — a stale proxy from a shut-down proxy client used to break
    // weather updates). See ProxySettings.
    private readonly HttpClient httpClient = ProxySettings.CreateHttpClient();

    /// <summary>Set once <see cref="Dispose"/> ran; makes disposal idempotent.</summary>
    private bool disposed;

    public async Task<ForecastResponse?> GetForecastAsync(double latitude, double longitude, string temperatureUnit)
    {
        try
        {
            var latitudeString = string.Format(CultureInfo.InvariantCulture, "{0:F4}", latitude);
            var longitudeString = string.Format(CultureInfo.InvariantCulture, "{0:F4}", longitude);
            
            var url = $"https://api.open-meteo.com/v1/forecast?" +
                      $"latitude={latitudeString}&" +
                      $"longitude={longitudeString}&" +
                      $"temperature_unit={temperatureUnit}&" +
                      $"current=temperature_2m,weathercode,surface_pressure&" +
                      $"hourly=temperature_2m,weathercode,uv_index,is_day&" +
                      $"daily=temperature_2m_min,temperature_2m_max,weathercode,sunrise,sunset&" +
                      $"timezone=auto";
            
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) throw new Exception(JsonSerializer.Serialize(response));

            var json = await response.Content.ReadAsStringAsync();
            var forecast = JsonSerializer.Deserialize<ForecastResponse>(json);

            return forecast;
        }
        catch (Exception e)
        {
            await LogCrashAsync(e);
            return null;
        }
    }

    public async Task<AirQualityResponse?> GetAirQualityAsync(double latitude, double longitude)
    {
        try
        {
            var latitudeString = string.Format(CultureInfo.InvariantCulture, "{0:F4}", latitude);
            var longitudeString = string.Format(CultureInfo.InvariantCulture, "{0:F4}", longitude);

            var url = $"https://air-quality-api.open-meteo.com/v1/air-quality?" +
                      $"latitude={latitudeString}&" +
                      $"longitude={longitudeString}&" +
                      $"current=european_aqi";
            
            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) throw new Exception(JsonSerializer.Serialize(response));

            var json = await response.Content.ReadAsStringAsync();
            var forecast = JsonSerializer.Deserialize<AirQualityResponse>(json);

            return forecast;
        }
        catch (Exception e)
        {
            await LogCrashAsync(e);
            return null;
        }
    } 

    public async Task<List<City>?> GetCitiesAsync(string cityName)
    {
        try
        {
            var language = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
            
            var url = $"https://geocoding-api.open-meteo.com/v1/search?" +
                      $"name={cityName}&" +
                      $"language={language}";

            var response = await httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
            var searchResults = JsonSerializer.Deserialize<GeocodingResponse>(json);

            return searchResults?.Cities;
        }
        catch (Exception e)
        {
            await LogCrashAsync(e);
            return null;
        }
    }

    /// <summary>
    /// Release the provider's own <see cref="HttpClient"/> (and through it the socket pool
    /// of its handler). The widget drops its provider when the view is unloaded, so without
    /// this every view instance — each settings save, each Gallery visit — would keep a
    /// client and its connections alive for the lifetime of the process. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Write a failed request to the diagnostic file. A request still in flight when the
    /// widget is unloaded fails with an <see cref="ObjectDisposedException"/> from the
    /// disposal above — that is ordinary teardown, not a crash, so it is not logged.
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