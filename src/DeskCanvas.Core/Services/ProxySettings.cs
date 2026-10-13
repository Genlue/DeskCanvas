using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Services;

/// <summary>
/// Configured HTTP proxy mode.
/// </summary>
public enum ProxyModeKind
{
    /// <summary>Automatically detect active proxy (system proxy, environment variables, common local ports) and fall back to direct connection.</summary>
    Auto,
    /// <summary>Direct connection, bypassing all proxies.</summary>
    Direct,
    /// <summary>Use the Windows system proxy configuration.</summary>
    System,
    /// <summary>Use a user-specified custom proxy URL.</summary>
    Custom
}

/// <summary>
/// Indicates how the effective proxy endpoint was resolved.
/// </summary>
public enum ProxySourceKind
{
    /// <summary>Direct connection (no proxy).</summary>
    Direct,
    /// <summary>Windows system proxy (Registry Internet Settings / WinINET).</summary>
    SystemProxy,
    /// <summary>Environment variable (HTTPS_PROXY / HTTP_PROXY / ALL_PROXY).</summary>
    EnvironmentVariable,
    /// <summary>Auto-detected local proxy client port (e.g. 7890, 7897, 10809).</summary>
    LocalPort,
    /// <summary>Custom user-configured proxy URL.</summary>
    CustomProxy
}

/// <summary>
/// Result of resolving the effective proxy for a network request.
/// </summary>
/// <param name="ProxyUrl">Normalized proxy URL (e.g. <c>http://127.0.0.1:7890</c>), or <c>null</c> for direct connection.</param>
/// <param name="Source">Where the proxy setting came from.</param>
/// <param name="DisplayEndpoint">Short human-readable endpoint (e.g. <c>127.0.0.1:7890</c>).</param>
public record ResolvedProxy(
    string? ProxyUrl,
    ProxySourceKind Source,
    string? DisplayEndpoint = null)
{
    /// <summary>Whether a proxy is used instead of a direct connection.</summary>
    public bool UsesProxy => !string.IsNullOrWhiteSpace(ProxyUrl);
}

/// <summary>
/// Resolves the HTTP proxy configuration from <c>appSettings.json</c>
/// (shared by the main app and the widget plugins).
/// <para>
/// <see cref="AppSettings.HttpProxy"/> semantics:
/// <c>null</c>/empty/<c>"auto"</c> = auto-detect active proxy (system proxy, environment variables,
/// common local proxy ports with TCP liveness verification, falling back to direct connection when
/// no proxy is active), <c>"direct"</c> = direct connection (bypass all proxies),
/// <c>"system"</c> = use the Windows system proxy, anything else = custom proxy URL.
/// </para>
/// </summary>
public static class ProxySettings
{
    /// <summary>
    /// Common local proxy ports used by mainstream proxy clients on Windows
    /// (Clash, Mihomo, Clash Verge, v2rayN, NekoBox, Sing-box, Shadowsocks).
    /// </summary>
    private static readonly (int Port, string Scheme)[] CommonLocalProxyPorts =
    [
        (7890, "http"),    // Clash / Mihomo / CFW / FlClash
        (7897, "http"),    // Clash Verge / Clash Verge Rev
        (10809, "http"),   // v2rayN HTTP
        (7891, "http"),    // Clash HTTP/Mixed secondary
        (2080, "http"),    // NekoBox / Sing-box
        (10800, "http"),   // V2Ray / Netch
        (10808, "socks5"), // v2rayN SOCKS5/Mixed
        (1080, "socks5")   // Shadowsocks / SOCKS5
    ];

    private static readonly string[] ProxyEnvVarNames =
    [
        "HTTPS_PROXY",
        "https_proxy",
        "HTTP_PROXY",
        "http_proxy",
        "ALL_PROXY",
        "all_proxy"
    ];

    /// <summary>
    /// Parse the raw <see cref="AppSettings.HttpProxy"/> string into a <see cref="ProxyModeKind"/>.
    /// </summary>
    public static ProxyModeKind GetMode(string? rawProxy)
    {
        if (string.IsNullOrWhiteSpace(rawProxy) ||
            rawProxy.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyModeKind.Auto;
        }

        if (rawProxy.Equals("direct", StringComparison.OrdinalIgnoreCase) ||
            rawProxy.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyModeKind.Direct;
        }

        if (rawProxy.Equals("system", StringComparison.OrdinalIgnoreCase))
        {
            return ProxyModeKind.System;
        }

        return ProxyModeKind.Custom;
    }

    /// <summary>
    /// Create an <see cref="HttpClient"/> whose handler follows the configured proxy setting.
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        var handler = CreateHandler();
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Create an <see cref="HttpClient"/> for a specific proxy setting string and optional timeout.
    /// </summary>
    public static HttpClient CreateHttpClient(string? configuredProxy, TimeSpan? timeout = null)
    {
        var resolved = ResolveProxy(configuredProxy);
        return CreateHttpClient(resolved, timeout);
    }

    /// <summary>
    /// Create an <see cref="HttpClient"/> using an already-resolved <see cref="ResolvedProxy"/>.
    /// </summary>
    public static HttpClient CreateHttpClient(ResolvedProxy resolved, TimeSpan? timeout = null)
    {
        var handler = CreateHandler(resolved);
        return new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(15) };
    }

    /// <summary>
    /// Create an <see cref="HttpClientHandler"/> following the configured proxy setting in <c>appSettings.json</c>.
    /// </summary>
    public static HttpClientHandler CreateHandler()
    {
        var resolved = ResolveProxy(GetProxySetting());
        return CreateHandler(resolved);
    }

    /// <summary>
    /// Create an <see cref="HttpClientHandler"/> for a specific proxy setting string.
    /// </summary>
    public static HttpClientHandler CreateHandler(string? configuredProxy)
    {
        var resolved = ResolveProxy(configuredProxy);
        return CreateHandler(resolved);
    }

    /// <summary>
    /// Create an <see cref="HttpClientHandler"/> for an already-resolved <see cref="ResolvedProxy"/>.
    /// </summary>
    public static HttpClientHandler CreateHandler(ResolvedProxy resolved)
    {
        if (!resolved.UsesProxy || string.IsNullOrWhiteSpace(resolved.ProxyUrl))
        {
            return new HttpClientHandler
            {
                UseProxy = false,
                Proxy = null
            };
        }

        return new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(resolved.ProxyUrl)
        };
    }

    /// <summary>
    /// Resolve the effective proxy according to the configured mode (Auto, Direct, System, or Custom).
    /// </summary>
    public static ResolvedProxy ResolveProxy(string? configuredProxy = null, Uri? targetUri = null)
    {
        var raw = configuredProxy ?? GetProxySetting();
        var mode = GetMode(raw);

        switch (mode)
        {
            case ProxyModeKind.Direct:
                return new ResolvedProxy(null, ProxySourceKind.Direct);

            case ProxyModeKind.Custom:
            {
                var normalized = NormalizeProxyUrl(raw);
                if (normalized != null && Uri.TryCreate(normalized, UriKind.Absolute, out var customUri))
                {
                    return new ResolvedProxy(normalized, ProxySourceKind.CustomProxy, $"{customUri.Host}:{customUri.Port}");
                }
                return new ResolvedProxy(null, ProxySourceKind.Direct);
            }

            case ProxyModeKind.System:
            {
                var sysUrl = TryGetWindowsSystemProxy(targetUri);
                if (sysUrl != null && Uri.TryCreate(sysUrl, UriKind.Absolute, out var sysUri))
                {
                    return new ResolvedProxy(sysUrl, ProxySourceKind.SystemProxy, $"{sysUri.Host}:{sysUri.Port}");
                }
                return new ResolvedProxy(null, ProxySourceKind.Direct);
            }

            case ProxyModeKind.Auto:
            default:
                return DetectActiveProxy(targetUri);
        }
    }

    /// <summary>
    /// Automatically detect an active, reachable proxy:
    /// 1. Live Windows system proxy (Registry Internet Settings / PAC) verified via TCP handshake.
    /// 2. Environment variables (<c>HTTPS_PROXY</c>, <c>HTTP_PROXY</c>, <c>ALL_PROXY</c>) verified via TCP handshake.
    /// 3. Common local proxy ports (7890, 7897, 10809, etc.) currently listening and accepting TCP connections.
    /// 4. Falls back to direct connection if no proxy is active.
    /// </summary>
    public static ResolvedProxy DetectActiveProxy(Uri? targetUri = null)
    {
        var activeLocalPorts = GetActiveLocalTcpPorts();

        // 1. Check Windows system proxy (live registry + PAC)
        var sysUrl = TryGetWindowsSystemProxy(targetUri);
        if (sysUrl != null && Uri.TryCreate(sysUrl, UriKind.Absolute, out var sysUri))
        {
            if (IsEndpointReachable(sysUri, activeLocalPorts, timeoutMs: 250))
            {
                return new ResolvedProxy(sysUrl, ProxySourceKind.SystemProxy, $"{sysUri.Host}:{sysUri.Port}");
            }
        }

        // 2. Check environment variables (Process -> User -> Machine)
        var envUrl = TryGetEnvironmentProxy();
        if (envUrl != null && Uri.TryCreate(envUrl, UriKind.Absolute, out var envUri))
        {
            if (IsEndpointReachable(envUri, activeLocalPorts, timeoutMs: 250))
            {
                return new ResolvedProxy(envUrl, ProxySourceKind.EnvironmentVariable, $"{envUri.Host}:{envUri.Port}");
            }
        }

        // 3. Probe common local proxy ports (instant check against OS listening table + fast TCP handshake)
        foreach (var (port, scheme) in CommonLocalProxyPorts)
        {
            if (activeLocalPorts.Count > 0 && !activeLocalPorts.Contains(port))
                continue;

            if (TryTcpConnect("127.0.0.1", port, timeoutMs: 150))
            {
                return new ResolvedProxy($"{scheme}://127.0.0.1:{port}", ProxySourceKind.LocalPort, $"127.0.0.1:{port}");
            }
        }

        return new ResolvedProxy(null, ProxySourceKind.Direct);
    }

    /// <summary>
    /// Read the <see cref="AppSettings.HttpProxy"/> value straight from the settings file.
    /// Reads the file directly so widget plugins can use it without DI.
    /// </summary>
    public static string? GetProxySetting()
    {
        try
        {
            if (!File.Exists(Const.AppSettingsFile)) return "auto";
            var json = File.ReadAllText(Const.AppSettingsFile);
            return JsonSerializer.Deserialize<AppSettings>(json)?.HttpProxy;
        }
        catch (Exception)
        {
            return "auto";
        }
    }

    /// <summary>
    /// Reads the live Windows system proxy from Registry (<c>Internet Settings</c>), falling back to
    /// <see cref="WebRequest.GetSystemWebProxy"/> when a PAC <c>AutoConfigURL</c> is present.
    /// </summary>
    public static string? TryGetWindowsSystemProxy(Uri? targetUri = null)
    {
        bool checkedRegistry = false;
        bool hasAutoConfigUrl = false;

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Internet Settings", writable: false);
                if (key != null)
                {
                    checkedRegistry = true;
                    var proxyEnableObj = key.GetValue("ProxyEnable");
                    bool enabled = proxyEnableObj switch
                    {
                        int i => i != 0,
                        long l => l != 0,
                        byte[] b when b.Length > 0 => b[0] != 0,
                        _ => false
                    };

                    if (enabled)
                    {
                        var proxyServer = key.GetValue("ProxyServer") as string;
                        var parsed = ParseWindowsProxyServer(proxyServer);
                        if (!string.IsNullOrWhiteSpace(parsed))
                            return parsed;
                    }

                    var autoConfig = key.GetValue("AutoConfigURL") as string;
                    hasAutoConfigUrl = !string.IsNullOrWhiteSpace(autoConfig);
                }
            }
            catch
            {
                // Ignore registry access errors
            }
        }

        // If we successfully read the Windows Registry and both ProxyEnable and AutoConfigURL are off,
        // do not fall back to WebRequest.GetSystemWebProxy() which may hold a stale startup cache.
        if (checkedRegistry && !hasAutoConfigUrl)
            return null;

        try
        {
            var probeUri = targetUri ?? new Uri("https://api.github.com");
            var systemProxy = WebRequest.GetSystemWebProxy();
            if (!systemProxy.IsBypassed(probeUri))
            {
                var proxyUri = systemProxy.GetProxy(probeUri);
                if (proxyUri != null && !string.Equals(proxyUri.Host, probeUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return NormalizeProxyUrl(proxyUri.ToString());
                }
            }
        }
        catch
        {
            // Ignore system web proxy lookup errors
        }

        return null;
    }

    /// <summary>
    /// Parses a Windows WinINET <c>ProxyServer</c> string (either <c>host:port</c> or
    /// <c>http=host:port;https=host:port;socks=host:port</c>) into a normalized proxy URL.
    /// </summary>
    public static string? ParseWindowsProxyServer(string? proxyServer)
    {
        if (string.IsNullOrWhiteSpace(proxyServer)) return null;
        proxyServer = proxyServer.Trim();

        if (!proxyServer.Contains('='))
        {
            return NormalizeProxyUrl(proxyServer, "http");
        }

        string? httpsCandidate = null;
        string? httpCandidate = null;
        string? socksCandidate = null;

        var parts = proxyServer.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            var eqIdx = part.IndexOf('=');
            if (eqIdx <= 0 || eqIdx >= part.Length - 1) continue;

            var key = part[..eqIdx].Trim().ToLowerInvariant();
            var val = part[(eqIdx + 1)..].Trim();
            if (string.IsNullOrWhiteSpace(val)) continue;

            switch (key)
            {
                case "https":
                    httpsCandidate = NormalizeProxyUrl(val, "http");
                    break;
                case "http":
                    httpCandidate = NormalizeProxyUrl(val, "http");
                    break;
                case "socks":
                case "socks5":
                    socksCandidate = NormalizeProxyUrl(val, "socks5");
                    break;
            }
        }

        return httpsCandidate ?? httpCandidate ?? socksCandidate;
    }

    /// <summary>
    /// Normalize a raw proxy string into an absolute URI string (prepending <paramref name="defaultScheme"/> if missing).
    /// </summary>
    public static string? NormalizeProxyUrl(string? raw, string defaultScheme = "http")
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Trim();

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = $"{defaultScheme}://{trimmed}";
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host) &&
            uri.Port > 0)
        {
            return $"{uri.Scheme}://{uri.Host}:{uri.Port}";
        }

        return null;
    }

    private static string? TryGetEnvironmentProxy()
    {
        foreach (var name in ProxyEnvVarNames)
        {
            var val = Environment.GetEnvironmentVariable(name);
            var normalized = NormalizeProxyUrl(val);
            if (normalized != null) return normalized;
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var name in ProxyEnvVarNames)
            {
                var val = Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
                          ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
                var normalized = NormalizeProxyUrl(val);
                if (normalized != null) return normalized;
            }
        }

        return null;
    }

    private static HashSet<int> GetActiveLocalTcpPorts()
    {
        try
        {
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var set = new HashSet<int>();
            foreach (var ep in listeners)
            {
                if (IPAddress.IsLoopback(ep.Address) ||
                    ep.Address.Equals(IPAddress.Any) ||
                    ep.Address.Equals(IPAddress.IPv6Any))
                {
                    set.Add(ep.Port);
                }
            }
            return set;
        }
        catch
        {
            return [];
        }
    }

    private static bool IsEndpointReachable(Uri proxyUri, HashSet<int> activeLocalPorts, int timeoutMs)
    {
        if (proxyUri.Port <= 0) return false;

        if (proxyUri.IsLoopback && activeLocalPorts.Count > 0 && !activeLocalPorts.Contains(proxyUri.Port))
        {
            return false;
        }

        return TryTcpConnect(proxyUri.Host, proxyUri.Port, timeoutMs);
    }

    private static bool TryTcpConnect(string host, int port, int timeoutMs)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            return connectTask.Wait(timeoutMs) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}

