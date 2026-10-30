using System.Text.Json.Serialization;

namespace Weather.Models.Forecast;

public class HourlyForecast
{
    [JsonPropertyName("time")] 
    public List<DateTime> Time { get; set; } = new();

    [JsonPropertyName("temperature_2m")] 
    public List<double> Temperature { get; set; } = new();
    
    [JsonPropertyName("uv_index")]
    public List<double> UVIndex { get; set; } = new();

    [JsonPropertyName("weathercode")] 
    public List<WeatherCode> WeatherCode { get; set; } = new();
    
    [JsonPropertyName("is_day")]
    public List<int> IsDay { get; set; } = new();
}