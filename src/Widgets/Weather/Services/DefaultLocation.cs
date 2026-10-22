using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using Weather.Models;

namespace Weather.Services;

/// <summary>
/// Starting location for a weather widget that has no location of its own (a freshly added
/// widget, or one restored from a layout entry that never stored settings).
/// <para>
/// 默认地区 wins when the user configured one in 设置 › 通用; otherwise the historical
/// Beijing fallback is kept, so an untouched installation behaves exactly as before. A widget
/// that already carries an explicit location is never touched — its stored model is passed in by
/// the host and this helper is not consulted.
/// </para>
/// </summary>
internal static class DefaultLocation
{
    public const string DefaultUnit = "celsius";

    public static ForecastModel Resolve(string unit = DefaultUnit, string? fallbackName = null)
    {
        var region = ReadDefaultRegion();
        if (region != null)
            return new ForecastModel(region.Name, region.Latitude, region.Longitude, unit);

        var name = string.IsNullOrWhiteSpace(fallbackName) ? DefaultRegion.Beijing.Name : fallbackName;
        return new ForecastModel(name, DefaultRegion.Beijing.Latitude, DefaultRegion.Beijing.Longitude, unit);
    }

    /// <summary>The configured 默认地区, or null when the host has none (or is not running).</summary>
    private static DefaultRegion? ReadDefaultRegion()
    {
        try
        {
            var provider = DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
            return provider?.Get().EffectiveDefaultRegion;
        }
        catch
        {
            // Settings are optional for a widget preview / headless host: keep the fallback.
            return null;
        }
    }
}
