using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

public readonly record struct WeatherRequest(double Latitude, double Longitude, string Unit = "celsius")
{
    public WeatherRequest Normalize() => new(Math.Round(Latitude, 4), Math.Round(Longitude, 4),
        string.IsNullOrWhiteSpace(Unit) ? "celsius" : Unit.Trim().ToLowerInvariant());
}

public sealed record WeatherSharedValue(double Temperature, int WeatherCode, double MaxTemperature, double MinTemperature);
public sealed record WeatherSharedStatistics(long Requests, long CacheHits, long Coalesced, long Failures, int ActiveOwners);

/// <summary>Compatibility API for compact weather consumers; uses the common lease/cache engine.</summary>
public sealed class WeatherSharedDataProvider
{
    private readonly SharedDataProvider<WeatherRequest, WeatherSharedValue> data;

    public WeatherSharedDataProvider(Func<WeatherRequest, CancellationToken, Task<WeatherSharedValue>> fetch, TimeSpan? ttl = null) =>
        data = new(fetch, ttl ?? TimeSpan.FromMinutes(5), "open-meteo");

    public WeatherSharedStatistics Statistics
    {
        get
        {
            var statistics = data.Statistics;
            return new(statistics.Requests, statistics.CacheHits, statistics.Coalesced, statistics.Failures, statistics.ActiveOwners);
        }
    }

    public async Task<WeatherSharedLease> AcquireAsync(string owner, WeatherRequest request, CancellationToken cancellationToken = default) =>
        new(await data.AcquireAsync(owner, request.Normalize(), cancellationToken).ConfigureAwait(false));

    public sealed class WeatherSharedLease : IDisposable
    {
        private readonly SharedDataProvider<WeatherRequest, WeatherSharedValue>.Lease lease;
        internal WeatherSharedLease(SharedDataProvider<WeatherRequest, WeatherSharedValue>.Lease lease) => this.lease = lease;
        public DataSnapshot<WeatherSharedValue> Snapshot => lease.Snapshot;
        public bool IsDisposed => lease.IsDisposed;
        public void Dispose() => lease.Dispose();
    }
}
