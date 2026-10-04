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
    /// Query GitHub release API, compare version, select appropriate architecture installer asset.
    /// </summary>
    public async Task<(ReleaseInfo? Info, UpdateCheckResult Status)> CheckForUpdatesDetailedAsync(bool isManual = false)
    {
        var settings = appSettingsProvider.Get();
        if (!isManual && settings.UpdateInterval == UpdateCheckInterval.Disabled)
            return (null, UpdateCheckResult.Disabled);

        try
        {
            var info = await FetchReleaseInfoAsync();
            if (info == null)
                return (null, UpdateCheckResult.Failed);

            // Record check timestamp
            var currentSettings = appSettingsProvider.Get();
            var updatedSettings = currentSettings with { LastUpdateCheckTime = DateTime.UtcNow };
            appSettingsProvider.Save(updatedSettings);

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

            var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
            if (info.Version > currentVersion)
            {
                return (info, UpdateCheckResult.UpdateAvailable);
            }

            return (info, UpdateCheckResult.UpToDate);
        }
        catch
        {
            return (null, UpdateCheckResult.Failed);
        }
    }

    /// <summary>
    /// Legacy compatibility method returning the latest Version if newer.
    /// </summary>
    public async Task<Version?> GetUpdateVersionAsync()
    {
        var (info, result) = await CheckForUpdatesDetailedAsync(isManual: true);
        return result == UpdateCheckResult.UpdateAvailable ? info?.Version : null;
    }

    /// <summary>
    /// Fetch latest release metadata from GitHub API.
    /// </summary>
    public async Task<ReleaseInfo?> FetchReleaseInfoAsync()
    {
        var effectiveUrl = appSettingsProvider.Get().EffectiveUpdateUrl;
        if (string.IsNullOrWhiteSpace(effectiveUrl)) return null;

        var apiUrl = ResolveApiUrl(effectiveUrl);

        try
        {
            using var httpClient = ProxySettings.CreateHttpClient();
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-Updater");
            httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github.v3+json");

            var response = await httpClient.GetAsync(apiUrl);
            if (!response.IsSuccessStatusCode) return null;

            var json = await response.Content.ReadAsStringAsync();
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
                    return null;
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

            return new ReleaseInfo(
                releaseVer,
                tagName,
                title,
                changelog,
                releasePageUrl,
                bestDownloadUrl,
                bestFileName,
                bestFileSize
            );
        }
        catch
        {
            return null;
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

        var handler = ProxySettings.CreateHandler();
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

