using DeskCanvas.Runtime;

namespace FixedWidgets.Services;

public sealed class SharedWeatherAdapter(WeatherSharedDataProvider provider, string owner)
{
    public Task<WeatherSharedDataProvider.WeatherSharedLease> GetAsync(double latitude, double longitude, string unit = "celsius", CancellationToken cancellationToken = default) =>
        provider.AcquireAsync(owner, new WeatherRequest(latitude, longitude, unit), cancellationToken);
}
