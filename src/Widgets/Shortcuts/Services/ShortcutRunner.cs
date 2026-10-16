using Avalonia.Controls;
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

public sealed record RunEvent(
    string ShortcutId,
    string ShortcutName,
    RunEventKind Kind,
    string? ActionId = null,
    string? Detail = null,
    int StepIndex = 0,
    int TotalSteps = 0,
    bool Success = true);

public sealed record RunResult(
    bool Success,
    bool Cancelled,
    bool Busy,
    string? Error,
    IReadOnlyDictionary<string, string> Outputs)
{
    public static RunResult Ok(IReadOnlyDictionary<string, string> outputs) =>
        new(true, false, false, null, outputs);

    public static RunResult CancelledResult(IReadOnlyDictionary<string, string> outputs) =>
        new(false, true, false, null, outputs);

    public static RunResult Failed(string error, IReadOnlyDictionary<string, string> outputs) =>
        new(false, false, false, error, outputs);

    public static readonly RunResult BusyResult = new(false, false, true, null,
        new Dictionary<string, string>());
}

/// <summary>Host services an action may use (captured from the launching widget/panel/editor).</summary>
public sealed class RunServices
{
    public IAppSettingsProvider? AppSettings { get; init; }
    public WidgetFactory? WidgetFactory { get; init; }
    public Func<TopLevel?>? TopLevelAccessor { get; init; }

    /// <summary>The window that may own confirm dialogs.</summary>
    public Window? Owner => TopLevelAccessor?.Invoke() as Window;

    public TopLevel? TopLevel => TopLevelAccessor?.Invoke();
}

/// <summary>Execution state for one run: outputs, variables and progress counters.</summary>
public sealed class RunContext
{
    public ShortcutDefinition Shortcut { get; }
    public RunServices Services { get; }
    public CancellationToken Token { get; }
    public Dictionary<string, string> Outputs { get; } = new(StringComparer.Ordinal);

    /// <summary>Most recent non-empty output ("result of previous step").</summary>
    public string? LastOutput { get; private set; }

    public int StepIndex { get; private set; }
    public int TotalSteps { get; }

    public RunContext(ShortcutDefinition shortcut, RunServices services, CancellationToken token)
    {
        Shortcut = shortcut;
        Services = services;
        Token = token;
        TotalSteps = Math.Max(1, shortcut.ActionCount);
    }

    // ---- parameter resolution -----------------------------------------------------

    /// <summary>Resolve a parameter: literal text, a magic-variable reference, or empty.</summary>
    public string Resolve(ShortcutAction action, string key)
    {
        if (!action.Params.TryGetValue(key, out var value) || value == null) return string.Empty;

        if (value.IsVariable)
        {
            if (value.SourceActionId == ValueKinds.LastResult)
                return LastOutput ?? string.Empty;
            return Outputs.GetValueOrDefault(value.SourceActionId!, string.Empty);
        }

        return value.Text ?? string.Empty;
    }

    /// <summary>Resolve with the catalog default when the parameter is empty.</summary>
    public string ResolveOrDefault(ShortcutAction action, string key)
    {
        var raw = Resolve(action, key);
        if (!string.IsNullOrWhiteSpace(raw)) return raw;

        var spec = ActionCatalog.Get(action.Type)?.Params.FirstOrDefault(p => p.Key == key);
        return spec?.DefaultValue ?? string.Empty;
    }

    public double ResolveNumber(ShortcutAction action, string key, double fallback)
    {
        var raw = ResolveOrDefault(action, key);
        return double.TryParse(raw, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
    }

    public double ResolveNumberClamped(ShortcutAction action, string key, double fallback)
    {
        var value = ResolveNumber(action, key, fallback);
        var spec = ActionCatalog.Get(action.Type)?.Params.FirstOrDefault(p => p.Key == key);
        if (spec == null) return value;
        return Math.Clamp(value, spec.Min, spec.Max);
    }

    // ---- outputs ------------------------------------------------------------------

    public void SetOutput(ShortcutAction action, string? value)
    {
        var text = value ?? string.Empty;
        Outputs[action.Id] = text;
        if (!string.IsNullOrEmpty(text)) LastOutput = text;
    }

    // ---- confirm ------------------------------------------------------------------

    /// <summary>Ask the user before a destructive step; without a window the step proceeds.</summary>
    public async Task<bool> ConfirmDestructiveAsync()
    {
        var owner = Services.Owner;
        if (owner == null) return true;

        try
        {
            return await DeskCanvas.Views.ConfirmDialog.ConfirmAsync(
                owner,
                Loc.Get("Shortcuts_ConfirmDestructive"),
                Loc.Get("Shortcuts_ConfirmRunOk"),
                Loc.Get("Shortcuts_Cancel"));
        }
        catch
        {
            return true;
        }
    }

    // ---- events -------------------------------------------------------------------

    internal void Raise(RunEventKind kind, string? actionId = null, string? detail = null, bool success = true)
    {
        ShortcutRunner.Emit(new RunEvent(Shortcut.Id, Shortcut.Name, kind, actionId, detail,
            StepIndex, TotalSteps, success));
    }

    internal void BeginStep() => StepIndex++;

    internal void RewindStep() => StepIndex = Math.Max(0, StepIndex - 1);
}

/// <summary>Raised by an executor (or the runner) when a step fails; stops the run.</summary>
public sealed class ActionFailedException(string message) : Exception(message);

/// <summary>
/// Sequential interpreter for a shortcut's action tree. Containers (repeat / if) are
/// expanded here; leaf actions go through <see cref="ActionExecutors"/>. Runs are keyed by
/// shortcut id — a shortcut cannot overlap itself; all runs are cancellable.
/// </summary>
public static class ShortcutRunner
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, CancellationTokenSource> ActiveRuns = new(StringComparer.Ordinal);
    private static readonly TimeSpan StandardStepTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WaitStepTimeout = TimeSpan.FromSeconds(610);

    /// <summary>Event for every progress step; subscribers marshal to the UI thread as needed.</summary>
    public static event Action<RunEvent>? RunEvent;

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

    /// <summary>Cancel an active run; returns true when there was one.</summary>
    public static bool Cancel(string shortcutId)
    {
        lock (Gate)
        {
            if (!ActiveRuns.TryGetValue(shortcutId, out var cts)) return false;
            try { cts.Cancel(); } catch { }
            return true;
        }
    }

    public static async Task<RunResult> RunAsync(ShortcutDefinition shortcut, RunServices services)
    {
        CancellationTokenSource cts;
        lock (Gate)
        {
            if (ActiveRuns.ContainsKey(shortcut.Id))
            {
                Emit(new RunEvent(shortcut.Id, shortcut.Name, RunEventKind.Busy));
                return RunResult.BusyResult;
            }

            cts = new CancellationTokenSource();
            ActiveRuns[shortcut.Id] = cts;
        }

        var context = new RunContext(shortcut, services, cts.Token);
        context.Raise(RunEventKind.Started);
        try
        {
            await ExecuteListAsync(shortcut.Actions, context);
            context.Raise(RunEventKind.Finished);
            return RunResult.Ok(context.Outputs);
        }
        catch (OperationCanceledException)
        {
            context.Raise(RunEventKind.Cancelled, success: false);
            return RunResult.CancelledResult(context.Outputs);
        }
        catch (ActionFailedException ex)
        {
            context.Raise(RunEventKind.Finished, detail: ex.Message, success: false);
            return RunResult.Failed(ex.Message, context.Outputs);
        }
        catch (Exception ex)
        {
            context.Raise(RunEventKind.Finished, detail: ex.Message, success: false);
            return RunResult.Failed(ex.Message, context.Outputs);
        }
        finally
        {
            lock (Gate)
            {
                if (ActiveRuns.TryGetValue(shortcut.Id, out var current) && ReferenceEquals(current, cts))
                    ActiveRuns.Remove(shortcut.Id);
            }
            cts.Dispose();
        }
    }

    private static async Task ExecuteListAsync(List<ShortcutAction> actions, RunContext context)
    {
        foreach (var action in actions)
        {
            context.Token.ThrowIfCancellationRequested();

            var definition = ActionCatalog.Get(action.Type);
            if (definition == null)
            {
                context.BeginStep();
                context.Raise(RunEventKind.StepStarted, action.Id);
                var message = Loc.Format("Shortcuts_Error_UnknownAction", action.Type);
                context.Raise(RunEventKind.StepFailed, action.Id, message, success: false);
                throw new ActionFailedException(message);
            }

            context.BeginStep();
            context.Raise(RunEventKind.StepStarted, action.Id);
            try
            {
                switch (action.Type)
                {
                    case "repeat":
                    {
                        var count = (int)Math.Clamp(context.ResolveNumber(action, "count", 1), 1, 100);
                        for (var i = 0; i < count; i++)
                        {
                            context.Token.ThrowIfCancellationRequested();
                            await ExecuteListAsync(action.Children, context);
                        }
                        context.SetOutput(action, Loc.Format("Shortcuts_Unit_Times", count));
                        context.Raise(RunEventKind.StepFinished, action.Id,
                            Loc.Format("Shortcuts_Unit_Times", count));
                        break;
                    }

                    case "if":
                    {
                        var taken = EvaluateIf(action, context);
                        await ExecuteListAsync(taken ? action.Children : action.ElseChildren, context);
                        context.Raise(RunEventKind.StepFinished, action.Id);
                        break;
                    }

                    case "comment":
                    {
                        var note = context.Resolve(action, "note");
                        context.SetOutput(action, note);
                        context.Raise(RunEventKind.StepFinished, action.Id, note);
                        break;
                    }

                    default:
                    {
                        var executor = ActionExecutors.Find(action.Type);
                        if (executor == null)
                            throw new ActionFailedException(Loc.Format("Shortcuts_Error_UnknownAction", action.Type));

                        var timeout = action.Type == "wait" ? WaitStepTimeout : StandardStepTimeout;
                        string? output;
                        try
                        {
                            output = await executor.ExecuteAsync(action, context)
                                .WaitAsync(timeout, context.Token);
                        }
                        catch (TimeoutException)
                        {
                            throw new ActionFailedException(Loc.Get("Shortcuts_Error_Timeout"));
                        }

                        context.SetOutput(action, output);
                        context.Raise(RunEventKind.StepFinished, action.Id, output);
                        break;
                    }
                }
            }
            catch (ActionFailedException ex)
            {
                context.Raise(RunEventKind.StepFailed, action.Id, ex.Message, success: false);
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Raise(RunEventKind.StepFailed, action.Id, ex.Message, success: false);
                throw new ActionFailedException(ex.Message);
            }
        }
    }

    private static bool EvaluateIf(ShortcutAction action, RunContext context)
    {
        var left = context.Resolve(action, "left");
        var op = context.ResolveOrDefault(action, "op");
        var right = context.Resolve(action, "right");

        switch (op)
        {
            case "isEmpty": return string.IsNullOrEmpty(left);
            case "isNotEmpty": return !string.IsNullOrEmpty(left);
            case "equals": return string.Equals(left, right, StringComparison.Ordinal);
            case "notEquals": return !string.Equals(left, right, StringComparison.Ordinal);
            case "contains": return right.Length == 0 || left.Contains(right, StringComparison.OrdinalIgnoreCase);
            case "startsWith": return left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
            case "endsWith": return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
            case "greater" or "less":
            {
                if (double.TryParse(left, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var l)
                    && double.TryParse(right, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var r))
                {
                    return op == "greater" ? l > r : l < r;
                }

                var cmp = string.Compare(left, right, StringComparison.CurrentCultureIgnoreCase);
                return op == "greater" ? cmp > 0 : cmp < 0;
            }
        }

        return false;
    }
}
