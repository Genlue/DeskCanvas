using System.Text.Json;
using DeskCanvas.Core.Models;

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
    /// The default packaged data folder (<c>%LocalAppData%\DeskCanvas</c>). Always writable and
    /// never inside a build output, so it also hosts the fallback pointer file when the exe
    /// folder is read-only (an installed app under Program Files).
    /// </summary>
    public static readonly string DefaultDataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);

    private static readonly (string Folder, DataFolderSource Source, string? Pointer) Resolution = ResolveDataFolder();

    /// <summary>
    /// The runtime data folder (settings, layout, widgets).
    /// <para>
    /// Resolution order — first match wins:
    /// <c>DESCANVAS_DATA_FOLDER</c> environment variable, a check-harness sandbox, a
    /// <c>dataFolder.json</c> pointer next to the exe, the classic portable layout (a
    /// <c>Widgets</c> folder next to the exe — redirected to <c>%LocalAppData%\DeskCanvas-dev</c>
    /// in Debug builds), a <c>dataFolder.json</c> pointer in the default packaged folder, and
    /// finally <see cref="DefaultDataFolder"/>.
    /// </para>
    /// </summary>
    public static readonly string DataFolder = Resolution.Folder;
    /// <summary>
    /// Which rule produced <see cref="DataFolder"/>. Shown in the settings UI so the location is
    /// never a guess.
    /// </summary>
    public static readonly DataFolderSource DataFolderSource = Resolution.Source;
    /// <summary>
    /// The <c>dataFolder.json</c> that pinned <see cref="DataFolder"/>, or <c>null</c> when the
    /// folder came from a layout/environment rule rather than a persisted user choice.
    /// </summary>
    public static readonly string? DataFolderPointerPath = Resolution.Pointer;
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

    /// <summary>Name of the folder holding widget assemblies (portable: next to the exe).</summary>
    public const string WidgetsFolderName = "Widgets";
    /// <summary>Name of the user-chosen data folder pointer file.</summary>
    public const string DataFolderPointerFileName = "dataFolder.json";
    /// <summary>JSON property holding the target folder inside <see cref="DataFolderPointerFileName"/>.</summary>
    public const string DataFolderPointerPropertyName = "DataFolder";
    /// <summary>Name of the application settings file inside the data folder.</summary>
    public const string AppSettingsFileName = "appSettings.json";
    /// <summary>Name of the widget layout file inside the data folder.</summary>
    public const string LayoutFileName = "layout.json";
    private const string RemindersFileName = "reminders.json";
    private const string TimetableFileName = "timetable.json";
    private const string ProfilesFolderName = "Profiles";

    private static (string Folder, DataFolderSource Source, string? Pointer) ResolveDataFolder()
    {
        // Test/tool isolation: a process may redirect the whole data folder (settings, layout,
        // timetable, reminders) away from the live user folder. The check/repro projects set
        // DESCANVAS_DATA_FOLDER first thing in Main — without it, a process that is not the
        // packaged app resolves DataFolder to %LocalAppData%\DeskCanvas anyway and its store
        // saves stomp the user's real data (a check run overwrote timetable.json with the
        // "测试学期" sample twice this way).
        var overrideFolder = Environment.GetEnvironmentVariable("DESCANVAS_DATA_FOLDER");
        if (!string.IsNullOrWhiteSpace(overrideFolder))
            return (overrideFolder.Trim(), Models.DataFolderSource.EnvironmentVariable, null);

        // Structural sandbox: every check/repro harness builds into tests\<Name>\bin\..., so
        // any process whose exe path carries a "tests" segment is a harness — give it its own
        // disposable data folder instead of the packaged app's live %LocalAppData%\DeskCanvas.
        // (Without this, a harness that constructs real widget views saves its stores straight
        // into the user's data — a TimetableChecks run overwrote timetable.json with the
        // "测试学期" sample twice.) The explicit override above wins when a check wants to
        // pin the folder itself.
        // It also wins over a user pointer: a harness must never read the live user data.
        var harness = TestHarnessSandboxName();
        if (harness != null)
            return (Path.Combine(Path.GetTempPath(), "DeskCanvas-TestHarnesses", harness),
                Models.DataFolderSource.TestHarness, null);

        // User choice, portable form: a dataFolder.json next to the exe pins the folder. It wins
        // over the portable-layout rule below so a portable install can keep its data out of the
        // build/dist folder it happens to live in (the dist\win-x64 publish output, which
        // build.ps1 wipes on every publish).
        var exePointer = Path.Combine(CurrentFolder, DataFolderPointerFileName);
        var exeTarget = ReadPointerFolder(exePointer);
        if (exeTarget != null)
            return (exeTarget, Models.DataFolderSource.ExePointer, exePointer);

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
            return (Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    AppName + "-dev"),
                Models.DataFolderSource.PortableDev, null);
#else
            return (CurrentFolder, Models.DataFolderSource.Portable, null);
#endif
        }

        // User choice, installed form: the exe folder is not writable for an installed app, so
        // the pointer lives in the default packaged folder instead. That folder is fixed (it
        // never follows DataFolder), which is exactly what makes it usable as an anchor.
        var anchorPointer = Path.Combine(DefaultDataFolder, DataFolderPointerFileName);
        var anchorTarget = ReadPointerFolder(anchorPointer);
        if (anchorTarget != null)
            return (anchorTarget, Models.DataFolderSource.AnchorPointer, anchorPointer);

        var folder = DefaultDataFolder;

        // One-time migration from the pre-rebrand data folder (%LocalAppData%\uWidgets):
        // copy settings, layout, reminders, profiles and extracted widgets into
        // %LocalAppData%\DeskCanvas so an in-place upgrade keeps the user's setup.
        // The old folder is left untouched as a fallback for rolling back.
        if (!Directory.Exists(folder))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
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
        return (folder, Models.DataFolderSource.Default, null);
    }

    /// <summary>
    /// Reads a <c>dataFolder.json</c> pointer (<c>{"DataFolder": "..."}</c>), or <c>null</c> when
    /// the file is missing, unreadable, malformed or carries an empty value — every failure mode
    /// falls through to the next resolution rule rather than crashing at startup.
    /// </summary>
    private static string? ReadPointerFolder(string pointerFile)
    {
        try
        {
            if (!File.Exists(pointerFile)) return null;

            using var document = JsonDocument.Parse(File.ReadAllText(pointerFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty(DataFolderPointerPropertyName, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String) return null;

            var folder = value.GetString();
            if (string.IsNullOrWhiteSpace(folder)) return null;

            folder = folder.Trim();
            return Path.GetFullPath(folder);
        }
        catch (Exception)
        {
            // A corrupt or locked pointer must never prevent startup: fall back to the default.
            return null;
        }
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
