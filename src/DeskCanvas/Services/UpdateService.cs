using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Views;

namespace DeskCanvas.Services;

/// <summary>
/// Status result of checking for updates.
/// </summary>
public enum UpdateCheckResult
{
    UpToDate,
    UpdateAvailable,
    Failed,
    Disabled,
    Ignored
}

/// <summary>
/// Detailed GitHub release metadata for auto-updater.
/// </summary>
public record ReleaseInfo(
    Version Version,
    string TagName,
    string Title,
    string Changelog,
    string ReleasePageUrl,
    string? DownloadUrl,
    string? AssetFileName,
    long AssetFileSize
);

/// <summary>
/// Result of testing network connectivity to the GitHub update endpoint.
/// </summary>
public record GitHubConnectivityResult(
    bool Success,
    int? StatusCode,
    long ElapsedMs,
    ResolvedProxy ProxyUsed,
    string Summary,
    string? ErrorDetail = null
);

public class UpdateService
{
    private readonly IAppSettingsProvider appSettingsProvider;

    public UpdateService(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        TimerService.Timer1Hour.Subscribe(OnTimerTick);
    }

    private void OnTimerTick()
    {
        var settings = appSettingsProvider.Get();
        if (ShouldCheck(settings.UpdateInterval, settings.LastUpdateCheckTime))
        {
            _ = CheckForUpdatesAsync(isManual: false);
        }
    }

    /// <summary>
    /// Checks whether an automatic background check should run given the configured interval and last check timestamp.
    /// </summary>
    public static bool ShouldCheck(UpdateCheckInterval interval, DateTime? lastCheck)
    {
        if (interval == UpdateCheckInterval.Disabled) return false;
        if (interval == UpdateCheckInterval.OnStartup) return false;
        if (lastCheck == null) return true;

        var elapsed = DateTime.UtcNow - lastCheck.Value;
        return interval switch
        {
            UpdateCheckInterval.Every6Hours => elapsed >= TimeSpan.FromHours(6),
            UpdateCheckInterval.Every12Hours => elapsed >= TimeSpan.FromHours(12),
            UpdateCheckInterval.Daily => elapsed >= TimeSpan.FromHours(24),
            UpdateCheckInterval.Weekly => elapsed >= TimeSpan.FromDays(7),
            _ => false
        };
    }

    /// <summary>
    /// Run update check on application startup (respects OnStartup or interval elapsed).
    /// </summary>
    public void CheckForUpdates()
    {
        var settings = appSettingsProvider.Get();
        if (settings.UpdateInterval == UpdateCheckInterval.Disabled) return;

        if (settings.UpdateInterval == UpdateCheckInterval.OnStartup ||
            ShouldCheck(settings.UpdateInterval, settings.LastUpdateCheckTime))
        {
            _ = CheckForUpdatesAsync(isManual: false);
        }
    }

    /// <summary>
    /// Check for updates and show the update popup if an update is found.
    /// </summary>
    public async Task CheckForUpdatesAsync(bool isManual = false)
    {
        var (info, result) = await CheckForUpdatesDetailedAsync(isManual);
        if (result == UpdateCheckResult.UpdateAvailable && info != null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                new UpdatePopup(info, this).Show();
            });
        }
    }

    /// <summary>
    /// Query GitHub release API, compare version, select appropriate architecture installer asset,
    /// and persist the check timestamp and outcome summary (whether connected or failed).
    /// </summary>
    public async Task<(ReleaseInfo? Info, UpdateCheckResult Status)> CheckForUpdatesDetailedAsync(bool isManual = false)
    {
        var settings = appSettingsProvider.Get();
        if (!isManual && settings.UpdateInterval == UpdateCheckInterval.Disabled)
            return (null, UpdateCheckResult.Disabled);

        try
        {
            var (info, proxyUsed, errorReason) = await FetchReleaseInfoWithDiagnosticsAsync().ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var timeStr = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var routeDesc = FormatRouteDescription(proxyUsed, isAutoMode: false);
            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

            if (info == null)
            {
                var failStatus = string.Format(
                    Locales.Locale.Settings_General_Update_Record_Failed,
                    timeStr,
                    errorReason ?? Locales.Locale.Settings_General_Update_Error_Network,
                    routeDesc);
                RecordCheckOutcome(now, failStatus);
                return (null, UpdateCheckResult.Failed);
            }

            bool hasNewVersion = info.Version > currentVersion;
            var statusSummary = hasNewVersion
                ? string.Format(Locales.Locale.Settings_General_Update_Record_FoundNew, timeStr, info.Version, routeDesc)
                : string.Format(Locales.Locale.Settings_General_Update_Record_UpToDate, timeStr, currentVersion, routeDesc);

            var currentSettings = RecordCheckOutcome(now, statusSummary);

            if (!isManual)
            {
                var ignoreVersionText = currentSettings.IgnoreUpdate;
                if (!string.IsNullOrWhiteSpace(ignoreVersionText) &&
                    Version.TryParse(ignoreVersionText, out var ignoreVersion) &&
                    info.Version <= ignoreVersion)
                {
                    return (info, UpdateCheckResult.Ignored);
                }
            }

            if (hasNewVersion)
            {
                return (info, UpdateCheckResult.UpdateAvailable);
            }

            return (info, UpdateCheckResult.UpToDate);
        }
        catch (Exception ex)
        {
            var now = DateTime.UtcNow;
            var timeStr = now.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            var failStatus = string.Format(
                Locales.Locale.Settings_General_Update_Record_Failed,
                timeStr,
                 ex.Message,
                Locales.Locale.Settings_General_Update_Route_Direct);
            RecordCheckOutcome(now, failStatus);
            return (null, UpdateCheckResult.Failed);
        }
    }

    private AppSettings RecordCheckOutcome(DateTime utcNow, string statusSummary)
    {
        var currentSettings = appSettingsProvider.Get();
        var updatedSettings = currentSettings with
        {
            LastUpdateCheckTime = utcNow,
            LastUpdateCheckStatus = statusSummary
        };
        appSettingsProvider.Save(updatedSettings);
        return updatedSettings;
    }

    /// <summary>
    /// Tests connectivity to the GitHub update API endpoint using the currently configured/auto-detected proxy.
    /// </summary>
    public async Task<GitHubConnectivityResult> TestGitHubConnectivityAsync(CancellationToken ct = default)
    {
        var settings = appSettingsProvider.Get();
        var effectiveUrl = settings.EffectiveUpdateUrl;
        var apiUrl = ResolveApiUrl(string.IsNullOrWhiteSpace(effectiveUrl) ? AppSettings.DefaultReleaseApiUrl : effectiveUrl);
        Uri.TryCreate(apiUrl, UriKind.Absolute, out var targetUri);

        bool isAutoMode = ProxySettings.GetMode(settings.HttpProxy) == ProxyModeKind.Auto;
        var resolvedProxy = ProxySettings.ResolveProxy(settings.HttpProxy, targetUri);
        var routeDesc = FormatRouteDescription(resolvedProxy, isAutoMode);

        var sw = Stopwatch.StartNew();
        try
        {
            using var httpClient = ProxySettings.CreateHttpClient(resolvedProxy, TimeSpan.FromSeconds(10));
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-Updater");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await httpClient.GetAsync(apiUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();

            int code = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                var summary = string.Format(
                    Locales.Locale.Settings_General_Update_Connectivity_Success,
                    code,
                    sw.ElapsedMilliseconds,
                    routeDesc);
                return new GitHubConnectivityResult(true, code, sw.ElapsedMilliseconds, resolvedProxy, summary);
            }

            if (code == 403)
            {
                var summary = string.Format(
                    Locales.Locale.Settings_General_Update_Connectivity_RateLimited,
                    sw.ElapsedMilliseconds,
                    routeDesc);
                return new GitHubConnectivityResult(true, code, sw.ElapsedMilliseconds, resolvedProxy, summary);
            }

            var errText = $"HTTP {code}";
            var failSummary = string.Format(
                Locales.Locale.Settings_General_Update_Connectivity_Failed,
                errText,
                sw.ElapsedMilliseconds,
                routeDesc);
            return new GitHubConnectivityResult(false, code, sw.ElapsedMilliseconds, resolvedProxy, failSummary, errText);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var errText = DescribeException(ex, resolvedProxy);
            var failSummary = string.Format(
                Locales.Locale.Settings_General_Update_Connectivity_Failed,
                errText,
                sw.ElapsedMilliseconds,
                routeDesc);
            return new GitHubConnectivityResult(false, null, sw.ElapsedMilliseconds, resolvedProxy, failSummary, errText);
        }
    }

    /// <summary>
    /// Formats a human-readable description of the resolved proxy route.
    /// </summary>
    public static string FormatRouteDescription(ResolvedProxy proxy, bool isAutoMode)
    {
        if (!proxy.UsesProxy)
        {
            return isAutoMode
                ? Locales.Locale.Settings_General_Update_Route_DirectNoProxy
                : Locales.Locale.Settings_General_Update_Route_Direct;
        }

        var endpoint = proxy.DisplayEndpoint ?? proxy.ProxyUrl ?? "";
        return proxy.Source switch
        {
            ProxySourceKind.SystemProxy => string.Format(Locales.Locale.Settings_General_Update_Route_SystemProxy, endpoint),
            ProxySourceKind.EnvironmentVariable => string.Format(Locales.Locale.Settings_General_Update_Route_EnvProxy, endpoint),
            ProxySourceKind.LocalPort => string.Format(Locales.Locale.Settings_General_Update_Route_LocalPort, endpoint),
            ProxySourceKind.CustomProxy => string.Format(Locales.Locale.Settings_General_Update_Route_CustomProxy, endpoint),
            _ => endpoint
        };
    }

    private static string DescribeException(Exception ex, ResolvedProxy proxy)
    {
        if (ex is TaskCanceledException or TimeoutException)
            return Locales.Locale.Settings_General_Update_Error_Timeout;

        var baseEx = ex.GetBaseException();
        if (baseEx is System.Net.Sockets.SocketException sockEx)
        {
            return sockEx.SocketErrorCode switch
            {
                System.Net.Sockets.SocketError.ConnectionRefused => proxy.UsesProxy
                    ? Locales.Locale.Settings_General_Update_Error_ProxyRefused
                    : Locales.Locale.Settings_General_Update_Error_Network,
                System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData =>
                    Locales.Locale.Settings_General_Update_Error_Dns,
                System.Net.Sockets.SocketError.ConnectionReset or System.Net.Sockets.SocketError.ConnectionAborted =>
                    Locales.Locale.Settings_General_Update_Error_TlsOrReset,
                System.Net.Sockets.SocketError.TimedOut =>
                    Locales.Locale.Settings_General_Update_Error_Timeout,
                _ => Locales.Locale.Settings_General_Update_Error_Network
            };
        }

        if (baseEx is System.Security.Authentication.AuthenticationException or IOException)
        {
            return Locales.Locale.Settings_General_Update_Error_TlsOrReset;
        }

        if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
        {
            int code = (int)httpEx.StatusCode.Value;
            return code == 403 ? Locales.Locale.Settings_General_Update_Error_RateLimit : $"HTTP {code}";
        }

        return Locales.Locale.Settings_General_Update_Error_Network;
    }

    /// <summary>
    /// Legacy compatibility method returning the latest Version if newer.
    /// </summary>
    public async Task<Version?> GetUpdateVersionAsync()
    {
        var (info, result) = await CheckForUpdatesDetailedAsync(isManual: true).ConfigureAwait(false);
        return result == UpdateCheckResult.UpdateAvailable ? info?.Version : null;
    }

    /// <summary>
    /// Fetch latest release metadata from GitHub API.
    /// </summary>
    public async Task<ReleaseInfo?> FetchReleaseInfoAsync()
    {
        var (info, _, _) = await FetchReleaseInfoWithDiagnosticsAsync().ConfigureAwait(false);
        return info;
    }

    /// <summary>
    /// Fetch latest release metadata from GitHub API along with the proxy route used and any failure reason.
    /// </summary>
    public async Task<(ReleaseInfo? Info, ResolvedProxy ProxyUsed, string? ErrorReason)> FetchReleaseInfoWithDiagnosticsAsync()
    {
        var settings = appSettingsProvider.Get();
        var effectiveUrl = settings.EffectiveUpdateUrl;
        if (string.IsNullOrWhiteSpace(effectiveUrl))
            return (null, new ResolvedProxy(null, ProxySourceKind.Direct), Locales.Locale.Settings_General_Update_Error_Network);

        var apiUrl = ResolveApiUrl(effectiveUrl);
        Uri.TryCreate(apiUrl, UriKind.Absolute, out var targetUri);

        var resolvedProxy = ProxySettings.ResolveProxy(settings.HttpProxy, targetUri);
        bool isAutoMode = ProxySettings.GetMode(settings.HttpProxy) == ProxyModeKind.Auto;

        var (info, errorReason) = await TryFetchWithProxyAsync(apiUrl, resolvedProxy).ConfigureAwait(false);
        if (info != null)
        {
            return (info, resolvedProxy, null);
        }

        // In Auto mode, if an auto-detected proxy failed (e.g. local port couldn't reach GitHub),
        // automatically try direct connection once as fallback.
        if (isAutoMode && resolvedProxy.UsesProxy)
        {
            var directProxy = new ResolvedProxy(null, ProxySourceKind.Direct);
            var (directInfo, directError) = await TryFetchWithProxyAsync(apiUrl, directProxy).ConfigureAwait(false);
            if (directInfo != null)
            {
                return (directInfo, directProxy, null);
            }
        }

        return (null, resolvedProxy, errorReason);
    }

    private static async Task<(ReleaseInfo? Info, string? ErrorReason)> TryFetchWithProxyAsync(string apiUrl, ResolvedProxy proxy)
    {
        try
        {
            using var httpClient = ProxySettings.CreateHttpClient(proxy);
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-Updater");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

            using var response = await httpClient.GetAsync(apiUrl).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                var reason = code == 403
                    ? Locales.Locale.Settings_General_Update_Error_RateLimit
                    : $"HTTP {code}";
                return (null, reason);
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var title = root.TryGetProperty("name", out var n) ? n.GetString() ?? tagName : tagName;
            var changelog = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var releasePageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            var cleanVer = tagName.TrimStart('v', 'V').Trim();
            if (!Version.TryParse(cleanVer, out var releaseVer))
            {
                var match = Regex.Match(cleanVer, @"\d+(\.\d+)+");
                if (!match.Success || !Version.TryParse(match.Value, out releaseVer))
                {
                    return (null, Locales.Locale.Settings_General_Update_Error_Network);
                }
            }

            var arch = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X64 => "x64",
                Architecture.Arm64 => "arm64",
                Architecture.X86 => "x86",
                _ => "x64"
            };

            string? bestDownloadUrl = null;
            string? bestFileName = null;
            long bestFileSize = 0;
            int bestScore = -1;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    var dUrl = asset.TryGetProperty("browser_download_url", out var bu) ? bu.GetString() ?? "" : "";
                    var size = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;

                    var ext = Path.GetExtension(name).ToLowerInvariant();
                    if (ext != ".msi" && ext != ".exe") continue;

                    int score = 10;
                    if (ext == ".msi") score += 5;

                    var lowerName = name.ToLowerInvariant();
                    bool matchesCurrentArch = lowerName.Contains(arch) || (arch == "x64" && (lowerName.Contains("win64") || lowerName.Contains("win-x64")));
                    bool matchesOtherArch = (arch != "arm64" && lowerName.Contains("arm64")) ||
                                            (arch != "x86" && (lowerName.Contains("x86") || lowerName.Contains("win32"))) ||
                                            (arch != "x64" && (lowerName.Contains("x64") || lowerName.Contains("win64")));

                    if (matchesOtherArch) score -= 100;
                    if (matchesCurrentArch) score += 50;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestDownloadUrl = dUrl;
                        bestFileName = name;
                        bestFileSize = size;
                    }
                }
            }

            return (new ReleaseInfo(
                releaseVer,
                tagName,
                title,
                changelog,
                releasePageUrl,
                bestDownloadUrl,
                bestFileName,
                bestFileSize
            ), null);
        }
        catch (Exception ex)
        {
            return (null, DescribeException(ex, proxy));
        }
    }

    /// <summary>
    /// Resolves web release page URL or custom URL to GitHub API releases endpoint.
    /// </summary>
    public static string ResolveApiUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return AppSettings.DefaultReleaseApiUrl;

        url = url.Trim();

        if (url.Contains("api.github.com", StringComparison.OrdinalIgnoreCase))
            return url;

        if (url.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            var uri = new Uri(url);
            var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                var owner = parts[0];
                var repo = parts[1];
                return $"https://api.github.com/repos/{owner}/{repo}/releases/latest";
            }
        }

        return url;
    }

    /// <summary>
    /// Downloads the installer matching the current release with progress reporting.
    /// </summary>
    public async Task<string> DownloadInstallerAsync(
        ReleaseInfo info,
        IProgress<(long downloaded, long total, double percentage)>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            throw new InvalidOperationException("No download URL found for this release.");

        var updateDir = Path.Combine(Path.GetTempPath(), Const.AppName, "Updates");
        Directory.CreateDirectory(updateDir);

        var fileName = !string.IsNullOrWhiteSpace(info.AssetFileName)
            ? info.AssetFileName
            : $"DeskCanvas-{info.Version}.msi";

        var targetFile = Path.Combine(updateDir, fileName);
        var tempFile = targetFile + ".download";

        if (File.Exists(tempFile))
        {
            try { File.Delete(tempFile); } catch { }
        }

        var handler = ProxySettings.CreateHandler(appSettingsProvider.Get().HttpProxy);
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-Updater");

        using var response = await client.GetAsync(info.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? (info.AssetFileSize > 0 ? info.AssetFileSize : -1L);

        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        await using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            long totalDownloaded = 0;
            int bytesRead;

            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                totalDownloaded += bytesRead;

                double pct = totalBytes > 0 ? (double)totalDownloaded / totalBytes * 100.0 : -1.0;
                progress?.Report((totalDownloaded, totalBytes, pct));
            }
        }

        if (File.Exists(targetFile))
        {
            try { File.Delete(targetFile); } catch { }
        }
        File.Move(tempFile, targetFile);

        return targetFile;
    }

    /// <summary>
    /// Launches the installer (.msi or .exe) and cleanly shuts down the DeskCanvas process.
    /// </summary>
    public void LaunchInstallerAndExit(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Installer file not found", filePath);

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        ProcessStartInfo startInfo = ext == ".msi"
            ? new ProcessStartInfo
            {
                FileName = "msiexec.exe",
                Arguments = $"/i \"{filePath}\"",
                UseShellExecute = true
            }
            : new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true
            };

        Process.Start(startInfo);

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
            }
            else
            {
                Environment.Exit(0);
            }
        });
    }

    /// <summary>
    /// Mark a version as ignored so background updates will not notify for it again.
    /// </summary>
    public void SkipVersion(Version version)
    {
        var settings = appSettingsProvider.Get() with { IgnoreUpdate = version.ToString() };
        appSettingsProvider.Save(settings);
    }
}

