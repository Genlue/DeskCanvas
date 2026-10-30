using System;

namespace Map.Services;

/// <summary>
/// Web Mercator (EPSG:3857) projection calculations for slippy map tiles.
/// </summary>
public static class MapProjection
{
    public const double EarthCircumferenceMeters = 40075016.686;

    /// <summary>
    /// Convert geographic coordinates (latitude, longitude) to world pixel coordinates at the given zoom level.
    /// Tile size is standard 256x256.
    /// </summary>
    public static (double X, double Y) LatLonToWorldPixels(double lat, double lon, double zoom)
    {
        var scale = 256.0 * Math.Pow(2.0, zoom);
        var x = (lon + 180.0) / 360.0 * scale;

        var clampedLat = Math.Clamp(lat, -85.05112878, 85.05112878);
        var latRad = clampedLat * Math.PI / 180.0;
        var y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) * 0.5 * scale;

        return (x, y);
    }

    /// <summary>
    /// Convert world pixel coordinates back to geographic coordinates (latitude, longitude) at the given zoom level.
    /// </summary>
    public static (double Lat, double Lon) WorldPixelsToLatLon(double x, double y, double zoom)
    {
        var scale = 256.0 * Math.Pow(2.0, zoom);
        var lon = (x / scale) * 360.0 - 180.0;

        var yFrac = 0.5 - (y / scale);
        var latRad = Math.Atan(Math.Sinh(yFrac * 2.0 * Math.PI));
        var lat = latRad * 180.0 / Math.PI;

        return (lat, lon);
    }

    /// <summary>
    /// Calculate ground resolution (meters per pixel) at a specific latitude and zoom level.
    /// Useful for iPad-style scale bar calculation.
    /// </summary>
    public static double MetersPerPixel(double lat, double zoom)
    {
        var clampedLat = Math.Clamp(lat, -85.05112878, 85.05112878);
        var latRad = clampedLat * Math.PI / 180.0;
        return (EarthCircumferenceMeters * Math.Cos(latRad)) / (256.0 * Math.Pow(2.0, zoom));
    }
}
