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

public class GeneralViewModel : ReactiveObject
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly UpdateService updateService;

    public GeneralViewModel(IAppSettingsProvider appSettingsProvider, UpdateService? updateService = null)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.updateService = updateService ?? new UpdateService(appSettingsProvider);

        var lastCheck = appSettingsProvider.Get().LastUpdateCheckTime;
        updateStatusText = lastCheck.HasValue
            ? string.Format(Locale.Settings_General_Update_LastChecked, lastCheck.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))
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
            if (result == UpdateCheckResult.UpdateAvailable && info != null)
            {
                UpdateStatusText = string.Format(Locale.Settings_General_Update_FoundNew, info.Version);
                Dispatcher.UIThread.Post(() =>
                {
                    new UpdatePopup(info, updateService).Show();
                });
            }
            else if (result == UpdateCheckResult.UpToDate)
            {
                var curVer = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "latest";
                UpdateStatusText = string.Format(Locale.Settings_General_Update_AlreadyLatest, curVer);
            }
            else
            {
                UpdateStatusText = Locale.Settings_General_Update_CheckFailed;
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

