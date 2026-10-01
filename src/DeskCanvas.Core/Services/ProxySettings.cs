using System.Net.Http;
using System.Text.Json;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Services;

/// <summary>
/// Resolves the HTTP proxy configuration from <c>appSettings.json</c>
/// (shared by the main app and the widget plugins).
/// <para>
/// <see cref="AppSettings.HttpProxy"/> semantics:
/// <c>null</c>/empty = direct connection (bypass the system proxy — fixes the
/// stale-proxy issue when a proxy client is shut down), <c>"system"</c> = use
/// the Windows system proxy, anything else = custom proxy URL.
/// </para>
/// </summary>
public static class ProxySettings
{
    /// <summary>
    /// Create an <see cref="HttpClient"/> whose handler follows the configured proxy setting.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = CreateHandler();
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Create an <see cref="HttpClientHandler"/> following the configured proxy setting.
    /// </summary>
    public static HttpClientHandler CreateHandler()
    {
        var handler = new HttpClientHandler { UseProxy = true };
        var proxy = GetProxySetting();

        if (string.IsNullOrEmpty(proxy) || proxy == "system")
        {
            // "system" → default handler behavior (system proxy); null/empty → bypass it.
            handler.UseProxy = proxy == "system";
        }
        else
        {
            handler.UseProxy = true;
            handler.Proxy = new System.Net.WebProxy(proxy);
        }

        return handler;
    }

    /// <summary>
    /// Read the <see cref="AppSettings.HttpProxy"/> value straight from the settings file.
    /// Reads the file directly so widget plugins can use it without DI.
    /// </summary>
    public static string? GetProxySetting()
    {
        try
        {
            if (!File.Exists(Const.AppSettingsFile)) return null;
            var json = File.ReadAllText(Const.AppSettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json)?.HttpProxy;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
