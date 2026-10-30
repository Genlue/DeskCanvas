using Avalonia.Media;
using Weather.Models.Forecast;
using Weather.ViewModels;

namespace Weather.Services;

public static class WeatherIconProvider
{
    public static StreamGeometry GetIcon(WeatherCode code)
    {
        return code switch
        {
            WeatherCode.ClearSky 
                or WeatherCode.MostlyClear 
                => WeatherIcon.Clear.Value,
            WeatherCode.PartlyCloudy
                => WeatherIcon.PartlyClear.Value,
            WeatherCode.Overcast 
                => WeatherIcon.Cloudy.Value,
            WeatherCode.Fog 
                or WeatherCode.DepositingRimeFog 
                => WeatherIcon.Fog.Value,
            WeatherCode.DrizzleLightIntensity 
                or WeatherCode.DrizzleModerateIntensity 
                or WeatherCode.DrizzleDenseIntensity
                or WeatherCode.FreezingDrizzleLightIntensity
                or WeatherCode.FreezingDrizzleDenseIntensity
                => WeatherIcon.Drizzle.Value,
            WeatherCode.RainSlightIntensity
                or WeatherCode.RainModerateIntensity
                or WeatherCode.FreezingRainLightIntensity
                => WeatherIcon.Rain.Value,
            WeatherCode.RainHeavyIntensity
                or WeatherCode.RainShowersSlightIntensity
                or WeatherCode.RainShowersModerateIntensity
                or WeatherCode.RainShowersViolentIntensity
                or WeatherCode.FreezingRainHeavyIntensity
                => WeatherIcon.HeavyRain.Value,
            WeatherCode.SnowFallSlightIntensity
                or WeatherCode.SnowFallModerateIntensity
                => WeatherIcon.Snow.Value,
            WeatherCode.SnowFallHeavyIntensity
                or WeatherCode.SnowGrains
                or WeatherCode.SnowShowersSlightIntensity
                or WeatherCode.SnowShowersHeavyIntensity
                => WeatherIcon.HeavySnow.Value,
            WeatherCode.ThunderstormSlightIntensity
                or WeatherCode.ThunderstormWithSlightHail
                or WeatherCode.ThunderstormWithHeavyHail
                => WeatherIcon.Thunderstorm.Value,
            _ => new StreamGeometry()
        };
    }
}