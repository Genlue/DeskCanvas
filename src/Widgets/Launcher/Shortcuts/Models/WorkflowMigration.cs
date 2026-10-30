using System.Text.RegularExpressions;
using Shortcuts.Services;

namespace Shortcuts.Models;

/// <summary>
/// Outcome of migrating a whole document. <see cref="ReadOnly"/> is true when the file was
/// written by a newer build: the document is left untouched and the UI must not save it.
/// </summary>
public sealed record MigrationResult(
    int FromVersion,
    int ToVersion,
    bool ReadOnly,
    IReadOnlyList<WorkflowDiagnostic> Diagnostics);

/// <summary>
/// Version ladder v1 → v2 → v3.
/// <para>
/// v1 was a nested action list, v2 an execution graph whose ports were still untyped, v3 the
/// current typed-port contract. Every step is idempotent: running the migration on an already
/// upgraded document must not move a node, rename a port or delete a link. Where the old model
/// cannot be expressed exactly (a toggle, a removed action, a localized date pattern), the
/// migration keeps the node alive and records a diagnostic instead of guessing silently.
/// </para>
/// </summary>
public static class WorkflowMigration
{
    // Ports are laid out left→right (in on the left edge, outputs on the right), so a chain
    // advances one column per step and nested chains take a new row — the node-editor
    // convention. Stacking a chain vertically would make every link wrap around its cards.
    private const double RowStep = 124d;
    private const double ColumnStep = 300d;

    private static readonly Regex TemplateToken = new(
        @"\{\{\s*([A-Za-z0-9_\-\.\$]+)\s*\}\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Build a graph from a v1 action tree. Never returns null.</summary>
    public static WorkflowGraph FromActions(List<ShortcutAction>? actions)
    {
        var graph = BuildFromActions(actions, null);
        // A direct call (templates, EffectiveGraph) still gets the v3 repairs: a tree authored
        // against the old catalog may contain comments, sendKeys or a currentDate format.
        MigrateGraph(graph, null, null);
        return graph;
    }

    /// <summary>
    /// Upgrade a whole document in place. The caller decides whether to persist the result;
    /// a newer document is never touched.
    /// </summary>
    public static MigrationResult Migrate(ShortcutsDocument document)
    {
        var diagnostics = new List<WorkflowDiagnostic>();
        if (document == null)
            return new MigrationResult(ShortcutsDocument.CurrentVersion, ShortcutsDocument.CurrentVersion, false, diagnostics);

        // A newer file must be rejected before any shape inference or mutation: a future schema
        // could still contain an "actions" array and must not be mistaken for v1.
        if (document.Version > ShortcutsDocument.CurrentVersion)
        {
            diagnostics.Add(new("NewerVersion", DiagnosticSeverity.Warning,
                "Shortcuts_Migrate_NewerVersion", Detail: $"{document.Version} > {ShortcutsDocument.CurrentVersion}"));
            return new MigrationResult(document.Version, document.Version, true, diagnostics);
        }

        document.Shortcuts ??= [];

        var from = document.Version;
        if (from < 1) from = 1;

        // A v1 file written before the version field existed deserializes to the property
        // initializer (CurrentVersion). The legacy tree is the reliable tell.
        if (from > 1 && document.Shortcuts.Any(s =>
                s != null && s.Graph == null && s.Actions is { Count: > 0 }))
            from = 1;

        foreach (var shortcut in document.Shortcuts)
        {
            if (shortcut == null) continue;

            // v1 → v2: the tree becomes the graph. A document may already have both (a failed
            // hand edit); the graph wins and the stale tree is dropped.
            if (shortcut.Graph == null)
            {
                shortcut.Graph = BuildFromActions(shortcut.Actions, diagnostics);
                shortcut.Actions = [];
            }
            else if (shortcut.Actions is { Count: > 0 })
            {
                shortcut.Actions = [];
            }

            MigrateGraph(shortcut.Graph, diagnostics, shortcut);
        }

        document.Version = ShortcutsDocument.CurrentVersion;
        return new MigrationResult(from, document.Version, false, diagnostics);
    }

    private static WorkflowGraph BuildFromActions(
        List<ShortcutAction>? actions, List<WorkflowDiagnostic>? diagnostics)
    {
        var graph = new WorkflowGraph();
        var start = EnsureStart(graph);
        var referenced = CollectReferences(actions ?? []);
        var cursor = new Cursor { X = ColumnStep, Y = 0 };
        // The start node feeds the first action: the v1 list had no explicit entry point, so the
        // chain's head is whatever the first action was.
        EmitChain(graph, actions ?? [], cursor, [(start.Id, WorkflowNodeTypes.Out)], referenced, diagnostics);

        graph.Invalidate();
        return graph;
    }

    private sealed class Cursor
    {
        public double X;
        public double Y;
    }

    // ---- v1 tree → graph ----------------------------------------------------------

    /// <summary>
    /// Emit one sequential chain and return the (node, port) pairs that still need to be
    /// connected to whatever comes next. A chain that terminates (break/end/return/note)
    /// returns none.
    /// </summary>
    private static List<(string Node, string Port)> EmitChain(
        WorkflowGraph graph,
        List<ShortcutAction> actions,
        Cursor cursor,
        List<(string Node, string Port)>? initialPending,
        HashSet<string> referenced,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var pending = initialPending ?? [];

        foreach (var action in actions)
        {
            if (action == null || string.IsNullOrWhiteSpace(action.Type)) continue;

            // A legacy comment is an annotation. When something referenced it, it becomes a
            // text data node so the reference keeps resolving; otherwise it is parked as a note
            // and must not swallow the execution chain.
            if (action.Type == "comment")
            {
                var note = action.Params?.GetValueOrDefault("note") ?? ShortcutValue.Literal(string.Empty);
                if (referenced.Contains(action.Id))
                {
                    var node = CreateNode(action, "text", cursor);
                    node.Params = new Dictionary<string, ShortcutValue> { ["value"] = note };
                    graph.Nodes.Add(node);
                    foreach (var (fromNode, fromPort) in pending)
                        graph.Links.Add(WorkflowLink.Exec(fromNode, fromPort, node.Id));
                    pending = [(node.Id, WorkflowNodeTypes.Out)];
                }
                else
                {
                    var node = CreateNode(action, WorkflowNodeTypes.Note, cursor);
                    node.Params = new Dictionary<string, ShortcutValue> { ["note"] = note };
                    graph.Nodes.Add(node);
                }

                cursor.X += ColumnStep;
                continue;
            }

            // sendKeys was one action with two independent inputs. Splitting it keeps the
            // original order (text first, then the key sequence) and keeps the original id on
            // the first node so an incoming link still lands on the same step.
            if (action.Type == "sendKeys")
            {
                pending = EmitSendKeys(graph, action, cursor, pending, diagnostics);
                cursor.X += ColumnStep;
                continue;
            }

            // The old toggle has no direct replacement. Explicit mute is the intent of the
            // bundled templates and the only safe non-destructive mapping; the diagnostic says
            // that the meaning became explicit.
            if (action.Type == "muteToggle")
            {
                var node = CreateNode(action, "setMute", cursor);
                node.Params = new Dictionary<string, ShortcutValue>
                {
                    ["muted"] = ShortcutValue.Literal("true"),
                };
                graph.Nodes.Add(node);
                foreach (var (fromNode, fromPort) in pending)
                    graph.Links.Add(WorkflowLink.Exec(fromNode, fromPort, node.Id));
                pending = [(node.Id, WorkflowNodeTypes.Out)];
                diagnostics?.Add(new WorkflowDiagnostic("MuteToggle", DiagnosticSeverity.Warning,
                    "Shortcuts_Migrate_Mute", action.Id));
                cursor.X += ColumnStep;
                continue;
            }

            // A note is an annotation, not a step. Like an unreferenced comment it must not
            // swallow the execution chain (notes have no exec input at all).
            if (action.Type == WorkflowNodeTypes.Note)
            {
                graph.Nodes.Add(CreateNode(action, WorkflowNodeTypes.Note, cursor));
                cursor.X += ColumnStep;
                continue;
            }

            if (ActionCatalog.Get(action.Type) == null)
            {
                pending = EmitUnknown(graph, action, cursor, pending, referenced, diagnostics);
                cursor.X += ColumnStep;
                continue;
            }

            var workflowNode = CreateNode(action, action.Type, cursor);
            if (workflowNode.Type == WorkflowNodeTypes.Switch)
                ConvertSwitchCases(graph, workflowNode, diagnostics);

            graph.Nodes.Add(workflowNode);
            var continuation = Expand(graph, workflowNode, action, referenced, diagnostics);

            foreach (var (fromNode, fromPort) in pending)
                graph.Links.Add(WorkflowLink.Exec(fromNode, fromPort, workflowNode.Id));

            pending = continuation;
            cursor.X += ColumnStep;
        }

        return pending;
    }

    private static WorkflowNode CreateNode(ShortcutAction action, string type, Cursor cursor) => new()
    {
        Id = string.IsNullOrWhiteSpace(action.Id) ? ShortcutDefinition.NewId() : action.Id,
        Type = type,
        Params = action.Params ?? [],
        X = cursor.X,
        Y = cursor.Y,
    };

    private static List<(string Node, string Port)> EmitSendKeys(
        WorkflowGraph graph,
        ShortcutAction action,
        Cursor cursor,
        List<(string Node, string Port)> pending,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var parts = new List<(string Type, string Key, ShortcutValue Value)>();
        var text = action.Params?.GetValueOrDefault("text");
        var keys = action.Params?.GetValueOrDefault("keys");
        if (IsSet(text)) parts.Add(("typeText", "text", text!));
        if (IsSet(keys)) parts.Add(("sendKeySequence", "keys", keys!));

        if (parts.Count == 0)
        {
            var note = CreateNode(action, WorkflowNodeTypes.Note, cursor);
            note.Params = new Dictionary<string, ShortcutValue>
            {
                ["note"] = ShortcutValue.Literal(Loc.Format("Shortcuts_Error_UnknownAction", "sendKeys")),
            };
            graph.Nodes.Add(note);
            diagnostics?.Add(new WorkflowDiagnostic("SendKeysSplit", DiagnosticSeverity.Warning,
                "Shortcuts_Migrate_SendKeys", action.Id, Detail: action.Id));
            // The old action did nothing; it must not terminate the chain.
            return pending;
        }

        for (var i = 0; i < parts.Count; i++)
        {
            var node = CreateNode(action, parts[i].Type, cursor);
            if (i > 0) node.Id = ShortcutDefinition.NewId();
            node.Params = new Dictionary<string, ShortcutValue> { [parts[i].Key] = parts[i].Value };
            graph.Nodes.Add(node);

            foreach (var (fromNode, fromPort) in pending)
                graph.Links.Add(WorkflowLink.Exec(fromNode, fromPort, node.Id));

            pending = [(node.Id, WorkflowNodeTypes.Out)];
            if (i + 1 < parts.Count) cursor.X += ColumnStep;
        }

        diagnostics?.Add(new WorkflowDiagnostic("SendKeysSplit", DiagnosticSeverity.Warning,
            "Shortcuts_Migrate_SendKeys", action.Id,
            Detail: string.Join(" + ", parts.Select(p => p.Type))));
        return pending;
    }

    private static List<(string Node, string Port)> EmitUnknown(
        WorkflowGraph graph,
        ShortcutAction action,
        Cursor cursor,
        List<(string Node, string Port)> pending,
        HashSet<string> referenced,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var oldType = action.Type;
        var explanation = Loc.Format("Shortcuts_Error_UnknownAction", oldType);
        var asData = referenced.Contains(action.Id);

        var node = CreateNode(action, asData ? "text" : WorkflowNodeTypes.Note, cursor);
        node.Params = asData
            ? new Dictionary<string, ShortcutValue> { ["value"] = ShortcutValue.Literal(explanation) }
            : new Dictionary<string, ShortcutValue> { ["note"] = ShortcutValue.Literal(explanation) };
        graph.Nodes.Add(node);

        if (asData)
        {
            foreach (var (fromNode, fromPort) in pending)
                graph.Links.Add(WorkflowLink.Exec(fromNode, fromPort, node.Id));
        }

        diagnostics?.Add(new WorkflowDiagnostic("UnknownAction", DiagnosticSeverity.Warning,
            "Shortcuts_Error_UnknownAction", action.Id, Detail: oldType));

        // Children of an unknown container still have to run; keep them rather than dropping
        // them with the parent.
        if (action.Children is { Count: > 0 })
        {
            var childCursor = new Cursor { X = cursor.X + ColumnStep, Y = cursor.Y + RowStep };
            var seed = asData ? new List<(string Node, string Port)> { (node.Id, WorkflowNodeTypes.Out) } : pending;
            var tails = EmitChain(graph, action.Children, childCursor, seed, referenced, diagnostics);
            cursor.X = Math.Max(cursor.X, childCursor.X);
            return tails;
        }

        return asData ? [(node.Id, WorkflowNodeTypes.Out)] : pending;
    }

    private static List<(string Node, string Port)> Expand(
        WorkflowGraph graph,
        WorkflowNode node,
        ShortcutAction source,
        HashSet<string> referenced,
        List<WorkflowDiagnostic>? diagnostics)
    {
        switch (source.Type)
        {
            case "if":
            {
                // v1 if → branch: then/else bodies become the two output chains.
                node.Type = WorkflowNodeTypes.Branch;
                var tails = new List<(string, string)>();

                var thenCursor = new Cursor { X = node.X + ColumnStep, Y = node.Y + RowStep };
                tails.AddRange(EmitChain(graph, source.Children ?? [], thenCursor,
                    [(node.Id, WorkflowNodeTypes.TruePort)], referenced, diagnostics));

                var elseCursor = new Cursor { X = node.X + ColumnStep, Y = node.Y + RowStep * 2 };
                tails.AddRange(EmitChain(graph, source.ElseChildren ?? [], elseCursor,
                    [(node.Id, WorkflowNodeTypes.FalsePort)], referenced, diagnostics));
                return tails;
            }

            case "repeat":
            {
                node.Type = WorkflowNodeTypes.ForLoop;
                EmitChain(graph, source.Children ?? [], new Cursor
                {
                    X = node.X + ColumnStep,
                    Y = node.Y + RowStep,
                }, [(node.Id, WorkflowNodeTypes.BodyPort)], referenced, diagnostics);
                return [(node.Id, WorkflowNodeTypes.DonePort)];
            }

            case "branch":
                return
                [
                    (node.Id, WorkflowNodeTypes.TruePort),
                    (node.Id, WorkflowNodeTypes.FalsePort),
                ];

            case "switch":
            {
                // A v1 switch had no per-case child lists. Its children (if any) are the
                // default arm; the next sibling still runs after that arm.
                if (source.Children is { Count: > 0 })
                {
                    return EmitChain(graph, source.Children, new Cursor
                    {
                        X = node.X + ColumnStep,
                        Y = node.Y + RowStep,
                    }, [(node.Id, WorkflowNodeTypes.DefaultPort)], referenced, diagnostics);
                }

                return [(node.Id, WorkflowNodeTypes.DefaultPort)];
            }

            case "forLoop":
            case "whileLoop":
            case "forEach":
                if (source.Children is { Count: > 0 })
                {
                    EmitChain(graph, source.Children, new Cursor
                    {
                        X = node.X + ColumnStep,
                        Y = node.Y + RowStep,
                    }, [(node.Id, WorkflowNodeTypes.BodyPort)], referenced, diagnostics);
                }
                return [(node.Id, WorkflowNodeTypes.DonePort)];

            case "break":
            case "continue":
            case "end":
            case "return":
            case "note":
                return [];

            default:
                return [(node.Id, WorkflowNodeTypes.Out)];
        }
    }

    private static bool IsSet(ShortcutValue? value) =>
        value != null && (value.IsVariable || !string.IsNullOrWhiteSpace(value.Text));

    private static HashSet<string> CollectReferences(List<ShortcutAction> actions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in FlattenSafe(actions))
        {
            if (action.Params == null) continue;
            foreach (var value in action.Params.Values)
            {
                if (value == null) continue;
                if (value.IsVariable && !string.IsNullOrEmpty(value.SourceActionId))
                    set.Add(value.SourceActionId);
                else if (value.Text != null)
                {
                    foreach (Match match in TemplateToken.Matches(value.Text))
                        set.Add(match.Groups[1].Value);
                }
            }
        }

        return set;
    }

    /// <summary>Null-safe tree walk (a hand-edited document may contain a null entry).</summary>
    private static IEnumerable<ShortcutAction> FlattenSafe(List<ShortcutAction> actions)
    {
        foreach (var action in actions)
        {
            if (action == null) continue;
            yield return action;
            foreach (var child in FlattenSafe(action.Children ?? [])) yield return child;
            foreach (var child in FlattenSafe(action.ElseChildren ?? [])) yield return child;
        }
    }

    // ---- v2 graph → v3 graph ------------------------------------------------------

    private static void MigrateGraph(
        WorkflowGraph graph,
        List<WorkflowDiagnostic>? diagnostics,
        ShortcutDefinition? owner)
    {
        graph.Nodes ??= [];
        graph.Links ??= [];

        // Null entries are corrupt JSON, not nodes/links; dropping them keeps the index and
        // the validator from throwing on a hand-edited file.
        graph.Nodes.RemoveAll(n => n == null);
        graph.Links.RemoveAll(l => l == null);

        // Initialize every parameter bag before any repair reads another node's parameters.
        foreach (var node in graph.Nodes)
        {
            if (node != null) node.Params ??= [];
        }

        // Snapshot: currentDate/sendKeys repairs may insert nodes.
        foreach (var node in graph.Nodes.ToList())
        {
            if (node == null) continue;
            node.Params ??= [];

            switch (node.Type)
            {
                case "if":
                    node.Type = WorkflowNodeTypes.Branch;
                    break;
                case "repeat":
                    node.Type = WorkflowNodeTypes.ForLoop;
                    break;
                case "comment":
                    ConvertCommentNode(graph, node);
                    break;
                case "sendKeys":
                    ConvertSendKeysNode(graph, node, diagnostics);
                    break;
                case "muteToggle":
                    ConvertMuteNode(node, diagnostics);
                    break;
                default:
                    if (ActionCatalog.Get(node.Type) == null)
                        ConvertUnknownNode(graph, node, diagnostics);
                    break;
            }

            if (node.Type == WorkflowNodeTypes.Switch)
                ConvertSwitchCases(graph, node, diagnostics);

            if (node.Type == "currentDate")
                ConvertCurrentDateFormat(graph, node, diagnostics);
        }

        RepairLinks(graph, diagnostics, owner);
        RepairReferences(graph, diagnostics, owner);

        // A graph with no entry point is not runnable. Add one and wire it to the first node
        // that has no incoming exec link; the diagnostic makes the repair visible.
        if (graph.Start == null)
        {
            var start = EnsureStart(graph);
            var entry = graph.Nodes.FirstOrDefault(n =>
                n.Id != start.Id &&
                WorkflowNodeTypes.HasInput(n.Type) &&
                !graph.Links.Any(l => l.Kind == LinkKinds.Exec && l.To == n.Id));
            if (entry != null)
                graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, entry.Id));

            diagnostics?.Add(new WorkflowDiagnostic("AddedStart", DiagnosticSeverity.Warning,
                "Shortcuts_Import_AddedStart", start.Id));
        }

        graph.Invalidate();
    }

    private static void ConvertCommentNode(WorkflowGraph graph, WorkflowNode node)
    {
        var note = node.Params.GetValueOrDefault("note") ?? ShortcutValue.Literal(string.Empty);
        var referenced = IsReferenced(graph, node.Id);
        var hasLinks = graph.Links.Any(l => l.From == node.Id || l.To == node.Id);

        if (referenced || hasLinks)
        {
            // Keep the node in the flow so an incoming/outgoing exec link still works; a text
            // node has both an exec exit and a data output.
            node.Type = "text";
            node.Params = new Dictionary<string, ShortcutValue> { ["value"] = note };

            // A data link that fed the old "note" parameter now feeds the text value.
            foreach (var link in graph.Links.Where(l => l.IsData && l.To == node.Id &&
                                                         string.Equals(l.ToPort, "note", StringComparison.Ordinal)))
                link.ToPort = "value";

            // A comment had no typed output; if a data link started at it, the text value is
            // the closest equivalent.
            foreach (var link in graph.Links.Where(l => l.IsData && l.From == node.Id))
                link.FromPort = "value";
        }
        else
        {
            node.Type = WorkflowNodeTypes.Note;
            node.Params = new Dictionary<string, ShortcutValue> { ["note"] = note };
        }
    }

    private static void ConvertSendKeysNode(
        WorkflowGraph graph,
        WorkflowNode node,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var parts = new List<(string Type, string Key, ShortcutValue Value)>();
        var text = node.Params.GetValueOrDefault("text");
        var keys = node.Params.GetValueOrDefault("keys");
        var hasText = IsSet(text) || graph.IncomingData(node.Id, "text") != null;
        var hasKeys = IsSet(keys) || graph.IncomingData(node.Id, "keys") != null;
        if (hasText) parts.Add(("typeText", "text", text ?? ShortcutValue.Literal(string.Empty)));
        if (hasKeys) parts.Add(("sendKeySequence", "keys", keys ?? ShortcutValue.Literal(string.Empty)));

        if (parts.Count == 0)
        {
            var old = node.Type;
            node.Type = "text";
            node.Params = new Dictionary<string, ShortcutValue>
            {
                ["value"] = ShortcutValue.Literal(Loc.Format("Shortcuts_Error_UnknownAction", old)),
            };
            diagnostics?.Add(new WorkflowDiagnostic("SendKeysSplit", DiagnosticSeverity.Warning,
                "Shortcuts_Migrate_SendKeys", node.Id, Detail: old));
            foreach (var link in graph.Links.Where(l => l.IsData && l.From == node.Id))
                link.FromPort = "value";
            return;
        }

        node.Type = parts[0].Type;
        node.Params = new Dictionary<string, ShortcutValue> { [parts[0].Key] = parts[0].Value };

        if (parts.Count > 1)
        {
            var second = new WorkflowNode
            {
                Type = parts[1].Type,
                X = node.X + ColumnStep,
                Y = node.Y,
                Params = new Dictionary<string, ShortcutValue> { [parts[1].Key] = parts[1].Value },
                OnError = node.OnError,
            };
            graph.Nodes.Add(second);

            // The old action had one error policy. Both halves keep it, and the fallback is
            // wired to the second half as well.
            foreach (var link in graph.Links.Where(l =>
                         l.Kind == LinkKinds.Exec && l.From == node.Id &&
                         string.Equals(l.FromPort, WorkflowNodeTypes.ErrorPort, StringComparison.Ordinal)).ToList())
                graph.Links.Add(WorkflowLink.Exec(second.Id, WorkflowNodeTypes.ErrorPort, link.To));

            // Insert the second step directly after the first: everything that followed the
            // old node now follows the key sequence.
            foreach (var link in graph.Links.Where(l =>
                         l.Kind == LinkKinds.Exec && l.From == node.Id &&
                         string.Equals(l.FromPort, WorkflowNodeTypes.Out, StringComparison.Ordinal)))
            {
                link.From = second.Id;
                link.FromPort = WorkflowNodeTypes.Out;
            }

            graph.Links.Add(WorkflowLink.Exec(node.Id, WorkflowNodeTypes.Out, second.Id));
        }

        diagnostics?.Add(new WorkflowDiagnostic("SendKeysSplit", DiagnosticSeverity.Warning,
            "Shortcuts_Migrate_SendKeys", node.Id,
            Detail: string.Join(" + ", parts.Select(p => p.Type))));
    }

    private static void ConvertMuteNode(WorkflowNode node, List<WorkflowDiagnostic>? diagnostics)
    {
        node.Type = "setMute";
        node.Params = new Dictionary<string, ShortcutValue>
        {
            ["muted"] = ShortcutValue.Literal("true"),
        };
        diagnostics?.Add(new WorkflowDiagnostic("MuteToggle", DiagnosticSeverity.Warning,
            "Shortcuts_Migrate_Mute", node.Id));
    }

    private static void ConvertUnknownNode(
        WorkflowGraph graph,
        WorkflowNode node,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var oldType = node.Type;
        var explanation = Loc.Format("Shortcuts_Error_UnknownAction", oldType);
        var keepInFlow = IsReferenced(graph, node.Id) ||
                         graph.Links.Any(l => l.From == node.Id || l.To == node.Id);

        node.Type = keepInFlow ? "text" : WorkflowNodeTypes.Note;
        node.Params = keepInFlow
            ? new Dictionary<string, ShortcutValue> { ["value"] = ShortcutValue.Literal(explanation) }
            : new Dictionary<string, ShortcutValue> { ["note"] = ShortcutValue.Literal(explanation) };

        if (keepInFlow)
        {
            // The old parameter names no longer exist on a text node; feed the value slot.
            foreach (var link in graph.Links.Where(l => l.IsData && l.To == node.Id))
                link.ToPort = "value";

            // Any old output becomes the single typed value the replacement can offer.
            foreach (var link in graph.Links.Where(l => l.IsData && l.From == node.Id))
                link.FromPort = "value";

            // Keep the replacement reachable through its single exec exit.
            foreach (var link in graph.Links.Where(l => l.Kind == LinkKinds.Exec && l.From == node.Id))
                link.FromPort = WorkflowNodeTypes.Out;
        }

        diagnostics?.Add(new WorkflowDiagnostic("UnknownAction", DiagnosticSeverity.Warning,
            "Shortcuts_Error_UnknownAction", node.Id, Detail: oldType));
    }

    /// <summary>
    /// Old switch documents stored cases as one pipe-joined string and addressed the arms as
    /// <c>case0…case3</c>. Those ids are kept, so an existing link keeps pointing at the same
    /// value; a missing arm becomes an empty case instead of a deleted link.
    /// </summary>
    private static void ConvertSwitchCases(
        WorkflowGraph graph,
        WorkflowNode node,
        List<WorkflowDiagnostic>? diagnostics)
    {
        var hasCases = node.Cases is { Count: > 0 };

        if (!hasCases && node.Params.TryGetValue("cases", out var casesValue))
        {
            var values = (casesValue?.Text ?? string.Empty)
                .Split('|', StringSplitOptions.None)
                .Select(v => v.Trim())
                .ToList();

            if (values.Count == 0) values.Add(string.Empty);
            node.Cases = values
                .Select((value, index) => new WorkflowCase { Id = $"case{index}", Value = value })
                .ToList();
            node.Params.Remove("cases");
            diagnostics?.Add(new WorkflowDiagnostic("SwitchCases", DiagnosticSeverity.Info,
                "Shortcuts_Migrate_Case", node.Id));
        }
        else if (!hasCases)
        {
            node.Cases = WorkflowCase.CreateDefaults();
        }

        // Preserve a link that points at an arm the old document no longer listed.
        var highest = -1;
        foreach (var link in graph.Links.Where(l =>
                     l.From == node.Id && l.FromPort.StartsWith("case", StringComparison.Ordinal)))
        {
            if (int.TryParse(link.FromPort[4..], out var index) && index > highest)
                highest = index;
        }

        while (node.Cases!.Count <= highest && node.Cases.Count < 64)
        {
            var index = node.Cases.Count;
            node.Cases.Add(new WorkflowCase { Id = $"case{index}", Value = string.Empty });
            diagnostics?.Add(new WorkflowDiagnostic("RemovedOutput", DiagnosticSeverity.Warning,
                "Shortcuts_Migrate_RemovedOutput", node.Id, PortId: $"case{index}", Detail: $"case{index}"));
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < node.Cases!.Count; i++)
        {
            var item = node.Cases[i];
            if (item == null)
            {
                item = new WorkflowCase { Id = $"case{i}" };
                node.Cases[i] = item;
            }

            item.Value ??= string.Empty;
            if (!string.IsNullOrWhiteSpace(item.Id) && seen.Add(item.Id)) continue;

            var oldId = item.Id;
            item.Id = $"case{i}";
            seen.Add(item.Id);

            if (string.IsNullOrEmpty(oldId)) continue;
            foreach (var link in graph.Links.Where(l =>
                         l.From == node.Id && string.Equals(l.FromPort, oldId, StringComparison.Ordinal)))
                link.FromPort = item.Id;

            foreach (var other in graph.Nodes)
            {
                foreach (var value in other.Params.Values)
                {
                    if (value is { IsVariable: true } &&
                        string.Equals(value.SourceActionId, node.Id, StringComparison.Ordinal) &&
                        string.Equals(value.SourcePort, oldId, StringComparison.Ordinal))
                        value.SourcePort = item.Id;
                }
            }
        }
    }

    /// <summary>
    /// v1 currentDate produced a formatted string through a <c>format</c> parameter. v3 keeps
    /// the date typed and formats in a separate node, so the old pattern is moved (with the
    /// references that pointed at the formatted result) rather than dropped.
    /// </summary>
    private static void ConvertCurrentDateFormat(
        WorkflowGraph graph,
        WorkflowNode node,
        List<WorkflowDiagnostic>? diagnostics)
    {
        if (!node.Params.TryGetValue("format", out var format) || format == null) return;
        node.Params.Remove("format");

        if (!format.IsVariable && string.IsNullOrWhiteSpace(format.Text))
        {
            diagnostics?.Add(new WorkflowDiagnostic("DateFormat", DiagnosticSeverity.Warning,
                "Shortcuts_Migrate_DateFormat", node.Id, "format"));
            return;
        }

        var formatter = new WorkflowNode
        {
            Type = "formatDate",
            X = node.X + ColumnStep,
            Y = node.Y,
            Params = new Dictionary<string, ShortcutValue> { ["format"] = format },
            OnError = node.OnError,
        };
        graph.Nodes.Add(formatter);

        // The old action's error fallback applies to the formatter too.
        foreach (var link in graph.Links.Where(l =>
                     l.Kind == LinkKinds.Exec && l.From == node.Id &&
                     string.Equals(l.FromPort, WorkflowNodeTypes.ErrorPort, StringComparison.Ordinal)).ToList())
            graph.Links.Add(WorkflowLink.Exec(formatter.Id, WorkflowNodeTypes.ErrorPort, link.To));

        // Everything that used to read the formatted text now reads the formatter.
        foreach (var link in graph.Links.Where(l =>
                     l.IsData && l.From == node.Id &&
                     (string.IsNullOrEmpty(l.FromPort) ||
                      l.FromPort is WorkflowNodeTypes.Value or "date" or "text")))
        {
            link.From = formatter.Id;
            link.FromPort = "text";
        }

        foreach (var other in graph.Nodes)
        {
            foreach (var value in other.Params.Values)
            {
                if (value is not { IsVariable: true }) continue;
                if (!string.Equals(value.SourceActionId, node.Id, StringComparison.Ordinal)) continue;
                if (value.SourcePort != null && value.SourcePort is not (WorkflowNodeTypes.Value or "date" or "text"))
                    continue;

                value.SourceActionId = formatter.Id;
                value.SourcePort = "text";
            }
        }

        foreach (var link in graph.Links.Where(l =>
                     l.Kind == LinkKinds.Exec && l.From == node.Id &&
                     string.Equals(l.FromPort, WorkflowNodeTypes.Out, StringComparison.Ordinal)))
        {
            link.From = formatter.Id;
            link.FromPort = WorkflowNodeTypes.Out;
        }

        graph.Links.Add(WorkflowLink.Data(node.Id, "date", formatter.Id, "date"));
        graph.Links.Add(WorkflowLink.Exec(node.Id, WorkflowNodeTypes.Out, formatter.Id));

        diagnostics?.Add(new WorkflowDiagnostic("DateFormat", DiagnosticSeverity.Warning,
            "Shortcuts_Migrate_DateFormat", node.Id, "format", Detail: format.Text));
    }

    private static void RepairLinks(
        WorkflowGraph graph,
        List<WorkflowDiagnostic>? diagnostics,
        ShortcutDefinition? owner)
    {
        foreach (var link in graph.Links.ToList())
        {
            if (link == null) continue;

            var from = graph.Find(link.From);
            var to = graph.Find(link.To);

            // Never delete a bad link: the validator (and the canvas) must be able to point at
            // the exact node/port that is wrong.
            if (from == null || to == null)
            {
                diagnostics?.Add(new WorkflowDiagnostic("LinkMissingNode", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_LinkMissingNode", from == null ? link.To : link.From,
                    Detail: $"{link.From} → {link.To}"));
                continue;
            }

            if (!link.IsData)
            {
                // An old exec link that actually carried a value (the port is a data port and
                // the target is a parameter) is reclassified instead of being rejected.
                var isDataPort = WorkflowNodeTypes.Outputs(from, owner)
                    .Any(p => p.IsData && string.Equals(p.Id, link.FromPort, StringComparison.Ordinal));
                if (isDataPort && WorkflowNodeTypes.IsBindableParam(to, link.ToPort))
                {
                    link.Kind = LinkKinds.Data;
                    diagnostics?.Add(new WorkflowDiagnostic("LinkKind", DiagnosticSeverity.Info,
                        "Shortcuts_Migrate_LinkKind", to.Id, link.ToPort, link.FromPort));
                    continue;
                }

                if (string.IsNullOrEmpty(link.FromPort)) link.FromPort = WorkflowNodeTypes.Out;
                if (string.IsNullOrEmpty(link.ToPort)) link.ToPort = WorkflowNodeTypes.In;

                if (!WorkflowNodeTypes.HasOutput(from, link.FromPort, owner))
                {
                    diagnostics?.Add(new WorkflowDiagnostic("BadPort", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_BadPort", from.Id, PortId: link.FromPort,
                        Detail: $"{from.Type}.{link.FromPort}"));
                }
                else if (WorkflowNodeTypes.Outputs(from, owner)
                         .Any(p => p.IsData && string.Equals(p.Id, link.FromPort, StringComparison.Ordinal)))
                {
                    // A data port wired as execution would silently skip the value.
                    diagnostics?.Add(new WorkflowDiagnostic("DataPortAsExec", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_DataPortAsExec", from.Id, PortId: link.FromPort,
                        Detail: link.FromPort));
                }

                if (!WorkflowNodeTypes.HasInput(to.Type))
                {
                    diagnostics?.Add(new WorkflowDiagnostic("NoExecInput", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_NoExecInput", to.Id, Detail: to.Type));
                }

                if (!string.Equals(link.ToPort, WorkflowNodeTypes.In, StringComparison.Ordinal))
                {
                    diagnostics?.Add(new WorkflowDiagnostic("ExecIntoParam", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_ExecIntoParam", to.Id, link.ToPort, link.FromPort,
                        $"{to.Type}.{link.ToPort}"));
                }

                continue;
            }

            if (string.IsNullOrEmpty(link.FromPort))
            {
                var port = WorkflowNodeTypes.DefaultDataPort(from, owner);
                if (port != null)
                {
                    link.FromPort = port;
                    diagnostics?.Add(new WorkflowDiagnostic("LinkPort", DiagnosticSeverity.Info,
                        "Shortcuts_Migrate_LinkPort", from.Id, PortId: port, Detail: port));
                }
            }

            // The old model had a universal "value" port; most actions no longer do.
            if (string.Equals(link.FromPort, WorkflowNodeTypes.Value, StringComparison.Ordinal) &&
                !WorkflowNodeTypes.HasOutput(from, link.FromPort, owner))
            {
                var port = WorkflowNodeTypes.DefaultDataPort(from, owner);
                if (port != null)
                {
                    link.FromPort = port;
                    diagnostics?.Add(new WorkflowDiagnostic("LinkPort", DiagnosticSeverity.Info,
                        "Shortcuts_Migrate_LinkPort", from.Id, PortId: port,
                        Detail: $"{WorkflowNodeTypes.Value} → {port}"));
                }
                else
                {
                    diagnostics?.Add(new WorkflowDiagnostic("RemovedOutput", DiagnosticSeverity.Warning,
                        "Shortcuts_Migrate_RemovedOutput", from.Id, PortId: link.FromPort,
                        Detail: link.FromPort));
                }
            }

            if (!WorkflowNodeTypes.HasOutput(from, link.FromPort, owner))
            {
                diagnostics?.Add(new WorkflowDiagnostic("BadPort", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_BadPort", from.Id, PortId: link.FromPort,
                    Detail: $"{from.Type}.{link.FromPort}"));
            }
            else if (WorkflowNodeTypes.Outputs(from, owner)
                     .Any(p => !p.IsData && string.Equals(p.Id, link.FromPort, StringComparison.Ordinal)))
            {
                diagnostics?.Add(new WorkflowDiagnostic("ExecPortAsData", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_ExecPortAsData", from.Id, PortId: link.FromPort,
                    Detail: link.FromPort));
            }

            var param = ActionCatalog.Get(to.Type)?.Param(link.ToPort);
            if (param == null)
            {
                diagnostics?.Add(new WorkflowDiagnostic("BadParam", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_BadParam", to.Id, link.ToPort, Detail: $"{to.Type}.{link.ToPort}"));
            }
            else if (!param.Bindable)
            {
                diagnostics?.Add(new WorkflowDiagnostic("NotBindable", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_NotBindable", to.Id, link.ToPort, Detail: param.Key));
            }
            else if (to.Type != WorkflowNodeTypes.Note &&
                     WorkflowNodeTypes.HasOutput(from, link.FromPort, owner) &&
                     WorkflowNodeTypes.Outputs(from, owner)
                         .Any(p => p.IsData && string.Equals(p.Id, link.FromPort, StringComparison.Ordinal)))
            {
                // A data link is the projected form of a binding. Keeping the parameter in sync
                // means the editor shows the real source and the validator does not mistake the
                // absent literal for an empty required value.
                to.Params[link.ToPort] = ShortcutValue.Variable(from.Id, link.FromPort);
            }
        }
    }

    private static void RepairReferences(
        WorkflowGraph graph,
        List<WorkflowDiagnostic>? diagnostics,
        ShortcutDefinition? owner)
    {
        foreach (var node in graph.Nodes)
        {
            foreach (var (key, value) in node.Params)
            {
                if (value == null) continue;

                if (value.IsVariable)
                {
                    var source = value.SourceActionId!;
                    if (string.Equals(source, ValueKinds.LastResult, StringComparison.Ordinal))
                    {
                        diagnostics?.Add(new WorkflowDiagnostic("LastResult", DiagnosticSeverity.Warning,
                            "Shortcuts_Migrate_LastResult", node.Id, key));
                        continue;
                    }

                    var sourceNode = graph.Find(source);
                    if (sourceNode == null) continue; // Named variable or already reported.

                    if (string.IsNullOrEmpty(value.SourcePort))
                    {
                        var port = WorkflowNodeTypes.DefaultDataPort(sourceNode, owner);
                        if (port == null)
                        {
                            // The source no longer exposes any data output (for example a
                            // control-flow node). Keep the reference for the editor to show.
                            diagnostics?.Add(new WorkflowDiagnostic("RemovedOutput", DiagnosticSeverity.Warning,
                                "Shortcuts_Migrate_RemovedOutput", node.Id, key, Detail: source));
                            continue;
                        }

                        value.SourcePort = port;
                        if (sourceNode.Type == WorkflowNodeTypes.SetVar)
                        {
                            diagnostics?.Add(new WorkflowDiagnostic("SetVarOutput", DiagnosticSeverity.Info,
                                "Shortcuts_Migrate_SetVarOutput", node.Id, key, port, source));
                        }
                    }
                    else if (string.Equals(value.SourcePort, WorkflowNodeTypes.Value, StringComparison.Ordinal) &&
                             !WorkflowNodeTypes.HasOutput(sourceNode, value.SourcePort, owner))
                    {
                        var port = WorkflowNodeTypes.DefaultDataPort(sourceNode, owner);
                        if (port != null)
                        {
                            value.SourcePort = port;
                            diagnostics?.Add(new WorkflowDiagnostic("LinkPort", DiagnosticSeverity.Info,
                                "Shortcuts_Migrate_LinkPort", sourceNode.Id, PortId: port,
                                Detail: $"{WorkflowNodeTypes.Value} → {port}"));
                        }
                        else
                        {
                            diagnostics?.Add(new WorkflowDiagnostic("RemovedOutput", DiagnosticSeverity.Warning,
                                "Shortcuts_Migrate_RemovedOutput", node.Id, key, Detail: source));
                        }
                    }
                    else if (!WorkflowNodeTypes.HasOutput(sourceNode, value.SourcePort, owner))
                    {
                        diagnostics?.Add(new WorkflowDiagnostic("BadPort", DiagnosticSeverity.Error,
                            "Shortcuts_Diag_BadPort", source, PortId: value.SourcePort,
                            Detail: value.SourcePort));
                    }
                    else if (WorkflowNodeTypes.Outputs(sourceNode, owner)
                             .Any(p => !p.IsData && string.Equals(p.Id, value.SourcePort, StringComparison.Ordinal)))
                    {
                        diagnostics?.Add(new WorkflowDiagnostic("ExecPortAsData", DiagnosticSeverity.Error,
                            "Shortcuts_Diag_ExecPortAsData", source, PortId: value.SourcePort,
                            Detail: value.SourcePort));
                    }

                    continue;
                }

                if (value.Text != null && ContainsLastToken(value.Text))
                {
                    diagnostics?.Add(new WorkflowDiagnostic("LastResult", DiagnosticSeverity.Warning,
                        "Shortcuts_Migrate_LastResult", node.Id, key));
                }
            }
        }
    }

    private static bool ContainsLastToken(string text) =>
        TemplateToken.Matches(text).Any(m =>
            m.Groups[1].Value is "last" or "$last");

    private static bool IsReferenced(WorkflowGraph graph, string nodeId)
    {
        foreach (var node in graph.Nodes)
        {
            foreach (var value in node.Params.Values)
            {
                if (value == null) continue;
                if (value.IsVariable &&
                    string.Equals(value.SourceActionId, nodeId, StringComparison.Ordinal))
                    return true;

                if (!value.IsVariable && value.Text != null &&
                    TemplateToken.Matches(value.Text).Any(m =>
                        string.Equals(m.Groups[1].Value, nodeId, StringComparison.Ordinal)))
                    return true;
            }
        }

        return graph.Links.Any(l => l.IsData && string.Equals(l.From, nodeId, StringComparison.Ordinal));
    }

    // ---- layout -------------------------------------------------------------------

    /// <summary>Add a start node when the graph has none and return the existing/created one.</summary>
    public static WorkflowNode EnsureStart(WorkflowGraph graph)
    {
        graph.Nodes ??= [];
        var start = graph.Start;
        if (start != null) return start;

        var finite = graph.Nodes.Where(n => n != null && double.IsFinite(n.X) && double.IsFinite(n.Y)).ToList();
        start = new WorkflowNode
        {
            Type = WorkflowNodeTypes.Start,
            X = finite.Count > 0 ? finite.Min(n => n.X) : 0,
            Y = finite.Count > 0 ? finite.Min(n => n.Y) - 96 : 0,
        };
        graph.Nodes.Insert(0, start);
        graph.Invalidate();
        return start;
    }

    /// <summary>
    /// Layered auto-layout used by import and by the canvas "整理布局" command.
    /// 委托给 <see cref="WorkflowLayout.Arrange"/>（Sugiyama-lite 分层引擎：拓扑去环 →
    /// 最长路径分层 → 子树连续打包 → 重心微调），保证确定性、无重叠且依赖方向正确。
    /// </summary>
    public static void AutoLayout(WorkflowGraph graph)
    {
        WorkflowLayout.Arrange(graph, null);
    }
}
