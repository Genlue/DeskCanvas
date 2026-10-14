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
    public static readonly string WidgetsFolder = GetWidgetsFolder();
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
    /// The path to the unified timetable/schedule data file.
    /// </summary>
    public static readonly string TimetableFile = Path.Combine(DataFolder, TimetableFileName);
    /// <summary>
    /// The folder for configuration profiles.
    /// </summary>
    public static readonly string ProfilesFolder = Path.Combine(DataFolder, ProfilesFolderName);

    private const string WidgetsFolderName = "Widgets";
    private const string AppSettingsFileName = "appSettings.json";
    private const string LayoutFileName = "layout.json";
    private const string RemindersFileName = "reminders.json";
    private const string TimetableFileName = "timetable.json";
    private const string ProfilesFolderName = "Profiles";

    private static string GetDataFolder()
    {
        // Test/tool isolation: a process may redirect the whole data folder (settings, layout,
        // timetable, reminders) away from the live user folder. The check/repro projects set
        // DESCANVAS_DATA_FOLDER first thing in Main — without it, a process that is not the
        // packaged app resolves DataFolder to %LocalAppData%\DeskCanvas anyway and its store
        // saves stomp the user's real data (a check run overwrote timetable.json with the
        // "测试学期" sample twice this way).
        var overrideFolder = Environment.GetEnvironmentVariable("DESCANVAS_DATA_FOLDER");
        if (!string.IsNullOrWhiteSpace(overrideFolder)) return overrideFolder;

        // Structural sandbox: every check/repro harness builds into tests\<Name>\bin\..., so
        // any process whose exe path carries a "tests" segment is a harness — give it its own
        // disposable data folder instead of the packaged app's live %LocalAppData%\DeskCanvas.
        // (Without this, a harness that constructs real widget views saves its stores straight
        // into the user's data — a TimetableChecks run overwrote timetable.json with the
        // "测试学期" sample twice.) The explicit override above wins when a check wants to
        // pin the folder itself.
        var harness = TestHarnessSandboxName();
        if (harness != null)
            return Path.Combine(Path.GetTempPath(), "DeskCanvas-TestHarnesses", harness);

        // Portable mode: a Widgets folder next to the exe (official layout / dev output).
        if (Directory.Exists(Path.Combine(CurrentFolder, WidgetsFolderName)))
        {
#if DEBUG
            // Dev builds keep the classic portable layout for the widget DLLs (they hot-load
            // from the exe-adjacent Widgets folder — see GetWidgetsFolder), but user data must
            // NOT live inside the build output: a Clean/rebuild wipes it, and the copied
            // layout/appSettings templates stomp whatever survived. Redirect dev data to a
            // sibling of the packaged folder so Debug runs, Release runs and the packaged app
            // never touch each other's data. (Release keeps the classic behaviour so the
            // shipped portable zip stays self-contained.)
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppName + "-dev");
#else
            return CurrentFolder;
#endif
        }

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

    /// <summary>
    /// Widget assemblies live next to the exe in portable mode (official layout, dev output —
    /// the assembly provider hot-loads them from there); packaged mode gets them
    /// extracted under the data folder. Decoupled from <see cref="DataFolder"/> because dev
    /// builds redirect the data folder out of the build output while the DLLs stay in it.
    /// </summary>
    /// <summary>
    /// The sandbox folder name for a check-harness process — its exe path carries a "tests"
    /// segment and the harness folder name is the segment right after it — or null for normal
    /// app processes (packaged app, dev build, release portable).
    /// </summary>
    private static string? TestHarnessSandboxName()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;

        var segments = exePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (segments[i].Equals("tests", StringComparison.OrdinalIgnoreCase))
                return segments[i + 1];
        }
        return null;
    }

    private static string GetWidgetsFolder()
    {
        if (Directory.Exists(Path.Combine(CurrentFolder, WidgetsFolderName)))
            return Path.Combine(CurrentFolder, WidgetsFolderName);
        return Path.Combine(DataFolder, WidgetsFolderName);
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
