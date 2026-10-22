using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace DeskCanvas.ViewModels;

public record UpdateIntervalOption(UpdateCheckInterval Value, string Label);

public class GeneralViewModel : ReactiveObject, IDisposable
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly UpdateService updateService;
    private bool attached;

    public GeneralViewModel(IAppSettingsProvider appSettingsProvider, UpdateService? updateService = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.updateService = updateService ?? new UpdateService(appSettingsProvider);

        updateStatusText = FormatStoredUpdateStatus(appSettingsProvider.Get());
        connectivityStatusText = Locale.Settings_General_Update_Connectivity_Subtitle;

        Attach();
    }

    /// <summary>
    /// Subscribe to settings changes while the page is attached to the visual tree.
    /// </summary>
    public void Attach()
    {
        if (attached) return;
        attached = true;
        appSettingsProvider.DataChanged += OnAppSettingsDataChanged;
        RefreshFromSettings(appSettingsProvider.Get());
    }

    /// <summary>
    /// Unsubscribe when the page leaves the visual tree so a closed settings window retains no page subscribers.
    /// </summary>
    public void Dispose()
    {
        if (!attached) return;
        attached = false;
        appSettingsProvider.DataChanged -= OnAppSettingsDataChanged;
    }

    private void OnAppSettingsDataChanged(object? sender, AppSettings? oldData, AppSettings newData)
    {
        Dispatcher.UIThread.Post(() => RefreshFromSettings(newData), DispatcherPriority.Background);
    }

    private void RefreshFromSettings(AppSettings settings)
    {
        this.RaisePropertyChanged(nameof(SelectedUpdateInterval));
        this.RaisePropertyChanged(nameof(AutoDetectProxy));
        this.RaisePropertyChanged(nameof(AutoProxySubtitle));
        if (!IsCheckingUpdates)
        {
            UpdateStatusText = FormatStoredUpdateStatus(settings);
        }
    }

    private static string FormatStoredUpdateStatus(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.LastUpdateCheckStatus))
        {
            return settings.LastUpdateCheckStatus;
        }

        return settings.LastUpdateCheckTime.HasValue
            ? string.Format(Locale.Settings_General_Update_LastChecked, settings.LastUpdateCheckTime.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
            : "";
    }

    public CultureInfo[] Languages => GetAvailableCultures().ToHashSet().OrderBy(x => x.DisplayName).ToArray();
    
    public CultureInfo Language
    {
        get => CultureInfo.GetCultureInfo(appSettingsProvider.Get().Region.Language);
        set
        {
            var settings = appSettingsProvider.Get();
            var newRegion = settings.Region with { Language = value.Name };
            var newSettings = settings with { Region = newRegion };
            appSettingsProvider.Save(newSettings);
        }
    }
    
    public bool RunOnStartup
    {
        get => new StartupService().IsEnabled();
        set
        {
            if (!new StartupService().SetRunOnStartup(value)) return;
            
            var settings = appSettingsProvider.Get();
            var newSettings = settings with { RunOnStartup = value };
            appSettingsProvider.Save(newSettings);
        }
    }

    public UpdateIntervalOption[] UpdateIntervals =>
    [
        new(UpdateCheckInterval.Disabled, Locale.Settings_General_Update_Interval_Disabled),
        new(UpdateCheckInterval.OnStartup, Locale.Settings_General_Update_Interval_OnStartup),
        new(UpdateCheckInterval.Every6Hours, Locale.Settings_General_Update_Interval_Every6Hours),
        new(UpdateCheckInterval.Every12Hours, Locale.Settings_General_Update_Interval_Every12Hours),
        new(UpdateCheckInterval.Daily, Locale.Settings_General_Update_Interval_Daily),
        new(UpdateCheckInterval.Weekly, Locale.Settings_General_Update_Interval_Weekly),
    ];

    public UpdateIntervalOption SelectedUpdateInterval
    {
        get
        {
            var current = appSettingsProvider.Get().UpdateInterval;
            return UpdateIntervals.FirstOrDefault(x => x.Value == current) ?? UpdateIntervals[4];
        }
        set
        {
            if (value == null) return;
            var settings = appSettingsProvider.Get();
            if (settings.UpdateInterval == value.Value) return;
            var newSettings = settings with { UpdateInterval = value.Value };
            appSettingsProvider.Save(newSettings);
            this.RaisePropertyChanged(nameof(SelectedUpdateInterval));
        }
    }

    /// <summary>
    /// Synchronized with <see cref="AppSettings.HttpProxy"/>: <c>true</c> sets mode to <c>"auto"</c>,
    /// <c>false</c> sets mode to <c>"direct"</c>.
    /// </summary>
    public bool AutoDetectProxy
    {
        get => ProxySettings.GetMode(appSettingsProvider.Get().HttpProxy) == ProxyModeKind.Auto;
        set
        {
            var settings = appSettingsProvider.Get();
            var currentIsAuto = ProxySettings.GetMode(settings.HttpProxy) == ProxyModeKind.Auto;
            if (currentIsAuto == value) return;

            var nextProxy = value ? "auto" : "direct";
            appSettingsProvider.Save(settings with { HttpProxy = nextProxy });
            this.RaisePropertyChanged(nameof(AutoDetectProxy));
            this.RaisePropertyChanged(nameof(AutoProxySubtitle));
        }
    }

    public string AutoProxySubtitle
    {
        get
        {
            var raw = appSettingsProvider.Get().HttpProxy;
            return ProxySettings.GetMode(raw) switch
            {
                ProxyModeKind.Auto => Locale.Settings_General_Update_AutoProxy_Subtitle_On,
                ProxyModeKind.Direct => Locale.Settings_General_Update_AutoProxy_Subtitle_Direct,
                ProxyModeKind.System => Locale.Settings_General_Update_AutoProxy_Subtitle_System,
                ProxyModeKind.Custom => string.Format(Locale.Settings_General_Update_AutoProxy_Subtitle_Custom, raw),
                _ => Locale.Settings_General_Update_AutoProxy_Subtitle_On
            };
        }
    }

    private bool isTestingConnectivity;
    public bool IsTestingConnectivity
    {
        get => isTestingConnectivity;
        set => this.RaiseAndSetIfChanged(ref isTestingConnectivity, value);
    }

    private string connectivityStatusText;
    public string ConnectivityStatusText
    {
        get => connectivityStatusText;
        set => this.RaiseAndSetIfChanged(ref connectivityStatusText, value);
    }

    public async Task TestGitHubConnectivity()
    {
        if (IsTestingConnectivity) return;
        IsTestingConnectivity = true;
        ConnectivityStatusText = Locale.Settings_General_Update_Connectivity_Testing;

        try
        {
            var result = await updateService.TestGitHubConnectivityAsync();
            ConnectivityStatusText = result.Summary;
        }
        catch (Exception ex)
        {
            ConnectivityStatusText = string.Format(
                Locale.Settings_General_Update_Connectivity_Failed,
                ex.Message,
                0,
                Locale.Settings_General_Update_Route_Direct);
        }
        finally
        {
            IsTestingConnectivity = false;
        }
    }

    private bool isCheckingUpdates;
    public bool IsCheckingUpdates
    {
        get => isCheckingUpdates;
        set => this.RaiseAndSetIfChanged(ref isCheckingUpdates, value);
    }

    private string updateStatusText;
    public string UpdateStatusText
    {
        get => updateStatusText;
        set => this.RaiseAndSetIfChanged(ref updateStatusText, value);
    }

    public async Task CheckForUpdates()
    {
        if (IsCheckingUpdates) return;
        IsCheckingUpdates = true;
        UpdateStatusText = Locale.Settings_General_Update_Checking;

        try
        {
            var (info, result) = await updateService.CheckForUpdatesDetailedAsync(isManual: true);
            var storedStatus = FormatStoredUpdateStatus(appSettingsProvider.Get());

            if (result == UpdateCheckResult.UpdateAvailable && info != null)
            {
                UpdateStatusText = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : string.Format(Locale.Settings_General_Update_FoundNew, info.Version);
                Dispatcher.UIThread.Post(() =>
                {
                    new UpdatePopup(info, updateService).Show();
                });
            }
            else if (result == UpdateCheckResult.UpToDate)
            {
                var curVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "latest";
                UpdateStatusText = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : string.Format(Locale.Settings_General_Update_AlreadyLatest, curVer);
            }
            else
            {
                UpdateStatusText = !string.IsNullOrWhiteSpace(storedStatus)
                    ? storedStatus
                    : Locale.Settings_General_Update_CheckFailed;
            }
        }
        catch
        {
            UpdateStatusText = Locale.Settings_General_Update_CheckFailed;
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }
    
    private static IEnumerable<CultureInfo> GetAvailableCultures()
    {
        var cultures = CultureInfo.GetCultures(CultureTypes.NeutralCultures);

        yield return new CultureInfo("en");
        
        foreach (var culture in cultures)
        {
            if (culture.Equals(CultureInfo.InvariantCulture)) continue;
            ResourceSet? resourceSet = null;
            
            try
            {
                resourceSet = Locale.ResourceManager.GetResourceSet(culture, true, false);
            }
            catch (CultureNotFoundException)
            {
                
            }
            
            if (resourceSet != null)
                yield return culture;
        }
    }

    // ---- 默认地区 ---------------------------------------------------------------

    private string regionQuery = string.Empty;
    private bool isSearchingRegion;
    private string regionStatus = string.Empty;
    private RegionSearchResult? selectedRegionResult;

    /// <summary>City name being typed into the search box.</summary>
    public string RegionQuery
    {
        get => regionQuery;
        set => this.RaiseAndSetIfChanged(ref regionQuery, value);
    }

    /// <summary>Geocoding hits for <see cref="RegionQuery"/>; picking one saves it as the default.</summary>
    public ObservableCollection<RegionSearchResult> RegionResults { get; } = [];

    public bool IsSearchingRegion
    {
        get => isSearchingRegion;
        set => this.RaiseAndSetIfChanged(ref isSearchingRegion, value);
    }

    /// <summary>One-line outcome of the last search (found / nothing / network failure).</summary>
    public string RegionStatus
    {
        get => regionStatus;
        set => this.RaiseAndSetIfChanged(ref regionStatus, value);
    }

    public RegionSearchResult? SelectedRegionResult
    {
        get => selectedRegionResult;
        set
        {
            this.RaiseAndSetIfChanged(ref selectedRegionResult, value);
            if (value != null) ApplyDefaultRegion(value);
        }
    }

    /// <summary>The configured 默认地区, or the "not configured" hint.</summary>
    public string DefaultRegionSummary
    {
        get
        {
            var region = appSettingsProvider.Get().EffectiveDefaultRegion;
            return region == null
                ? Locale.Settings_General_DefaultRegion_NotSet
                : string.Format(Locale.Settings_General_DefaultRegion_Current,
                    region.Name, region.Latitude, region.Longitude);
        }
    }

    public bool HasDefaultRegion => appSettingsProvider.Get().EffectiveDefaultRegion != null;

    /// <summary>Look the typed city up through Open-Meteo geocoding (same source the weather widget uses).</summary>
    public async Task SearchRegionAsync()
    {
        var query = (RegionQuery ?? string.Empty).Trim();
        if (query.Length == 0) return;
        if (IsSearchingRegion) return;

        IsSearchingRegion = true;
        RegionStatus = Locale.Settings_General_DefaultRegion_Searching;

        try
        {
            var language = Thread.CurrentThread.CurrentUICulture.TwoLetterISOLanguageName;
            var request = new OpenMeteoRequest(default, WeatherResource.Geocoding, query, language, 8);
            using var lease = await SharedDataServices.Weather
                .AcquireAsync("settings-default-region", request)
                .ConfigureAwait(true);

            RegionResults.Clear();
            foreach (var hit in ParseGeocodingResults(lease.Snapshot.Value))
                RegionResults.Add(hit);

            if (RegionResults.Count == 1) SelectedRegionResult = RegionResults[0];

            RegionStatus = RegionResults.Count == 0
                ? Locale.Settings_General_DefaultRegion_NoResult
                : string.Format(Locale.Settings_General_DefaultRegion_Results, RegionResults.Count);
        }
        catch (Exception)
        {
            RegionResults.Clear();
            RegionStatus = Locale.Settings_General_DefaultRegion_Failed;
        }
        finally
        {
            IsSearchingRegion = false;
        }
    }

    /// <summary>Persist one hit as the 默认地区 (and remember the city name).</summary>
    public void ApplyDefaultRegion(RegionSearchResult result)
    {
        var settings = appSettingsProvider.Get();
        var region = new DefaultRegion(result.Name, result.Latitude, result.Longitude);
        if (!region.IsValid) return;

        appSettingsProvider.Save(settings with { DefaultRegion = region.Normalized() });
        RaiseDefaultRegionChanged();
    }

    /// <summary>Forget the 默认地区 — every widget falls back to its own historical location.</summary>
    public void ClearDefaultRegion()
    {
        var settings = appSettingsProvider.Get();
        if (settings.DefaultRegion == null) return;

        appSettingsProvider.Save(settings with { DefaultRegion = null });
        SelectedRegionResult = null;
        RegionResults.Clear();
        RegionStatus = string.Empty;
        RaiseDefaultRegionChanged();
    }

    private void RaiseDefaultRegionChanged()
    {
        this.RaisePropertyChanged(nameof(DefaultRegionSummary));
        this.RaisePropertyChanged(nameof(HasDefaultRegion));
    }

    /// <summary>
    /// Flatten the Open-Meteo geocoding payload. Only the fields the settings page shows are read;
    /// a hit without coordinates is dropped rather than saved as an unusable region.
    /// </summary>
    private static IEnumerable<RegionSearchResult> ParseGeocodingResults(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) yield break;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                yield break;
            }

            foreach (var item in results.EnumerateArray())
            {
                if (!item.TryGetProperty("name", out var nameElement)) continue;
                if (!item.TryGetProperty("latitude", out var latElement)) continue;
                if (!item.TryGetProperty("longitude", out var lonElement)) continue;
                if (!latElement.TryGetDouble(out var latitude) || !lonElement.TryGetDouble(out var longitude)) continue;

                var name = nameElement.GetString() ?? string.Empty;
                if (name.Length == 0) continue;

                var region = ReadString(item, "admin1");
                var country = ReadString(item, "country");
                var detail = string.Join(" · ", new[] { region, country }.Where(part => !string.IsNullOrWhiteSpace(part)));

                yield return new RegionSearchResult(name, latitude, longitude,
                    detail.Length == 0 ? name : $"{name} · {detail}");
            }
        }
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>One geocoding hit shown in the 默认地区 picker.</summary>
/// <param name="Name">City name as returned by the geocoder.</param>
/// <param name="Latitude">WGS-84 latitude.</param>
/// <param name="Longitude">WGS-84 longitude.</param>
/// <param name="Display">"杭州 · 浙江 · 中国" style label.</param>
public record RegionSearchResult(string Name, double Latitude, double Longitude, string Display)
{
    public override string ToString() => Display;
}

