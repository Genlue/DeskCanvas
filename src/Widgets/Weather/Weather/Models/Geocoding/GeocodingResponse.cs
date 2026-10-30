using System.Text.Json.Serialization;

namespace Weather.Models.Geocoding;

public class GeocodingResponse
{
    [JsonPropertyName("results")]
    public List<City> Cities { get; set; } = new();
    
    [JsonPropertyName("error")]
    public string Error { get; set; } = string.Empty;
    
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}