using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shortcuts.Models;

/// <summary>
/// A ComfyUI-style execution graph: nodes carry parameters, links carry control flow
/// (exec ports) and data (a node's typed output feeding another node's parameter).
/// <para>
/// The v1 model was a nested list (<c>repeat</c>/<c>if</c> owned child lists), which could only
/// ever express "run these in order, nested". A graph makes real branching and looping possible:
/// a loop node owns a <c>body</c> port and a <c>done</c> port, the body chain can branch, jump
/// back through the loop again, or terminate early with <c>break</c>/<c>continue</c>.
/// </para>
/// </summary>
public sealed class WorkflowGraph
{
    public List<WorkflowNode> Nodes { get; set; } = [];

    public List<WorkflowLink> Links { get; set; } = [];

    private Dictionary<string, WorkflowNode>? index;

    [JsonIgnore]
    public WorkflowNode? Start => Nodes.FirstOrDefault(n => n.Type == WorkflowNodeTypes.Start);

    public WorkflowNode? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        index ??= BuildIndex();
        return index.GetValueOrDefault(id);
    }

    /// <summary>All links leaving a node through one port (exec ports should have at most one).</summary>
    public IEnumerable<WorkflowLink> Outgoing(string nodeId, string port) =>
        Links.Where(l => l.From == nodeId && string.Equals(l.FromPort, port, StringComparison.Ordinal));

    public WorkflowLink? FirstOutgoing(string nodeId, string port) =>
        Outgoing(nodeId, port).FirstOrDefault();

    /// <summary>The exec link to follow after a node finishes on <paramref name="port"/>.</summary>
    public string? NextNode(string nodeId, string port) => FirstOutgoing(nodeId, port)?.To;

    /// <summary>The data link feeding one parameter of a node, if any.</summary>
    public WorkflowLink? IncomingData(string nodeId, string paramKey) =>
        Links.FirstOrDefault(l =>
            l.To == nodeId &&
            l.Kind == LinkKinds.Data &&
            string.Equals(l.ToPort, paramKey, StringComparison.Ordinal));

    /// <summary>Every data link that starts at one output port of a node.</summary>
    public IEnumerable<WorkflowLink> DataFrom(string nodeId, string port) =>
        Links.Where(l => l.From == nodeId && l.Kind == LinkKinds.Data &&
                         string.Equals(l.FromPort, port, StringComparison.Ordinal));

    /// <summary>
    /// Connect a node's default data output to one parameter of another node, replacing whatever
    /// fed that parameter before. Returns the created link (or null when the source has no data
    /// output at all).
    /// </summary>
    public WorkflowLink? AddDataLink(string fromId, string toId, string paramKey, string? fromPort = null)
    {
        var from = Find(fromId);
        if (from == null) return null;

        var port = fromPort ?? WorkflowNodeTypes.DefaultDataPort(from);
        if (port == null) return null;

        Links.RemoveAll(l => l.To == toId && l.Kind == LinkKinds.Data &&
                             string.Equals(l.ToPort, paramKey, StringComparison.Ordinal));
        var link = WorkflowLink.Data(fromId, port, toId, paramKey);
        Links.Add(link);
        Invalidate();
        return link;
    }

    public void Invalidate() => index = null;

    public WorkflowGraph Clone()
    {
        var json = JsonSerializer.Serialize(this, DocumentJson.Options);
        return JsonSerializer.Deserialize<WorkflowGraph>(json, DocumentJson.Options) ?? new WorkflowGraph();
    }

    /// <summary>Drop links that point at nodes (or ports) that no longer exist.</summary>
    public int PruneDanglingLinks()
    {
        index = BuildIndex();
        var removed = Links.RemoveAll(l =>
        {
            var from = Find(l.From);
            var to = Find(l.To);
            if (from == null || to == null) return true;

            if (!WorkflowNodeTypes.HasOutput(from, l.FromPort)) return true;

            return l.Kind == LinkKinds.Data
                ? !WorkflowNodeTypes.IsBindableParam(to, l.ToPort)
                : !WorkflowNodeTypes.HasInput(to.Type);
        });
        return removed;
    }

    private Dictionary<string, WorkflowNode> BuildIndex()
    {
        var map = new Dictionary<string, WorkflowNode>(StringComparer.Ordinal);
        foreach (var node in Nodes)
        {
            if (string.IsNullOrEmpty(node.Id) || map.ContainsKey(node.Id)) continue;
            map[node.Id] = node;
        }
        return map;
    }
}

/// <summary>One executable node: a flat action (id/type/params) plus its canvas position.</summary>
public sealed class WorkflowNode
{
    public string Id { get; set; } = ShortcutDefinition.NewId();

    /// <summary>Action type id resolved through <see cref="ActionCatalog"/>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Canvas position in graph space (DIP).</summary>
    public double X { get; set; }

    public double Y { get; set; }

    public Dictionary<string, ShortcutValue> Params { get; set; } = [];

    /// <summary>
    /// Switch cases: each row is one exec output port with a stable id, so editing the list
    /// never silently re-points an existing link (the old fixed <c>case0..case3</c> + <c>|</c>
    /// joined list could not be edited at all).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<WorkflowCase>? Cases { get; set; }

    /// <summary>Per-node error strategy; null means the action's catalog default.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorPolicy? OnError { get; set; }

    /// <summary>Optional user label shown instead of the action title.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    private ShortcutAction? action;

    /// <summary>
    /// The executor-facing view of this node. Executors are written against
    /// <see cref="ShortcutAction"/>, and a node is exactly an action with a position — sharing
    /// the type keeps every executor and the parameter editor unchanged.
    /// </summary>
    [JsonIgnore]
    public ShortcutAction AsAction => action ??= new ShortcutAction { Id = Id, Type = Type, Params = Params };

    public void ResetActionCache() => action = null;

    /// <summary>Effective error strategy for this node.</summary>
    [JsonIgnore]
    public ErrorPolicy ErrorPolicy =>
        OnError ?? ActionCatalog.Get(Type)?.DefaultErrorPolicy ?? ErrorPolicy.Stop;

    public static WorkflowNode Create(ActionDefinition definition, double x, double y)
    {
        var action = ShortcutAction.Create(definition);
        var node = new WorkflowNode
        {
            Id = action.Id,
            Type = action.Type,
            Params = action.Params,
            X = x,
            Y = y,
        };

        if (definition.Type == WorkflowNodeTypes.Switch) node.Cases = WorkflowCase.CreateDefaults();
        return node;
    }

    public static WorkflowNode With(string type, double x, double y, params (string Key, string Value)[] values)
    {
        var node = Create(type, x, y);
        foreach (var (key, value) in values)
            node.Params[key] = ShortcutValue.Literal(value);
        return node;
    }

    /// <summary>Factory that tolerates unknown types (import of a newer document).</summary>
    public static WorkflowNode Create(string type, double x, double y) =>
        ActionCatalog.Get(type) is { } definition
            ? Create(definition, x, y)
            : new WorkflowNode { Type = type, X = x, Y = y };

    /// <summary>Switch cases of this node (never null for a switch; empty for anything else).</summary>
    public List<WorkflowCase> EffectiveCases =>
        Type == WorkflowNodeTypes.Switch ? Cases ??= WorkflowCase.CreateDefaults() : [];
}

/// <summary>One switch arm: a stable id (the port name) and the value it matches.</summary>
public sealed class WorkflowCase
{
    public string Id { get; set; } = ShortcutDefinition.NewId();

    public string Value { get; set; } = string.Empty;

    public static List<WorkflowCase> CreateDefaults() =>
    [
        new() { Id = "case_" + ShortcutDefinition.NewId()[..6], Value = string.Empty },
        new() { Id = "case_" + ShortcutDefinition.NewId()[..6], Value = string.Empty },
    ];
}

public static class LinkKinds
{
    /// <summary>Control flow: which node runs next.</summary>
    public const string Exec = "exec";

    /// <summary>Data: the source node's output value feeds a target parameter.</summary>
    public const string Data = "data";
}

/// <summary>One connection: <c>From:FromPort → To:ToPort</c>.</summary>
public sealed class WorkflowLink
{
    public string Id { get; set; } = ShortcutDefinition.NewId();

    public string From { get; set; } = string.Empty;

    /// <summary>Output port id on the source node (<c>out</c>, <c>true</c>, <c>body</c>, <c>text</c>, …).</summary>
    public string FromPort { get; set; } = WorkflowNodeTypes.Out;

    public string To { get; set; } = string.Empty;

    /// <summary>Input port: <c>in</c> for exec links, or the parameter key for data links.</summary>
    public string ToPort { get; set; } = WorkflowNodeTypes.In;

    /// <summary><see cref="LinkKinds.Exec"/> or <see cref="LinkKinds.Data"/>.</summary>
    public string Kind { get; set; } = LinkKinds.Exec;

    [JsonIgnore]
    public bool IsData => Kind == LinkKinds.Data;

    public static WorkflowLink Exec(string from, string fromPort, string to) => new()
    {
        From = from,
        FromPort = fromPort,
        To = to,
        ToPort = WorkflowNodeTypes.In,
        Kind = LinkKinds.Exec,
    };

    public static WorkflowLink Data(string from, string to, string paramKey) =>
        Data(from, WorkflowNodeTypes.Value, to, paramKey);

    public static WorkflowLink Data(string from, string fromPort, string to, string toPort) => new()
    {
        From = from,
        FromPort = string.IsNullOrEmpty(fromPort) ? WorkflowNodeTypes.Value : fromPort,
        To = to,
        ToPort = toPort,
        Kind = LinkKinds.Data,
    };
}

/// <summary>An output port a node can emit on.</summary>
/// <param name="Id">Port id written into <see cref="WorkflowLink.FromPort"/>.</param>
/// <param name="LabelKey">Locale key of the port label shown on the node card.</param>
/// <param name="IsData">True for a data port.</param>
/// <param name="DataType">Type carried by a data port.</param>
/// <param name="Display">Literal label used instead of the locale key (switch case values).</param>
public sealed record WorkflowPort(
    string Id,
    string LabelKey,
    bool IsData = false,
    DataType DataType = DataType.Null,
    string? Display = null);

/// <summary>
/// The graph vocabulary: port sets and the control-flow node definitions that the v1 tree
/// could not express. Action (leaf) nodes come from <see cref="ActionCatalog"/>; this class
/// owns the shape of every node's wiring so the runner, the canvas and the AI spec document
/// all agree.
/// </summary>
public static class WorkflowNodeTypes
{
    public const string Start = "start";
    public const string Branch = "branch";
    public const string Switch = "switch";
    public const string ForLoop = "forLoop";
    public const string WhileLoop = "whileLoop";
    public const string ForEach = "forEach";
    public const string Break = "break";
    public const string Continue = "continue";
    public const string End = "end";
    public const string Note = "note";
    public const string SetVar = "setVar";
    public const string Collect = "collect";
    public const string MergeValue = "mergeValue";
    public const string Return = "return";

    public const string In = "in";
    public const string Out = "out";
    public const string Value = "value";
    public const string ErrorPort = "error";
    public const string TruePort = "true";
    public const string FalsePort = "false";
    public const string BodyPort = "body";
    public const string DonePort = "done";
    public const string DefaultPort = "default";
    public const string CollectedPort = "collected";

    /// <summary>Reserved loop variable names; a setVar may not shadow them.</summary>
    public static readonly IReadOnlyList<string> ReservedNames = ["index", "item"];

    /// <summary>True for nodes that only route execution (no executor behind them).</summary>
    public static bool IsControlFlow(string type) => type is
        Start or Branch or Switch or ForLoop or WhileLoop or ForEach or Break or Continue or End or Note
        or Collect or MergeValue or Return;

    /// <summary>True for nodes whose single exec exit is <c>out</c> and that carry no ports.</summary>
    public static bool IsSimpleAction(string type) => !IsControlFlow(type) && type != SetVar;

    /// <summary>True for loop containers (they own a body/done pair and a loop frame).</summary>
    public static bool IsLoop(string type) => type is ForLoop or WhileLoop or ForEach;

    /// <summary>Type-only port table (spec document, import validation before a node exists).</summary>
    public static IReadOnlyList<WorkflowPort> Outputs(string type)
    {
        switch (type)
        {
            case Start:
                return [new(Out, "Shortcuts_Port_Out")];
            case Branch:
                return
                [
                    new(TruePort, "Shortcuts_Port_True"),
                    new(FalsePort, "Shortcuts_Port_False"),
                ];
            case Switch:
            {
                var ports = new List<WorkflowPort>
                {
                    new("case_0", "Shortcuts_Port_Case", Display: "case 1"),
                    new("case_1", "Shortcuts_Port_Case", Display: "case 2"),
                };
                ports.Add(new(DefaultPort, "Shortcuts_Port_Default"));
                return ports;
            }
            case ForLoop:
            case WhileLoop:
            case ForEach:
                return
                [
                    new(BodyPort, "Shortcuts_Port_Body"),
                    new(DonePort, "Shortcuts_Port_Done"),
                    new(CollectedPort, "Shortcuts_Port_Collected", IsData: true, DataType: DataType.List),
                ];
            case Break:
            case Continue:
            case End:
            case Note:
            case Return:
                return [];
            case Collect:
                return
                [
                    new(Out, "Shortcuts_Port_Out"),
                    new(ErrorPort, "Shortcuts_Port_Error"),
                    new("list", "Shortcuts_Port_List", IsData: true, DataType: DataType.List),
                ];
            case MergeValue:
                return
                [
                    new(Out, "Shortcuts_Port_Out"),
                    new(ErrorPort, "Shortcuts_Port_Error"),
                    new(Value, "Shortcuts_Port_Value", IsData: true, DataType: DataType.Any),
                ];
            case SetVar:
                return
                [
                    new(Out, "Shortcuts_Port_Out"),
                    new(ErrorPort, "Shortcuts_Port_Error"),
                    new(Value, "Shortcuts_Port_Value", IsData: true, DataType: DataType.Any),
                ];
            default:
                // Every catalog action: exec exit, error exit, then its declared data ports.
                return ActionCatalog.Get(type)?.Outputs
                           .Select(o => new WorkflowPort(o.Id, o.LabelKey, o.IsData, o.DataType))
                           .ToList()
                       ?? [];
        }
    }

    /// <summary>
    /// Full port table for a concrete node: switch cases come from the node, and a start node
    /// exposes one data port per declared shortcut input.
    /// </summary>
    public static IReadOnlyList<WorkflowPort> Outputs(WorkflowNode node, ShortcutDefinition? owner = null)
    {
        if (node.Type == Switch)
        {
            var ports = node.EffectiveCases
                .Select(c => new WorkflowPort(c.Id, "Shortcuts_Port_Case",
                    Display: string.IsNullOrEmpty(c.Value) ? "(case)" : c.Value))
                .ToList();
            ports.Add(new(DefaultPort, "Shortcuts_Port_Default"));
            return ports;
        }

        if (node.Type == Start && owner is { Inputs.Count: > 0 })
        {
            var ports = new List<WorkflowPort> { new(Out, "Shortcuts_Port_Out") };
            foreach (var field in owner.Inputs)
            {
                ports.Add(new WorkflowPort(field.Key, "Shortcuts_Port_Input",
                    IsData: true, DataType: field.DataType, Display: field.Name));
            }
            return ports;
        }

        return Outputs(node.Type);
    }

    public static bool HasOutput(string? type, string port) =>
        type != null && Outputs(type).Any(p => string.Equals(p.Id, port, StringComparison.Ordinal));

    public static bool HasOutput(WorkflowNode node, string port, ShortcutDefinition? owner = null) =>
        Outputs(node, owner).Any(p => string.Equals(p.Id, port, StringComparison.Ordinal));

    /// <summary>Whether the node accepts an incoming exec link.</summary>
    public static bool HasInput(string? type) => type is not (Start or Note);

    /// <summary>Whether the node exposes <paramref name="paramKey"/> as a bindable data input.</summary>
    public static bool HasParam(string? type, string paramKey) =>
        type != null && ActionCatalog.Get(type) is { } definition &&
        definition.Params.Any(p => string.Equals(p.Key, paramKey, StringComparison.Ordinal));

    /// <summary>Whether a data link may target this parameter.</summary>
    public static bool IsBindableParam(WorkflowNode node, string paramKey) =>
        ActionCatalog.Get(node.Type)?.Param(paramKey) is { Bindable: true };

    /// <summary>Bindable parameters — the data ports drawn on the left edge of a card.</summary>
    public static IReadOnlyList<ParamSpec> DataInputs(string type) =>
        ActionCatalog.Get(type)?.Params.Where(p => p.Bindable).ToList() ?? [];

    public static IReadOnlyList<ParamSpec> DataInputs(WorkflowNode node) => DataInputs(node.Type);

    /// <summary>
    /// Parameters currently shown for a node: a conditional parameter (find/replace, case
    /// sensitivity, …) disappears when its trigger value says it does not apply. The card, the
    /// port anchors and the editor all read this, so hiding a row can never desynchronise the
    /// port geometry.
    /// </summary>
    public static IReadOnlyList<ParamSpec> VisibleParams(WorkflowNode node) =>
        DataInputs(node)
            .Where(spec => spec.VisibleWhen == null ||
                           spec.VisibleWhen.IsVisible(key => node.Params.GetValueOrDefault(key)?.Text))
            .ToList();

    /// <summary>The port a bare data link from this node binds to (null when it has none).</summary>
    public static string? DefaultDataPort(WorkflowNode node, ShortcutDefinition? owner = null) =>
        Outputs(node, owner).FirstOrDefault(p => p.IsData && p.Id != ErrorPort)?.Id;

    /// <summary>Data type carried by one output port (Any when unknown).</summary>
    public static DataType OutputType(WorkflowNode node, string portId, ShortcutDefinition? owner = null) =>
        Outputs(node, owner).FirstOrDefault(p => p.Id == portId)?.DataType ?? DataType.Any;

    /// <summary>True when a data link from <paramref name="fromPort"/> may feed <paramref name="param"/>.</summary>
    public static bool IsTypeCompatible(DataType output, ParamSpec param) =>
        output == DataType.Any || param.DataType == DataType.Any ||
        output == param.DataType ||
        (output is DataType.Text or DataType.Path && param.DataType is DataType.Text or DataType.Path);
}
