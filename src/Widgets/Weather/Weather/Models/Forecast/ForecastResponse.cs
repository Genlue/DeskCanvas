using System.Text.Json.Serialization;

namespace Weather.Models.Forecast;

public class ForecastResponse
{
    [JsonPropertyName("current")]
    public CurrentForecast Current { get; set; } = new();

    [JsonPropertyName("hourly")] 
    public HourlyForecast Hourly { get; set; } = new();

    [JsonPropertyName("daily")] 
    public DailyForecast Daily { get; set; } = new();
        
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
    
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}
