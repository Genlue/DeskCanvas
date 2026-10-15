using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeskCanvas.Core;
using DeskCanvas.Core.Models;

namespace DeskCanvas.Services;

/// <summary>
/// Why a data-folder change was rejected. The settings UI turns these into localised messages;
/// the service itself stays UI-free.
/// </summary>
public enum DataFolderError
{
    /// <summary>No error — the change was applied.</summary>
    None,

    /// <summary>The candidate path is empty or whitespace.</summary>
    Empty,

    /// <summary>The candidate path is not a legal path (invalid characters, not rooted, …).</summary>
    Invalid,

    /// <summary>The candidate path points at an existing file, not a folder.</summary>
    IsFile,

    /// <summary>The folder cannot be created, or is not writable.</summary>
    Unwritable,

    /// <summary>The candidate is already the folder in use.</summary>
    SameAsCurrent,

    /// <summary>No writable location for the pointer file (neither the exe folder nor the anchor).</summary>
    NoPointerLocation,

    /// <summary>An unexpected IO failure; <see cref="DataFolderResult.Message"/> carries the detail.</summary>
    IoFailure
}

/// <summary>
/// Outcome of a data-folder change.
/// </summary>
/// <param name="Error">Why it failed, or <see cref="DataFolderError.None"/>.</param>
/// <param name="Message">Underlying exception message for <see cref="DataFolderError.IoFailure"/>.</param>
public readonly record struct DataFolderResult(DataFolderError Error, string? Message = null)
{
    /// <summary>True when the change was applied.</summary>
    public bool Ok => Error == DataFolderError.None;

    /// <summary>A successful result.</summary>
    public static DataFolderResult Success => new(DataFolderError.None);

    /// <summary>A failure of the given kind.</summary>
    public static DataFolderResult Fail(DataFolderError error, string? message = null) => new(error, message);
}

/// <summary>
/// Reads and writes the user's data-folder choice.
/// <para>
/// The choice is a <c>dataFolder.json</c> pointer, not a setting inside <c>appSettings.json</c>:
/// the pointer must be readable <i>before</i> the data folder is known, so it cannot live inside
/// the folder it selects. Two locations are tried — next to the exe (portable installs, where
/// the whole folder travels together) and in <see cref="Const.DefaultDataFolder"/> (installed
/// apps, whose exe folder under Program Files is not writable).
/// </para>
/// <para>
/// The app reads the pointer once at startup (<see cref="Const.DataFolder"/>), so every change
/// here needs a restart to take effect.
/// </para>
/// </summary>
public static class DataFolderService
{
    /// <summary>The data folder currently in use.</summary>
    public static string Current => Const.DataFolder;

    /// <summary>Which rule picked <see cref="Current"/>.</summary>
    public static DataFolderSource Source => Const.DataFolderSource;

    /// <summary>True when the folder comes from a saved user choice rather than a layout rule.</summary>
    public static bool IsUserPinned =>
        Source is DataFolderSource.ExePointer or DataFolderSource.AnchorPointer;

    /// <summary>
    /// True when the folder is at its implicit location (no user choice involved), which is what
    /// "restore default" returns the app to.
    /// </summary>
    public static bool IsAtDefaultLocation => !IsUserPinned;

    /// <summary>
    /// The pointer file location that should receive a new choice: the exe folder when it is
    /// writable (portable), otherwise the fixed anchor in <see cref="Const.DefaultDataFolder"/>.
    /// A saved choice keeps its own location so the two never disagree.
    /// </summary>
    public static string? WritablePointerPath()
    {
        if (Source is DataFolderSource.ExePointer or DataFolderSource.Portable or DataFolderSource.PortableDev)
        {
            var exePointer = Path.Combine(Const.CurrentFolder, Const.DataFolderPointerFileName);
            if (CanWriteTo(Const.CurrentFolder)) return exePointer;
        }

        if (Const.DataFolderPointerPath != null && CanWriteTo(Path.GetDirectoryName(Const.DataFolderPointerPath)!))
            return Const.DataFolderPointerPath;

        var anchorPointer = Path.Combine(Const.DefaultDataFolder, Const.DataFolderPointerFileName);
        if (CanWriteTo(Const.DefaultDataFolder)) return anchorPointer;

        // Last resort for a fully portable install on read-only media: try the exe folder anyway.
        return CanWriteTo(Const.CurrentFolder)
            ? Path.Combine(Const.CurrentFolder, Const.DataFolderPointerFileName)
            : null;
    }

    /// <summary>
    /// Validates a candidate folder without touching the disk beyond a create/write probe.
    /// Returns the failure kind, or <see cref="DataFolderError.None"/> when it is usable.
    /// </summary>
    public static DataFolderError Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return DataFolderError.Empty;

        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception)
        {
            return DataFolderError.Invalid;
        }

        if (File.Exists(full)) return DataFolderError.IsFile;
        if (string.Equals(full, Path.GetFullPath(Current), StringComparison.OrdinalIgnoreCase))
            return DataFolderError.SameAsCurrent;

        return CanWriteTo(full) ? DataFolderError.None : DataFolderError.Unwritable;
    }

    /// <summary>
    /// Saves <paramref name="path"/> as the data folder. Existing data is copied first when
    /// <paramref name="migrate"/> is set; files already present in the target are left alone.
    /// </summary>
    public static DataFolderResult Apply(string? path, bool migrate)
    {
        var error = Validate(path);
        if (error != DataFolderError.None) return DataFolderResult.Fail(error);

        var target = Path.GetFullPath(path!.Trim());
        var pointer = WritablePointerPath();
        if (pointer == null) return DataFolderResult.Fail(DataFolderError.NoPointerLocation);

        try
        {
            Directory.CreateDirectory(target);

            if (migrate) Migrate(Current, target);

            WritePointer(pointer, target);
            return DataFolderResult.Success;
        }
        catch (Exception ex)
        {
            return DataFolderResult.Fail(DataFolderError.IoFailure, ex.Message);
        }
    }

    /// <summary>
    /// Drops the saved choice, so the folder falls back to the layout rules (portable /
    /// packaged / dev). Existing data is copied back to the default folder first when
    /// <paramref name="migrate"/> is set.
    /// </summary>
    public static DataFolderResult Reset(bool migrate)
    {
        try
        {
            var fallback = FallbackFolderAfterReset();

            if (migrate && !string.Equals(fallback, Path.GetFullPath(Current), StringComparison.OrdinalIgnoreCase))
                Migrate(Current, fallback);

            // A choice can only ever live in one of the two locations, but clear both: a stale
            // pointer left behind by an older build must not resurrect itself after a reset.
            foreach (var candidate in PointerCandidates())
            {
                try
                {
                    if (File.Exists(candidate)) File.Delete(candidate);
                }
                catch (Exception)
                {
                    // A locked pointer is reported through the exe-folder probe below.
                }
            }

            var stillThere = Const.DataFolderPointerPath != null && File.Exists(Const.DataFolderPointerPath);
            return stillThere
                ? DataFolderResult.Fail(DataFolderError.IoFailure, Const.DataFolderPointerPath)
                : DataFolderResult.Success;
        }
        catch (Exception ex)
        {
            return DataFolderResult.Fail(DataFolderError.IoFailure, ex.Message);
        }
    }

    /// <summary>
    /// Copies the app's data files from <paramref name="source"/> to <paramref name="target"/>.
    /// Never overwrites: an existing file in the target wins, so pointing at a folder that
    /// already holds a setup cannot clobber it.
    /// </summary>
    public static void Migrate(string source, string target)
    {
        if (!Directory.Exists(source)) return;
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return;

        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            // Derived bulk (extracted widget DLLs, image/GPU caches, trace logs) is rebuilt on
            // demand; copying it would only make the move slow and stale.
            if (IsDerived(Path.GetFileName(file))) continue;

            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination)) continue;

            try
            {
                File.Copy(file, destination, overwrite: false);
            }
            catch (IOException)
            {
                // Locked or already there: the app rewrites its own data on the next save.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(directory);
            if (IsDerived(name)) continue;
            CopyDirectory(directory, Path.Combine(target, name));
        }
    }

    /// <summary>
    /// True when the folder already holds an app setup (so a reset/move has something to carry
    /// over and a "start fresh" choice is a visible one).
    /// </summary>
    public static bool HasData(string folder)
    {
        try
        {
            return File.Exists(Path.Combine(folder, Const.LayoutFileName))
                   || File.Exists(Path.Combine(folder, Const.AppSettingsFileName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The folder the app would use after a reset — the same layout rules <see cref="Const"/>
    /// applies, minus any pointer. Shown in the confirmation so the user knows where data lands.
    /// </summary>
    public static string FallbackFolderAfterReset()
    {
        if (Source is DataFolderSource.TestHarness or DataFolderSource.EnvironmentVariable)
            return Current;

        if (Directory.Exists(Path.Combine(Const.CurrentFolder, Const.WidgetsFolderName)))
        {
#if DEBUG
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Const.AppName + "-dev");
#else
            return Const.CurrentFolder;
#endif
        }

        return Const.DefaultDataFolder;
    }

    /// <summary>
    /// Opens the folder in the shell (Explorer). No-op when the folder does not exist.
    /// </summary>
    public static void OpenInShell(string folder)
    {
        if (!Directory.Exists(folder)) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true
        });
    }

    private static IEnumerable<string> PointerCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Const.DataFolderPointerPath != null && seen.Add(Const.DataFolderPointerPath))
            yield return Const.DataFolderPointerPath;

        var exe = Path.Combine(Const.CurrentFolder, Const.DataFolderPointerFileName);
        if (seen.Add(exe)) yield return exe;

        var anchor = Path.Combine(Const.DefaultDataFolder, Const.DataFolderPointerFileName);
        if (seen.Add(anchor)) yield return anchor;
    }

    private static void WritePointer(string pointerPath, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(pointerPath)!);

        var json = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [Const.DataFolderPointerPropertyName] = target
        });

        File.WriteAllText(pointerPath, json);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (File.Exists(destination)) continue;

            try
            {
                File.Copy(file, destination, overwrite: false);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    /// <summary>
    /// True for entries the app regenerates: the extracted widget bundle, caches and trace logs.
    /// Skipped by <see cref="Migrate"/>.
    /// </summary>
    private static bool IsDerived(string name) =>
        name.Equals(Const.WidgetsFolderName, StringComparison.OrdinalIgnoreCase)
        || name.Equals("Cache", StringComparison.OrdinalIgnoreCase)
        || name.Equals("ClipboardCache", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writability probe: the folder must exist (or be creatable) and accept a new file. Cheaper
    /// and far more reliable than reading ACLs.
    /// </summary>
    private static bool CanWriteTo(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder)) return false;

            Directory.CreateDirectory(folder);

            var probe = Path.Combine(folder, Path.GetRandomFileName());
            using var stream = File.Create(probe, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
