using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FixedWidgets.Models;

public class City
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("admin1")]
    public string? Admin1 { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    public string SearchName
    {
        get
        {
            if (!string.IsNullOrEmpty(Admin1) && !string.Equals(Admin1, Name, StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(Country) ? $"{Name} ({Admin1}, {Country})" : $"{Name} ({Admin1})";
            }
            return !string.IsNullOrEmpty(Country) ? $"{Name}, {Country}" : Name;
        }
    }
}

public class GeocodingResponse
{
    [JsonPropertyName("results")]
    public List<City>? Results { get; set; }
}
