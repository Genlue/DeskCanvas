namespace Map.Models;

/// <summary>
/// Configuration model for the Map widget, stored in layout.json.
/// </summary>
/// <param name="Latitude">Center point latitude (WGS-84 / GCJ-02).</param>
/// <param name="Longitude">Center point longitude (WGS-84 / GCJ-02).</param>
/// <param name="Zoom">Zoom level (3 to 18).</param>
/// <param name="Provider">Map tile provider and style.</param>
/// <param name="LocationName">Friendly display name of the place (e.g. "北京 · 故宫博物院").</param>
/// <param name="LocationDescription">Optional subtitle / description or address.</param>
/// <param name="ShowPin">Whether to display the iPad-style center marker pin.</param>
/// <param name="ShowInfoPill">Whether to display the floating location info card.</param>
/// <param name="ShowControls">Whether to display the 4-theme floating control buttons.</param>
/// <param name="ShowScaleBar">Whether to display the Apple-style scale indicator.</param>
/// <param name="AllowMapDrag">Whether dragging on the map surface pans the map.</param>
/// <param name="BaiduApiKey">Optional Baidu Maps API Key (AK).</param>
public record MapModel(
    double Latitude = 39.9042,
    double Longitude = 116.4074,
    int Zoom = 14,
    MapProvider Provider = MapProvider.Amap,
    string? LocationName = "北京 · 故宫博物院",
    string? LocationDescription = "北京市东城区景山前街4号",
    bool ShowPin = true,
    bool ShowInfoPill = true,
    bool ShowControls = true,
    bool ShowScaleBar = true,
    bool AllowMapDrag = true,
    string? BaiduApiKey = null,
    bool AutoLocate = false,
    string? LocatedCity = null
)
{
    public const int MinZoom = 3;
    public const int MaxZoom = 18;

    public int ClampedZoom => Math.Clamp(Zoom, MinZoom, MaxZoom);
}
