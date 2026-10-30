using System.Text.Json;
using Avalonia.Threading;
using DeskCanvas.Core;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// Unified single data source for shortcuts: one file, shared by every Shortcuts card, panel and
/// editor window across all screens and all configuration profiles.
/// <para>
/// The file lives at the data root, not inside a profile folder, so switching 方案 never swaps
/// the shortcut set out and removing the last Shortcuts widget never takes the data with it.
/// Everything stays in sync live, including external file edits.
/// </para>
/// </summary>
public static class ShortcutsStore
{
    private static readonly object FileLock = new();
    private static ShortcutsDocument? cachedDocument;
    private static FileSystemWatcher? fileWatcher;
    private static DateTime lastWriteTime = DateTime.MinValue;
    private static volatile bool isReadOnly;

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

    /// <summary>
    /// Raised after a load-time migration with a localized, user-facing message. The UI uses it
    /// to explain a read-only newer file or the repairs applied to an old one.
    /// </summary>
    public static event Action<string>? MigrationNotice;

    /// <summary>
    /// True when the file was written by a newer build. The document is returned for viewing
    /// but every save is skipped so the newer data is never overwritten.
    /// </summary>
    public static bool IsReadOnly => isReadOnly;

    /// <summary>The single shared document file path (data root, outside any profile folder).</summary>
    public static string FilePath => Const.ShortcutsFile;

    static ShortcutsStore()
    {
        InitializeWatcher();
        // No ActiveProfileChanged subscription on purpose: the file is not profile-scoped, so a
        // profile switch must neither drop the cache nor re-point the watcher. Shortcuts stay put.
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

            isReadOnly = false;
            cachedDocument = CreateDefault();
            // Normalize the seeded document too: a template authored against the legacy tree
            // must not leave two formats in the file.
            Normalize(cachedDocument);
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
            document.Shortcuts ??= [];
            var index = document.Shortcuts.FindIndex(s => s != null && s.Id == shortcut.Id);
            shortcut.UpdatedAt = DateTime.Now;
            if (index >= 0) document.Shortcuts[index] = shortcut;
            else document.Shortcuts.Add(shortcut);
            SaveInternal(document);
        }

        NotifyModelChanged(cachedDocument!, sender);
    }

    /// <summary>
    /// Move one shortcut one slot earlier (<paramref name="delta"/> = -1) or later (+1) in the
    /// shared order. The list order IS the card order, so this is the whole manual-sorting story:
    /// the card, the panel and the editor all read the same document.
    /// Returns false at the edges (nothing to move) and for an unknown id.
    /// </summary>
    public static bool Move(string shortcutId, int delta, object? sender = null)
    {
        if (delta == 0) return false;

        var moved = false;
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            document.Shortcuts ??= [];

            var index = document.Shortcuts.FindIndex(s => s != null && s.Id == shortcutId);
            var target = index + delta;
            if (index < 0 || target < 0 || target >= document.Shortcuts.Count) return false;

            var item = document.Shortcuts[index];
            document.Shortcuts.RemoveAt(index);
            document.Shortcuts.Insert(target, item);
            SaveInternal(document);
            moved = true;
        }

        if (moved) NotifyModelChanged(cachedDocument!, sender);
        return moved;
    }

    /// <summary>Delete a shortcut by id; returns true when it existed.</summary>
    public static bool Delete(string shortcutId, object? sender = null)
    {
        bool removed;
        lock (FileLock)
        {
            var document = cachedDocument ??= LoadOrCreateLocked();
            document.Shortcuts ??= [];
            removed = document.Shortcuts.RemoveAll(s => s != null && s.Id == shortcutId) > 0;
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
            document.Shortcuts ??= [];
            var source = document.Shortcuts.FirstOrDefault(s => s != null && s.Id == shortcutId);
            if (source == null) return null;

            copy = source.Clone();
            copy.Id = ShortcutDefinition.NewId();
            copy.Name = UniqueName(document, Loc.Format("Shortcuts_Param_CopyOf", source.Name));
            // Copying the binding would immediately collide with the original in the hotkey
            // table; a duplicate starts unbound.
            copy.Hotkey = null;
            copy.UpdatedAt = DateTime.Now;
            ReassignGraphIds(copy);
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
        while (document.Shortcuts.Any(s => s != null && s.Name == name))
            name = $"{baseName} {i++}";
        return name;
    }

    /// <summary>
    /// Fresh ids for a duplicated shortcut's nodes, links, switch cases and references. A
    /// case id is a port name, so it has to be remapped everywhere a port can be named:
    /// <see cref="WorkflowLink.FromPort"/> and <see cref="ShortcutValue.SourcePort"/>.
    /// </summary>
    private static void ReassignGraphIds(ShortcutDefinition copy)
    {
        var graph = copy.EffectiveGraph;
        var nodeMap = new Dictionary<string, string>(StringComparer.Ordinal);
        var caseMaps = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

        foreach (var node in graph.Nodes)
        {
            if (node == null) continue;
            if (string.IsNullOrEmpty(node.Id)) node.Id = ShortcutDefinition.NewId();
            node.Params ??= [];

            var oldId = node.Id;
            node.Id = ShortcutDefinition.NewId();
            node.ResetActionCache();
            nodeMap[oldId] = node.Id;

            if (node.Type == WorkflowNodeTypes.Switch && node.Cases is { Count: > 0 })
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var item in node.Cases)
                {
                    if (item == null) continue;
                    var oldCaseId = item.Id;
                    item.Id = $"case_{ShortcutDefinition.NewId()[..6]}";
                    if (!string.IsNullOrEmpty(oldCaseId)) map[oldCaseId] = item.Id;
                }

                caseMaps[oldId] = map;
            }
        }

        foreach (var link in graph.Links)
        {
            if (link == null) continue;
            link.Id = ShortcutDefinition.NewId();

            var oldFrom = link.From;
            if (!string.IsNullOrEmpty(oldFrom) && nodeMap.TryGetValue(oldFrom, out var from))
            {
                link.From = from;
                if (caseMaps.TryGetValue(oldFrom, out var cases) &&
                    !string.IsNullOrEmpty(link.FromPort) &&
                    cases.TryGetValue(link.FromPort, out var casePort))
                    link.FromPort = casePort;
            }

            if (!string.IsNullOrEmpty(link.To) && nodeMap.TryGetValue(link.To, out var to))
                link.To = to;
        }

        foreach (var node in graph.Nodes)
        {
            if (node == null) continue;
            node.Params ??= [];
            foreach (var value in node.Params.Values)
            {
                if (value is not { IsVariable: true } || value.SourceActionId is not { } oldSource)
                    continue;

                if (nodeMap.TryGetValue(oldSource, out var remapped))
                    value.SourceActionId = remapped;

                if (caseMaps.TryGetValue(oldSource, out var cases) &&
                    value.SourcePort != null &&
                    cases.TryGetValue(value.SourcePort, out var casePort))
                    value.SourcePort = casePort;
            }
        }

        graph.Invalidate();
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

    private static void RaiseMigrationNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                MigrationNotice?.Invoke(message);
            else
                Dispatcher.UIThread.Post(() => MigrationNotice?.Invoke(message));
        }
        catch
        {
            try { MigrationNotice?.Invoke(message); } catch { }
        }
    }

    private static void SaveInternal(ShortcutsDocument document)
    {
        if (isReadOnly) return;

        try
        {
            var path = FilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            // A save must never write an old schema: migrate first so a caller that hands us a
            // freshly built v1 tree still produces a v3 file.
            var migration = WorkflowMigration.Migrate(document);
            if (migration.ReadOnly)
            {
                isReadOnly = true;
                RaiseMigrationNotice(Loc.Format("Shortcuts_Migrate_NewerVersion",
                    migration.FromVersion, ShortcutsDocument.CurrentVersion));
                return;
            }

            // Before rewriting a v1/v2 file, keep a copy named after the schema it used. This is
            // best effort: a failed backup must never fail the save.
            BackupVersioned(path);

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

    /// <summary>Copy the current on-disk file to <c>&lt;file&gt;.v&lt;n&gt;.bak</c> when it is v1/v2.</summary>
    private static void BackupVersioned(string path)
    {
        try
        {
            if (!File.Exists(path)) return;

            var declared = ReadDeclaredVersion(File.ReadAllText(path));
            if (declared is < 1 or >= ShortcutsDocument.CurrentVersion) return;

            File.Copy(path, $"{path}.v{declared}.bak", overwrite: true);
        }
        catch
        {
            // Best effort only.
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
            if (!File.Exists(path))
            {
                isReadOnly = false;
                return null;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                isReadOnly = false;
                return null;
            }

            var document = JsonSerializer.Deserialize<ShortcutsDocument>(json, DocumentJson.FileOptions);
            if (document == null)
            {
                isReadOnly = false;
                return null;
            }

            // The model's Version initializer is CurrentVersion, so a missing field cannot be
            // distinguished after deserialization. Read the raw file first.
            var declared = ReadDeclaredVersion(json);
            if (declared > 0) document.Version = declared;

            var migration = WorkflowMigration.Migrate(document);
            isReadOnly = migration.ReadOnly;

            if (migration.ReadOnly)
            {
                // Never normalize or save a document we do not understand. The list is made
                // non-null only so the UI can render it safely.
                document.Shortcuts ??= [];
                RaiseMigrationNotice(Loc.Format("Shortcuts_Migrate_NewerVersion",
                    migration.FromVersion, ShortcutsDocument.CurrentVersion));
                return document;
            }

            Normalize(document);

            if (migration.FromVersion < ShortcutsDocument.CurrentVersion)
            {
                // Rewrite the upgraded file immediately; SaveInternal makes the versioned backup.
                SaveInternal(document);
                if (migration.Diagnostics.Count > 0)
                    RaiseMigrationNotice(FormatDiagnostics(migration.Diagnostics));
            }

            return document;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ShortcutsStore] Load failed: {ex.Message}");
            isReadOnly = false;
            return null;
        }
    }

    private static string? FormatDiagnostics(IReadOnlyList<WorkflowDiagnostic> diagnostics)
    {
        var text = string.Join(Environment.NewLine, diagnostics
            .Where(d => d.Severity != DiagnosticSeverity.Info)
            .Select(d => d.Text));

        return string.IsNullOrWhiteSpace(text) ? null : text;
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

            // Canonicalize a valid hotkey; keep an invalid one (trimmed) so the editor can show
            // "invalid" instead of silently deleting what the user typed.
            var hotkey = shortcut.Hotkey?.Trim();
            if (string.IsNullOrEmpty(hotkey))
            {
                shortcut.Hotkey = null;
            }
            else if (HotkeyGesture.TryParse(hotkey, requireModifier: true, out var gesture, out _))
            {
                shortcut.Hotkey = gesture.Text;
            }
            else
            {
                shortcut.Hotkey = hotkey[..Math.Min(hotkey.Length, 64)];
            }

            shortcut.Actions ??= [];

            var actionIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var action in FlattenActions(shortcut.Actions))
                NormalizeAction(action, actionIds);

            NormalizeGraph(shortcut);
        }

        document.Version = ShortcutsDocument.CurrentVersion;
    }

    /// <summary>
    /// Make a shortcut's graph safe to run: one start node, unique ids, finite coordinates,
    /// switch cases with stable ids, and no links to nodes that no longer exist. A v1 document
    /// is migrated here once.
    /// </summary>
    private static void NormalizeGraph(ShortcutDefinition shortcut)
    {
        var graph = shortcut.EffectiveGraph;
        graph.Nodes ??= [];
        graph.Links ??= [];
        graph.Nodes.RemoveAll(n => n == null);
        graph.Links.RemoveAll(l => l == null);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = graph.Nodes.Count - 1; i >= 0; i--)
        {
            var node = graph.Nodes[i];
            if (node == null || string.IsNullOrWhiteSpace(node.Type))
            {
                graph.Nodes.RemoveAt(i);
                continue;
            }

            if (string.IsNullOrWhiteSpace(node.Id) || !seen.Add(node.Id))
            {
                node.Id = ShortcutDefinition.NewId();
                node.ResetActionCache();
                seen.Add(node.Id);
            }

            node.Params ??= [];
            if (!double.IsFinite(node.X)) node.X = 0;
            if (!double.IsFinite(node.Y)) node.Y = 0;
            node.X = Math.Clamp(node.X, -20000, 20000);
            node.Y = Math.Clamp(node.Y, -20000, 20000);

            if (node.Type == "if")
            {
                node.Type = WorkflowNodeTypes.Branch;
                node.ResetActionCache();
            }
            else if (node.Type == "repeat")
            {
                node.Type = WorkflowNodeTypes.ForLoop;
                node.ResetActionCache();
            }

            if (node.Type == WorkflowNodeTypes.Switch)
            {
                node.Cases ??= WorkflowCase.CreateDefaults();
                if (node.Cases.Count == 0) node.Cases = WorkflowCase.CreateDefaults();

                var caseIds = new HashSet<string>(StringComparer.Ordinal);
                for (var c = 0; c < node.Cases.Count; c++)
                {
                    var item = node.Cases[c];
                    if (item == null)
                    {
                        item = new WorkflowCase { Id = $"case{c}" };
                        node.Cases[c] = item;
                    }

                    item.Value ??= string.Empty;
                    if (string.IsNullOrWhiteSpace(item.Id) || !caseIds.Add(item.Id))
                    {
                        item.Id = $"case{c}";
                        caseIds.Add(item.Id);
                    }
                }
            }
        }

        // Exactly one start node; the interpreter needs a single entry point.
        var starts = graph.Nodes.Where(n => n.Type == WorkflowNodeTypes.Start).ToList();
        for (var i = starts.Count - 1; i >= 1; i--)
        {
            graph.Nodes.Remove(starts[i]);
        }

        if (starts.Count == 0) WorkflowMigration.EnsureStart(graph);

        graph.Invalidate();
        PruneDanglingLinks(graph, shortcut);

        // Duplicate exec links out of the same port would make "next" ambiguous; keep the first.
        var execPorts = new HashSet<string>(StringComparer.Ordinal);
        graph.Links.RemoveAll(l => l.Kind == LinkKinds.Exec && !execPorts.Add($"{l.From}|{l.FromPort}"));
        graph.Invalidate();

        shortcut.Actions = [];
        shortcut.Graph = graph;
    }

    /// <summary>
    /// Drop links that point at nodes or ports that no longer exist. This mirrors
    /// <see cref="WorkflowGraph.PruneDanglingLinks"/> but knows the shortcut's input ports,
    /// which only exist on the start node of its owner.
    /// </summary>
    private static void PruneDanglingLinks(WorkflowGraph graph, ShortcutDefinition owner)
    {
        graph.Invalidate();
        graph.Links.RemoveAll(l =>
        {
            var from = graph.Find(l.From);
            var to = graph.Find(l.To);
            if (from == null || to == null) return true;
            if (!WorkflowNodeTypes.HasOutput(from, l.FromPort, owner)) return true;

            return l.Kind == LinkKinds.Data
                ? !WorkflowNodeTypes.IsBindableParam(to, l.ToPort)
                : !WorkflowNodeTypes.HasInput(to.Type);
        });
        graph.Invalidate();
    }

    /// <summary>Null-safe legacy tree walk (a hand-edited file may contain a null entry).</summary>
    private static IEnumerable<ShortcutAction> FlattenActions(List<ShortcutAction> actions)
    {
        foreach (var action in actions)
        {
            if (action == null) continue;
            yield return action;
            foreach (var child in FlattenActions(action.Children ?? [])) yield return child;
            foreach (var child in FlattenActions(action.ElseChildren ?? [])) yield return child;
        }
    }

    private static void NormalizeAction(ShortcutAction action, HashSet<string> seenIds)
    {        if (string.IsNullOrWhiteSpace(action.Id) || !seenIds.Add(action.Id))
        {
            action.Id = ShortcutDefinition.NewId();
            seenIds.Add(action.Id);
        }

        action.Params ??= [];
        action.Children ??= [];
        action.ElseChildren ??= [];

        // Keep hand-edited sendHotkey values canonical; the executor and chip both parse the
        // text anyway, but a stable stored form avoids duplicate spellings in the document.
        if (action.Type == "sendHotkey" &&
            action.Params.TryGetValue("hotkey", out var hotkey) &&
            hotkey is { IsVariable: false, Text: { } text } &&
            HotkeyGesture.TryParse(text, requireModifier: false, out var gesture, out _))
        {
            hotkey.Text = gesture.Text;
        }
    }

    /// <summary>Starter shortcuts for a fresh install (also used as panel templates).</summary>
    private static ShortcutsDocument CreateDefault()
    {
        return new ShortcutsDocument
        {
            Version = ShortcutsDocument.CurrentVersion,
            Shortcuts = ShortcutTemplates.CreateStarterSet(),
        };
    }

    /// <summary>
    /// The version a file declares. A missing field is inferred from the shape so a v1/v2 file
    /// still gets its <c>.v&lt;n&gt;.bak</c> before it is rewritten.
    /// </summary>
    private static int ReadDeclaredVersion(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return 0;

            if (TryGetProperty(root, "version", out var version) && TryReadInt(version, out var declared))
                return declared;

            if (TryGetProperty(root, "actions", out var actions) &&
                actions.ValueKind == JsonValueKind.Array && actions.GetArrayLength() > 0)
                return 1;

            if (TryGetProperty(root, "graph", out var graph) && graph.ValueKind == JsonValueKind.Object)
                return 2;

            if (TryGetProperty(root, "shortcuts", out var shortcuts) && shortcuts.ValueKind == JsonValueKind.Array)
            {
                foreach (var shortcut in shortcuts.EnumerateArray())
                {
                    if (shortcut.ValueKind != JsonValueKind.Object) continue;

                    if (TryGetProperty(shortcut, "actions", out var legacy) &&
                        legacy.ValueKind == JsonValueKind.Array && legacy.GetArrayLength() > 0)
                        return 1;

                    if (TryGetProperty(shortcut, "graph", out var current) && current.ValueKind == JsonValueKind.Object)
                        return 2;
                }
            }

            // The store always writes a version. A missing one means a file from before the
            // field existed; treat it as v1 so it is backed up before the rewrite.
            return 1;
        }
        catch
        {
            return 0;
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryReadInt(JsonElement value, out int result)
    {
        result = 0;
        if (value.ValueKind == JsonValueKind.Number) return value.TryGetInt32(out result);
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out result);
    }

    private static void InitializeWatcher()
    {
        try
        {
            fileWatcher?.Dispose();
            fileWatcher = null;
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
            isReadOnly = false;
        }
    }
}
