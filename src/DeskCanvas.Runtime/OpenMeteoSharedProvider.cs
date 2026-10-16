using System.Globalization;
using System.Text.Json;

namespace DeskCanvas.Runtime;

public enum WeatherResource { Forecast, AirQuality, Geocoding }
public readonly record struct OpenMeteoRequest(WeatherRequest Location, WeatherResource Resource = WeatherResource.Forecast,
    string? Query = null, string? Language = null, int? Count = null)
{
    public OpenMeteoRequest Normalize() => Resource == WeatherResource.Geocoding
        ? new(new(0, 0), Resource, Query ?? "", Language?.Trim().ToLowerInvariant() ?? "en", Count)
        : new(Location.Normalize() with
        {
            // Air quality does not vary with the temperature display unit.
            Unit = Resource == WeatherResource.AirQuality ? "celsius" : Location.Normalize().Unit
        }, Resource);
}

/// <summary>Shared immutable Open-Meteo payloads; plugins deserialize their own compatibility models.</summary>
public sealed class OpenMeteoSharedProvider
{
    private readonly SharedDataProvider<OpenMeteoRequest, string> data;

    public OpenMeteoSharedProvider(Func<Uri, CancellationToken, Task<string>> fetch, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        data = new(async (request, token) =>
        {
            var json = await fetch(BuildUri(request), token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Open-Meteo rejected the request.");
            return json;
        }, ttl ?? TimeSpan.FromMinutes(5), "open-meteo");
    }

    public SharedDataStatistics Statistics => data.Statistics;

    public Task<SharedDataProvider<OpenMeteoRequest, string>.Lease> AcquireAsync(string owner, OpenMeteoRequest request, CancellationToken cancellationToken = default) =>
        data.AcquireAsync(owner, request.Normalize(), cancellationToken);

    public static Uri BuildUri(OpenMeteoRequest request)
    {
        request = request.Normalize();
        if (request.Resource == WeatherResource.Geocoding)
        {
            var options = request.Count is int count ? $"&count={count}&format=json" : "";
            return new Uri($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(request.Query!)}&language={Uri.EscapeDataString(request.Language!)}{options}");
        }
        var location = request.Location;
        var coordinates = string.Create(CultureInfo.InvariantCulture, $"latitude={location.Latitude:F4}&longitude={location.Longitude:F4}");
        return request.Resource == WeatherResource.AirQuality
            ? new Uri($"https://air-quality-api.open-meteo.com/v1/air-quality?{coordinates}&current=european_aqi")
            : new Uri($"https://api.open-meteo.com/v1/forecast?{coordinates}&temperature_unit={Uri.EscapeDataString(location.Unit)}&current=temperature_2m,weathercode,surface_pressure&hourly=temperature_2m,weathercode,uv_index,is_day&daily=temperature_2m_min,temperature_2m_max,weathercode,sunrise,sunset&timezone=auto");
    }
}
