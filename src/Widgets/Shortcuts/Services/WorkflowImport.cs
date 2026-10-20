using System.Text.Json;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>Outcome of importing one workflow document.</summary>
public sealed class WorkflowImportResult
{
    public WorkflowImportResult(
        IReadOnlyList<ShortcutDefinition> shortcuts,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings)
        : this(shortcuts, errors, warnings, [], [])
    {
    }

    public WorkflowImportResult(
        IReadOnlyList<ShortcutDefinition> shortcuts,
        IReadOnlyList<string> errors,
        IReadOnlyList<string> warnings,
        IReadOnlyList<WorkflowDiagnostic> diagnostics,
        IReadOnlyList<string> repairs)
    {
        Shortcuts = shortcuts;
        Errors = errors;
        Warnings = warnings;
        Diagnostics = diagnostics;
        Repairs = repairs;
    }

    public IReadOnlyList<ShortcutDefinition> Shortcuts { get; }

    public IReadOnlyList<string> Errors { get; }

    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Structured version of every error/warning, including the node and port.</summary>
    public IReadOnlyList<WorkflowDiagnostic> Diagnostics { get; }

    /// <summary>Repairs the importer applied (auto start, chaining, layout, port remap).</summary>
    public IReadOnlyList<string> Repairs { get; }

    public bool Success => Errors.Count == 0 && Shortcuts.Count > 0;

    public static WorkflowImportResult Fail(string error) => new([], [error], []);
}

/// <summary>
/// Parses the JSON an AI (or a hand edit) produces into runnable shortcuts.
/// <para>
/// The order is deliberate: parse the candidate graph, repair only the unambiguous shape
/// problems (a missing start, a linear document without links, a legacy <c>value</c> port),
/// then validate. A bad link is never pruned before it is reported — the whole point is that
/// the model (or the user) can see which node and port are wrong and fix them.
/// </para>
/// </summary>
public static class WorkflowImport
{
    /// <summary>
    /// Pulls the workflow JSON out of a pasted model reply. The AI-authoring spec allows the
    /// model to print a short safety notice before the JSON (and replies are usually wrapped in
    /// a fenced code block), so the importer must not require the paste to start with <c>{</c>.
    /// Order: verbatim text → first fenced <c>json</c> block → first <c>{</c> to last <c>}</c>.
    /// Unrecognized text is returned as-is so <see cref="Parse"/> reports its own clear error.
    /// </summary>
    public static string ExtractJson(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith('{')) return trimmed;

        // ```json ... ``` (also ``` with other casing / no language tag).
        var fenceStart = raw.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            var lineEnd = raw.IndexOf('\n', fenceStart);
            if (lineEnd < 0) break;

            var fenceEnd = raw.IndexOf("```", lineEnd, StringComparison.Ordinal);
            if (fenceEnd < 0) break;

            var candidate = raw[(lineEnd + 1)..fenceEnd].Trim();
            if (candidate.StartsWith('{') && candidate.EndsWith('}')) return candidate;

            fenceStart = raw.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal);
        }

        // Bare JSON with prose around it: the outermost braces of the reply.
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        return start >= 0 && end > start ? raw[start..(end + 1)] : raw;
    }

    /// <summary>
    /// Parse one workflow document (or a list of them).
    /// <paramref name="relayout"/> 为 true 时忽略文档自带的坐标，解析完成后总是用
    /// <see cref="WorkflowLayout.Arrange"/> 重新排版；默认 false 保持原行为
    /// （只有完全没有坐标信息时才自动排版）。
    /// </summary>
    public static WorkflowImportResult Parse(string json, bool relayout = false)
    {
        if (string.IsNullOrWhiteSpace(json)) return WorkflowImportResult.Fail(Loc.Get("Shortcuts_Import_Empty"));

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            return WorkflowImportResult.Fail(Loc.Format("Shortcuts_Import_BadJson", ex.Message));
        }

        using (document)
        {
            var root = document.RootElement;
            var errors = new List<string>();
            var warnings = new List<string>();
            var diagnostics = new List<WorkflowDiagnostic>();
            var repairs = new List<string>();
            var shortcuts = new List<ShortcutDefinition>();

            switch (root.ValueKind)
            {
                case JsonValueKind.Array:
                    foreach (var item in root.EnumerateArray())
                        ParseShortcut(item, shortcuts, errors, warnings, diagnostics, repairs, relayout);
                    break;

                case JsonValueKind.Object:
                    if (TryGetProperty(root, "shortcuts", out var list) && list.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in list.EnumerateArray())
                            ParseShortcut(item, shortcuts, errors, warnings, diagnostics, repairs, relayout);
                    }
                    else
                    {
                        ParseShortcut(root, shortcuts, errors, warnings, diagnostics, repairs, relayout);
                    }
                    break;

                default:
                    return WorkflowImportResult.Fail(Loc.Get("Shortcuts_Import_NoWorkflow"));
            }

            if (shortcuts.Count == 0 && errors.Count == 0)
            {
                errors.Add(Loc.Get("Shortcuts_Import_NoWorkflow"));
                diagnostics.Add(new WorkflowDiagnostic("NoWorkflow", DiagnosticSeverity.Error,
                    "Shortcuts_Import_NoWorkflow"));
            }

            return new WorkflowImportResult(shortcuts, errors, warnings, diagnostics, repairs);
        }
    }

    private static void ParseShortcut(
        JsonElement element,
        List<ShortcutDefinition> shortcuts,
        List<string> errors,
        List<string> warnings,
        List<WorkflowDiagnostic> diagnostics,
        List<string> repairs,
        bool relayout = false)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, diagnostics, "BadNode", "Shortcuts_Import_NoWorkflow");
            return;
        }

        // Accept the workflow wrapped in a "workflow" property (some models nest it).
        if (TryGetProperty(element, "workflow", out var nested) && nested.ValueKind == JsonValueKind.Object)
            element = nested;

        if (!TryGetProperty(element, "nodes", out var nodesElement) || nodesElement.ValueKind != JsonValueKind.Array)
        {
            AddError(errors, diagnostics, "NoNodes", "Shortcuts_Import_NoNodes");
            return;
        }

        var shortcutId = ReadString(element, "id");
        var shortcut = new ShortcutDefinition
        {
            Id = string.IsNullOrWhiteSpace(shortcutId) ? ShortcutDefinition.NewId() : shortcutId,
            Name = ReadString(element, "name") ?? Loc.Get("Shortcuts_Import_DefaultName"),
            Glyph = ReadString(element, "glyph") ?? "bolt",
            Color = ReadString(element, "color") ?? "#534AB7",
            ConfirmBeforeRun = ReadBool(element, "confirmBeforeRun") ?? ReadBool(element, "confirm") ?? false,
            Hotkey = ReadString(element, "hotkey"),
            Graph = new WorkflowGraph(),
        };

        if (!GlyphLibrary.PickerGlyphs.Contains(shortcut.Glyph)) shortcut.Glyph = "bolt";

        ParseInputs(element, shortcut);

        var graph = shortcut.Graph!;
        var byId = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        var index = 0;

        foreach (var nodeElement in nodesElement.EnumerateArray())
        {
            index++;
            if (nodeElement.ValueKind != JsonValueKind.Object)
            {
                AddError(errors, diagnostics, "BadNode", "Shortcuts_Import_BadNode", index.ToString());
                continue;
            }

            var type = ReadString(nodeElement, "type");
            if (string.IsNullOrWhiteSpace(type))
            {
                AddError(errors, diagnostics, "NodeNoType", "Shortcuts_Import_NodeNoType", index.ToString());
                continue;
            }

            var id = ReadString(nodeElement, "id");
            if (string.IsNullOrWhiteSpace(id))
            {
                id = $"n{index}";
                var suffix = 2;
                while (byId.ContainsKey(id)) id = $"n{index}_{suffix++}";
            }

            if (byId.ContainsKey(id))
            {
                AddError(errors, diagnostics, "DuplicateId", "Shortcuts_Import_DuplicateId", id);
                continue;
            }

            var node = new WorkflowNode
            {
                Id = id,
                Type = type,
                X = ReadDouble(nodeElement, "x") ?? 0,
                Y = ReadDouble(nodeElement, "y") ?? 0,
                Title = ReadString(nodeElement, "title"),
            };

            if (!double.IsFinite(node.X)) node.X = 0;
            if (!double.IsFinite(node.Y)) node.Y = 0;

            if (TryGetProperty(nodeElement, "params", out var paramsElement) &&
                paramsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var param in paramsElement.EnumerateObject())
                {
                    var value = ReadValue(param.Value);
                    if (value != null) node.Params[param.Name] = value;
                }
            }

            ParseCases(nodeElement, node);
            ParseOnError(nodeElement, node, warnings, diagnostics);

            graph.Nodes.Add(node);
            byId[id] = node;
        }

        if (graph.Nodes.Count == 0)
        {
            AddError(errors, diagnostics, "NoNodes", "Shortcuts_Import_NoNodes");
            return;
        }

        // Links (also accept "edges"/"connections" as the key).
        if (TryGetProperty(element, "links", out var linksElement) ||
            TryGetProperty(element, "edges", out linksElement) ||
            TryGetProperty(element, "connections", out linksElement))
        {
            if (linksElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var linkElement in linksElement.EnumerateArray())
                    ParseLink(graph, byId, shortcut, linkElement, errors, warnings, diagnostics, repairs);
            }
        }

        // The old bug: after adding a start node, Links.Count was no longer zero, so the
        // remaining actions were never chained. Decide from exec links only, before any repair.
        var execLinks = graph.Links.Count(l => l.Kind == LinkKinds.Exec);
        var hasControlFlow = graph.Nodes.Any(n => IsImportControlFlow(n.Type));
        var hasDataLinks = graph.Links.Any(l => l.Kind == LinkKinds.Data);
        var simple = execLinks == 0 && !hasControlFlow && !hasDataLinks;

        if (simple)
        {
            if (graph.Start == null)
            {
                WorkflowMigration.EnsureStart(graph);
                repairs.Add(Loc.Get("Shortcuts_Import_AddedStart"));
            }

            if (ChainInDocumentOrder(graph)) repairs.Add(Loc.Get("Shortcuts_Import_AutoChained"));
        }
        else
        {
            // Explicit wiring is required. Only the unambiguous "two starts" repair is applied;
            // a missing start is an error the validator reports.
            var starts = graph.Nodes.Where(n => n.Type == WorkflowNodeTypes.Start).ToList();
            if (starts.Count > 1)
            {
                for (var i = starts.Count - 1; i >= 1; i--) graph.Nodes.Remove(starts[i]);
                repairs.Add(Loc.Get("Shortcuts_Import_MultipleStart"));
            }
        }

        var placed = graph.Nodes.Any(n =>
            n.Type is not (WorkflowNodeTypes.Start or WorkflowNodeTypes.Note or "comment") &&
            (n.X != 0 || n.Y != 0));
        // 没有任何坐标 → 总是自动排版；调用方要求重新排版（relayout）→ 无论坐标如何都排。
        if (!placed || relayout)
        {
            WorkflowLayout.Arrange(graph, shortcut);
            repairs.Add(Loc.Get("Shortcuts_Import_AutoLayout"));
        }

        graph.Invalidate();

        // Validate after the repairs, never before: the report must describe the graph the
        // user is actually going to run.
        var report = WorkflowValidator.Validate(graph, shortcut, forRun: true);
        diagnostics.AddRange(report.Diagnostics);

        foreach (var diagnostic in report.Diagnostics)
        {
            switch (diagnostic.Severity)
            {
                case DiagnosticSeverity.Error:
                    errors.Add(diagnostic.Text);
                    break;
                case DiagnosticSeverity.Warning:
                    // A disconnected step is fatal whenever the document was expected to be
                    // wired. Notes are annotations and may legitimately float.
                    if (diagnostic.Code == "NotConnected" &&
                        !IsNoteNode(graph, diagnostic.NodeId))
                    {
                        errors.Add(diagnostic.Text);
                    }
                    else
                    {
                        warnings.Add(diagnostic.Text);
                    }
                    break;
            }
        }

        // Repairs are also surfaced as warnings so the existing import dialog (which shows
        // Warnings) keeps explaining the automatic start / chain / layout.
        warnings.AddRange(repairs);

        shortcuts.Add(shortcut);
    }

    // ---- nodes --------------------------------------------------------------------

    private static void ParseInputs(JsonElement element, ShortcutDefinition shortcut)
    {
        if (!TryGetProperty(element, "inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            return;

        shortcut.InputFields = [];
        foreach (var item in inputs.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var key = ReadString(item, "key");
            if (string.IsNullOrWhiteSpace(key)) key = $"input{shortcut.InputFields.Count + 1}";

            shortcut.InputFields.Add(new ShortcutInputField
            {
                Key = key,
                Name = ReadString(item, "name") ?? key,
                DataType = ParseDataType(ReadString(item, "type")),
                Required = ReadBool(item, "required") ?? false,
                DefaultValue = ReadScalar(item, "default") ?? ReadScalar(item, "defaultValue"),
            });
        }
    }

    private static void ParseCases(JsonElement nodeElement, WorkflowNode node)
    {
        if (node.Type != WorkflowNodeTypes.Switch) return;

        if (TryGetProperty(nodeElement, "cases", out var cases) && cases.ValueKind == JsonValueKind.Array)
        {
            node.Cases = [];
            var index = 0;
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var item in cases.EnumerateArray())
            {
                string? id = null;
                string value;

                if (item.ValueKind == JsonValueKind.Object)
                {
                    id = ReadString(item, "id");
                    value = ReadScalar(item, "value") ?? string.Empty;
                }
                else
                {
                    value = item.ValueKind == JsonValueKind.String ? item.GetString() ?? string.Empty : item.GetRawText();
                }

                if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                    id = $"case{index}";

                seen.Add(id);
                node.Cases.Add(new WorkflowCase { Id = id, Value = value });
                index++;
            }

            if (node.Cases.Count == 0) node.Cases = WorkflowCase.CreateDefaults();
            node.Params.Remove("cases");
            return;
        }

        // Accept the legacy pipe-joined "cases" parameter too.
        if (node.Params.TryGetValue("cases", out var legacy) && legacy?.Text is { Length: > 0 } text)
        {
            node.Cases = text
                .Split('|', StringSplitOptions.None)
                .Select((value, index) => new WorkflowCase { Id = $"case{index}", Value = value.Trim() })
                .ToList();
            node.Params.Remove("cases");
            return;
        }

        node.Cases = WorkflowCase.CreateDefaults();
    }

    private static void ParseOnError(
        JsonElement nodeElement,
        WorkflowNode node,
        List<string> warnings,
        List<WorkflowDiagnostic> diagnostics)
    {
        if (!TryGetProperty(nodeElement, "onError", out var value)) return;

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()?.Trim();
            if (string.Equals(text, "route", StringComparison.OrdinalIgnoreCase))
            {
                node.OnError = ErrorPolicy.Route;
                return;
            }

            if (string.Equals(text, "stop", StringComparison.OrdinalIgnoreCase))
            {
                node.OnError = ErrorPolicy.Stop;
                return;
            }
        }
        else if (value.ValueKind == JsonValueKind.True)
        {
            node.OnError = ErrorPolicy.Route;
            return;
        }
        else if (value.ValueKind == JsonValueKind.False)
        {
            node.OnError = ErrorPolicy.Stop;
            return;
        }

        warnings.Add(Loc.Format("Shortcuts_Import_BadOnError", node.Id));
        diagnostics.Add(new WorkflowDiagnostic("BadOnError", DiagnosticSeverity.Warning,
            "Shortcuts_Import_BadOnError", node.Id, Detail: node.Id));
    }

    // ---- links --------------------------------------------------------------------

    private static void ParseLink(
        WorkflowGraph graph,
        IReadOnlyDictionary<string, WorkflowNode> byId,
        ShortcutDefinition owner,
        JsonElement element,
        List<string> errors,
        List<string> warnings,
        List<WorkflowDiagnostic> diagnostics,
        List<string> repairs)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            AddError(errors, diagnostics, "BadLink", "Shortcuts_Import_BadLink");
            return;
        }

        var from = ReadString(element, "from") ?? ReadString(element, "source") ?? ReadString(element, "fromNode");
        var to = ReadString(element, "to") ?? ReadString(element, "target") ?? ReadString(element, "toNode");
        var fromPort = ReadString(element, "fromPort") ?? ReadString(element, "from_port") ??
                       ReadString(element, "sourcePort");
        var toPort = ReadString(element, "toPort") ?? ReadString(element, "to_port") ??
                     ReadString(element, "targetPort");

        fromPort = fromPort?.Trim();
        toPort = toPort?.Trim();

        if (string.IsNullOrWhiteSpace(from) || string.IsNullOrWhiteSpace(to))
        {
            AddError(errors, diagnostics, "BadLink", "Shortcuts_Import_BadLink");
            return;
        }

        byId.TryGetValue(from, out var fromNode);
        byId.TryGetValue(to, out var toNode);

        var kind = ReadString(element, "kind") ?? ReadString(element, "type");
        bool? explicitData = kind?.Trim().ToLowerInvariant() switch
        {
            "data" => true,
            "exec" or "execution" => false,
            _ => null,
        };

        if (kind != null && explicitData == null)
        {
            warnings.Add(Loc.Format("Shortcuts_Import_BadKind", kind));
            diagnostics.Add(new WorkflowDiagnostic("BadKind", DiagnosticSeverity.Warning,
                "Shortcuts_Import_BadKind", to, Detail: kind));
        }

        var isData = explicitData ?? InferDataLink(fromNode, toNode, fromPort, toPort, owner);

        if (isData)
        {
            if (fromNode != null)
            {
                if (string.IsNullOrEmpty(fromPort))
                {
                    var port = WorkflowNodeTypes.DefaultDataPort(fromNode, owner);
                    if (port != null)
                    {
                        fromPort = port;
                        repairs.Add(Loc.Format("Shortcuts_Import_RepairPort",
                            $"{fromNode.Type}.<default> → {port}"));
                    }
                }
                else if (string.Equals(fromPort, WorkflowNodeTypes.Value, StringComparison.Ordinal) &&
                         !WorkflowNodeTypes.HasOutput(fromNode, fromPort, owner))
                {
                    var port = WorkflowNodeTypes.DefaultDataPort(fromNode, owner);
                    if (port != null)
                    {
                        repairs.Add(Loc.Format("Shortcuts_Import_RepairPort",
                            $"{fromNode.Type}.value → {port}"));
                        fromPort = port;
                    }
                }
            }

            graph.Links.Add(new WorkflowLink
            {
                From = from,
                FromPort = string.IsNullOrEmpty(fromPort) ? WorkflowNodeTypes.Value : fromPort,
                To = to,
                ToPort = toPort ?? string.Empty,
                Kind = LinkKinds.Data,
            });

            // Project the link into the target parameter, matching the editor's binding model:
            // the visible source and the effective source must agree, and a data-only binding
            // must not look like an absent literal to the validator.
            if (fromNode != null && toNode != null &&
                toNode.Type != WorkflowNodeTypes.Note &&
                !string.IsNullOrEmpty(toPort) &&
                WorkflowNodeTypes.IsBindableParam(toNode, toPort) &&
                WorkflowNodeTypes.Outputs(fromNode, owner)
                    .Any(p => p.IsData && string.Equals(p.Id, fromPort, StringComparison.Ordinal)))
            {
                toNode.Params[toPort] = ShortcutValue.Variable(from, fromPort);
            }

            return;
        }

        graph.Links.Add(new WorkflowLink
        {
            From = from,
            FromPort = string.IsNullOrEmpty(fromPort) ? WorkflowNodeTypes.Out : fromPort,
            To = to,
            ToPort = string.IsNullOrEmpty(toPort) ? WorkflowNodeTypes.In : toPort,
            Kind = LinkKinds.Exec,
        });
    }

    /// <summary>
    /// A link is data when it leaves a data port or lands on a bindable parameter. An omitted
    /// <c>fromPort</c> with a parameter target is therefore data, not exec.
    /// </summary>
    private static bool InferDataLink(
        WorkflowNode? from,
        WorkflowNode? to,
        string? fromPort,
        string? toPort,
        ShortcutDefinition owner)
    {
        if (from != null && !string.IsNullOrEmpty(fromPort) &&
            WorkflowNodeTypes.Outputs(from, owner)
                .Any(p => p.IsData && string.Equals(p.Id, fromPort, StringComparison.Ordinal)))
            return true;

        if (to != null && !string.IsNullOrEmpty(toPort) &&
            !string.Equals(toPort, WorkflowNodeTypes.In, StringComparison.Ordinal) &&
            WorkflowNodeTypes.IsBindableParam(to, toPort))
            return true;

        return fromPort == null && toPort != null &&
               !string.Equals(toPort, WorkflowNodeTypes.In, StringComparison.Ordinal);
    }

    /// <summary>
    /// A document with no links at all is a linear list. Add the implicit "next" edges. The
    /// start node is already present (or was just added) and is not part of the sequence.
    /// </summary>
    private static bool ChainInDocumentOrder(WorkflowGraph graph)
    {
        var start = graph.Start;
        var ordered = graph.Nodes.Where(n => n.Type != WorkflowNodeTypes.Start).ToList();
        string? previousId = start?.Id;
        var added = false;

        foreach (var node in ordered)
        {
            if (!WorkflowNodeTypes.HasInput(node.Type)) continue;
            // Legacy comments are annotations; do not wire them into the exec chain.
            if (node.Type == "comment") continue;

            if (previousId != null)
            {
                graph.Links.Add(WorkflowLink.Exec(previousId, WorkflowNodeTypes.Out, node.Id));
                added = true;
            }

            previousId = node.Type == WorkflowNodeTypes.Return ? null : node.Id;
        }

        graph.Invalidate();
        return added;
    }

    // ---- helpers ------------------------------------------------------------------

    private static bool IsImportControlFlow(string type) => type is
        WorkflowNodeTypes.Branch or WorkflowNodeTypes.Switch or WorkflowNodeTypes.ForLoop
        or WorkflowNodeTypes.WhileLoop or WorkflowNodeTypes.ForEach or WorkflowNodeTypes.Break
        or WorkflowNodeTypes.Continue or WorkflowNodeTypes.End
        // Legacy containers are control flow even though the library no longer offers them.
        or "if" or "repeat";

    private static bool IsNoteNode(WorkflowGraph graph, string? nodeId) =>
        nodeId != null && graph.Find(nodeId)?.Type is WorkflowNodeTypes.Note or "comment";

    private static DataType ParseDataType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "number" => DataType.Number,
        "boolean" or "bool" => DataType.Boolean,
        "datetime" or "date" => DataType.DateTime,
        "path" => DataType.Path,
        "list" => DataType.List,
        "object" or "json" => DataType.Object,
        "null" => DataType.Null,
        "any" => DataType.Any,
        _ => DataType.Text,
    };

    private static void AddError(
        List<string> errors,
        List<WorkflowDiagnostic> diagnostics,
        string code,
        string messageKey,
        string? detail = null)
    {
        errors.Add(detail == null ? Loc.Get(messageKey) : Loc.Format(messageKey, detail));
        diagnostics.Add(new WorkflowDiagnostic(code, DiagnosticSeverity.Error, messageKey, Detail: detail));
    }

    // ---- json helpers -------------------------------------------------------------

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDouble(out var parsed) ? parsed : null,
            JsonValueKind.String => double.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    private static bool? ReadBool(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : null,
            _ => null,
        };
    }

    /// <summary>String form of a scalar (string/number/bool) used for input defaults.</summary>
    private static string? ReadScalar(JsonElement element, string name)
    {
        if (!TryGetProperty(element, name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static ShortcutValue? ReadValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => ShortcutValue.Literal(value.GetString()),
        JsonValueKind.Number => ShortcutValue.Literal(value.GetRawText()),
        JsonValueKind.True => ShortcutValue.Literal("true"),
        JsonValueKind.False => ShortcutValue.Literal("false"),
        JsonValueKind.Null => null,
        JsonValueKind.Object => ReadVariable(value) ?? ShortcutValue.Literal(value.GetRawText()),
        JsonValueKind.Array => ShortcutValue.Literal(value.GetRawText()),
        _ => null,
    };

    private static ShortcutValue? ReadVariable(JsonElement value)
    {
        var kind = ReadString(value, "kind");
        var source = ReadString(value, "sourceActionId") ?? ReadString(value, "source");
        if (string.Equals(kind, ValueKinds.Variable, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(source))
        {
            return ShortcutValue.Variable(source, ReadString(value, "sourcePort"));
        }

        var text = ReadString(value, "text");
        return text == null ? null : ShortcutValue.Literal(text);
    }
}
