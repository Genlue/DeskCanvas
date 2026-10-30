using Shortcuts.Models;

namespace Shortcuts.Services;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// One problem found in a graph. Every diagnostic points at the node / port / parameter that
/// caused it, because "the run failed" without a location is exactly the silent failure the
/// validator exists to remove.
/// </summary>
/// <param name="Code">Stable machine code (used by tests and by the UI to group problems).</param>
/// <param name="Severity">Errors block a run; warnings are advisory.</param>
/// <param name="MessageKey">Locale key of the message.</param>
/// <param name="NodeId">Node the problem belongs to, when it belongs to one.</param>
/// <param name="ParamKey">Parameter the problem belongs to, when it belongs to one.</param>
/// <param name="PortId">Port the problem belongs to, when it belongs to one.</param>
/// <param name="Detail">Pre-formatted detail appended to the localized message.</param>
public sealed record WorkflowDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string MessageKey,
    string? NodeId = null,
    string? ParamKey = null,
    string? PortId = null,
    string? Detail = null)
{
    public string Text => Detail == null
        ? Loc.Get(MessageKey)
        : Loc.Format(MessageKey, Detail);
}

/// <summary>The result of validating one graph.</summary>
public sealed record ValidationReport(IReadOnlyList<WorkflowDiagnostic> Diagnostics)
{
    public static readonly ValidationReport Empty = new([]);

    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);

    public bool HasWarnings => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Warning);

    public IEnumerable<WorkflowDiagnostic> Errors =>
        Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error);

    /// <summary>First error as user-facing text (used as a run failure message).</summary>
    public string? FirstErrorText => Errors.Select(d => d.Text).FirstOrDefault();
}

/// <summary>
/// One validator shared by the canvas (link creation), the importer, the load-time diagnostics
/// and the pre-run gate. Having a single implementation is the point: the editor, the file and
/// the runner must never disagree about whether a graph is legal.
/// </summary>
public static class WorkflowValidator
{
    /// <summary>Validate a whole graph.</summary>
    /// <param name="graph">Graph to inspect.</param>
    /// <param name="owner">Shortcut that owns the graph (start-node inputs live there).</param>
    /// <param name="forRun">
    /// True when the report gates a run: warnings that would only make a half-built graph
    /// confusing (an unreachable node, an empty loop body) are then still warnings, but
    /// reference-integrity problems are errors.
    /// </param>
    public static ValidationReport Validate(WorkflowGraph graph, ShortcutDefinition? owner = null, bool forRun = false)
    {
        var diagnostics = new List<WorkflowDiagnostic>();
        if (graph == null) return new ValidationReport(diagnostics);

        var byId = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (string.IsNullOrEmpty(node.Id))
            {
                diagnostics.Add(new("EmptyId", DiagnosticSeverity.Error, "Shortcuts_Diag_EmptyId"));
                continue;
            }

            if (!byId.TryAdd(node.Id, node))
            {
                diagnostics.Add(new("DuplicateId", DiagnosticSeverity.Error, "Shortcuts_Diag_DuplicateId",
                    node.Id, Detail: node.Id));
                continue;
            }

            if (ActionCatalog.Get(node.Type) == null)
            {
                diagnostics.Add(new("UnknownType", DiagnosticSeverity.Error, "Shortcuts_Diag_UnknownType",
                    node.Id, Detail: node.Type));
            }
        }

        // ---- entry point -----------------------------------------------------------
        var starts = graph.Nodes.Where(n => n.Type == WorkflowNodeTypes.Start).ToList();
        if (starts.Count == 0)
            diagnostics.Add(new("NoStart", DiagnosticSeverity.Error, "Shortcuts_Error_NoStart"));
        else if (starts.Count > 1)
            diagnostics.Add(new("MultipleStart", DiagnosticSeverity.Error, "Shortcuts_Diag_MultipleStart",
                starts[1].Id));

        // ---- links -----------------------------------------------------------------
        var dataTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in graph.Links)
        {
            var from = graph.Find(link.From);
            var to = graph.Find(link.To);
            if (from == null || to == null)
            {
                diagnostics.Add(new("LinkMissingNode", DiagnosticSeverity.Error, "Shortcuts_Diag_LinkMissingNode",
                    from == null ? link.To : link.From, Detail: $"{link.From} → {link.To}"));
                continue;
            }

            if (link.IsData)
            {
                ValidateDataLink(graph, owner, link, from, to, diagnostics);
                var key = link.To + "\u0000" + link.ToPort;
                if (!dataTargets.Add(key))
                {
                    diagnostics.Add(new("DuplicateDataLink", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_DuplicateDataLink", to.Id, link.ToPort, Detail: link.ToPort));
                }
            }
            else
            {
                ValidateExecLink(graph, owner, link, from, to, diagnostics);
            }
        }

        // ---- ports / parameters ----------------------------------------------------
        foreach (var node in graph.Nodes)
        {
            ValidateNode(graph, owner, node, diagnostics);
        }

        // ---- control-flow reachability --------------------------------------------
        var start = starts.FirstOrDefault();
        if (start != null)
        {
            var reachable = ExecReachable(graph, start);
            foreach (var node in graph.Nodes)
            {
                if (node.Type == WorkflowNodeTypes.Start) continue;
                if (reachable.Contains(node.Id)) continue;
                diagnostics.Add(new("NotConnected", DiagnosticSeverity.Warning,
                    "Shortcuts_Diag_NotConnected", node.Id));
            }

            ValidateLoops(graph, diagnostics);
            ValidateBreakContinue(graph, start, diagnostics);
            ValidateCycles(graph, start, diagnostics);
            ValidateReferences(graph, owner, start, reachable, diagnostics);
        }

        return new ValidationReport(diagnostics);
    }

    /// <summary>
    /// Check one prospective link without adding it (the canvas uses this to refuse a drop and
    /// to explain why). Returns null when the link is legal.
    /// </summary>
    public static WorkflowDiagnostic? CheckLink(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowNode from,
        string fromPort,
        WorkflowNode to,
        string toPort,
        bool isData)
    {
        if (from.Id == to.Id)
            return new("SelfLink", DiagnosticSeverity.Error, "Shortcuts_Diag_SelfLink", to.Id);

        if (isData)
        {
            if (!WorkflowNodeTypes.HasOutput(from, fromPort, owner))
                return new("BadPort", DiagnosticSeverity.Error, "Shortcuts_Diag_BadPort", from.Id, PortId: fromPort,
                    Detail: $"{from.Type}.{fromPort}");

            var param = ActionCatalog.Get(to.Type)?.Param(toPort);
            if (param == null)
                return new("BadParam", DiagnosticSeverity.Error, "Shortcuts_Diag_BadParam", to.Id, toPort,
                    Detail: $"{to.Type}.{toPort}");
            if (!param.Bindable)
                return new("NotBindable", DiagnosticSeverity.Error, "Shortcuts_Diag_NotBindable", to.Id, toPort,
                    Detail: param.Key);

            var outputType = WorkflowNodeTypes.OutputType(from, fromPort, owner);
            if (!WorkflowNodeTypes.IsTypeCompatible(outputType, param))
            {
                return new("TypeMismatch", DiagnosticSeverity.Error, "Shortcuts_Diag_TypeMismatch", to.Id, toPort,
                    Detail: $"{Loc.Get(ActionCatalog.TypeLabelKey(outputType))} → {Loc.Get(ActionCatalog.TypeLabelKey(param.DataType))}");
            }

            return null;
        }

        var port = WorkflowNodeTypes.Outputs(from, owner).FirstOrDefault(p => p.Id == fromPort);
        if (port == null || port.IsData)
            return new("BadPort", DiagnosticSeverity.Error, "Shortcuts_Diag_BadPort", from.Id, PortId: fromPort,
                Detail: $"{from.Type}.{fromPort}");

        if (!WorkflowNodeTypes.HasInput(to.Type))
            return new("NoExecInput", DiagnosticSeverity.Error, "Shortcuts_Diag_NoExecInput", to.Id,
                Detail: to.Type);

        return null;
    }

    // ---- link rules ---------------------------------------------------------------

    private static void ValidateDataLink(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowLink link,
        WorkflowNode from,
        WorkflowNode to,
        List<WorkflowDiagnostic> diagnostics)
    {
        var port = WorkflowNodeTypes.Outputs(from, owner)
            .FirstOrDefault(p => string.Equals(p.Id, link.FromPort, StringComparison.Ordinal));
        if (port == null)
        {
            diagnostics.Add(new("BadPort", DiagnosticSeverity.Error, "Shortcuts_Diag_BadPort",
                from.Id, PortId: link.FromPort, Detail: $"{from.Type}.{link.FromPort}"));
            return;
        }

        if (!port.IsData)
        {
            diagnostics.Add(new("ExecPortAsData", DiagnosticSeverity.Error,
                "Shortcuts_Diag_ExecPortAsData", from.Id, PortId: link.FromPort, Detail: link.FromPort));
            return;
        }

        var param = ActionCatalog.Get(to.Type)?.Param(link.ToPort);
        if (param == null)
        {
            diagnostics.Add(new("BadParam", DiagnosticSeverity.Error, "Shortcuts_Diag_BadParam",
                to.Id, link.ToPort, Detail: $"{to.Type}.{link.ToPort}"));
            return;
        }

        if (!param.Bindable)
        {
            diagnostics.Add(new("NotBindable", DiagnosticSeverity.Error, "Shortcuts_Diag_NotBindable",
                to.Id, link.ToPort, Detail: param.Key));
            return;
        }

        if (!WorkflowNodeTypes.IsTypeCompatible(port.DataType, param))
        {
            diagnostics.Add(new("TypeMismatch", DiagnosticSeverity.Error, "Shortcuts_Diag_TypeMismatch",
                to.Id, link.ToPort,
                Detail: $"{Loc.Get(ActionCatalog.TypeLabelKey(port.DataType))} → {Loc.Get(ActionCatalog.TypeLabelKey(param.DataType))}"));
        }
    }

    private static void ValidateExecLink(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowLink link,
        WorkflowNode from,
        WorkflowNode to,
        List<WorkflowDiagnostic> diagnostics)
    {
        var port = WorkflowNodeTypes.Outputs(from, owner)
            .FirstOrDefault(p => string.Equals(p.Id, link.FromPort, StringComparison.Ordinal));
        if (port == null)
        {
            diagnostics.Add(new("BadPort", DiagnosticSeverity.Error, "Shortcuts_Diag_BadPort",
                from.Id, PortId: link.FromPort, Detail: $"{from.Type}.{link.FromPort}"));
            return;
        }

        if (port.IsData)
        {
            diagnostics.Add(new("DataPortAsExec", DiagnosticSeverity.Error,
                "Shortcuts_Diag_DataPortAsExec", from.Id, PortId: link.FromPort, Detail: link.FromPort));
            return;
        }

        // An exec link may only land on the exec input — landing on a parameter dot was
        // previously accepted and silently did nothing.
        if (!string.Equals(link.ToPort, WorkflowNodeTypes.In, StringComparison.Ordinal))
        {
            diagnostics.Add(new("ExecIntoParam", DiagnosticSeverity.Error,
                "Shortcuts_Diag_ExecIntoParam", to.Id, link.ToPort, link.FromPort,
                $"{to.Type}.{link.ToPort}"));
            return;
        }

        if (!WorkflowNodeTypes.HasInput(to.Type))
        {
            diagnostics.Add(new("NoExecInput", DiagnosticSeverity.Error, "Shortcuts_Diag_NoExecInput",
                to.Id, Detail: to.Type));
            return;
        }

        var fanOut = graph.Outgoing(from.Id, link.FromPort).Count();
        if (fanOut > 1)
        {
            diagnostics.Add(new("ExecFanOut", DiagnosticSeverity.Error, "Shortcuts_Diag_ExecFanOut",
                from.Id, PortId: link.FromPort, Detail: link.FromPort));
        }
    }

    // ---- node rules ---------------------------------------------------------------

    private static void ValidateNode(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowNode node,
        List<WorkflowDiagnostic> diagnostics)
    {
        var definition = ActionCatalog.Get(node.Type);
        if (definition == null) return;

        foreach (var spec in definition.Params)
        {
            if (!IsProvided(graph, node, spec.Key)) continue;

            // Fed by a link: the literal is irrelevant, the source's type was checked already.
            if (graph.IncomingData(node.Id, spec.Key) != null) continue;

            var value = node.Params.GetValueOrDefault(spec.Key);
            if (value is { IsVariable: true })
            {
                ValidateReference(graph, owner, node, spec, value, diagnostics);
                continue;
            }

            var text = value?.Text ?? string.Empty;

            // A template literal is resolved at run time, so its text is not the value that will
            // reach the parameter: validating "{{n}}" as a number would reject a legal graph.
            var isTemplate = text.Contains("{{", StringComparison.Ordinal);

            if (spec.DataType == DataType.Number && text.Length > 0 && !isTemplate)
            {
                if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) ||
                    !double.IsFinite(number))
                {
                    diagnostics.Add(new("NumberInvalid", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_NumberInvalid", node.Id, spec.Key, Detail: Loc.Get(spec.LabelKey)));
                }
                else if (number < spec.Min || number > spec.Max)
                {
                    diagnostics.Add(new("NumberOutOfRange", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_NumberOutOfRange", node.Id, spec.Key,
                        Detail: $"{Loc.Get(spec.LabelKey)} [{spec.Min}, {spec.Max}]"));
                }
            }

            if (spec.Required && !spec.AllowEmpty && text.Length == 0)
            {
                diagnostics.Add(new("RequiredEmpty", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_RequiredEmpty", node.Id, spec.Key, Detail: Loc.Get(spec.LabelKey)));
            }
        }

        // Required parameters with no value at all and no data link: only reported when the
        // node is actually wired into the flow (an unfinished node is not a run blocker).
        foreach (var spec in definition.Params)
        {
            if (!spec.Required) continue;
            if (graph.IncomingData(node.Id, spec.Key) != null) continue;
            if (node.Params.ContainsKey(spec.Key)) continue;
            if (graph.Links.Any(l => l.To == node.Id)) continue;

            diagnostics.Add(new("RequiredMissing", DiagnosticSeverity.Warning,
                "Shortcuts_Diag_RequiredMissing", node.Id, spec.Key, Detail: Loc.Get(spec.LabelKey)));
        }
    }

    private static bool IsProvided(WorkflowGraph graph, WorkflowNode node, string key) =>
        node.Params.ContainsKey(key) || graph.IncomingData(node.Id, key) != null;

    private static void ValidateReference(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowNode node,
        ParamSpec spec,
        ShortcutValue value,
        List<WorkflowDiagnostic> diagnostics)
    {
        var source = value.SourceActionId;
        if (source == ValueKinds.LastResult) return;
        if (string.IsNullOrEmpty(source)) return;

        // A named variable written by setVar is resolved at run time; the reference is only
        // wrong when the name can never be produced.
        var isNode = graph.Find(source) != null;
        if (!isNode)
        {
            var produced = graph.Nodes.Any(n =>
                n.Type == WorkflowNodeTypes.SetVar &&
                string.Equals(n.Params.GetValueOrDefault("name")?.Text, source, StringComparison.Ordinal));
            if (!produced)
            {
                diagnostics.Add(new("UnknownSource", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_UnknownSource", node.Id, spec.Key, Detail: source));
            }
            return;
        }

        if (value.SourcePort != null &&
            !WorkflowNodeTypes.HasOutput(graph.Find(source)!, value.SourcePort, owner))
        {
            diagnostics.Add(new("BadPort", DiagnosticSeverity.Error, "Shortcuts_Diag_BadPort",
                source, PortId: value.SourcePort, Detail: value.SourcePort));
        }
    }

    // ---- control flow -------------------------------------------------------------

    /// <summary>Nodes reachable from <paramref name="start"/> following exec links only.</summary>
    public static HashSet<string> ExecReachable(WorkflowGraph graph, WorkflowNode start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start.Id };
        var queue = new Queue<string>();
        queue.Enqueue(start.Id);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var node = graph.Find(id);
            if (node == null) continue;

            foreach (var link in graph.Links)
            {
                if (link.IsData || link.From != id) continue;
                if (!seen.Add(link.To)) continue;
                queue.Enqueue(link.To);
            }
        }

        return seen;
    }

    /// <summary>Nodes reachable from one port of a node, exec links only.</summary>
    private static HashSet<string> ReachableFromPort(WorkflowGraph graph, string nodeId, string port)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var link in graph.Outgoing(nodeId, port))
        {
            if (link.IsData) continue;
            if (seen.Add(link.To)) queue.Enqueue(link.To);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var link in graph.Links)
            {
                if (link.IsData || link.From != id) continue;
                if (seen.Add(link.To)) queue.Enqueue(link.To);
            }
        }

        return seen;
    }

    private static void ValidateLoops(WorkflowGraph graph, List<WorkflowDiagnostic> diagnostics)
    {
        foreach (var loop in graph.Nodes.Where(n => WorkflowNodeTypes.IsLoop(n.Type)))
        {
            var body = graph.NextNode(loop.Id, WorkflowNodeTypes.BodyPort);
            if (body == null)
            {
                diagnostics.Add(new("EmptyLoopBody", DiagnosticSeverity.Warning,
                    "Shortcuts_Diag_EmptyLoopBody", loop.Id));
                continue;
            }

            // A body chain that flows into the loop's done successor would run that successor
            // once per iteration AND again after the loop: the classic duplicate-execution bug.
            var donePath = ReachableFromPort(graph, loop.Id, WorkflowNodeTypes.DonePort);
            var bodyPath = ReachableFromPort(graph, loop.Id, WorkflowNodeTypes.BodyPort);
            var overlap = bodyPath.Intersect(donePath).ToList();
            if (overlap.Count > 0)
            {
                diagnostics.Add(new("BodyReachesDone", DiagnosticSeverity.Error,
                    "Shortcuts_Diag_BodyReachesDone", loop.Id, Detail: graph.Find(overlap[0])?.Type ?? overlap[0]));
            }
        }
    }

    private static void ValidateBreakContinue(WorkflowGraph graph, WorkflowNode start, List<WorkflowDiagnostic> diagnostics)
    {
        foreach (var node in graph.Nodes)
        {
            if (node.Type is not (WorkflowNodeTypes.Break or WorkflowNodeTypes.Continue)) continue;
            if (EnclosingLoop(graph, node.Id) != null) continue;

            diagnostics.Add(new(node.Type == WorkflowNodeTypes.Break ? "BreakOutsideLoop" : "ContinueOutsideLoop",
                DiagnosticSeverity.Error,
                node.Type == WorkflowNodeTypes.Break
                    ? "Shortcuts_Diag_BreakOutsideLoop"
                    : "Shortcuts_Diag_ContinueOutsideLoop",
                node.Id));
        }
    }

    /// <summary>The innermost loop whose body contains <paramref name="nodeId"/>.</summary>
    public static WorkflowNode? EnclosingLoop(WorkflowGraph graph, string nodeId)
    {
        WorkflowNode? best = null;
        var bestSize = int.MaxValue;

        foreach (var loop in graph.Nodes.Where(n => WorkflowNodeTypes.IsLoop(n.Type)))
        {
            var body = ReachableFromPort(graph, loop.Id, WorkflowNodeTypes.BodyPort);
            if (!body.Contains(nodeId)) continue;
            if (body.Count >= bestSize) continue;
            best = loop;
            bestSize = body.Count;
        }

        return best;
    }

    private static void ValidateCycles(WorkflowGraph graph, WorkflowNode start, List<WorkflowDiagnostic> diagnostics)
    {
        // Depth-first search over exec links; a back edge to a node still on the stack is a
        // cycle. Edges that enter a loop body are exempt because the loop owns the iteration.
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        var reported = false;

        void Visit(string id)
        {
            if (reported) return;
            state[id] = 1;
            stack.Add(id);

            var node = graph.Find(id);
            if (node != null)
            {
                foreach (var link in graph.Links.Where(l => !l.IsData && l.From == id))
                {
                    var child = graph.Find(link.To);
                    if (child == null) continue;

                    if (!state.TryGetValue(child.Id, out var color))
                    {
                        Visit(child.Id);
                    }
                    else if (color == 1 && !IsLoopEdge(graph, link))
                    {
                        diagnostics.Add(new("Cycle", DiagnosticSeverity.Error, "Shortcuts_Error_Cycle",
                            child.Id, Detail: child.Type));
                        reported = true;
                        return;
                    }
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[id] = 2;
        }

        Visit(start.Id);
    }

    /// <summary>A back edge that is the body entry of an enclosing loop is legal control flow.</summary>
    private static bool IsLoopEdge(WorkflowGraph graph, WorkflowLink link)
    {
        var from = graph.Find(link.From);
        return from != null && WorkflowNodeTypes.IsLoop(from.Type) &&
               string.Equals(link.FromPort, WorkflowNodeTypes.BodyPort, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reference integrity: a node may only read a source that is guaranteed to have run —
    /// i.e. the source dominates the reader on the exec graph. This is what stops "read the
    /// result of the branch that was skipped" from silently producing an empty string.
    /// </summary>
    private static void ValidateReferences(
        WorkflowGraph graph,
        ShortcutDefinition? owner,
        WorkflowNode start,
        HashSet<string> reachable,
        List<WorkflowDiagnostic> diagnostics)
    {
        var dominators = ComputeDominators(graph, start);

        foreach (var node in graph.Nodes)
        {
            foreach (var (key, value) in node.Params)
            {
                if (value is not { IsVariable: true }) continue;
                var source = value.SourceActionId;
                if (source is null or ValueKinds.LastResult) continue;
                if (graph.Find(source) == null) continue;

                if (!reachable.Contains(source))
                {
                    diagnostics.Add(new("SourceNotExecuted", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_SourceNotExecuted", node.Id, key, Detail: source));
                    continue;
                }

                if (!dominators.TryGetValue(node.Id, out var set)) continue;
                if (!set.Contains(source))
                {
                    diagnostics.Add(new("SourceNotGuaranteed", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_SourceNotGuaranteed", node.Id, key, Detail: source));
                }
            }

            // A data link is the same dependency expressed as an edge.
            foreach (var link in graph.Links.Where(l => l.IsData && l.To == node.Id))
            {
                if (!reachable.Contains(link.From))
                {
                    diagnostics.Add(new("SourceNotExecuted", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_SourceNotExecuted", node.Id, link.ToPort, Detail: link.From));
                    continue;
                }

                // mergeValue exists to read whichever branch ran, so "not guaranteed to run
                // before" is its normal input rather than a mistake.
                if (node.Type == WorkflowNodeTypes.MergeValue) continue;

                if (!dominators.TryGetValue(node.Id, out var set)) continue;
                if (!set.Contains(link.From))
                {
                    diagnostics.Add(new("SourceNotGuaranteed", DiagnosticSeverity.Error,
                        "Shortcuts_Diag_SourceNotGuaranteed", node.Id, link.ToPort, Detail: link.From));
                }
            }
        }
    }

    /// <summary>Iterative dominator sets over exec links (Cooper-Harvey-Kennedy style).</summary>
    private static Dictionary<string, HashSet<string>> ComputeDominators(WorkflowGraph graph, WorkflowNode start)
    {
        var nodes = graph.Nodes.Where(n => n.Id != null).Select(n => n.Id).ToList();
        var all = new HashSet<string>(nodes, StringComparer.Ordinal);
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var id in nodes)
            result[id] = string.Equals(id, start.Id, StringComparison.Ordinal)
                ? new HashSet<string>(StringComparer.Ordinal) { id }
                : new HashSet<string>(all, StringComparer.Ordinal);

        var changed = true;
        var guard = 0;
        while (changed && guard++ < nodes.Count + 2)
        {
            changed = false;
            foreach (var id in nodes)
            {
                if (string.Equals(id, start.Id, StringComparison.Ordinal)) continue;

                var preds = graph.Links
                    .Where(l => !l.IsData && string.Equals(l.To, id, StringComparison.Ordinal))
                    .Select(l => l.From)
                    .Where(result.ContainsKey)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                HashSet<string> next;
                if (preds.Count == 0)
                {
                    next = new HashSet<string>(StringComparer.Ordinal) { id };
                }
                else
                {
                    next = new HashSet<string>(result[preds[0]], StringComparer.Ordinal);
                    foreach (var pred in preds.Skip(1))
                        next.IntersectWith(result[pred]);
                    next.Add(id);
                }

                if (!next.SetEquals(result[id]))
                {
                    result[id] = next;
                    changed = true;
                }
            }
        }

        return result;
    }
}
