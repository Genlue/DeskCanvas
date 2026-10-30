using System.Text.Json.Serialization;

namespace Weather.Models.Forecast;

public class CurrentAirQuality
{
    [JsonPropertyName("european_aqi")]
    public int AirQualityIndex { get; set; }
}