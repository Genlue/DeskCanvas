using System.Text.Json;
using Avalonia.Threading;
using DeskCanvas.Core;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// Unified single data source for all shortcuts widgets across all screens.
/// Persists the document to <c>shortcuts.json</c> in the data folder and keeps every
/// widget, panel and editor window in sync (live), including external file edits.
/// </summary>
public static class ShortcutsStore
{
    private static readonly object FileLock = new();
    private static ShortcutsDocument? cachedDocument;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;

    /// <summary>
    /// Minimum age of the existing <c>.bak</c> before it is rotated again. The editor saves on
    /// every committed edit, so rotating on every save would copy the whole file per keystroke
    /// commit; a short interval keeps one recovery point while collapsing a burst of edits into
    /// a single backup. The atomic tmp+move replace is unaffected, so crash-safety is unchanged.
    /// </summary>
    private static readonly TimeSpan BackupMinAge = TimeSpan.FromSeconds(30);

    /// <summary>Raised whenever the document changes (any widget, the editor, or an external edit).</summary>
    public static event Action<ShortcutsDocument, object?>? ModelChanged;

    /// <summary>Raised when persisting the document fails (disk full, lock, ACL, ...).</summary>
    public static event Action<string>? SaveFailed;

    /// <summary>The document file path inside the writable data folder.</summary>
    public static string FilePath => Const.ShortcutsFile;

    static ShortcutsStore()
    {
        InitializeWatcher();
    }

    /// <summary>
    /// The current document; loaded on first use, seeded with starter shortcuts on a fresh install.
    /// A file that exists but cannot be parsed is NOT overwritten — the in-memory default is
    /// returned so recoverable user data survives.
    /// </summary>
    public static ShortcutsDocument Get()
    {
        if (cachedDocument != null) return cachedDocument;

        lock (FileLock)
        {
            if (cachedDocument != null) return cachedDocument;

            var fileExists = File.Exists(FilePath);
            cachedDocument = LoadFromFile();
            if (cachedDocument != null) return cachedDocument;

            cachedDocument = CreateDefault();
            if (!fileExists) SaveInternal(cachedDocument);
            return cachedDocument;
        }
    }

    /// <summary>Replace the whole document and broadcast the change.</summary>
    public static void Save(ShortcutsDocument document, object? sender = null)
    {
        lock (FileLock)
        {
            cachedDocument = document;
            SaveInternal(document);
        }

        NotifyModelChanged(document, sender);
    }

    /// <summary>Insert or replace one shortcut (matched by id) and persist.</summary>
    public static void Upsert(ShortcutDefinition shortcut, object? sender = null)
    {
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            var index = document.Shortcuts.FindIndex(s => s.Id == shortcut.Id);
            shortcut.UpdatedAt = DateTime.Now;
            if (index >= 0) document.Shortcuts[index] = shortcut;
            else document.Shortcuts.Add(shortcut);
            SaveInternal(document);
        }

        NotifyModelChanged(cachedDocument!, sender);
    }

    /// <summary>Delete a shortcut by id; returns true when it existed.</summary>
    public static bool Delete(string shortcutId, object? sender = null)
    {
        bool removed;
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            removed = document.Shortcuts.RemoveAll(s => s.Id == shortcutId) > 0;
            if (removed) SaveInternal(document);
        }

        if (removed) NotifyModelChanged(cachedDocument!, sender);
        return removed;
    }

    /// <summary>Deep copy a shortcut under a new id, appended after the original.</summary>
    public static ShortcutDefinition? Duplicate(string shortcutId, object? sender = null)
    {
        ShortcutDefinition? copy = null;
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            var source = document.Shortcuts.FirstOrDefault(s => s.Id == shortcutId);
            if (source == null) return null;

            copy = source.Clone();
            copy.Id = ShortcutDefinition.NewId();
            copy.Name = UniqueName(document, Loc.Format("Shortcuts_Param_CopyOf", source.Name));
            copy.UpdatedAt = DateTime.Now;
            foreach (var action in ShortcutTree.Flatten(copy.Actions))
                action.Id = ShortcutDefinition.NewId();
            document.Shortcuts.Insert(document.Shortcuts.IndexOf(source) + 1, copy);
            SaveInternal(document);
        }

        NotifyModelChanged(cachedDocument!, sender);
        return copy;
    }

    /// <summary>Remember the editor's current subject; persisted without broadcasting.</summary>
    public static void SetLastEdited(string shortcutId)
    {
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            if (document.LastEditedId == shortcutId) return;
            document.LastEditedId = shortcutId;
            SaveInternal(document);
        }
    }

    private static string UniqueName(ShortcutsDocument document, string baseName)
    {
        var name = baseName;
        var i = 2;
        while (document.Shortcuts.Any(s => s.Name == name))
            name = $"{baseName} {i++}";
        return name;
    }

    private static ShortcutsDocument LoadOrCreateLocked() => LoadFromFile() ?? CreateDefault();

    private static void NotifyModelChanged(ShortcutsDocument document, object? sender)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                ModelChanged?.Invoke(document, sender);
            else
                Dispatcher.UIThread.Post(() => ModelChanged?.Invoke(document, sender));
        }
        catch
        {
            ModelChanged?.Invoke(document, sender);
        }
    }

    private static void SaveInternal(ShortcutsDocument document)
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(document, DocumentJson.FileOptions);

            // Rotate a .bak before replacing: one generation of undo for hand edits. Throttled by
            // the age of the existing backup so a rapid burst of edits does not re-copy the file
            // on every save.
            try
            {
                var backup = path + ".bak";
                if (File.Exists(path) && ShouldRotateBackup(backup))
                    File.Copy(path, backup, overwrite: true);
            }
            catch
            {
                // Best-effort backup only.
            }

            // Atomic replace: write to .tmp beside the target, then move. A crash mid-write
            // must never leave a torn file.
            var tmp = path + ".tmp";
            lastWriteTime = DateTime.UtcNow;
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShortcutsStore] Save failed: {ex.Message}");
            RaiseSaveFailed(ex.Message);
        }
    }

    /// <summary>Whether the existing backup is old enough to be rotated again (missing ⇒ yes).</summary>
    private static bool ShouldRotateBackup(string backupPath)
    {
        try
        {
            if (!File.Exists(backupPath)) return true;
            return DateTime.UtcNow - File.GetLastWriteTimeUtc(backupPath) >= BackupMinAge;
        }
        catch
        {
            return true; // Unreadable metadata: prefer keeping a backup.
        }
    }

    private static void RaiseSaveFailed(string message)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                SaveFailed?.Invoke(message);
            else
                Dispatcher.UIThread.Post(() => SaveFailed?.Invoke(message));
        }
        catch
        {
            SaveFailed?.Invoke(message);
        }
    }

    private static ShortcutsDocument? LoadFromFile()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;

            var document = JsonSerializer.Deserialize<ShortcutsDocument>(json, DocumentJson.FileOptions);
            if (document == null) return null;

            Normalize(document);
            return document;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShortcutsStore] Load failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>Repair anything a hand edit could have broken; never throws.</summary>
    private static void Normalize(ShortcutsDocument document)
    {
        document.Shortcuts ??= [];

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        for (var i = document.Shortcuts.Count - 1; i >= 0; i--)
        {
            var shortcut = document.Shortcuts[i];
            if (shortcut == null)
            {
                document.Shortcuts.RemoveAt(i);
                continue;
            }

            if (string.IsNullOrWhiteSpace(shortcut.Id) || !seenIds.Add(shortcut.Id))
            {
                shortcut.Id = ShortcutDefinition.NewId();
                seenIds.Add(shortcut.Id);
            }

            shortcut.Name = string.IsNullOrWhiteSpace(shortcut.Name)
                ? Loc.Get("Shortcuts_NewName")
                : shortcut.Name.Trim()[..Math.Min(shortcut.Name.Trim().Length, 60)];

            if (string.IsNullOrWhiteSpace(shortcut.Glyph) || !GlyphLibrary.PickerGlyphs.Contains(shortcut.Glyph))
                shortcut.Glyph = string.IsNullOrWhiteSpace(shortcut.Glyph) ? "bolt" : shortcut.Glyph;

            shortcut.Color = string.IsNullOrWhiteSpace(shortcut.Color) ? "#534AB7" : shortcut.Color;
            shortcut.Actions ??= [];

            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in ShortcutTree.Flatten(shortcut.Actions))
                NormalizeAction(action, actionIds);
        }
    }

    private static void NormalizeAction(ShortcutAction action, HashSet<string> seenIds)
    {
        if (string.IsNullOrWhiteSpace(action.Id) || !seenIds.Add(action.Id))
        {
            action.Id = ShortcutDefinition.NewId();
            seenIds.Add(action.Id);
        }

        action.Params ??= [];
        action.Children ??= [];
        action.ElseChildren ??= [];
    }

    /// <summary>Starter shortcuts for a fresh install (also used as panel templates).</summary>
    private static ShortcutsDocument CreateDefault()
    {
        return new ShortcutsDocument
        {
            Version = 1,
            Shortcuts = ShortcutTemplates.CreateStarterSet(),
        };
    }

    private static void InitializeWatcher()
    {
        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;

            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            fileWatcher = new FileSystemWatcher(dir, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
            };

            fileWatcher.Changed += OnFileChanged;
            fileWatcher.Created += OnFileChanged;
            fileWatcher.Renamed += OnFileRenamed;
            fileWatcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShortcutsStore] Watcher init failed: {ex.Message}");
        }
    }

    private static void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Ignore our own writes.
        if ((DateTime.UtcNow - lastWriteTime).TotalMilliseconds < 1500) return;

        Dispatcher.UIThread.Post(() =>
        {
            lock (FileLock)
            {
                var reloaded = LoadFromFile();
                if (reloaded == null) return;
                cachedDocument = reloaded;
                NotifyModelChanged(cachedDocument, null);
            }
        }, DispatcherPriority.Background);
    }

    private static void OnFileRenamed(object sender, RenamedEventArgs e) => OnFileChanged(sender, e);

    /// <summary>Test helper: reset the in-memory cache.</summary>
    public static void ResetForTesting(ShortcutsDocument? document = null)
    {
        lock (FileLock)
        {
            cachedDocument = document;
        }
    }
}
