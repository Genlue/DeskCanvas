namespace DeskCanvas.Core;

/// <summary>
/// Constants for the application.
/// </summary>
public static class Const
{
    /// <summary>
    /// The name of the application.
    /// </summary>
    public const string AppName = "DeskCanvas";
    /// <summary>
    /// The folder with the application.
    /// </summary>
    public static readonly string CurrentFolder = Path.GetDirectoryName(Environment.ProcessPath)!;
    /// <summary>
    /// The runtime data folder (settings, layout, widgets).
    /// <para>
    /// Portable mode: the folder of the exe (official layout, dev output).
    /// Packaged single-file mode: %LocalAppData%\DeskCanvas (extracted by <c>WidgetBundle</c>).
    /// </para>
    /// </summary>
    public static readonly string DataFolder = GetDataFolder();
    /// <summary>
    /// The folder with the widgets.
    /// </summary>
    public static readonly string WidgetsFolder = Path.Combine(DataFolder, WidgetsFolderName);
    /// <summary>
    /// The path to the application settings file.
    /// </summary>
    public static readonly string AppSettingsFile = Path.Combine(DataFolder, AppSettingsFileName);
    /// <summary>
    /// The path to the layout file.
    /// </summary>
    public static readonly string LayoutFile = Path.Combine(DataFolder, LayoutFileName);
    /// <summary>
    /// The path to the unified reminders/checklist data file.
    /// </summary>
    public static readonly string RemindersFile = Path.Combine(DataFolder, RemindersFileName);
    /// <summary>
    /// The folder for configuration profiles.
    /// </summary>
    public static readonly string ProfilesFolder = Path.Combine(DataFolder, ProfilesFolderName);

    private static string WidgetsFolderName => "Widgets";
    private static string AppSettingsFileName => "appSettings.json";
    private static string LayoutFileName => "layout.json";
    private static string RemindersFileName => "reminders.json";
    private static string ProfilesFolderName => "Profiles";

    private static string GetDataFolder()
    {
        // Portable mode: a Widgets folder next to the exe (official layout / dev output).
        if (Directory.Exists(Path.Combine(CurrentFolder, WidgetsFolderName)))
            return CurrentFolder;

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(localAppData, AppName);

        // One-time migration from the pre-rebrand data folder (%LocalAppData%\uWidgets):
        // copy settings, layout, reminders, profiles and extracted widgets into
        // %LocalAppData%\DeskCanvas so an in-place upgrade keeps the user's setup.
        // The old folder is left untouched as a fallback for rolling back.
        if (!Directory.Exists(folder))
        {
            var legacy = Path.Combine(localAppData, "uWidgets");
            if (Directory.Exists(legacy))
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    foreach (var entry in Directory.GetFileSystemEntries(legacy))
                        SafeCopy(entry, Path.Combine(folder, Path.GetFileName(entry)));
                }
                catch
                {
                    // Migration is best-effort: a fresh start beats a crash at startup.
                }
            }
        }

        // Packaged single-file mode: runtime data lives in LocalAppData (writable, hot-updatable).
        return folder;
    }

    private static void SafeCopy(string source, string target)
    {
        if (Directory.Exists(source))
        {
            CopyDirectory(source, target);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        try
        {
            File.Copy(source, target, overwrite: false);
        }
        catch (IOException)
        {
            // Lost race or unreadable file: skip the entry, the app rewrites its own data.
        }
        catch (UnauthorizedAccessException)
        {
            // Locked or access-denied file: skip it too.
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.EnumerateFiles(sourceDir))
        {
            var fileTarget = Path.Combine(targetDir, Path.GetFileName(file));
            try
            {
                File.Copy(file, fileTarget, overwrite: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
    }
}
