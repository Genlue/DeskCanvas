using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;

namespace DeskCanvas.Services;

/// <summary>
/// Service managing multi-profile configuration and layout switching.
/// Each profile is stored as an independent directory in <see cref="Const.ProfilesFolder"/>.
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

            var defaultFolder = Path.Combine(Const.ProfilesFolder, Const.DefaultProfileName);
            if (!Directory.Exists(defaultFolder))
            {
                Directory.CreateDirectory(defaultFolder);
            }

            var activeName = GetActiveProfile();
            var activeFolder = Path.Combine(Const.ProfilesFolder, activeName);
            if (!Directory.Exists(activeFolder))
            {
                Directory.CreateDirectory(activeFolder);
            }

            SaveCurrentProfile(activeName);
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
                return [Const.DefaultProfileName];

            var dirs = Directory.GetDirectories(Const.ProfilesFolder)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            if (dirs.Count == 0)
                dirs.Add(Const.DefaultProfileName);

            return dirs!;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] GetProfiles failed: {ex.Message}");
            return [Const.DefaultProfileName];
        }
    }

    /// <summary>
    /// Gets the name of the currently active configuration profile.
    /// </summary>
    public string GetActiveProfile() => Const.ActiveProfile;

    /// <summary>
    /// Get the absolute directory path for a profile.
    /// </summary>
    public string GetProfilePath(string profileName) =>
        Path.Combine(Const.ProfilesFolder, SanitizeProfileName(profileName));

    /// <summary>
    /// Saves the current configuration and layout snapshot into the profile directory.
    /// </summary>
    public void SaveCurrentProfile(string? profileName = null)
    {
        var targetName = string.IsNullOrWhiteSpace(profileName) ? GetActiveProfile() : profileName;
        try
        {
            var targetDir = GetProfilePath(targetName);
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

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

            appSettingsProvider.Save(currentSettings);
            layoutProvider.Save(currentScreens);
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

        var targetName = SanitizeProfileName(targetProfileName);
        var path = GetProfilePath(targetName);
        if (!Directory.Exists(path)) return false;

        try
        {
            // 1. Save current profile snapshot
            var currentName = GetActiveProfile();
            SaveCurrentProfile(currentName);

            // 2. Tear the outgoing widgets down BEFORE changing the active profile directory
            OnUi(widgetFactory.CloseAll);

            // 3. Switch active profile pointer and notify all stores
            Const.SetActiveProfile(targetName);

            // 4. Reload data providers from the new profile folder
            appSettingsProvider.Reload();
            layoutProvider.Reload();

            // 5. Update settings to match target name
            var targetSettings = appSettingsProvider.Get() with { ActiveProfile = targetName };
            appSettingsProvider.Save(targetSettings);

            // 6. Immediately refresh display monitor so internal screen configs match the newly loaded layout
            displayMonitor.Refresh();

            // 7. Apply system-wide theme and locale live
            localeService.SetCulture(targetSettings.Region.Language);
            themeService.Apply(targetSettings);

            // 8. Build the desktop widgets of the new configuration
            OnUi(widgetFactory.CreateFromLayout);

            // 9. Notify listeners
            ActiveProfileChanged?.Invoke(this, EventArgs.Empty);

            // 10. Maintenance trim once settled
            System.Threading.Tasks.Task.Delay(1500).ContinueWith(_ => MemoryTrimmerService.RequestTrim());

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] SwitchProfile to '{targetProfileName}' failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Run a widget-window operation on the UI thread.
    /// </summary>
    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    /// <summary>
    /// Create a new configuration profile directory.
    /// </summary>
    /// <param name="name">Profile name.</param>
    /// <param name="copyCurrent">
    /// <c>true</c> duplicates the active profile as-is: appearance, layout and widget data are
    /// copied verbatim. <c>false</c> is what "新建方案" uses — the new profile starts empty, from
    /// the shipped default appearance with no widgets and no widget data — while the app-level
    /// preferences that are not part of a profile's look (UI language, autostart, updates,
    /// proxy, tray icon, title bar) keep their current values.
    /// </param>
    /// <returns>True if created successfully.</returns>
    public bool CreateProfile(string name, bool copyCurrent = false)
    {
        name = SanitizeProfileName(name);
        if (string.IsNullOrWhiteSpace(name)) return false;

        var path = GetProfilePath(name);
        if (Directory.Exists(path)) return false;

        try
        {
            Directory.CreateDirectory(path);

            if (copyCurrent && Directory.Exists(Const.ActiveProfileFolder))
            {
                foreach (var file in Directory.GetFiles(Const.ActiveProfileFolder, "*.json"))
                {
                    var dest = Path.Combine(path, Path.GetFileName(file));
                    File.Copy(file, dest, overwrite: true);
                }

                var newSettingsFile = Path.Combine(path, Const.AppSettingsFileName);
                if (File.Exists(newSettingsFile))
                {
                    try
                    {
                        var json = File.ReadAllText(newSettingsFile);
                        var settings = JsonSerializer.Deserialize<AppSettings>(json);
                        if (settings != null)
                        {
                            settings = settings with { ActiveProfile = name };
                            File.WriteAllText(newSettingsFile, JsonSerializer.Serialize(settings));
                        }
                    }
                    catch { }
                }
            }
            else
            {
                var emptySettings = CreateEmptyProfileSettings(name);
                var emptyScreens = new ScreensLayout([new ScreenLayout(ScreensLayout.LegacyPrimaryId, null, null, null, null, null, [])]);
                File.WriteAllText(Path.Combine(path, Const.AppSettingsFileName), AppSettingsProvider.ToFileJson(emptySettings));
                File.WriteAllText(Path.Combine(path, Const.LayoutFileName), JsonSerializer.Serialize(emptyScreens));
            }

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
    /// Settings of a brand-new empty profile.
    /// <para>
    /// The appearance groups (theme, surface materials, templates, font, grid, widget
    /// dimensions, layout behaviour) come from the shipped <c>appSettings.json</c> template —
    /// the same file a fresh data folder is seeded with — so the new profile opens with the
    /// factory look instead of inheriting whatever the active profile was styled with.
    /// </para>
    /// <para>
    /// App-level preferences are carried over because they are not part of a profile's look:
    /// resetting them would flip the UI language, forget the custom update source or drop the
    /// proxy setting just because a profile was created.
    /// </para>
    /// </summary>
    private AppSettings CreateEmptyProfileSettings(string name)
    {
        var current = appSettingsProvider.Get();
        var defaults = ReadShippedDefaultSettings();

        if (defaults == null)
        {
            // The embedded template is always part of this assembly; this path only exists so a
            // build without it can still create a usable profile. It inherits the current
            // appearance — the lesser evil compared to a profile with no settings file at all.
            defaults = current;
        }

        return defaults with
        {
            ActiveProfile = name,
            Region = current.Region,
            RunOnStartup = current.RunOnStartup,
            IgnoreUpdate = current.IgnoreUpdate,
            UpdateUrl = current.UpdateUrl,
            UpdateInterval = current.UpdateInterval,
            LastUpdateCheckTime = current.LastUpdateCheckTime,
            LastUpdateCheckStatus = current.LastUpdateCheckStatus,
            HttpProxy = current.HttpProxy, // the file form (ProxyAutoDetect) is derived on write
            ShowTrayIcon = current.ShowTrayIcon,
            TitleBarStyle = current.TitleBarStyle,
            TitleBarSize = current.TitleBarSize
        };
    }

    /// <summary>
    /// Deserialize the shipped default <c>appSettings.json</c>, or <c>null</c> when the template
    /// is missing or unreadable.
    /// </summary>
    private static AppSettings? ReadShippedDefaultSettings()
    {
        try
        {
            var json = WidgetBundle.ReadDefaultAppSettingsJson();
            return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AppSettings>(json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ProfileService] Shipped default settings unreadable: {ex.Message}");
            return null;
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

        if (!Directory.Exists(oldPath) || Directory.Exists(newPath))
            return false;

        try
        {
            var isActive = string.Equals(oldName, GetActiveProfile(), StringComparison.OrdinalIgnoreCase);
            if (isActive)
            {
                SaveCurrentProfile(oldName);
            }

            Directory.Move(oldPath, newPath);

            var settingsFile = Path.Combine(newPath, Const.AppSettingsFileName);
            if (File.Exists(settingsFile))
            {
                try
                {
                    var json = File.ReadAllText(settingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings = settings with { ActiveProfile = newName };
                        File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings));
                    }
                }
                catch { }
            }

            if (isActive)
            {
                Const.SetActiveProfile(newName);
                appSettingsProvider.Reload();
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
    /// Delete an existing profile directory. Cannot delete the active profile or the last remaining profile.
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
        if (!Directory.Exists(path)) return false;

        try
        {
            Directory.Delete(path, recursive: true);
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
    /// Duplicate a profile directory.
    /// </summary>
    public bool DuplicateProfile(string sourceName, string newName)
    {
        sourceName = SanitizeProfileName(sourceName);
        newName = SanitizeProfileName(newName);

        var sourcePath = GetProfilePath(sourceName);
        var targetPath = GetProfilePath(newName);

        if (!Directory.Exists(sourcePath) || Directory.Exists(targetPath))
            return false;

        try
        {
            Directory.CreateDirectory(targetPath);
            foreach (var file in Directory.GetFiles(sourcePath, "*.json"))
            {
                var dest = Path.Combine(targetPath, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            var settingsFile = Path.Combine(targetPath, Const.AppSettingsFileName);
            if (File.Exists(settingsFile))
            {
                try
                {
                    var json = File.ReadAllText(settingsFile);
                    var settings = JsonSerializer.Deserialize<AppSettings>(json);
                    if (settings != null)
                    {
                        settings = settings with { ActiveProfile = newName };
                        File.WriteAllText(settingsFile, JsonSerializer.Serialize(settings));
                    }
                }
                catch { }
            }

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
    /// Export a profile directory (as a zip archive or json file).
    /// </summary>
    public void ExportProfile(string profileName, string destinationFilePath)
    {
        var path = GetProfilePath(profileName);
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Profile folder '{profileName}' not found.");

        if (destinationFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(destinationFilePath)) File.Delete(destinationFilePath);
            ZipFile.CreateFromDirectory(path, destinationFilePath);
        }
        else
        {
            // Backward-compatible single json export
            var settingsFile = Path.Combine(path, Const.AppSettingsFileName);
            var layoutFile = Path.Combine(path, Const.LayoutFileName);
            var settings = File.Exists(settingsFile) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsFile)) : appSettingsProvider.Get();
            var layout = File.Exists(layoutFile) ? JsonSerializer.Deserialize<ScreensLayout>(File.ReadAllText(layoutFile)) : layoutProvider.Get();
            var backup = new BackupData(BackupData.CurrentVersion, settings ?? appSettingsProvider.Get(), layout ?? layoutProvider.Get());
            File.WriteAllText(destinationFilePath, BackupService.ToJson(backup));
        }
    }

    /// <summary>
    /// Import a profile from an external zip or JSON file.
    /// </summary>
    public string ImportProfile(string sourceFilePath)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceFilePath);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
            baseName = isZh ? "导入配置" : "Imported Profile";
        }

        baseName = SanitizeProfileName(baseName);
        var targetName = baseName;
        int counter = 1;
        while (Directory.Exists(GetProfilePath(targetName)))
        {
            targetName = $"{baseName}_{counter++}";
        }

        var targetPath = GetProfilePath(targetName);
        Directory.CreateDirectory(targetPath);

        if (sourceFilePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(sourceFilePath, targetPath);
        }
        else
        {
            var json = File.ReadAllText(sourceFilePath);
            var backup = BackupService.Parse(json);
            backup = backup with { AppSettings = backup.AppSettings with { ActiveProfile = targetName } };
            File.WriteAllText(Path.Combine(targetPath, Const.AppSettingsFileName), JsonSerializer.Serialize(backup.AppSettings));
            File.WriteAllText(Path.Combine(targetPath, Const.LayoutFileName), JsonSerializer.Serialize(backup.Screens));
        }

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
