using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Resources;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;
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
}

