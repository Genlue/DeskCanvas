using DeskCanvas.Runtime;
using FixedWidgets.Services;
using Monitor.Models;
using Monitor.Services;
using Weather.Services;

var failures = new List<string>();
void Check(string name, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (!ok) failures.Add(name); }
const string forecastJson = """
{"current":{"temperature_2m":21,"weathercode":2,"surface_pressure":1010},"hourly":{"temperature_2m":[21],"weathercode":[2],"uv_index":[3],"is_day":[1]},"daily":{"temperature_2m_min":[10],"temperature_2m_max":[25],"weathercode":[2],"sunrise":["2026-10-16T06:00"],"sunset":["2026-10-16T18:00"]}}
""";
var fetches = new List<Uri>();
var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var shared = new OpenMeteoSharedProvider((uri, _) => { fetches.Add(uri); return gate.Task; });
using var weather = new OpenMeteoWeatherProvider(shared);
using var fixedWeather = new OpenMeteoWeatherService(shared);
var richTask = weather.GetForecastAsync(39.9042, 116.4074, "celsius");
var compactTask = fixedWeather.GetWeatherAsync(39.9042, 116.4074, " CELSIUS ");
gate.SetResult(forecastJson);
var rich = await richTask;
var compact = await compactTask;
Check("Weather and Fixed public entrypoints share one forecast fetch", fetches.Count == 1 && shared.Statistics.Coalesced == 1);
Check("rich Weather fields survive shared payload", rich?.Current.Temperature == 21 && rich.Hourly.UVIndex[0] == 3 && rich.Daily.Sunrise.Count == 1);
Check("Fixed compact projection preserves values", compact is { Temperature: 21, WeatherCode: 2, MaxTemp: 25, MinTemp: 10 });
Check("compatibility calls release all leases", shared.Statistics.ActiveOwners == 0);
var anotherRich = await weather.GetForecastAsync(39.9042, 116.4074, "celsius");
Check("compatibility results do not share mutable plugin models", rich is not null && anotherRich is not null && !ReferenceEquals(rich, anotherRich) && fetches.Count == 1);

var requests = new List<Uri>();
var full = new OpenMeteoSharedProvider((uri, _) =>
{
    requests.Add(uri);
    return Task.FromResult(uri.Host.StartsWith("air-quality") ? "{\"current\":{\"european_aqi\":31}}" : "{\"results\":[{\"name\":\"A & B\",\"latitude\":1,\"longitude\":2}]}");
});
using var weatherSearch = new OpenMeteoWeatherProvider(full);
using var fixedSearch = new OpenMeteoWeatherService(full);
var aqi = await weatherSearch.GetAirQualityAsync(1, 2);
var city = await weatherSearch.GetCitiesAsync("A & B");
var fixedCity = await fixedSearch.SearchCitiesAsync(" A & B ");
Check("AQI public path delegates to shared provider", aqi?.Current.AirQualityIndex == 31);
Check("both geocoding public paths delegate and preserve models", city?[0].Name == "A & B" && fixedCity?[0].Name == "A & B");
Check("geocoding escapes query and preserves Fixed count", requests.Count == 3 && requests.Where(x => x.Host.StartsWith("geocoding")).All(x => x.Query.Contains("name=A%20%26%20B")) && requests.Last().Query.Contains("count=10"));
await weatherSearch.GetCitiesAsync("A & B");
await fixedSearch.SearchCitiesAsync(" A & B ");
Check("geocoding cache and owner release", requests.Count == 3 && full.Statistics.ActiveOwners == 0);

var geocodingGate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var geocodingCalls = 0;
var geocodingProvider = new OpenMeteoSharedProvider((_, _) => { geocodingCalls++; return geocodingGate.Task; });
using var searching = new OpenMeteoWeatherProvider(geocodingProvider);
using var otherSearching = new OpenMeteoWeatherProvider(geocodingProvider);
using var searchCancellation = new CancellationTokenSource();
var cancelledSearch = searching.GetCitiesAsync("New York", searchCancellation.Token);
var liveSearch = otherSearching.GetCitiesAsync("New York");
searchCancellation.Cancel();
Check("superseded city search releases its owner", await cancelledSearch is null && geocodingProvider.Statistics.ActiveOwners == 1);
geocodingGate.SetResult("{\"results\":[{\"name\":\"New York\"}]}");
Check("another city search survives caller cancellation", (await liveSearch)?[0].Name == "New York" && geocodingCalls == 1 && geocodingProvider.Statistics.ActiveOwners == 0);

var slow = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
var cancelling = new OpenMeteoSharedProvider((_, _) => slow.Task);
var removed = new OpenMeteoWeatherProvider(cancelling);
using var surviving = new OpenMeteoWeatherService(cancelling);
var removedWait = removed.GetForecastAsync(1, 2, "celsius");
var survivingWait = surviving.GetWeatherAsync(1, 2);
removed.Dispose();
Check("disposed compatibility owner cancels its wait", await removedWait is null && cancelling.Statistics.ActiveOwners == 1);
slow.SetResult(forecastJson);
Check("other widget survives compatibility owner disposal", (await survivingWait)?.Temperature == 21 && cancelling.Statistics.ActiveOwners == 0);

var nativeReads = 0;
var metricGate = new TaskCompletionSource<SystemMetricValue>(TaskCreationOptions.RunContinuationsAsynchronously);
var metrics = new SystemMetricsSharedProvider((_, _) => { nativeReads++; return metricGate.Task; });
var single = MetricService.GetMetricValue(MetricType.CpuUsage, "single-card", provider: metrics);
var dashboard = MetricService.GetMetricValue(MetricType.CpuUsage, "dashboard", provider: metrics);
metricGate.SetResult(new(.75));
Check("Monitor single/dashboard adapter shares native resource sample", await single == .75 && await dashboard == .75 && nativeReads == 1 && metrics.Statistics.Coalesced == 1);
Check("Monitor adapter releases all owners", metrics.Statistics.ActiveOwners == 0);

Console.WriteLine(failures.Count == 0 ? "ProviderIntegrationChecks ALL PASS" : $"ProviderIntegrationChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
