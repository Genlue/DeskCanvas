using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using Shortcuts.Models;
using Shortcuts.Services.Executors;

namespace Shortcuts.Services;

/// <summary>Progress event emitted while a shortcut runs.</summary>
public enum RunEventKind
{
    Started,
    StepStarted,
    StepFinished,
    StepFailed,
    Finished,
    Cancelled,
    Busy,
}

/// <summary>
/// One progress event. <see cref="RunId"/> identifies the exact run (a parent and its child
/// shortcut share neither id nor event stream), so a stale event from a finished run can never
/// light up a node of the next one.
/// </summary>
public sealed record RunEvent(
    string RunId,
    string? ParentRunId,
    string ShortcutId,
    string ShortcutName,
    RunEventKind Kind,
    string? ActionId = null,
    string? Detail = null,
    int StepIndex = 0,
    int TotalSteps = 0,
    bool Success = true,
    int Iteration = -1,
    string? PortId = null);

/// <summary>The typed outputs of one node: port id → value.</summary>
public sealed class NodeOutput
{
    private readonly Dictionary<string, RunValue> ports = new(StringComparer.Ordinal);

    public RunValue? this[string portId]
    {
        get => ports.GetValueOrDefault(portId);
        set
        {
            if (value == null) ports.Remove(portId);
            else ports[portId] = value;
        }
    }

    public IReadOnlyDictionary<string, RunValue> Ports => ports;

    public RunValue? Default => ports.Values.FirstOrDefault();
}

/// <summary>Outcome of one run.</summary>
public sealed record RunResult(
    string RunId,
    bool Success,
    bool Cancelled,
    bool Busy,
    string? Error,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyDictionary<string, NodeOutput> Data,
    RunValue? ReturnValue,
    string? ErrorCode = null,
    string? ErrorNodeId = null)
{
    public static RunResult Ok(string runId, IReadOnlyDictionary<string, string> outputs,
        IReadOnlyDictionary<string, NodeOutput> data, RunValue? returnValue) =>
        new(runId, true, false, false, null, outputs, data, returnValue);

    public static RunResult CancelledResult(string runId, IReadOnlyDictionary<string, string> outputs,
        IReadOnlyDictionary<string, NodeOutput> data) =>
        new(runId, false, true, false, null, outputs, data, null, "Cancelled");

    public static RunResult Failed(string runId, string error, IReadOnlyDictionary<string, string> outputs,
        IReadOnlyDictionary<string, NodeOutput> data, string? code = null, string? nodeId = null) =>
        new(runId, false, false, false, error, outputs, data, null, code, nodeId);

    public static RunResult BusyResult(string runId) =>
        new(runId, false, false, true, null, new Dictionary<string, string>(),
            new Dictionary<string, NodeOutput>(), null, "Busy");
}

/// <summary>Host services an action may use (captured from the launching widget/panel/editor).</summary>
public sealed class RunServices
{
    public IAppSettingsProvider? AppSettings { get; init; }
    public WidgetFactory? WidgetFactory { get; init; }
    public Func<TopLevel?>? TopLevelAccessor { get; init; }

    /// <summary>
    /// Shortcut ids already on the call stack for <c>runShortcut</c>; used to block direct and
    /// indirect recursion without threading a mutable stack through every executor.
    /// </summary>
    public IReadOnlyList<string> Chain { get; init; } = Array.Empty<string>();

    /// <summary>Injected system adapter (tests substitute a fake; production uses the real one).</summary>
    public ISystemAdapter System
    {
        get => system ??= new HostSystemAdapter(TopLevelAccessor);
        init => system = value;
    }

    /// <summary>Injected dialog service (tests substitute a fake).</summary>
    public IUserPromptService Prompts
    {
        get => prompts ??= new HostPromptService(TopLevelAccessor);
        init => prompts = value;
    }

    private ISystemAdapter? system;
    private IUserPromptService? prompts;

    /// <summary>The window that may own confirm dialogs.</summary>
    public Window? Owner => TopLevelAccessor?.Invoke() as Window;

    public TopLevel? TopLevel => TopLevelAccessor?.Invoke();

    /// <summary>Copy of these services with an extended nesting chain.</summary>
    public RunServices WithChain(IReadOnlyList<string> chain) => new()
    {
        AppSettings = AppSettings,
        WidgetFactory = WidgetFactory,
        TopLevelAccessor = TopLevelAccessor,
        Chain = chain,
        System = System,
        Prompts = Prompts,
    };
}

/// <summary>Options for one run entry point.</summary>
public sealed class RunOptions
{
    /// <summary>Explicit run id (tests / diagnostics); a fresh id is generated when null.</summary>
    public string? RunId { get; init; }

    /// <summary>The run that started this one through <c>runShortcut</c>.</summary>
    public string? ParentRunId { get; init; }

    /// <summary>Cancellation owned by the caller; linked into the run's own token.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Shared step/deadline budget; a child run spends the root's budget.</summary>
    public RunBudget? Budget { get; init; }

    /// <summary>Declared input values keyed by field key.</summary>
    public IReadOnlyDictionary<string, RunValue>? Inputs { get; init; }

    /// <summary>Component-level "ask before running" setting (widget card / panel).</summary>
    public bool RequireConfirm { get; init; }

    /// <summary>True when the caller already confirmed this run.</summary>
    public bool Confirmed { get; init; }

    /// <summary>Nesting depth for <c>runShortcut</c> (0 = user-initiated).</summary>
    public int Depth { get; init; }

    /// <summary>Overrides the per-step timeout (checks use a short one; production keeps 2 min).</summary>
    public TimeSpan? StepTimeout { get; init; }
}

/// <summary>Step and wall-clock budget shared by a root run and every nested run.</summary>
public sealed class RunBudget
{
    private int remainingSteps;

    public RunBudget(int maxSteps, TimeSpan deadline)
    {
        MaxSteps = Math.Max(1, maxSteps);
        remainingSteps = MaxSteps;
        Deadline = DateTime.UtcNow + deadline;
    }

    public int MaxSteps { get; }

    public DateTime Deadline { get; }

    public int RemainingSteps => Volatile.Read(ref remainingSteps);

    /// <summary>Spend one step; false when the budget is exhausted.</summary>
    public bool TrySpend()
    {
        if (DateTime.UtcNow > Deadline) return false;
        return Interlocked.Decrement(ref remainingSteps) >= 0;
    }
}

/// <summary>One loop invocation's state: iteration counters, collected values, scoped names.</summary>
internal sealed class LoopFrame
{
    public required WorkflowNode Loop { get; init; }
    public required HashSet<string> BodyNodes { get; init; }
    public required string ItemVariable { get; init; }
    public int Index { get; set; }
    public RunValue? Item { get; set; }
    public List<RunValue> Collected { get; } = [];
    public int Iteration => Index;
}

/// <summary>How a resolved parameter was obtained.</summary>
public readonly record struct ParamResolution(
    RunValue Value,
    ParamOrigin Origin,
    string? SourceNodeId = null,
    string? SourcePortId = null);

/// <summary>Execution state for one run: snapshot graph, typed outputs, variables and frames.</summary>
public sealed class RunContext
{
    private static readonly System.Text.RegularExpressions.Regex TemplateToken = new(
        @"\{\{\s*([A-Za-z0-9_\-\.\$]+)\s*\}\}",
        System.Text.RegularExpressions.RegexOptions.Compiled |
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private readonly Dictionary<string, NodeOutput> outputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RunValue> variables = new(StringComparer.Ordinal);
    private readonly List<LoopFrame> frames = [];
    private readonly Dictionary<string, RunValue> inputs = new(StringComparer.Ordinal);

    public string RunId { get; }
    public string? ParentRunId { get; }
    public ShortcutDefinition Shortcut { get; }

    /// <summary>The graph snapshot taken when the run started; edits during the run cannot affect it.</summary>
    public WorkflowGraph Graph { get; }

    public RunServices Services { get; }
    public RunBudget Budget { get; }

    /// <summary>
    /// The token the current step observes. The runner swaps in a per-step linked token so a
    /// step timeout really cancels the underlying operation instead of abandoning the wait.
    /// </summary>
    public CancellationToken ActiveToken { get; internal set; }

    public CancellationToken Token => ActiveToken;

    /// <summary>Named variables written by <c>setVar</c>.</summary>
    public IReadOnlyDictionary<string, RunValue> Variables => variables;

    /// <summary>Per-step timeout override, inherited from <see cref="RunOptions"/>.</summary>
    public TimeSpan? StepTimeout { get; init; }

    /// <summary>The most recent data result and where it came from.</summary>
    public (RunValue Value, string NodeId, string PortId)? LastData { get; private set; }

    /// <summary>Value produced by a <c>return</c> node.</summary>
    public RunValue? ReturnValue { get; internal set; }

    public int StepIndex { get; private set; }
    public int TotalSteps { get; }

    /// <summary>Innermost loop frame (null outside every loop).</summary>
    internal LoopFrame? CurrentFrame => frames.Count == 0 ? null : frames[^1];

    public IReadOnlyList<string> Chain => Services.Chain;

    public RunContext(ShortcutDefinition shortcut, RunServices services, RunOptions options, RunBudget budget)
        : this(shortcut, shortcut.EffectiveGraph.Clone(), services, options, budget)
    {
    }

    public RunContext(ShortcutDefinition shortcut, WorkflowGraph graph, RunServices services,
        RunOptions options, RunBudget budget)
    {
        RunId = options.RunId ?? ShortcutRunner.NewRunId();
        ParentRunId = options.ParentRunId;
        Shortcut = shortcut;
        Graph = graph;
        Services = services;
        Budget = budget;
        StepTimeout = options.StepTimeout;
        ActiveToken = options.CancellationToken;
        TotalSteps = Math.Max(1, graph.Nodes.Count(n =>
            n.Type != WorkflowNodeTypes.Start && n.Type != WorkflowNodeTypes.Note));

        if (options.Inputs != null)
        {
            foreach (var (key, value) in options.Inputs) inputs[key] = value;
        }
    }

    // ---- parameter resolution -----------------------------------------------------

    /// <summary>
    /// Resolve one parameter. The order is fixed and shared by the runner, the validator and
    /// the editor: an explicit binding (data link, then variable reference) wins, then the
    /// literal, then — only when the parameter was never provided — the catalog default.
    /// <para>
    /// A bound value is returned <b>as is</b>: templates are expanded only for literals, so a
    /// result that itself contains <c>{{…}}</c> is never expanded a second time.
    /// </para>
    /// </summary>
    public ParamResolution ResolveParam(WorkflowNode node, string key)
    {
        var spec = ActionCatalog.Get(node.Type)?.Param(key);

        if (Graph.IncomingData(node.Id, key) is { } link)
        {
            var value = ReadPort(link.From, link.FromPort);
            if (value == null) throw MissingSource(node, key, link.From);
            return new ParamResolution(value, ParamOrigin.Link, link.From, link.FromPort);
        }

        if (node.Params.TryGetValue(key, out var stored))
        {
            if (stored == null) return new ParamResolution(RunValue.Null, ParamOrigin.Literal);

            if (stored.IsVariable)
            {
                var source = stored.SourceActionId!;
                if (source == ValueKinds.LastResult)
                {
                    if (LastData is not { } last) throw MissingSource(node, key, ValueKinds.LastResult);
                    return new ParamResolution(last.Value, ParamOrigin.Variable, last.NodeId, last.PortId);
                }

                // A loop-scoped name (index / item) is read from the innermost frame.
                if (LookupFrameVariable(source) is { } loopValue)
                    return new ParamResolution(loopValue, ParamOrigin.LoopVariable, source);

                if (Graph.Find(source) != null)
                {
                    var port = stored.SourcePort ?? WorkflowNodeTypes.DefaultDataPort(Graph.Find(source)!);
                    var value = port == null ? null : ReadPort(source, port);
                    if (value == null) throw MissingSource(node, key, source);
                    return new ParamResolution(value, ParamOrigin.Variable, source, port);
                }

                if (variables.TryGetValue(source, out var variable))
                    return new ParamResolution(variable, ParamOrigin.Variable, source);

                throw MissingSource(node, key, source);
            }

            return new ParamResolution(RunValue.FromText(Expand(stored.Text)), ParamOrigin.Literal);
        }

        if (spec?.DefaultValue != null)
            return new ParamResolution(RunValue.FromText(Expand(spec.DefaultValue)), ParamOrigin.Default);

        return new ParamResolution(RunValue.Null, ParamOrigin.Default);
    }

    /// <summary>Declared input value supplied by the caller (null when not supplied).</summary>
    public RunValue? Input(string key) => inputs.GetValueOrDefault(key);

    /// <summary>Resolve and enforce Required / AllowEmpty for one parameter.</summary>
    public ParamResolution Require(WorkflowNode node, string key)
    {
        var resolution = ResolveParam(node, key);
        var spec = ActionCatalog.Get(node.Type)?.Param(key);
        if (spec == null) return resolution;

        if (resolution.Value.IsNull)
        {
            if (spec.Required)
                throw new ActionFailedException("RequiredMissing", node, key,
                    Loc.Format("Shortcuts_Error_MissingParam", Loc.Get(spec.LabelKey), NodeLabel(node)));
        }
        else if (spec.Required && !spec.AllowEmpty &&
                 resolution.Value.Type is DataType.Text or DataType.Path &&
                 string.IsNullOrEmpty(resolution.Value.Text))
        {
            throw new ActionFailedException("RequiredEmpty", node, key,
                Loc.Format("Shortcuts_Error_MissingParam", Loc.Get(spec.LabelKey), NodeLabel(node)));
        }

        return resolution;
    }

    /// <summary>Text value of a parameter (no template re-expansion for bound values).</summary>
    public string Text(WorkflowNode node, string key) => Require(node, key).Value.ToText();

    public string Text(ShortcutAction action, string key) => Text(ForAction(action), key);

    /// <summary>Numeric value; a non-numeric value is an error, never a silent fallback.</summary>
    public double Number(WorkflowNode node, string key)
    {
        var resolution = Require(node, key);
        if (resolution.Value.IsNull && !IsRequired(node, key)) return 0;
        if (!resolution.Value.TryGetNumber(out var value))
        {
            throw new ActionFailedException("NumberInvalid", node, key,
                Loc.Format("Shortcuts_Error_NotNumber", Loc.Get(ParamLabel(node, key)), resolution.Value.DisplayText));
        }

        if (!double.IsFinite(value))
            throw new ActionFailedException("NumberInvalid", node, key, Loc.Get("Shortcuts_Error_NotNumber"));

        var spec = ActionCatalog.Get(node.Type)?.Param(key);
        return spec == null ? value : Math.Clamp(value, spec.Min, spec.Max);
    }

    public double Number(ShortcutAction action, string key) => Number(ForAction(action), key);

    public bool Bool(WorkflowNode node, string key)
    {
        var resolution = Require(node, key);
        if (resolution.Value.IsNull) return false;
        if (!resolution.Value.TryGetBoolean(out var value))
        {
            throw new ActionFailedException("BooleanInvalid", node, key,
                Loc.Format("Shortcuts_Error_NotBoolean", Loc.Get(ParamLabel(node, key)), resolution.Value.DisplayText));
        }

        return value;
    }

    public bool Bool(ShortcutAction action, string key) => Bool(ForAction(action), key);

    public DateTime Date(ShortcutAction action, string key) => Date(ForAction(action), key);

    public IReadOnlyList<RunValue> List(ShortcutAction action, string key) => List(ForAction(action), key);

    public RunValue Value(ShortcutAction action, string key) => Value(ForAction(action), key);

    public RunValue Optional(ShortcutAction action, string key) => Optional(ForAction(action), key);

    public DateTime Date(WorkflowNode node, string key)
    {
        var resolution = Require(node, key);
        if (resolution.Value.IsNull) return DateTime.Now;
        if (!resolution.Value.TryGetDate(out var value))
        {
            throw new ActionFailedException("DateInvalid", node, key,
                Loc.Format("Shortcuts_Error_NotDate", Loc.Get(ParamLabel(node, key)), resolution.Value.DisplayText));
        }

        return value;
    }

    /// <summary>
    /// List view of a parameter. Text is accepted too (one item per line), which is how a list
    /// is edited by hand and how legacy separated-text values arrive through a link.
    /// </summary>
    public IReadOnlyList<RunValue> List(WorkflowNode node, string key)
    {
        var value = Require(node, key).Value;
        if (value.Type == DataType.List) return value.AsList();
        if (value.IsNull) return [];

        return value.ToText()
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(RunValue.FromText)
            .ToList();
    }

    /// <summary>Raw typed value (used by pass-through data nodes).</summary>
    public RunValue Value(WorkflowNode node, string key) => Require(node, key).Value;

    /// <summary>Raw typed value without the Required gate (merge / collect read optional slots).</summary>
    public RunValue Optional(WorkflowNode node, string key) => ResolveParam(node, key).Value;

    // Executors were written against ShortcutAction; a node IS an action plus a position, so
    // these overloads keep every executor unchanged while still honouring data links.

    public string Resolve(ShortcutAction action, string key) => Text(ForAction(action), key);

    public string Resolve(WorkflowNode node, string key) => Text(node, key);

    public double ResolveNumber(ShortcutAction action, string key) => Number(ForAction(action), key);

    private WorkflowNode ForAction(ShortcutAction action) =>
        Graph.Find(action.Id) ?? new WorkflowNode { Id = action.Id, Type = action.Type, Params = action.Params };

    private static bool IsRequired(WorkflowNode node, string key) =>
        ActionCatalog.Get(node.Type)?.Param(key) is { Required: true };

    private static string ParamLabel(WorkflowNode node, string key) =>
        ActionCatalog.Get(node.Type)?.Param(key) is { } spec ? Loc.Get(spec.LabelKey) : key;

    internal static string NodeLabel(WorkflowNode node)
    {
        if (!string.IsNullOrWhiteSpace(node.Title)) return node.Title!;
        var definition = ActionCatalog.Get(node.Type);
        return definition == null ? node.Type : Loc.Get(definition.TitleKey);
    }

    /// <summary>
    /// Expand <c>{{name}}</c> tokens in a literal. Unknown tokens are left untouched so a typo
    /// stays visible instead of silently becoming an empty string.
    /// </summary>
    public string Expand(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("{{", StringComparison.Ordinal))
            return text ?? string.Empty;

        return TemplateToken.Replace(text, match =>
        {
            var name = match.Groups[1].Value;
            var resolved = Lookup(name);
            return resolved ?? match.Value;
        });
    }

    private string? Lookup(string name)
    {
        if (name is "last" or "$last" or "上一步") return LastData?.Value.ToText();

        // A declared shortcut input is published as a data port on the start node, so `{{name}}`
        // reads it exactly like any other port.
        if (Graph.Start is { } startNode &&
            outputs.TryGetValue(startNode.Id, out var startOutputs) &&
            startOutputs[name] is { } inputValue)
        {
            return inputValue.ToText();
        }

        if (LookupFrameVariable(name) is { } loopValue) return loopValue.ToText();
        if (variables.TryGetValue(name, out var variable)) return variable.ToText();
        if (outputs.TryGetValue(name, out var node))
        {
            var port = Graph.Find(name) is { } definition ? WorkflowNodeTypes.DefaultDataPort(definition) : null;
            var value = port == null ? node.Default : node[port];
            if (value != null) return value.ToText();
        }
        if (inputs.TryGetValue(name, out var input)) return input.ToText();
        return null;
    }

    private RunValue? LookupFrameVariable(string name)
    {
        for (var i = frames.Count - 1; i >= 0; i--)
        {
            var frame = frames[i];
            if (name == "index") return RunValue.FromNumber(frame.Index);
            if (string.Equals(frame.ItemVariable, name, StringComparison.Ordinal) && frame.Item != null)
                return frame.Item;
        }
        return null;
    }

    // ---- outputs ------------------------------------------------------------------

    /// <summary>Read one typed output port of a node; null when that node never produced it.</summary>
    public RunValue? ReadPort(string nodeId, string portId)
    {
        if (!outputs.TryGetValue(nodeId, out var node)) return null;
        return node[portId];
    }

    public NodeOutput? NodeOutputs(string nodeId) => outputs.GetValueOrDefault(nodeId);

    /// <summary>Record one output port and (for data ports) update the "latest data result".</summary>
    public void SetOutput(string nodeId, string portId, RunValue value)
    {
        if (!outputs.TryGetValue(nodeId, out var node))
        {
            node = new NodeOutput();
            outputs[nodeId] = node;
        }

        node[portId] = value;

        var owner = Graph.Find(nodeId);
        if (owner == null) return;
        if (WorkflowNodeTypes.OutputType(owner, portId) == DataType.Null) return;

        LastData = (value, nodeId, portId);
    }

    /// <summary>Store every declared data port of a node (missing ports become null).</summary>
    public void SetOutputs(WorkflowNode node, IReadOnlyDictionary<string, RunValue>? produced)
    {
        foreach (var port in WorkflowNodeTypes.Outputs(node).Where(p => p.IsData))
        {
            var value = produced != null && produced.TryGetValue(port.Id, out var found)
                ? found
                : RunValue.Null;
            SetOutput(node.Id, port.Id, value);
        }
    }

    /// <summary>Drop a node's outputs so a later read reports "never executed".</summary>
    public void ClearOutput(string nodeId) => outputs.Remove(nodeId);

    public Dictionary<string, string> FlattenOutputs()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (nodeId, node) in outputs)
        {
            var owner = Graph.Find(nodeId);
            var port = owner == null ? null : WorkflowNodeTypes.DefaultDataPort(owner);
            var value = port == null ? node.Default : node[port];
            if (value != null) map[nodeId] = value.ToText();
        }
        return map;
    }

    public IReadOnlyDictionary<string, NodeOutput> AllOutputs => outputs;

    // ---- variables ----------------------------------------------------------------

    public void SetVariable(string name, RunValue value) => variables[name] = value;

    public RunValue? GetVariable(string name) => variables.GetValueOrDefault(name);

    public bool IsReservedName(string name)
    {
        if (WorkflowNodeTypes.ReservedNames.Contains(name, StringComparer.Ordinal)) return true;
        return frames.Any(f => string.Equals(f.ItemVariable, name, StringComparison.Ordinal));
    }

    // ---- loops --------------------------------------------------------------------

    internal LoopFrame PushFrame(WorkflowNode loop, HashSet<string> bodyNodes, string itemVariable)
    {
        var frame = new LoopFrame { Loop = loop, BodyNodes = bodyNodes, ItemVariable = itemVariable };
        frames.Add(frame);
        return frame;
    }

    internal void PopFrame() => frames.RemoveAt(frames.Count - 1);

    /// <summary>
    /// Forget everything the loop body produced, so an iteration can never read the previous
    /// iteration's leftovers (that is what "the loop silently used stale data" looked like).
    /// </summary>
    internal void ResetFrameOutputs(LoopFrame frame)
    {
        foreach (var nodeId in frame.BodyNodes) outputs.Remove(nodeId);
    }

    internal void Collect(RunValue value, WorkflowNode node)
    {
        if (CurrentFrame is not { } frame)
        {
            throw new ActionFailedException("CollectOutsideLoop", node, "value",
                Loc.Get("Shortcuts_Error_CollectOutsideLoop"));
        }

        frame.Collected.Add(value);
    }

    // ---- confirm ------------------------------------------------------------------

    /// <summary>
    /// Ask the user before a destructive step. A run with no window cannot ask: that is an
    /// explicit failure, never an implicit "yes".
    /// </summary>
    public async Task<bool> ConfirmDestructiveAsync() =>
        await ConfirmAsync(Loc.Get("Shortcuts_ConfirmDestructive")).ConfigureAwait(true);

    public async Task<bool> ConfirmAsync(string message)
    {
        var owner = Services.Owner;
        if (owner == null)
        {
            throw new ActionFailedException("ConfirmUnavailable", null, null,
                Loc.Get("Shortcuts_Error_ConfirmUnavailable"));
        }

        try
        {
            return await DeskCanvas.Views.ConfirmDialog.ConfirmAsync(
                owner,
                message,
                Loc.Get("Shortcuts_ConfirmRunOk"),
                Loc.Get("Shortcuts_Cancel")).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            throw new ActionFailedException("ConfirmFailed", null, null,
                Loc.Get("Shortcuts_Error_ConfirmUnavailable") + " - " + ex.Message);
        }
    }

    // ---- events -------------------------------------------------------------------

    internal int CurrentIteration => CurrentFrame?.Iteration ?? -1;

    internal void Raise(RunEventKind kind, string? nodeId = null, string? detail = null, bool success = true,
        string? portId = null)
    {
        ShortcutRunner.Emit(new RunEvent(RunId, ParentRunId, Shortcut.Id, Shortcut.Name, kind, nodeId, detail,
            StepIndex, TotalSteps, success, CurrentIteration, portId));
    }

    internal bool BeginStep() => Budget.TrySpend() && ++StepIndex > 0;

    internal void SetActiveToken(CancellationToken token) => ActiveToken = token;

    private ActionFailedException MissingSource(WorkflowNode node, string key, string source)
    {
        var message = source == ValueKinds.LastResult
            ? Loc.Get("Shortcuts_Error_NoLastResult")
            : Loc.Format("Shortcuts_Error_SourceNotRun", source);
        return new ActionFailedException("SourceNotExecuted", node, key, message);
    }
}

/// <summary>
/// A step failure with a stable code and a precise location. The message is what the user
/// sees; the code/node/param triple is what the editor highlights.
/// </summary>
public sealed class ActionFailedException : Exception
{
    public ActionFailedException(string message) : this("StepFailed", null, null, message)
    {
    }

    public ActionFailedException(string code, WorkflowNode? node, string? paramKey, string message)
        : base(message)
    {
        Code = code;
        NodeId = node?.Id;
        ParamKey = paramKey;
    }

    public string Code { get; }

    public string? NodeId { get; }

    public string? ParamKey { get; }
}

/// <summary>How a chain stopped, so containers can react (loop break/continue, full stop).</summary>
internal enum FlowSignal
{
    None,
    Break,
    Continue,
    Stop,
    Return,

    /// <summary>The chain walked into an enclosing loop's done-path; the loop must resume.</summary>
    LeftBody,
}

/// <summary>
/// Graph interpreter. Execution starts at the <c>start</c> node and follows exec links; loop
/// nodes re-enter their <c>body</c> chain per iteration, so branches and loops are real control
/// flow rather than nesting. Runs are keyed by shortcut id — a shortcut cannot overlap itself —
/// and every run owns a snapshot of the graph, a budget shared with its nested runs, and a
/// cancellation token that propagates down the call stack.
/// </summary>
public static class ShortcutRunner
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, RunHandle> ActiveRuns = new(StringComparer.Ordinal);

    private static readonly TimeSpan StandardStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WaitStepTimeout = TimeSpan.FromSeconds(610);

    /// <summary>Hard guards against a hand-drawn cycle or a runaway loop.</summary>
    private const int MaxChainDepth = 64;
    private const int MaxStepsPerRun = 20000;

    /// <summary>Event for every progress step; subscribers marshal to the UI thread as needed.</summary>
    public static event Action<RunEvent>? RunEvent;

    private sealed class RunHandle(CancellationTokenSource cts, string runId)
    {
        public CancellationTokenSource Source { get; } = cts;
        public string RunId { get; } = runId;
    }

    internal static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    internal static void Emit(RunEvent runEvent)
    {
        try
        {
            RunEvent?.Invoke(runEvent);
        }
        catch
        {
            // A broken subscriber must never abort a run.
        }
    }

    public static bool IsRunning(string shortcutId)
    {
        lock (Gate) return ActiveRuns.ContainsKey(shortcutId);
    }

    /// <summary>Cancel an active run (and, through the linked tokens, every run it started).</summary>
    public static bool Cancel(string shortcutId)
    {
        lock (Gate)
        {
            if (!ActiveRuns.TryGetValue(shortcutId, out var handle)) return false;
            try { handle.Source.Cancel(); } catch { }
            return true;
        }
    }

    /// <summary>Cancel one run by id (the run details panel's stop button).</summary>
    public static bool CancelRun(string runId)
    {
        lock (Gate)
        {
            var handle = ActiveRuns.Values.FirstOrDefault(h => h.RunId == runId);
            if (handle == null) return false;
            try { handle.Source.Cancel(); } catch { }
            return true;
        }
    }

    public static Task<RunResult> RunAsync(ShortcutDefinition shortcut, RunServices services) =>
        RunAsync(shortcut, services, new RunOptions());

    public static Task<RunResult> RunAsync(ShortcutDefinition shortcut, RunServices services,
        RunOptions options)
    {
        // ---- UI 亲和门 ----
        // 执行器会触碰 UI 亲和对象：TopLevel 剪贴板（读 .Clipboard 属性本身就会触发
        // Avalonia 12 的线程检查）、确认对话框、宿主组件界面。入口决定线程：编辑器 /
        // 卡片 / 面板本来就在 UI 线程上，一切正常；但全局热键从后台线程启动，整条执行
        // 链都跑在池线程上 —— 第一个 UI 亲和操作就会抛出跨线程异常，动作虽然已经生效
        // （这就是"明明能正常运行却也报错"的原因），catch-all 却把这次运行标记为失败。
        // 只要宿主应用在运行（存在 TopLevel），就把整条管线钉在 UI 线程上；wait /
        // runCommand / http 等长等待都是真异步，UI 不会卡顿。无头测试不提供 TopLevel，
        // 保持原来的池线程行为（在无消息循环的环境里向 UI 线程派发会死锁）。
        if (services.TopLevel != null && !Dispatcher.UIThread.CheckAccess())
        {
            return Dispatcher.UIThread.InvokeAsync(() => RunCore(shortcut, services, options));
        }

        return RunCore(shortcut, services, options);
    }

    private static async Task<RunResult> RunCore(ShortcutDefinition shortcut, RunServices services,
        RunOptions options)
    {
        var runId = options.RunId ?? NewRunId();

        // The graph is snapshotted before anything runs: editing a node while the shortcut is
        // executing must not change what the running instance does.
        var snapshot = shortcut.Clone();
        var graph = snapshot.EffectiveGraph.Clone();

        CancellationTokenSource cts;
        lock (Gate)
        {
            if (options.ParentRunId == null && ActiveRuns.ContainsKey(snapshot.Id))
            {
                Emit(new RunEvent(runId, null, snapshot.Id, snapshot.Name, RunEventKind.Busy));
                return RunResult.BusyResult(runId);
            }

            cts = options.CancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken)
                : new CancellationTokenSource();

            if (options.ParentRunId == null)
            {
                ActiveRuns[snapshot.Id] = new RunHandle(cts, runId);
            }
        }

        var budget = options.Budget ?? new RunBudget(MaxStepsPerRun, StandardStepTimeout * 30);
        var effectiveOptions = new RunOptions
        {
            RunId = runId,
            ParentRunId = options.ParentRunId,
            CancellationToken = cts.Token,
            Budget = budget,
            Inputs = options.Inputs,
            Confirmed = options.Confirmed,
            Depth = options.Depth,
            StepTimeout = options.StepTimeout,
        };

        var context = new RunContext(snapshot, graph, services, effectiveOptions, budget);
        var trace = RunHistory.Begin(runId, options.ParentRunId, snapshot.Id, snapshot.Name);

        try
        {
            // Confirmation happens exactly once, at the single run entry point — no matter
            // whether the run came from a card, the panel, the editor, a hotkey or a
            // runShortcut step.
            if ((snapshot.ConfirmBeforeRun || options.RequireConfirm) && !options.Confirmed)
            {
                if (!await context.ConfirmAsync(Loc.Format("Shortcuts_ConfirmRunMessage", snapshot.Name))
                        .ConfigureAwait(true))
                {
                    context.Raise(RunEventKind.Cancelled, success: false);
                    RunHistory.Complete(trace, "cancelled", null);
                    return RunResult.CancelledResult(runId, new Dictionary<string, string>(),
                        new Dictionary<string, NodeOutput>());
                }
            }

            context.Raise(RunEventKind.Started);

            var start = context.Graph.Start;
            if (start == null) throw new ActionFailedException("NoStart", null, null, Loc.Get("Shortcuts_Error_NoStart"));

            var report = WorkflowValidator.Validate(context.Graph, snapshot, forRun: true);
            if (report.HasErrors)
            {
                throw new ActionFailedException("InvalidGraph", null, null,
                    report.FirstErrorText ?? Loc.Get("Shortcuts_Error_InvalidGraph"));
            }

            PublishInputs(context, start, snapshot);

            var first = context.Graph.NextNode(start.Id, WorkflowNodeTypes.Out);
            if (first != null)
            {
                await ExecuteChainAsync(first, context, 0, null).ConfigureAwait(true);
            }

            context.Raise(RunEventKind.Finished);
            RunHistory.Complete(trace, "success", null);
            return RunResult.Ok(runId, context.FlattenOutputs(), context.AllOutputs, context.ReturnValue);
        }
        catch (OperationCanceledException)
        {
            context.Raise(RunEventKind.Cancelled, success: false);
            RunHistory.Complete(trace, "cancelled", null);
            return RunResult.CancelledResult(runId, context.FlattenOutputs(), context.AllOutputs);
        }
        catch (ActionFailedException ex)
        {
            context.Raise(RunEventKind.Finished, ex.NodeId, ex.Message, success: false);
            RunHistory.Complete(trace, "failed", ex.Message);
            return RunResult.Failed(runId, ex.Message, context.FlattenOutputs(), context.AllOutputs,
                ex.Code, ex.NodeId);
        }
        catch (Exception ex)
        {
            context.Raise(RunEventKind.Finished, null, ex.Message, success: false);
            RunHistory.Complete(trace, "failed", ex.Message);
            return RunResult.Failed(runId, ex.Message, context.FlattenOutputs(), context.AllOutputs,
                "Unhandled", null);
        }
        finally
        {
            lock (Gate)
            {
                if (ActiveRuns.TryGetValue(snapshot.Id, out var current) &&
                    ReferenceEquals(current.Source, cts))
                {
                    ActiveRuns.Remove(snapshot.Id);
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>Expose the shortcut's declared inputs as start-node output ports.</summary>
    private static void PublishInputs(RunContext context, WorkflowNode start, ShortcutDefinition shortcut)
    {
        if (shortcut.InputFields is not { Count: > 0 }) return;

        foreach (var field in shortcut.InputFields)
        {
            var value = context.Input(field.Key);
            if (value == null)
            {
                value = field.DefaultValue != null
                    ? RunValue.FromText(field.DefaultValue)
                    : RunValue.Null;
            }

            if (field.Required && value.IsNull)
            {
                throw new ActionFailedException("InputMissing", start, field.Key,
                    Loc.Format("Shortcuts_Error_InputMissing", field.Name));
            }

            context.SetOutput(start.Id, field.Key, value);
        }
    }

    /// <summary>
    /// Run one chain from <paramref name="nodeId"/> until it dead-ends, hits a control signal,
    /// or walks into an enclosing loop's done-path (<paramref name="boundary"/>).
    /// </summary>
    private static async Task<FlowSignal> ExecuteChainAsync(string? nodeId, RunContext context, int depth,
        HashSet<string>? boundary)
    {
        if (depth > MaxChainDepth)
            throw new ActionFailedException("TooDeep", null, null, Loc.Get("Shortcuts_Error_TooDeep"));

        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (!string.IsNullOrEmpty(nodeId))
        {
            context.Token.ThrowIfCancellationRequested();

            // Reaching the loop's done successor from inside its own body means the iteration
            // is over — running it here would execute that node once per iteration.
            if (boundary != null && boundary.Contains(nodeId)) return FlowSignal.LeftBody;

            var node = context.Graph.Find(nodeId);
            if (node == null) return FlowSignal.None;

            // A node revisited inside the same chain can only be a cycle: the graph has no
            // implicit "next", so re-entering means the user wired execution back on itself.
            if (!visited.Add(node.Id))
                throw new ActionFailedException("Cycle", node, null, Loc.Get("Shortcuts_Error_Cycle"));

            var definition = ActionCatalog.Get(node.Type);
            if (definition == null)
            {
                context.BeginStep();
                context.Raise(RunEventKind.StepStarted, node.Id);
                var message = Loc.Format("Shortcuts_Error_UnknownAction", node.Type);
                context.Raise(RunEventKind.StepFailed, node.Id, message, success: false);
                throw new ActionFailedException("UnknownType", node, null, message);
            }

            if (!context.BeginStep())
                throw new ActionFailedException("BudgetExhausted", node, null, Loc.Get("Shortcuts_Error_TooManySteps"));

            context.Raise(RunEventKind.StepStarted, node.Id);
            var started = Stopwatch.GetTimestamp();
            string? nextPort;

            try
            {
                var (port, signal) = await ExecuteNodeAsync(node, context, depth, boundary).ConfigureAwait(true);
                if (signal != FlowSignal.None)
                {
                    RunHistory.RecordStep(context, node, started, null, null);
                    return signal;
                }

                // Control-flow nodes are steps too: the run details panel must show the branch
                // that was taken and the loop iteration, not just the leaf actions.
                if (WorkflowNodeTypes.IsControlFlow(node.Type))
                    RunHistory.RecordStep(context, node, started, null, null);

                nextPort = port;
            }
            catch (ActionFailedException ex)
            {
                RunHistory.RecordStep(context, node, started, null, ex.Message);
                context.Raise(RunEventKind.StepFailed, node.Id, ex.Message, success: false);
                throw;
            }
            catch (OperationCanceledException)
            {
                RunHistory.RecordStep(context, node, started, null, Loc.Get("Shortcuts_Cancelled"));
                throw;
            }
            catch (Exception ex)
            {
                RunHistory.RecordStep(context, node, started, null, ex.Message);
                context.Raise(RunEventKind.StepFailed, node.Id, ex.Message, success: false);
                throw new ActionFailedException("StepFailed", node, null, ex.Message);
            }

            nodeId = nextPort == null ? null : context.Graph.NextNode(node.Id, nextPort);
        }

        return FlowSignal.None;
    }

    /// <summary>
    /// Execute one node. Returns the output port whose link should be followed next (null when
    /// the chain ends here), plus an optional control signal for containers.
    /// </summary>
    private static async Task<(string? Port, FlowSignal Signal)> ExecuteNodeAsync(
        WorkflowNode node, RunContext context, int depth, HashSet<string>? boundary)
    {
        switch (node.Type)
        {
            case WorkflowNodeTypes.Start:
                return (WorkflowNodeTypes.Out, FlowSignal.None);

            case WorkflowNodeTypes.Note:
                return (null, FlowSignal.None);

            case WorkflowNodeTypes.Break:
                context.Raise(RunEventKind.StepFinished, node.Id, Loc.Get("Shortcuts_Action_break"));
                return (null, FlowSignal.Break);

            case WorkflowNodeTypes.Continue:
                context.Raise(RunEventKind.StepFinished, node.Id, Loc.Get("Shortcuts_Action_continue"));
                return (null, FlowSignal.Continue);

            case WorkflowNodeTypes.End:
                context.Raise(RunEventKind.StepFinished, node.Id, Loc.Get("Shortcuts_Action_end"));
                return (null, FlowSignal.Stop);

            case WorkflowNodeTypes.Return:
            {
                context.ReturnValue = context.Optional(node, "value");
                context.Raise(RunEventKind.StepFinished, node.Id, context.ReturnValue.DisplayText);
                return (null, FlowSignal.Return);
            }

            case WorkflowNodeTypes.Branch:
            {
                var left = context.Value(node, "left");
                var right = context.Optional(node, "right");
                var op = context.Text(node, "op");
                var ignoreCase = context.Bool(node, "ignoreCase");
                var taken = EvaluateCondition(left, op, right, ignoreCase);
                var port = taken ? WorkflowNodeTypes.TruePort : WorkflowNodeTypes.FalsePort;
                context.Raise(RunEventKind.StepFinished, node.Id,
                    Loc.Get(taken ? "Shortcuts_Port_True" : "Shortcuts_Port_False"), portId: port);
                return (port, FlowSignal.None);
            }

            case WorkflowNodeTypes.Switch:
            {
                var value = context.Value(node, "value");
                var ignoreCase = context.Bool(node, "ignoreCase");
                var port = WorkflowNodeTypes.DefaultPort;

                foreach (var branch in node.EffectiveCases)
                {
                    if (branch.Value.Length == 0) continue;
                    if (!CompareText(value.ToText(), branch.Value, ignoreCase)) continue;
                    port = branch.Id;
                    break;
                }

                context.Raise(RunEventKind.StepFinished, node.Id, value.DisplayText, portId: port);
                return (port, FlowSignal.None);
            }

            case WorkflowNodeTypes.SetVar:
            {
                var name = context.Text(node, "name").Trim();
                if (name.Length == 0) name = "result";
                if (context.IsReservedName(name))
                {
                    throw new ActionFailedException("ReservedName", node, "name",
                        Loc.Format("Shortcuts_Error_ReservedName", name));
                }

                var value = context.Optional(node, "value");
                context.SetVariable(name, value);
                context.SetOutputs(node, new Dictionary<string, RunValue> { ["value"] = value });
                context.Raise(RunEventKind.StepFinished, node.Id, $"{name} = {value.DisplayText}");
                return (WorkflowNodeTypes.Out, FlowSignal.None);
            }

            case WorkflowNodeTypes.Collect:
            {
                var value = context.Optional(node, "value");
                context.Collect(value, node);
                var collected = context.CurrentFrame?.Collected.ToList() ?? [];
                context.SetOutputs(node, new Dictionary<string, RunValue>
                {
                    ["list"] = RunValue.FromList(collected),
                });
                context.Raise(RunEventKind.StepFinished, node.Id,
                    Loc.Format("Shortcuts_Output_Collected", collected.Count));
                return (WorkflowNodeTypes.Out, FlowSignal.None);
            }

            case WorkflowNodeTypes.MergeValue:
            {
                RunValue? found = null;
                foreach (var key in new[] { "a", "b", "c", "d" })
                {
                    if (!node.Params.ContainsKey(key) && context.Graph.IncomingData(node.Id, key) == null)
                        continue;

                    // A slot fed by the branch that did NOT run must be skipped, not evaluated:
                    // merge exists precisely to pick whichever side produced a value.
                    RunValue candidate;
                    try
                    {
                        candidate = context.Optional(node, key);
                    }
                    catch (ActionFailedException ex) when (ex.Code == "SourceNotExecuted")
                    {
                        continue;
                    }

                    if (candidate.IsNull) continue;
                    if (found != null)
                    {
                        throw new ActionFailedException("MergeConflict", node, key,
                            Loc.Get("Shortcuts_Error_MergeConflict"));
                    }

                    found = candidate;
                }

                if (found == null)
                {
                    throw new ActionFailedException("MergeEmpty", node, null,
                        Loc.Get("Shortcuts_Error_MergeEmpty"));
                }

                context.SetOutputs(node, new Dictionary<string, RunValue> { ["value"] = found });
                context.Raise(RunEventKind.StepFinished, node.Id, found.DisplayText);
                return (WorkflowNodeTypes.Out, FlowSignal.None);
            }

            case WorkflowNodeTypes.ForLoop:
            {
                var count = (int)Math.Clamp(context.Number(node, "count"), 0, 1000);
                var signal = await RunLoopAsync(node, context, depth, count, null, "index").ConfigureAwait(true);
                if (signal != FlowSignal.None) return (null, signal);
                return (WorkflowNodeTypes.DonePort, FlowSignal.None);
            }

            case WorkflowNodeTypes.WhileLoop:
            {
                var max = (int)Math.Clamp(context.Number(node, "max"), 1, 1000);
                var signal = await RunWhileAsync(node, context, depth, max).ConfigureAwait(true);
                if (signal != FlowSignal.None) return (null, signal);
                return (WorkflowNodeTypes.DonePort, FlowSignal.None);
            }

            case WorkflowNodeTypes.ForEach:
            {
                var items = ReadIterationItems(node, context);
                var varName = context.Text(node, "var").Trim();
                if (varName.Length == 0) varName = "item";

                var signal = await RunLoopAsync(node, context, depth, items.Count, items, varName)
                    .ConfigureAwait(true);
                if (signal != FlowSignal.None) return (null, signal);
                return (WorkflowNodeTypes.DonePort, FlowSignal.None);
            }

            case "comment":
            {
                var note = context.Text(node, "note");
                context.SetOutput(node.Id, WorkflowNodeTypes.Out, RunValue.FromText(note));
                context.Raise(RunEventKind.StepFinished, node.Id, note);
                return (WorkflowNodeTypes.Out, FlowSignal.None);
            }

            default:
                return await ExecuteActionAsync(node, context).ConfigureAwait(true);
        }
    }

    private static async Task<(string? Port, FlowSignal Signal)> ExecuteActionAsync(
        WorkflowNode node, RunContext context)
    {
        var executor = ActionExecutors.Find(node.Type);
        if (executor == null)
        {
            throw new ActionFailedException("UnknownType", node, null,
                Loc.Format("Shortcuts_Error_UnknownAction", node.Type));
        }

        var timeout = context.StepTimeout ?? (node.Type == "wait" ? WaitStepTimeout : StandardStepTimeout);
        var previous = context.Token;
        using var stepCts = CancellationTokenSource.CreateLinkedTokenSource(previous);
        stepCts.CancelAfter(timeout);
        context.SetActiveToken(stepCts.Token);

        var started = Stopwatch.GetTimestamp();
        ActionResult result;
        try
        {
            // Awaited directly (not through WaitAsync): when the timeout cancels the token the
            // executor unwinds and releases its resources before the failure is reported.
            result = await executor.ExecuteAsync(node.AsAction, context).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!previous.IsCancellationRequested)
        {
            RunHistory.RecordStep(context, node, started, null, Loc.Get("Shortcuts_Error_Timeout"));
            throw new ActionFailedException("Timeout", node, null, Loc.Get("Shortcuts_Error_Timeout"));
        }
        catch (ActionFailedException ex)
        {
            RunHistory.RecordStep(context, node, started, null, ex.Message);
            throw;
        }
        finally
        {
            context.SetActiveToken(previous);
        }

        if (result.Error is { } error)
        {
            // The outputs are recorded even when the run stops: a 404 body is exactly what an
            // error handler needs to see.
            context.SetOutputs(node, result.Outputs);
            RunHistory.RecordStep(context, node, started, result.Outputs, error.Message);

            // The chain raises StepFailed once, for every failure path (routed or not), so a
            // listener never sees the same failure twice.
            if (node.ErrorPolicy == ErrorPolicy.Route &&
                context.Graph.NextNode(node.Id, WorkflowNodeTypes.ErrorPort) != null)
            {
                return (WorkflowNodeTypes.ErrorPort, FlowSignal.None);
            }

            throw new ActionFailedException(error.Code, node, error.ParamKey, error.Message);
        }

        context.SetOutputs(node, result.Outputs);
        RunHistory.RecordStep(context, node, started, result.Outputs, null);

        context.Raise(RunEventKind.StepFinished, node.Id,
            result.Summary ?? DescribeOutputs(node, result.Outputs), portId: WorkflowNodeTypes.Out);

        return (WorkflowNodeTypes.Out, FlowSignal.None);
    }

    private static string? DescribeOutputs(WorkflowNode node, IReadOnlyDictionary<string, RunValue>? outputs)
    {
        if (outputs == null || outputs.Count == 0) return null;
        var port = WorkflowNodeTypes.DefaultDataPort(node);
        return port != null && outputs.TryGetValue(port, out var value) ? value.DisplayText : null;
    }

    private static IReadOnlyList<RunValue> ReadIterationItems(WorkflowNode node, RunContext context)
    {
        var resolution = context.Require(node, "list");
        if (resolution.Value.Type == DataType.List) return resolution.Value.AsList();

        // Legacy documents stored the list as separated text; keep honouring them.
        var separator = context.Text(node, "separator") switch
        {
            "comma" => ',',
            "space" => ' ',
            _ => '\n',
        };

        return SplitList(resolution.Value.ToText(), separator).Select(RunValue.FromText).ToList();
    }

    /// <summary>Shared for/forEach body loop: frames, per-iteration output reset, done boundary.</summary>
    private static async Task<FlowSignal> RunLoopAsync(WorkflowNode loop, RunContext context, int depth,
        int count, IReadOnlyList<RunValue>? items, string itemVariable)
    {
        var body = context.Graph.NextNode(loop.Id, WorkflowNodeTypes.BodyPort);
        if (body == null)
        {
            context.Raise(RunEventKind.StepFinished, loop.Id, Loc.Format("Shortcuts_Unit_Times", count));
            return FlowSignal.None;
        }

        var bodyNodes = BodyPath(context.Graph, loop);
        var donePath = DonePath(context.Graph, loop);

        var boundary = new HashSet<string>(donePath, StringComparer.Ordinal);
        var frame = context.PushFrame(loop, bodyNodes, itemVariable);
        try
        {
            for (var i = 0; i < count; i++)
            {
                context.Token.ThrowIfCancellationRequested();
                frame.Index = i;
                frame.Item = items != null && i < items.Count ? items[i] : null;

                context.ResetFrameOutputs(frame);

                var signal = await ExecuteChainAsync(body, context, depth + 1, boundary).ConfigureAwait(true);
                switch (signal)
                {
                    case FlowSignal.Stop:
                    case FlowSignal.Return:
                        return signal;
                    case FlowSignal.Break:
                        i = count;
                        break;
                }
            }
        }
        finally
        {
            context.PopFrame();
        }

        context.SetOutput(loop.Id, WorkflowNodeTypes.CollectedPort, RunValue.FromList(frame.Collected));
        context.Raise(RunEventKind.StepFinished, loop.Id, Loc.Format("Shortcuts_Unit_Times", count));
        return FlowSignal.None;
    }

    private static async Task<FlowSignal> RunWhileAsync(WorkflowNode loop, RunContext context, int depth, int max)
    {
        var body = context.Graph.NextNode(loop.Id, WorkflowNodeTypes.BodyPort);
        var donePath = DonePath(context.Graph, loop);
        var boundary = new HashSet<string>(donePath, StringComparer.Ordinal);
        var bodyNodes = body == null ? [] : BodyPath(context.Graph, loop);
        var frame = context.PushFrame(loop, bodyNodes, "item");

        var iterations = 0;
        var stillTrue = false;
        try
        {
            while (iterations < max)
            {
                context.Token.ThrowIfCancellationRequested();

                var left = context.Value(loop, "left");
                var right = context.Optional(loop, "right");
                var op = context.Text(loop, "op");
                var ignoreCase = context.Bool(loop, "ignoreCase");
                if (!EvaluateCondition(left, op, right, ignoreCase))
                {
                    stillTrue = false;
                    break;
                }

                stillTrue = true;
                frame.Index = iterations;
                iterations++;
                if (body == null) continue;

                context.ResetFrameOutputs(frame);
                var signal = await ExecuteChainAsync(body, context, depth + 1, boundary).ConfigureAwait(true);
                if (signal is FlowSignal.Stop or FlowSignal.Return) return signal;
                if (signal == FlowSignal.Break) { stillTrue = false; break; }
            }
        }
        finally
        {
            context.PopFrame();
        }

        // Reaching the cap while the condition still holds is a failure, not a quiet success:
        // the workflow did not do what it says it does.
        if (stillTrue && iterations >= max)
        {
            context.SetOutput(loop.Id, WorkflowNodeTypes.CollectedPort, RunValue.FromList(frame.Collected));
            throw new ActionFailedException("IterationLimitExceeded", loop, "max",
                Loc.Format("Shortcuts_Error_IterationLimit", max));
        }

        context.SetOutput(loop.Id, WorkflowNodeTypes.CollectedPort, RunValue.FromList(frame.Collected));
        context.Raise(RunEventKind.StepFinished, loop.Id, Loc.Format("Shortcuts_Unit_Times", iterations));
        return FlowSignal.None;
    }

    /// <summary>Nodes reachable from the loop's done port (they must not be cleared per iteration).</summary>
    private static HashSet<string> DonePath(WorkflowGraph graph, WorkflowNode loop)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var link in graph.Outgoing(loop.Id, WorkflowNodeTypes.DonePort))
        {
            if (link.IsData) continue;
            if (seen.Add(link.To)) queue.Enqueue(link.To);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var link in graph.Links.Where(l => !l.IsData && l.From == id))
            {
                if (seen.Add(link.To)) queue.Enqueue(link.To);
            }
        }

        return seen;
    }

    /// <summary>Nodes reachable from the loop's body port, minus the done path.</summary>
    private static HashSet<string> BodyPath(WorkflowGraph graph, WorkflowNode loop)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var link in graph.Outgoing(loop.Id, WorkflowNodeTypes.BodyPort))
        {
            if (link.IsData) continue;
            if (seen.Add(link.To)) queue.Enqueue(link.To);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var link in graph.Links.Where(l => !l.IsData && l.From == id))
            {
                if (seen.Add(link.To)) queue.Enqueue(link.To);
            }
        }

        seen.ExceptWith(DonePath(graph, loop));
        return seen;
    }

    internal static List<string> SplitList(string text, char separator)
    {
        var items = new List<string>();
        if (string.IsNullOrEmpty(text)) return items;

        foreach (var part in text.Split(separator))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) items.Add(trimmed);
        }

        return items;
    }

    private static bool CompareText(string left, string right, bool ignoreCase) =>
        string.Equals(left, right, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Shared typed comparison used by <c>branch</c> and <c>whileLoop</c>. Ordering comparisons
    /// require numbers: falling back to a lexicographic compare made "10" &lt; "9" true.
    /// </summary>
    internal static bool EvaluateCondition(RunValue left, string op, RunValue right, bool ignoreCase)
    {
        switch (op)
        {
            case "isEmpty": return left.IsEmpty;
            case "isNotEmpty": return !left.IsEmpty;
            case "isTrue": return left.TryGetBoolean(out var t) && t;
            case "isFalse": return left.TryGetBoolean(out var f) && !f;

            case "equals":
            case "notEquals":
            {
                var equal = AreEqual(left, right, ignoreCase);
                return op == "equals" ? equal : !equal;
            }

            case "contains":
                return right.ToText().Length == 0 ||
                       left.ToText().Contains(right.ToText(),
                           ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "startsWith":
                return left.ToText().StartsWith(right.ToText(),
                    ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "endsWith":
                return left.ToText().EndsWith(right.ToText(),
                    ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

            case "greater":
            case "less":
            case "greaterOrEqual":
            case "lessOrEqual":
            {
                if (!left.TryGetNumber(out var l) || !right.TryGetNumber(out var r))
                {
                    throw new ActionFailedException("ComparisonNotNumeric",
                        null, null, Loc.Get("Shortcuts_Error_CompareNotNumber"));
                }

                return op switch
                {
                    "greater" => l > r,
                    "less" => l < r,
                    "greaterOrEqual" => l >= r,
                    _ => l <= r,
                };
            }

            default:
                throw new ActionFailedException("UnknownOperator", null, null,
                    Loc.Format("Shortcuts_Error_UnknownOperator", op));
        }
    }

    private static bool AreEqual(RunValue left, RunValue right, bool ignoreCase)
    {
        // Numeric and boolean pairs compare by value, so 3 == "3.0" and true == "true".
        if (left.TryGetNumber(out var ln) && right.TryGetNumber(out var rn) &&
            left.Type is not (DataType.Text or DataType.Path) &&
            right.Type is not (DataType.Text or DataType.Path))
        {
            return Math.Abs(ln - rn) < 1e-9;
        }

        if (left.Type == DataType.Boolean && right.TryGetBoolean(out var rb)) return left.Boolean == rb;
        if (right.Type == DataType.Boolean && left.TryGetBoolean(out var lb)) return lb == right.Boolean;

        if (left.Type == DataType.DateTime && right.TryGetDate(out var rd)) return left.Date == rd;
        if (right.Type == DataType.DateTime && left.TryGetDate(out var ld)) return ld == right.Date;

        return CompareText(left.ToText(), right.ToText(), ignoreCase);
    }
}
