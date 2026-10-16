using DeskCanvas.Runtime;

var failures = new List<string>();
void Check(string n, bool ok) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {n}"); if (!ok) failures.Add(n); }
var calls = 0;
var provider = new WeatherSharedDataProvider(async (request, _) => { Interlocked.Increment(ref calls); await Task.Delay(30); return new WeatherSharedValue(request.Latitude, 1, 2, 0); }, TimeSpan.FromMinutes(1));
var aTask = provider.AcquireAsync("weather-1", new WeatherRequest(1.23456, 2.34567));
var bTask = provider.AcquireAsync("fixed-1", new WeatherRequest(1.23456, 2.34567));
using var a = await aTask; using var b = await bTask;
Check("same normalized key single-flights", calls == 1 && provider.Statistics.Coalesced >= 1);
Check("different owners retain independent leases", provider.Statistics.ActiveOwners == 2);
using var c = await provider.AcquireAsync("weather-2", new WeatherRequest(1.23456, 2.34567, "fahrenheit"));
Check("unit remains part of key", calls == 2);
a.Dispose(); Check("releasing one owner keeps other active", provider.Statistics.ActiveOwners == 2);
b.Dispose(); c.Dispose(); Check("all leases release", provider.Statistics.ActiveOwners == 0);

var pending = new TaskCompletionSource<WeatherSharedValue>(TaskCreationOptions.RunContinuationsAsynchronously);
var sharedCalls = 0;
var cancellable = new WeatherSharedDataProvider((_, token) =>
{
    Check("provider fetch is independent of owner cancellation", !token.CanBeCanceled);
    Interlocked.Increment(ref sharedCalls);
    return pending.Task;
});
using var cancelOwner = new CancellationTokenSource();
var cancelled = cancellable.AcquireAsync("cancelled-owner", new(1, 2), cancelOwner.Token);
var survivor = cancellable.AcquireAsync("surviving-owner", new(1, 2));
cancelOwner.Cancel();
try { await cancelled; Check("cancelled owner throws", false); }
catch (OperationCanceledException) { Check("cancelling owner releases only its wait", cancellable.Statistics.ActiveOwners == 1); }
pending.SetResult(new(20, 0, 25, 10));
using (var survivingLease = await survivor)
{
    Check("other owner completes from original fetch", sharedCalls == 1 && survivingLease.Snapshot.Value?.Temperature == 20);
}
Check("cancelled and surviving owners both release", cancellable.Statistics.ActiveOwners == 0);

var refreshCalls = 0;
var refreshing = new WeatherSharedDataProvider((_, _) => Task.FromResult(new WeatherSharedValue(++refreshCalls, 0, 2, 0)), TimeSpan.FromMilliseconds(30));
using var oldLease = await refreshing.AcquireAsync("old", new(5, 6));
using var cachedLease = await refreshing.AcquireAsync("cached", new(5, 6, " CELSIUS "));
Check("cache leases share stable completion metadata", oldLease.Snapshot.Version == cachedLease.Snapshot.Version && oldLease.Snapshot.FetchedAt == cachedLease.Snapshot.FetchedAt && refreshCalls == 1);
await Task.Delay(60);
using var refreshedLease = await refreshing.AcquireAsync("fresh", new(5, 6));
Check("TTL refresh preserves old lease accounting", refreshCalls == 2 && refreshing.Statistics.ActiveOwners == 3 && refreshedLease.Snapshot.Version > oldLease.Snapshot.Version);
oldLease.Dispose(); cachedLease.Dispose(); refreshedLease.Dispose();
Check("expired and fresh leases release independently", refreshing.Statistics.ActiveOwners == 0);

var attempts = 0;
var retrying = new WeatherSharedDataProvider((_, _) => ++attempts == 1
    ? Task.FromException<WeatherSharedValue>(new IOException("offline"))
    : Task.FromResult(new WeatherSharedValue(10, 0, 20, 5)));
try { await retrying.AcquireAsync("failure", new(3, 4)); } catch (IOException) { }
using (var retry = await retrying.AcquireAsync("retry", new(3, 4)))
    Check("failed request releases owner and retries", attempts == 2 && retrying.Statistics.Failures == 1 && retrying.Statistics.ActiveOwners == 1);
Check("retry lease releases", retrying.Statistics.ActiveOwners == 0);

var metricsGate = new TaskCompletionSource<SystemMetricValue>(TaskCreationOptions.RunContinuationsAsynchronously);
var reads = 0;
var metrics = new SystemMetricsSharedProvider((_, _) => { reads++; return metricsGate.Task; });
var single = metrics.AcquireAsync("monitor-single", SystemMetric.CpuUsage);
var dashboard = metrics.AcquireAsync("monitor-dashboard", SystemMetric.CpuUsage);
metricsGate.SetResult(new(.42));
using var singleLease = await single;
using var dashboardLease = await dashboard;
Check("metrics consumers single-flight native collection", reads == 1 && metrics.Statistics.Coalesced == 1 && singleLease.Snapshot.Value?.Ratio == .42);
singleLease.Dispose();
Check("metrics leases retain independent owners", metrics.Statistics.ActiveOwners == 1);
dashboardLease.Dispose();
using (var metricCache = await metrics.AcquireAsync("monitor-cache", SystemMetric.CpuUsage))
    Check("metrics TTL reuses successful sample", reads == 1 && metrics.Statistics.CacheHits == 1);
Check("metrics owners return to zero", metrics.Statistics.ActiveOwners == 0);

var slowGate = new TaskCompletionSource<WeatherSharedValue>(TaskCreationOptions.RunContinuationsAsynchronously);
var slowCalls = 0;
var slowProvider = new WeatherSharedDataProvider((_, _) => { slowCalls++; return slowGate.Task; }, TimeSpan.FromMilliseconds(20));
var firstSlow = slowProvider.AcquireAsync("first-slow", new(7, 8));
await Task.Delay(50);
var secondSlow = slowProvider.AcquireAsync("second-slow", new(7, 8));
Check("slow request stays single-flight beyond TTL", slowCalls == 1);
slowGate.SetResult(new(1, 0, 2, 0));
using (var slowA = await firstSlow)
using (var slowB = await secondSlow)
    Check("TTL starts from success completion", !slowA.Snapshot.IsStale(DateTimeOffset.UtcNow) && slowA.Snapshot.FetchedAt == slowB.Snapshot.FetchedAt);

using var alreadyCancelled = new CancellationTokenSource();
alreadyCancelled.Cancel();
var beforeCancelled = slowProvider.Statistics.Requests;
try { await slowProvider.AcquireAsync("already-cancelled", new(9, 10), alreadyCancelled.Token); }
catch (OperationCanceledException) { }
Check("pre-cancelled owner starts no request or lease", slowProvider.Statistics.Requests == beforeCancelled && slowProvider.Statistics.ActiveOwners == 0);
Console.WriteLine(failures.Count == 0 ? "WeatherSharedChecks ALL PASS" : $"WeatherSharedChecks FAILED: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;
