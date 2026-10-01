using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using FixedWidgets.Models;
using DeskCanvas.Core.Services;

namespace FixedWidgets.Services;

public record WeatherInfo(
    double Temperature,
    int WeatherCode,
    string ConditionText,
    double MaxTemp,
    double MinTemp,
    StreamGeometry Icon);

public class OpenMeteoWeatherService : IDisposable
{
    private readonly HttpClient httpClient = ProxySettings.CreateHttpClient();

    /// <summary>Set once <see cref="Dispose"/> ran; makes disposal idempotent.</summary>
    private bool disposed;

    public async Task<List<City>?> SearchCitiesAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        try
        {
            var language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            if (string.IsNullOrEmpty(language)) language = "zh";

            var url = $"https://geocoding-api.open-meteo.com/v1/search?" +
                      $"name={Uri.EscapeDataString(query.Trim())}&" +
                      $"count=10&language={language}&format=json";

            using var resp = await httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
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
            var latStr = string.Format(CultureInfo.InvariantCulture, "{0:F4}", latitude);
            var lonStr = string.Format(CultureInfo.InvariantCulture, "{0:F4}", longitude);

            var url = $"https://api.open-meteo.com/v1/forecast?latitude={latStr}&longitude={lonStr}" +
                      $"&current=temperature_2m,weathercode&daily=temperature_2m_max,temperature_2m_min" +
                      $"&temperature_unit={unit}&timezone=auto";

            using var resp = await httpClient.GetAsync(url).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            var data = JsonSerializer.Deserialize<OpenMeteoResponse>(json);
            if (data?.Current == null) return null;

            double curTemp = data.Current.Temperature2m;
            int code = data.Current.WeatherCode;
            double maxTemp = data.Daily?.Max != null && data.Daily.Max.Length > 0 ? data.Daily.Max[0] : curTemp;
            double minTemp = data.Daily?.Min != null && data.Daily.Min.Length > 0 ? data.Daily.Min[0] : curTemp;

            string condition = GetConditionText(code);
            StreamGeometry icon = GetWeatherIcon(code);

            return new WeatherInfo(curTemp, code, condition, maxTemp, minTemp, icon);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Release the service's own <see cref="HttpClient"/> (and through it the socket pool of
    /// its handler). The widget drops its service when the view is unloaded, so without this
    /// every view instance — each settings save, each Gallery visit — would keep a client and
    /// its connections alive for the lifetime of the process. Idempotent.
    /// </summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    public static string GetConditionText(int code) => code switch
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
    };

    public static StreamGeometry GetWeatherIcon(int code)
    {
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
