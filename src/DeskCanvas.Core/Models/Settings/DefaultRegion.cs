using System;

namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// 默认地区: the place location-aware widgets (weather, map, …) start from.
/// <para>
/// Stored in <c>appSettings.json</c>; <c>null</c> means "not configured", and every consumer then
/// keeps its own historical fallback. A widget that carries an explicit location in its own
/// settings keeps that location — the default region is only the starting point for widgets that
/// never had one, which is what "优先调用默认地区（可更改）" means in practice.
/// </para>
/// </summary>
/// <param name="Name">Display name of the place (e.g. 杭州).</param>
/// <param name="Latitude">WGS-84 latitude.</param>
/// <param name="Longitude">WGS-84 longitude.</param>
public record DefaultRegion(string Name, double Latitude, double Longitude)
{
    /// <summary>Lowest latitude a Web-Mercator map can show (the poles are not projectable).</summary>
    public const double MinLatitude = -85d;

    /// <summary>Highest latitude a Web-Mercator map can show.</summary>
    public const double MaxLatitude = 85d;

    /// <summary>Used when nothing is configured — the historical fallback of the weather widget.</summary>
    public static DefaultRegion Beijing { get; } = new("北京", 39.9042, 116.4074);

    /// <summary>Whether the record can be used as a location (non-empty name, real coordinates).</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Name) && IsValidCoordinate(Latitude, Longitude);

    /// <summary>Whether a latitude / longitude pair is finite and inside the projectable range.</summary>
    public static bool IsValidCoordinate(double latitude, double longitude) =>
        double.IsFinite(latitude) && double.IsFinite(longitude) &&
        latitude is >= MinLatitude and <= MaxLatitude &&
        longitude is >= -180d and <= 180d;

    /// <summary>Trimmed name and coordinates clamped into the projectable range.</summary>
    public DefaultRegion Normalized() => this with
    {
        Name = (Name ?? string.Empty).Trim(),
        Latitude = Math.Clamp(double.IsFinite(Latitude) ? Latitude : Beijing.Latitude, MinLatitude, MaxLatitude),
        Longitude = NormalizeLongitude(Longitude),
    };

    /// <summary>Wrap a longitude into [-180, 180).</summary>
    public static double NormalizeLongitude(double longitude)
    {
        if (!double.IsFinite(longitude)) return Beijing.Longitude;
        var wrapped = (longitude + 180d) % 360d;
        if (wrapped < 0) wrapped += 360d;
        return wrapped - 180d;
    }
}
