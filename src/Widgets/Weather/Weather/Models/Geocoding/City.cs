using System.Text.Json.Serialization;

namespace Weather.Models.Geocoding;

public class City
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    
    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }
    
    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }
    
    [JsonPropertyName("country")]
    public string Country { get; set; } = string.Empty;

    public string SearchName => $"{Name}, {Country}";
}