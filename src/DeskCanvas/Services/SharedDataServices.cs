using System;
using System.Threading.Tasks;
using DeskCanvas.Core.Services;
using DeskCanvas.Runtime;

namespace DeskCanvas.Services;

/// <summary>One provider per host process, including legacy parameterless plugin constructors.</summary>
public static class SharedDataServices
{
    public static OpenMeteoSharedProvider Weather { get; } = new(async (uri, token) =>
    {
        // Resolve the current proxy on each actual network round. Cache hits and
        // coalesced consumers do not allocate a client or re-probe local ports.
        using var client = ProxySettings.CreateHttpClient();
        return await client.GetStringAsync(uri, token).ConfigureAwait(false);
    });

    public static SystemMetricsSharedProvider SystemMetrics { get; } = new((metric, token) =>
        Task.Run(() => NativeSystemMetrics.Read(metric), token));

    public static StorageDrivesSharedProvider Storage { get; } = new((_, token) =>
        Task.Run(SharedDriveSnapshot.CaptureAll, token));
}
