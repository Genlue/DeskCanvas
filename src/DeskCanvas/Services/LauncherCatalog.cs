using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace DeskCanvas.Services;

/// <summary>
/// One entry in the launcher: a shortcut the user dropped into the launcher folder.
/// <para>
/// Immutable identity (name / path / target) plus the icon the view model fills in later — the
/// catalogue itself never touches Avalonia bitmaps, so it stays a plain index that can be asserted
/// without a UI.
/// </para>
/// </summary>
/// <param name="Name">Display name shown in the grid (shell display name, not the raw file name).</param>
/// <param name="ShortcutPath">Absolute path to the <c>.lnk</c> / <c>.url</c>.</param>
/// <param name="Target">Resolved target for a <c>.lnk</c> (null for <c>.url</c> and unresolved links).</param>
/// <param name="IsBroken">
/// True when a resolved <paramref name="Target"/> no longer exists. Broken shortcuts stay in the
/// grid with a placeholder icon and 目标不可用 — they are never silently deleted, because the
/// target may live on a drive that is not mounted yet.
/// </param>
public sealed record LauncherEntry(string Name, string ShortcutPath, string? Target, bool IsBroken = false)
{
    /// <summary>Stable identity used for de-duplication and for keying icons.</summary>
    public string Key => ShortcutPath.ToLowerInvariant();
}

/// <summary>
/// The user-curated shortcut folder: a one-shot scan plus a debounced <see cref="FileSystemWatcher"/>.
/// <para>
/// Deliberately narrow. It lists <c>.lnk</c> and <c>.url</c> files directly inside the folder
/// (no recursion — an installer's sub-folder must not leak its uninstaller into the grid), sorts
/// them by display name, and raises <see cref="Changed"/> whenever the folder's content changes.
/// </para>
/// <para>
/// <b>Threading:</b> <see cref="Changed"/> is raised on the watcher's thread (or the caller's for
/// <see cref="Start"/>). Consumers must marshal to the UI thread themselves — this class has no
/// dispatcher dependency on purpose.
/// </para>
/// </summary>
public sealed class LauncherCatalog : IDisposable
{
    /// <summary>Coalesce the burst of events Explorer produces for one drop (copy + rename + write).</summary>
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(300);

    private readonly object gate = new();
    private readonly List<LauncherEntry> entries = [];

    private FileSystemWatcher? watcher;
    private Timer? debounce;
    private string? folder;
    private bool disposed;

    /// <summary>The current entries, sorted by display name. Never null; replaced wholesale on rescan.</summary>
    public IReadOnlyList<LauncherEntry> Entries
    {
        get { lock (gate) return entries.ToArray(); }
    }

    /// <summary>The folder currently being watched (null before <see cref="Start"/>).</summary>
    public string? Folder
    {
        get { lock (gate) return folder; }
    }

    /// <summary>Raised after a rescan produced a different entry set.</summary>
    public event Action? Changed;

    /// <summary>
    /// Point the catalogue at <paramref name="path"/>: create it when missing (so the user has
    /// somewhere to drop shortcuts), scan once, then watch for changes. Calling it again with a
    /// different path tears the old watcher down first.
    /// </summary>
    public void Start(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        lock (gate)
        {
            if (disposed) return;
            if (string.Equals(folder, path, StringComparison.OrdinalIgnoreCase) && watcher != null) return;

            folder = path;
            TearDownWatcherLocked();
        }

        try { Directory.CreateDirectory(path); }
        catch
        {
            // A read-only or unavailable folder still yields an empty grid rather than a crash;
            // the empty state explains where shortcuts belong.
        }

        Rescan();

        try
        {
            var created = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                Filter = "*.*",
                EnableRaisingEvents = true,
            };

            created.Created += OnFileEvent;
            created.Deleted += OnFileEvent;
            created.Changed += OnFileEvent;
            created.Renamed += OnRenamed;
            created.Error += OnWatcherError;

            lock (gate) watcher = created;
        }
        catch
        {
            // Watching is an enhancement: without it the grid simply refreshes on next summon.
        }
    }

    /// <summary>Re-read the folder now (also used by the view model after an import).</summary>
    public void Rescan()
    {
        string? path;
        lock (gate) path = folder;
        if (path == null) return;

        var scanned = Scan(path);

        bool changed;
        lock (gate)
        {
            // Both sides are name-sorted, so a plain sequence comparison is enough: a re-scan that
            // yields the same shortcuts must not repaint the grid.
            changed = !entries.SequenceEqual(scanned, EntryComparer.Instance);
            if (changed)
            {
                entries.Clear();
                entries.AddRange(scanned);
            }
        }

        if (changed) Changed?.Invoke();
    }

    /// <summary>Read the folder once, without touching the watcher.</summary>
    public static List<LauncherEntry> Scan(string path)
    {
        var result = new List<LauncherEntry>();
        if (!Directory.Exists(path)) return result;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path))
            {
                if (!IsShortcut(file)) continue;

                var name = ShellIconService.GetDisplayName(file);
                if (string.IsNullOrWhiteSpace(name)) continue;

                var target = ShellIconService.GetShortcutTarget(file);
                // A .url (Target == null) is never broken — there is nothing to stat.
                var broken = target is { } t && !File.Exists(t) && !Directory.Exists(t);
                result.Add(new LauncherEntry(name, file, target, broken));
            }
        }
        catch
        {
            // Permission errors / a folder deleted mid-enumeration: keep whatever was read.
        }

        result.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return result;
    }

    /// <summary>True for the two shortcut kinds the launcher lists.</summary>
    public static bool IsShortcut(string path) =>
        path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".url", StringComparison.OrdinalIgnoreCase);

    /// <summary>Open the folder in Explorer (the empty state and the settings page both offer it).</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Explorer refusing to open a folder is not worth failing a settings action over.
        }
    }

    /// <summary>Launch an entry through the shell, so the shortcut's own arguments/working dir apply.</summary>
    public static void Launch(LauncherEntry entry)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = entry.ShortcutPath,
            UseShellExecute = true,
        });
    }

    /// <summary>
    /// Open any path through the shell — a file search hit, a folder, a document. The default
    /// handler decides what "open" means, exactly like double-clicking it in Explorer.
    /// </summary>
    public static void OpenPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }

    /// <summary>Show <paramref name="path"/> selected in its Explorer window (Ctrl+Enter).</summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (Directory.Exists(path))
            {
                OpenPath(path);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                // /select, must be glued to the quoted path; Explorer's parser is literal here.
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch
        {
            // Revealing is a convenience; a refused call must not take the launcher down.
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            TearDownWatcherLocked();
            folder = null;
        }

        debounce?.Dispose();
        debounce = null;
    }

    // ---- watcher plumbing ---------------------------------------------------------

    private void TearDownWatcherLocked()
    {
        var existing = watcher;
        watcher = null;
        if (existing == null) return;

        try
        {
            existing.EnableRaisingEvents = false;
            existing.Created -= OnFileEvent;
            existing.Deleted -= OnFileEvent;
            existing.Changed -= OnFileEvent;
            existing.Renamed -= OnRenamed;
            existing.Error -= OnWatcherError;
            existing.Dispose();
        }
        catch { }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsShortcut(e.FullPath)) return;
        ScheduleRescan();
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (!IsShortcut(e.FullPath) && !IsShortcut(e.OldFullPath)) return;
        ScheduleRescan();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // A buffer overflow drops events; one rescan resynchronises. Do not tear the watcher down.
        ScheduleRescan();
    }

    private void ScheduleRescan()
    {
        lock (gate)
        {
            if (disposed) return;
            debounce ??= new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);
            debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Element identity for the re-scan comparison (path + display name).</summary>
    private sealed class EntryComparer : IEqualityComparer<LauncherEntry>
    {
        public static readonly EntryComparer Instance = new();

        public bool Equals(LauncherEntry? x, LauncherEntry? y) =>
            x is not null && y is not null
            && string.Equals(x.Key, y.Key, StringComparison.Ordinal)
            && string.Equals(x.Name, y.Name, StringComparison.Ordinal);

        public int GetHashCode(LauncherEntry obj) => HashCode.Combine(obj.Key, obj.Name);
    }
}
