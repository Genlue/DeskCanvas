using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// Service managing multi-profile configuration and layout switching.
/// Profiles are saved as versioned <see cref="BackupData"/> JSON documents in <see cref="Const.ProfilesFolder"/>.
/// </summary>
public class ProfileService
{
    private readonly IAppSettingsProvider appSettingsProvider;
    private readonly ILayoutProvider layoutProvider;
    private readonly IThemeService themeService;
    private readonly ILocaleService localeService;
    private readonly WidgetFactory widgetFactory;
    private readonly DisplayMonitorService displayMonitor;

    /// <summary>Raised when the active profile changes.</summary>
    public event EventHandler? ActiveProfileChanged;

    /// <summary>Raised when the profile list changes (created, renamed, deleted, imported).</summary>
    public event EventHandler? ProfilesListChanged;

    public ProfileService(
        IAppSettingsProvider appSettingsProvider,
        ILayoutProvider layoutProvider,
        IThemeService themeService,
        ILocaleService localeService,
        WidgetFactory widgetFactory,
        DisplayMonitorService displayMonitor)
    {
        this.appSettingsProvider = appSettingsProvider;
        this.layoutProvider = layoutProvider;
        this.themeService = themeService;
        this.localeService = localeService;
        this.widgetFactory = widgetFactory;
        this.displayMonitor = displayMonitor;

        EnsureSeeded();
    }

    /// <summary>
    /// Ensure the Profiles directory exists and seed with the default profile if empty.
    /// </summary>
    public void EnsureSeeded()
    {
        try
        {
            if (!Directory.Exists(Const.ProfilesFolder))
            {
                Directory.CreateDirectory(Const.ProfilesFolder);
            }

            var profiles = GetProfiles();
            var activeName = GetActiveProfile();

            if (profiles.Count == 0)
            {
                SaveCurrentProfile(AppSettings.DefaultProfileName);
            }
            else if (!File.Exists(GetProfilePath(activeName)))
            {
                // Active profile points to a nonexistent file: save current state as that profile
                SaveCurrentProfile(activeName);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] EnsureSeeded failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets all available profile names (sorted with active profile first).
    /// </summary>
    public IReadOnlyList<string> GetProfiles()
    {
        try
        {
            if (!Directory.Exists(Const.ProfilesFolder))
                return [AppSettings.DefaultProfileName];

            var files = Directory.GetFiles(Const.ProfilesFolder, "*.json");
            var names = files
                .Select(Path.GetFileNameWithoutExtension)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            if (names.Count == 0)
                names.Add(AppSettings.DefaultProfileName);

            return names!;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] GetProfiles failed: {ex.Message}");
            return [AppSettings.DefaultProfileName];
        }
    }

    /// <summary>
    /// Gets the name of the currently active configuration profile.
    /// </summary>
    public string GetActiveProfile()
    {
        try
        {
            return appSettingsProvider.Get().EffectiveActiveProfile;
        }
        catch
        {
            return AppSettings.DefaultProfileName;
        }
    }

    /// <summary>
    /// Get the absolute file path for a profile.
    /// </summary>
    public string GetProfilePath(string profileName) =>
        Path.Combine(Const.ProfilesFolder, $"{SanitizeProfileName(profileName)}.json");

    /// <summary>
    /// Saves the current configuration and layout snapshot into the profile file.
    /// </summary>
    public void SaveCurrentProfile(string? profileName = null)
    {
        var targetName = string.IsNullOrWhiteSpace(profileName) ? GetActiveProfile() : profileName;
        try
        {
            if (!Directory.Exists(Const.ProfilesFolder))
                Directory.CreateDirectory(Const.ProfilesFolder);

            var currentSettings = appSettingsProvider.Get();
            if (currentSettings.ActiveProfile != targetName)
            {
                currentSettings = currentSettings with { ActiveProfile = targetName };
            }

            var currentScreens = layoutProvider.Get();
            var primaryGrid = currentScreens.Screens.FirstOrDefault(s => s.Id == ScreensLayout.LegacyPrimaryId || s.Key == null)?.Grid
                              ?? currentScreens.Screens.FirstOrDefault()?.Grid;
            if (primaryGrid != null)
            {
                currentSettings = currentSettings with { Grid = primaryGrid };
            }

            var snapshot = new BackupData(BackupData.CurrentVersion, currentSettings, currentScreens);
            var json = BackupService.ToJson(snapshot);
            var path = GetProfilePath(targetName);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] SaveCurrentProfile '{targetName}' failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Switch to a target profile immediately without restarting the application.
    /// </summary>
    public bool SwitchProfile(string targetProfileName)
    {
        if (string.IsNullOrWhiteSpace(targetProfileName)) return false;

        var path = GetProfilePath(targetProfileName);
        if (!File.Exists(path)) return false;

        try
        {
            // 1. Save current profile snapshot so changes made during this session aren't lost
            var currentName = GetActiveProfile();
            SaveCurrentProfile(currentName);

            // 2. Tear the outgoing widgets down BEFORE the stored layout is replaced.
            //    A live widget keeps its layout subscriptions until it is really gone;
            //    if the layout is swapped first, that widget writes its own entry into
            //    the incoming layout (the provider no longer finds the entry, and the
            //    old append-on-missing path re-created it) — which showed the previous
            //    profile's widgets on top of the new ones, persisted to disk, so the
            //    duplicate set also survived a restart.
            OnUi(widgetFactory.CloseAll);

            // 3. Read and parse target profile
            var json = File.ReadAllText(path);
            var backup = BackupService.Parse(json);

            // 4. Mark target profile as active & synchronize primary grid
            var targetScreens = backup.Screens;
            var targetGrid = targetScreens.Screens.FirstOrDefault(s => s.Id == ScreensLayout.LegacyPrimaryId || s.Key == null)?.Grid
                             ?? targetScreens.Screens.FirstOrDefault()?.Grid
                             ?? backup.AppSettings.Grid;

            var targetSettings = backup.AppSettings with
            {
                ActiveProfile = targetProfileName,
                Grid = targetGrid ?? backup.AppSettings.Grid
            };

            // 5. Update data providers (persists to appSettings.json and layout.json)
            appSettingsProvider.Save(targetSettings);
            layoutProvider.Save(targetScreens);

            // 6. Immediately refresh display monitor so internal screen configs match the newly loaded layout
            displayMonitor.Refresh();

            // 7. Apply system-wide theme and locale live
            localeService.SetCulture(targetSettings.Region.Language);
            themeService.Apply(targetSettings.Theme);

            // 8. Build the desktop widgets of the new configuration
            OnUi(widgetFactory.CreateFromLayout);

            // 9. Notify listeners
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);

            // 10. Trim working set after widgets have settled
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => InteropService.TrimProcessMemory());

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] SwitchProfile to '{targetProfileName}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run a widget-window operation on the UI thread (window creation/teardown must
    /// happen there; profile switches can also be triggered from background paths).
    /// </summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Create a new configuration profile.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <param name="copyCurrent">Whether to copy current configuration and layout into the new profile.</param>
    /// <returns>True if created successfully.</returns>
    public bool CreateProfile(string name, bool copyCurrent = true)
    {
        name = SanitizeProfileName(name);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var path = GetProfilePath(name);
        if (File.Exists(path)) return false;

        try
        {
            if (!Directory.Exists(Const.ProfilesFolder))
                Directory.CreateDirectory(Const.ProfilesFolder);

            BackupData snapshot;
            if (copyCurrent)
            {
                var currentSettings = appSettingsProvider.Get() with { ActiveProfile = name };
                var currentScreens = layoutProvider.Get();
                var primaryGrid = currentScreens.Screens.FirstOrDefault(s => s.Id == ScreensLayout.LegacyPrimaryId || s.Key == null)?.Grid
                                  ?? currentScreens.Screens.FirstOrDefault()?.Grid;
                if (primaryGrid != null)
                {
                    currentSettings = currentSettings with { Grid = primaryGrid };
                }

                snapshot = new BackupData(
                    BackupData.CurrentVersion,
                    currentSettings,
                    currentScreens);
            }
            else
            {
                // Default clean profile
                snapshot = new BackupData(
                    BackupData.CurrentVersion,
                    appSettingsProvider.Get() with { ActiveProfile = name },
                    new ScreensLayout([new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, [])]));
            }

            File.WriteAllText(path, BackupService.ToJson(snapshot));
            ProfilesListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] CreateProfile '{name}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Rename an existing profile.
    /// </summary>
    public bool RenameProfile(string oldName, string newName)
    {
        oldName = SanitizeProfileName(oldName);
        newName = SanitizeProfileName(newName);

        if (string.IsNullOrWhiteSpace(oldName) || string.IsNullOrWhiteSpace(newName))
            return false;

        if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            return true;

        var oldPath = GetProfilePath(oldName);
        var newPath = GetProfilePath(newName);

        if (!File.Exists(oldPath) || File.Exists(newPath))
            return false;

        try
        {
            // If active, save current state first
            var isActive = string.Equals(oldName, GetActiveProfile(), StringComparison.OrdinalIgnoreCase);
            if (isActive)
            {
                SaveCurrentProfile(oldName);
            }

            // Read, update active profile field inside JSON, and write to new path
            var json = File.ReadAllText(oldPath);
            var backup = BackupService.Parse(json);
            backup = backup with { AppSettings = backup.AppSettings with { ActiveProfile = newName } };
            File.WriteAllText(newPath, BackupService.ToJson(backup));
            File.Delete(oldPath);

            if (isActive)
            {
                appSettingsProvider.Save(appSettingsProvider.Get() with { ActiveProfile = newName });
                ActiveProfileChanged?.Invoke(this, EventArgs.Empty);
            }

            ProfilesListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] RenameProfile '{oldName}' -> '{newName}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Delete an existing profile. Cannot delete the active profile or the last remaining profile.
    /// </summary>
    public bool DeleteProfile(string name)
    {
        name = SanitizeProfileName(name);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var profiles = GetProfiles();
        if (profiles.Count <= 1) return false;

        if (string.Equals(name, GetActiveProfile(), StringComparison.OrdinalIgnoreCase))
            return false; // Must switch away first

        var path = GetProfilePath(name);
        if (!File.Exists(path)) return false;

        try
        {
            File.Delete(path);
            ProfilesListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] DeleteProfile '{name}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Duplicate a profile.
    /// </summary>
    public bool DuplicateProfile(string sourceName, string newName)
    {
        sourceName = SanitizeProfileName(sourceName);
        newName = SanitizeProfileName(newName);

        var sourcePath = GetProfilePath(sourceName);
        var targetPath = GetProfilePath(newName);

        if (!File.Exists(sourcePath) || File.Exists(targetPath))
            return false;

        try
        {
            var json = File.ReadAllText(sourcePath);
            var backup = BackupService.Parse(json);
            backup = backup with { AppSettings = backup.AppSettings with { ActiveProfile = newName } };
            File.WriteAllText(targetPath, BackupService.ToJson(backup));
            ProfilesListChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] DuplicateProfile failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Export a profile to an external JSON file.
    /// </summary>
    public void ExportProfile(string profileName, string destinationFilePath)
    {
        var path = GetProfilePath(profileName);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile file '{profileName}' not found.");

        File.Copy(path, destinationFilePath, true);
    }

    /// <summary>
    /// Import a profile from an external JSON file.
    /// </summary>
    public string ImportProfile(string sourceFilePath)
    {
        var json = File.ReadAllText(sourceFilePath);
        var backup = BackupService.Parse(json);

        var baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "导入配置";

        baseName = SanitizeProfileName(baseName);
        var targetName = baseName;
        int counter = 1;
        while (File.Exists(GetProfilePath(targetName)))
        {
            targetName = $"{baseName}_{counter++}";
        }

        backup = backup with { AppSettings = backup.AppSettings with { ActiveProfile = targetName } };
        File.WriteAllText(GetProfilePath(targetName), BackupService.ToJson(backup));
        ProfilesListChanged?.Invoke(this, EventArgs.Empty);
        return targetName;
    }

    private static string SanitizeProfileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Where(c => !invalidChars.Contains(c)).ToArray()).Trim();
        return cleaned;
    }
}
