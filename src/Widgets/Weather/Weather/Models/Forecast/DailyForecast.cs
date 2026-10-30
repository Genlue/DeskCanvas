using System.Text.Json.Serialization;

namespace Weather.Models.Forecast;

public class DailyForecast
{
    [JsonPropertyName("time")] 
    public List<DateTime> Time { get; set; } = new();

    [JsonPropertyName("temperature_2m_min")]
    public List<double> Min { get; set; } = new();

    [JsonPropertyName("temperature_2m_max")]
    public List<double> Max { get; set; } = new();

    [JsonPropertyName("weathercode")] 
    public List<WeatherCode> WeatherCode { get; set; } = new();
    
    [JsonPropertyName("sunrise")]
    public List<DateTime> Sunrise { get; set; } = new();
    
    [JsonPropertyName("sunset")]
    public List<DateTime> Sunset { get; set; } = new();
}