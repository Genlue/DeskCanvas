using System.Text.Json.Serialization;

namespace Weather.Models.Forecast;

public class AirQualityResponse
{
    [JsonPropertyName("current")]
    public CurrentAirQuality Current { get; set; } = new();
    
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
    
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}