using System.Collections.Concurrent;
using System.Diagnostics;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>One executed step, as shown by the run details panel.</summary>
/// <param name="Index">1-based order of execution within the run.</param>
/// <param name="NodeId">Node that ran.</param>
/// <param name="NodeType">Action type id.</param>
/// <param name="Title">Display title at the time of the run.</param>
/// <param name="Iteration">Loop iteration the step belonged to (-1 outside loops).</param>
/// <param name="Inputs">Resolved parameters (label → displayed value).</param>
/// <param name="Outputs">Declared data ports (port id → displayed value).</param>
/// <param name="DurationMs">Wall-clock duration.</param>
/// <param name="Error">Failure message, null on success.</param>
/// <param name="Summary">Localized one-line feedback reported by the executor.</param>
public sealed record RunStepRecord(
    int Index,
    string NodeId,
    string NodeType,
    string Title,
    int Iteration,
    IReadOnlyDictionary<string, string> Inputs,
    IReadOnlyDictionary<string, string> Outputs,
    long DurationMs,
    string? Error,
    string? Summary);

/// <summary>Everything one run did, keyed by <see cref="RunId"/>.</summary>
public sealed class RunTrace
{
    internal readonly List<RunStepRecord> StepList = [];

    public required string RunId { get; init; }

    public string? ParentRunId { get; init; }

    public required string ShortcutId { get; init; }

    public required string ShortcutName { get; init; }

    public DateTime StartedAt { get; } = DateTime.Now;

    public DateTime? FinishedAt { get; internal set; }

    /// <summary>running / success / failed / cancelled.</summary>
    public string Status { get; internal set; } = "running";

    public string? Error { get; internal set; }

    public IReadOnlyList<RunStepRecord> Steps => StepList;

    public TimeSpan? Duration => FinishedAt - StartedAt;
}

/// <summary>
/// Bounded in-memory log of recent runs. Bounded on purpose: a runaway loop must not be able to
/// grow the process while it fills the details panel.
/// </summary>
public static class RunHistory
{
    private const int MaxTraces = 24;
    private const int MaxStepsPerTrace = 2000;

    private static readonly object Gate = new();
    private static readonly LinkedList<RunTrace> Traces = new();
    private static readonly ConcurrentDictionary<string, RunTrace> ById = new(StringComparer.Ordinal);

    /// <summary>Raised after a trace changed (the details panel re-reads it).</summary>
    public static event Action<RunTrace>? TraceUpdated;

    public static RunTrace Begin(string runId, string? parentRunId, string shortcutId, string shortcutName)
    {
        var trace = new RunTrace
        {
            RunId = runId,
            ParentRunId = parentRunId,
            ShortcutId = shortcutId,
            ShortcutName = shortcutName,
        };

        lock (Gate)
        {
            Traces.AddFirst(trace);
            ById[runId] = trace;
            while (Traces.Count > MaxTraces)
            {
                var dropped = Traces.Last!.Value;
                Traces.RemoveLast();
                ById.TryRemove(dropped.RunId, out _);
            }
        }

        return trace;
    }

    public static void RecordStep(RunContext context, WorkflowNode node, long startedTimestamp,
        IReadOnlyDictionary<string, RunValue>? outputs, string? error)
    {
        if (!ById.TryGetValue(context.RunId, out var trace)) return;
        if (trace.StepList.Count >= MaxStepsPerTrace) return;

        var elapsed = Stopwatch.GetTimestamp() - startedTimestamp;
        var ms = (long)(elapsed * 1000.0 / Stopwatch.Frequency);

        var record = new RunStepRecord(
            trace.StepList.Count + 1,
            node.Id,
            node.Type,
            RunContext.NodeLabel(node),
            context.CurrentIteration,
            DescribeInputs(context, node),
            DescribeOutputs(node, outputs),
            ms,
            error,
            error == null ? null : error);

        lock (Gate) trace.StepList.Add(record);
        Notify(trace);
    }

    public static void Complete(RunTrace trace, string status, string? error)
    {
        trace.Status = status;
        trace.Error = error;
        trace.FinishedAt = DateTime.Now;
        Notify(trace);
    }

    public static RunTrace? Find(string? runId) =>
        runId != null && ById.TryGetValue(runId, out var trace) ? trace : null;

    /// <summary>Most recent trace of a shortcut (used by the editor's details panel).</summary>
    public static RunTrace? Latest(string shortcutId)
    {
        lock (Gate)
        {
            return Traces.FirstOrDefault(t => t.ShortcutId == shortcutId);
        }
    }

    public static IReadOnlyList<RunTrace> Recent(string shortcutId)
    {
        lock (Gate)
        {
            return Traces.Where(t => t.ShortcutId == shortcutId).ToList();
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Traces.Clear();
            ById.Clear();
        }
    }

    private static void Notify(RunTrace trace)
    {
        try
        {
            TraceUpdated?.Invoke(trace);
        }
        catch
        {
            // A broken subscriber must never abort a run.
        }
    }

    private static IReadOnlyDictionary<string, string> DescribeInputs(RunContext context, WorkflowNode node)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var definition = ActionCatalog.Get(node.Type);
        if (definition == null) return map;

        foreach (var spec in definition.Params)
        {
            if (!node.Params.ContainsKey(spec.Key) && context.Graph.IncomingData(node.Id, spec.Key) == null)
                continue;

            try
            {
                var resolution = context.ResolveParam(node, spec.Key);
                map[Loc.Get(spec.LabelKey)] = resolution.Value.DisplayText;
            }
            catch (ActionFailedException ex)
            {
                map[Loc.Get(spec.LabelKey)] = "!" + ex.Message;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> DescribeOutputs(WorkflowNode node,
        IReadOnlyDictionary<string, RunValue>? outputs)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outputs == null) return map;

        foreach (var port in WorkflowNodeTypes.Outputs(node).Where(p => p.IsData))
        {
            if (!outputs.TryGetValue(port.Id, out var value)) continue;
            map[port.Id] = value.DisplayText;
        }

        return map;
    }
}
