using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using FixedWidgets.Models;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

namespace FixedWidgets.Services;

/// <summary>
/// One weather reading. Deliberately carries the <see cref="WeatherCode"/> instead of a parsed
/// icon geometry: this record is filled on whatever thread the HTTP continuation lands on, and an
/// Avalonia <see cref="StreamGeometry"/> belongs to the thread that created it (see
/// <see cref="OpenMeteoWeatherService.GetWeatherIcon"/>). The view model parses the icon itself,
/// on the UI thread.
/// </summary>
public record WeatherInfo(
    double Temperature,
    int WeatherCode,
    string ConditionText,
    double MaxTemp,
    double MinTemp);

public class OpenMeteoWeatherService : IDisposable
{
    private readonly OpenMeteoSharedProvider shared;
    private readonly CancellationTokenSource lifetime = new();
    private readonly string owner = $"fixed-weather-{Guid.NewGuid():N}";

    public OpenMeteoWeatherService() : this(SharedDataServices.Weather) { }
    public OpenMeteoWeatherService(OpenMeteoSharedProvider shared) => this.shared = shared ?? throw new ArgumentNullException(nameof(shared));

    /// <summary>Set once <see cref="Dispose"/> ran; makes disposal idempotent.</summary>
    private bool disposed;

    public async Task<List<City>?> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            var language = isZh ? "zh" : "en";

            if (disposed) return null;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, cancellationToken);
            using var lease = await shared.AcquireAsync(owner, new(default, WeatherResource.Geocoding, query.Trim(), language, 10), linked.Token).ConfigureAwait(false);
            if (disposed) return null;
            var json = lease.Snapshot.Value!;
            var data = JsonSerializer.Deserialize<GeocodingResponse>(json);
            return data?.Results;
        }
        catch
        {
            return null;
        }
    }

    public async Task<WeatherInfo?> GetWeatherAsync(double latitude, double longitude, string unit = "celsius")
    {
        try
        {
            if (disposed) return null;
            using var lease = await shared.AcquireAsync(owner, new(new WeatherRequest(latitude, longitude, unit)), lifetime.Token).ConfigureAwait(false);
            if (disposed) return null;
            var json = lease.Snapshot.Value!;
            var data = JsonSerializer.Deserialize<OpenMeteoResponse>(json);
            if (data?.Current == null) return null;

            double curTemp = data.Current.Temperature2m;
            int code = data.Current.WeatherCode;
            double maxTemp = data.Daily?.Max != null && data.Daily.Max.Length > 0 ? data.Daily.Max[0] : curTemp;
            double minTemp = data.Daily?.Min != null && data.Daily.Min.Length > 0 ? data.Daily.Min[0] : curTemp;

            string condition = GetConditionText(code);

            // No geometry is built here: this method resumes on a thread-pool thread (every
            // await above is ConfigureAwait(false)), and a StreamGeometry parsed there belongs
            // to that thread. The view model parses the icon on the UI thread instead.
            return new WeatherInfo(curTemp, code, condition, maxTemp, minTemp);
        }
        catch
        {
            return null;
        }
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

    public static string GetConditionText(int code)
    {
        bool isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        return isZh
            ? code switch
            {
                0 => "晴朗",
                1 => "晴间多云",
                2 => "多云",
                3 => "阴天",
                45 or 48 => "有雾",
                51 or 53 or 55 => "毛毛雨",
                56 or 57 => "冻毛毛雨",
                61 => "小雨",
                63 => "中雨",
                65 => "大雨",
                66 or 67 => "冻雨",
                71 => "小雪",
                73 => "中雪",
                75 => "大雪",
                77 => "雪粒",
                80 or 81 or 82 => "阵雨",
                85 or 86 => "阵雪",
                95 => "雷阵雨",
                96 or 99 => "雷阵雨伴有冰雹",
                _ => "晴"
            }
            : code switch
            {
                0 => "Clear",
                1 => "Mostly Clear",
                2 => "Partly Cloudy",
                3 => "Overcast",
                45 or 48 => "Fog",
                51 or 53 or 55 => "Drizzle",
                56 or 57 => "Freezing Drizzle",
                61 => "Light Rain",
                63 => "Moderate Rain",
                65 => "Heavy Rain",
                66 or 67 => "Freezing Rain",
                71 => "Light Snow",
                73 => "Moderate Snow",
                75 => "Heavy Snow",
                77 => "Snow Grains",
                80 or 81 or 82 => "Rain Showers",
                85 or 86 => "Snow Showers",
                95 => "Thunderstorm",
                96 or 99 => "Thunderstorm with Hail",
                _ => "Clear"
            };
    }

    /// <summary>
    /// Parses the icon geometry for a weather code. <b>UI thread only.</b>
    /// <para>
    /// Avalonia's <see cref="Geometry"/> is thread-affine: the instance captures the dispatcher of
    /// the thread that created it, and the <c>Path</c> bound to it then throws
    /// "The calling thread cannot access this object because a different thread owns it" from
    /// <c>Shape.MeasureOverride</c> — inside the render pass, where nothing catches it, so the
    /// process dies. Parsing this in the weather service used to do exactly that, because
    /// <see cref="GetWeatherAsync"/> resumes on a pool thread; the view model (on the UI thread)
    /// is now the only caller.
    /// </para>
    /// </summary>
    public static StreamGeometry GetWeatherIcon(int code)
    {
        System.Diagnostics.Debug.Assert(
            Avalonia.Threading.Dispatcher.UIThread.CheckAccess(),
            "GetWeatherIcon must run on the UI thread: Avalonia geometry is thread-affine.");

        string pathData = code switch
        {
            // Sunny
            0 => "M12 7c-2.76 0-5 2.24-5 5s2.24 5 5 5 5-2.24 5-5-2.24-5-5-5zM2 13h2c.55 0 1-.45 1-1s-.45-1-1-1H2c-.55 0-1 .45-1 1s.45 1 1 1zm18 0h2c.55 0 1-.45 1-1s-.45-1-1-1h-2c-.55 0-1 .45-1 1s.45 1 1 1zM11 2v2c0 .55.45 1 1 1s1-.45 1-1V2c0-.55-.45-1-1-1s-1 .45-1 1zm0 18v2c0 .55.45 1 1 1s1-.45 1-1v-2c0-.55-.45-1-1-1s-1 .45-1 1zM5.99 4.58a.996.996 0 00-1.41 0 .996.996 0 000 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41L5.99 4.58zm12.37 12.37a.996.996 0 00-1.41 0 .996.996 0 000 1.41l1.06 1.06c.39.39 1.03.39 1.41 0s.39-1.03 0-1.41l-1.06-1.06zm1.06-10.96a.996.996 0 000-1.41.996.996 0 00-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06zM7.05 18.36a.996.996 0 000-1.41.996.996 0 00-1.41 0l-1.06 1.06c-.39.39-.39 1.03 0 1.41s1.03.39 1.41 0l1.06-1.06z",
            // Partly cloudy / Cloudy
            1 or 2 => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM19 18H6c-2.21 0-4-1.79-4-4 0-2.05 1.53-3.76 3.56-3.97l1.07-.11.5-.95C8.08 7.14 9.94 6 12 6c2.62 0 4.88 1.86 5.39 4.43l.3 1.5 1.53.11c1.56.1 2.78 1.41 2.78 2.96 0 1.65-1.35 3-3 3z",
            // Overcast
            3 => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z",
            // Rain / Drizzle
            51 or 53 or 55 or 61 or 63 or 65 or 80 or 81 or 82 => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM8 22c-.55 0-1-.45-1-1v-2c0-.55.45-1 1-1s1 .45 1 1v2c0 .55-.45 1-1 1zm4 0c-.55 0-1-.45-1-1v-2c0-.55.45-1 1-1s1 .45 1 1v2c0 .55-.45 1-1 1zm4 0c-.55 0-1-.45-1-1v-2c0-.55.45-1 1-1s1 .45 1 1v2c0 .55-.45 1-1 1z",
            // Snow
            71 or 73 or 75 or 77 or 85 or 86 => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM8 21.5a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm4 0a1.5 1.5 0 110-3 1.5 1.5 0 010 3zm4 0a1.5 1.5 0 110-3 1.5 1.5 0 010 3z",
            // Thunderstorm
            95 or 96 or 99 => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96zM11 23l2-5h-3l2-5h3l-2 5h3l-2 5z",
            // Fog
            _ => "M19.35 10.04C18.67 6.59 15.64 4 12 4 9.11 4 6.6 5.64 5.35 8.04 2.34 8.36 0 10.91 0 14c0 3.31 2.69 6 6 6h13c2.76 0 5-2.24 5-5 0-2.64-2.05-4.78-4.65-4.96z"
        };

        try
        {
            return StreamGeometry.Parse(pathData);
        }
        catch
        {
            return new StreamGeometry();
        }
    }

    private class OpenMeteoResponse
    {
        [JsonPropertyName("current")]
        public CurrentData? Current { get; set; }

        [JsonPropertyName("daily")]
        public DailyData? Daily { get; set; }
    }

    private class CurrentData
    {
        [JsonPropertyName("temperature_2m")]
        public double Temperature2m { get; set; }

        [JsonPropertyName("weathercode")]
        public int WeatherCode { get; set; }
    }

    private class DailyData
    {
        [JsonPropertyName("temperature_2m_max")]
        public double[]? Max { get; set; }

        [JsonPropertyName("temperature_2m_min")]
        public double[]? Min { get; set; }
    }
}
