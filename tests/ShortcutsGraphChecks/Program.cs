// Headless regression suite for the shortcuts engine.
// Covers the contract the implementation plan pins down: typed ports, data vs exec links,
// reference integrity, loop frames and boundaries, error routing, cancellation and budgets,
// the action semantics, migration/import/spec round-trips and three end-to-end workflows.
// Usage: dotnet run --project tests/ShortcutsGraphChecks -c Release
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using DeskCanvas.Core;
using Shortcuts.Locales;
using Shortcuts.Models;
using Shortcuts.Services;

namespace ShortcutsGraphChecks;

internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine($"  [ok]   {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    [STAThread]
    private static int Main()
    {
        var sandbox = Path.Combine(Path.GetTempPath(),
            "DeskCanvas-GraphChecks-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        // The geometry parser behind glyphs needs a render interface, same as the other suites.
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        Console.WriteLine("=== Shortcuts graph checks ===");

        // The headless platform installs a dispatcher-backed SynchronizationContext on this
        // thread; awaiting inside it without a running message loop would deadlock. Running the
        // checks on a pool thread with no context keeps `ConfigureAwait(true)` honest.
        Task.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(null);

            Run("migration", CheckMigration);
            Run("branch", CheckBranch);
            Run("switch", CheckSwitch);
            Run("loops", CheckLoops);
            Run("data", CheckDataSemantics);
            Run("validation", CheckValidation);
            Run("lifecycle", CheckLifecycle);
            Run("actions", CheckActions);
            Run("compatibility", CheckCompatibility);
            Run("shared-data", CheckSharedData);
            Run("end-to-end", CheckEndToEnd);
        }).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"ShortcutsGraphChecks: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action check)
    {
        Console.WriteLine();
        Console.WriteLine($"[{name}]");
        try
        {
            check();
        }
        catch (Exception ex)
        {
            Check(false, $"{name} threw {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---- helpers ------------------------------------------------------------------

    private sealed class Harness
    {
        public FakeSystem System { get; } = new();
        public FakePrompts Prompts { get; } = new();
        public RunServices Services => new() { System = System, Prompts = Prompts };
    }

    private static RunResult RunGraph(ShortcutDefinition shortcut, Harness? harness = null,
        RunOptions? options = null)
    {
        harness ??= new Harness();
        var result = ShortcutRunner.RunAsync(shortcut, harness.Services, options ?? new RunOptions())
            .GetAwaiter().GetResult();
        if (!result.Success && !result.Cancelled && !result.Busy)
            Console.WriteLine($"         · run error [{result.ErrorCode}] node={result.ErrorNodeId}: {result.Error}");
        return result;
    }

    private static ShortcutDefinition NewShortcut(params WorkflowNode[] nodes)
    {
        var shortcut = new ShortcutDefinition { Name = "test", Graph = new WorkflowGraph() };
        shortcut.Graph!.Nodes.AddRange(nodes);
        shortcut.Graph.Nodes.Insert(0, new WorkflowNode { Type = WorkflowNodeTypes.Start, X = 0, Y = 0 });
        return shortcut;
    }

    private static void Exec(ShortcutDefinition shortcut, WorkflowNode from, string port, WorkflowNode to) =>
        shortcut.Graph!.Links.Add(WorkflowLink.Exec(from.Id, port, to.Id));

    private static void Data(ShortcutDefinition shortcut, WorkflowNode from, string port, WorkflowNode to,
        string param) => shortcut.Graph!.Links.Add(WorkflowLink.Data(from.Id, port, to.Id, param));

    private static WorkflowNode Start(ShortcutDefinition shortcut) => shortcut.Graph!.Start!;

    private static string? Output(RunResult result, WorkflowNode node) =>
        result.Outputs.TryGetValue(node.Id, out var text) ? text : null;

    private static WorkflowNode N(string type, double x = 0, double y = 0,
        params (string Key, string Value)[] values) => WorkflowNode.With(type, x, y, values);

    // ---- migration ----------------------------------------------------------------

    private static void CheckMigration()
    {
        var comment = ShortcutAction.With("comment", ("note", "a"));
        var setVar = ShortcutAction.With("setVar", ("name", "flag"), ("value", "yes"));
        var repeat = ShortcutAction.Create("repeat");
        repeat.Params["count"] = ShortcutValue.Literal("2");
        var body = ShortcutAction.With("setVar", ("name", "touched"), ("value", "1"));
        repeat.Children.Add(body);
        var ifAction = ShortcutAction.Create("if");
        ifAction.Params["left"] = ShortcutValue.Variable(body.Id);
        ifAction.Params["right"] = ShortcutValue.Literal("1");
        var thenChild = ShortcutAction.With("setVar", ("name", "t"), ("value", "then"));
        ifAction.Children.Add(thenChild);

        var definition = new ShortcutDefinition
        {
            Name = "migrate",
            Actions = [comment, setVar, repeat, ifAction],
        };

        var graph = definition.EffectiveGraph;
        Check(definition.Actions.Count == 0, "migration empties the legacy action list");
        Check(graph.Start != null, "migration adds a start node");
        Check(graph.Nodes.Count(n => n.Type == WorkflowNodeTypes.ForLoop) == 1, "repeat becomes forLoop");
        Check(graph.Nodes.Count(n => n.Type == WorkflowNodeTypes.Branch) == 1, "if becomes branch");
        Check(graph.Nodes.Any(n => n.Id == body.Id), "migrated nodes keep their ids");
        Check(graph.Nodes.Any(n => n.Type is WorkflowNodeTypes.Note or "text"),
            "an unreferenced comment becomes a canvas note");
        Check(graph.NextNode(graph.Start!.Id, WorkflowNodeTypes.Out) == setVar.Id,
            "the chain starts at the first executable action");
        Check(graph.NextNode(setVar.Id, WorkflowNodeTypes.Out) == repeat.Id, "siblings stay chained");
        Check(graph.NextNode(repeat.Id, WorkflowNodeTypes.BodyPort) == body.Id, "loop body is connected");
        Check(graph.NextNode(ifAction.Id, WorkflowNodeTypes.TruePort) == thenChild.Id,
            "then branch is connected");
        Check(graph.Nodes.Where(n => n.Type != WorkflowNodeTypes.Start).All(n => n.X > 0),
            "migrated layout advances left to right");

        // setVar exposes a real value port now, so an old reference to it resolves.
        var setVarNode = graph.Find(setVar.Id)!;
        Check(WorkflowNodeTypes.Outputs(setVarNode).Any(p => p.Id == "value" && p.IsData),
            "setVar exposes a value data port");

        // sendKeys is split into two ordered steps.
        var sendKeys = ShortcutAction.With("sendKeys", ("keys", "Ctrl+A"), ("text", "hello"));
        var split = new ShortcutDefinition { Name = "split", Actions = [sendKeys] }.EffectiveGraph;
        Check(split.Nodes.Count(n => n.Type == "typeText") == 1 &&
              split.Nodes.Count(n => n.Type == "sendKeySequence") == 1,
            "sendKeys splits into typeText + sendKeySequence");
        var typeText = split.Nodes.First(n => n.Type == "typeText");
        var sequence = split.Nodes.First(n => n.Type == "sendKeySequence");
        Check(split.NextNode(typeText.Id, WorkflowNodeTypes.Out) == sequence.Id,
            "the split keeps the original order");

        // A pipe-separated switch becomes real case ports.
        var legacySwitch = ShortcutAction.With("switch", ("value", "b"), ("cases", "a|b|c"));
        var switched = new ShortcutDefinition { Name = "sw", Actions = [legacySwitch] }.EffectiveGraph;
        var switchNode = switched.Nodes.First(n => n.Type == WorkflowNodeTypes.Switch);
        Check(switchNode.EffectiveCases.Count == 3 &&
              switchNode.EffectiveCases.Select(c => c.Value).SequenceEqual(["a", "b", "c"]),
            "switch cases are rebuilt from the pipe list");

        // Running the migration twice is a no-op.
        var document = new ShortcutsDocument
        {
            Version = 1,
            Shortcuts = [new ShortcutDefinition { Name = "twice", Actions = [setVar] }],
        };
        var first = WorkflowMigration.Migrate(document);
        var nodesAfterFirst = document.Shortcuts[0].EffectiveGraph.Nodes.Count;
        var linksAfterFirst = document.Shortcuts[0].EffectiveGraph.Links.Count;
        var second = WorkflowMigration.Migrate(document);
        Check(document.Shortcuts[0].EffectiveGraph.Nodes.Count == nodesAfterFirst &&
              document.Shortcuts[0].EffectiveGraph.Links.Count == linksAfterFirst,
            "migrating twice changes nothing");
        Check(second.Diagnostics.Count <= first.Diagnostics.Count, "the second pass adds no new diagnostics");

        // A newer version is opened read-only and never rewritten.
        var future = new ShortcutsDocument { Version = 99 };
        var futureResult = WorkflowMigration.Migrate(future);
        Check(futureResult.ReadOnly && future.Version == 99, "a newer document is read-only");
        Check(futureResult.Diagnostics.Count > 0, "the newer version produces a diagnostic");
    }

    // ---- branch -------------------------------------------------------------------

    private static void CheckBranch()
    {
        var flag = N("setVar", 0, 0, ("name", "flag"), ("value", "yes"));
        var branch = N("branch", 300, 0, ("left", "{{flag}}"), ("op", "equals"), ("right", "yes"));
        var yes = N("setVar", 600, 0, ("name", "took"), ("value", "yes"));
        var no = N("setVar", 600, 124, ("name", "took"), ("value", "no"));
        var after = N("setVar", 900, 0, ("name", "after"), ("value", "1"));

        var shortcut = NewShortcut(flag, branch, yes, no, after);
        Exec(shortcut, Start(shortcut), WorkflowNodeTypes.Out, flag);
        Exec(shortcut, flag, WorkflowNodeTypes.Out, branch);
        Exec(shortcut, branch, WorkflowNodeTypes.TruePort, yes);
        Exec(shortcut, branch, WorkflowNodeTypes.FalsePort, no);
        Exec(shortcut, yes, WorkflowNodeTypes.Out, after);
        Exec(shortcut, no, WorkflowNodeTypes.Out, after);

        var result = RunGraph(shortcut);
        Check(result.Success, "branch run succeeds");
        Check(Output(result, yes) == "yes", "true branch executed");
        Check(Output(result, no) == null, "false branch skipped");
        Check(Output(result, after) == "1", "the merge runs the successor once");

        flag.Params["value"] = ShortcutValue.Literal("no");
        result = RunGraph(shortcut);
        Check(Output(result, no) == "no" && Output(result, yes) == null,
            "false branch taken when the condition fails");

        // Ordering comparisons are numeric; a lexicographic fallback would say "10" < "9".
        var ten = N("number", 0, 0, ("value", "10"));
        var nine = N("number", 300, 0, ("value", "9"));
        var greater = N("branch", 600, 0, ("op", "greater"));
        var hit = N("setVar", 900, 0, ("name", "r"), ("value", "hit"));
        var numeric = NewShortcut(ten, nine, greater, hit);
        Exec(numeric, Start(numeric), WorkflowNodeTypes.Out, ten);
        Exec(numeric, ten, WorkflowNodeTypes.Out, nine);
        Exec(numeric, nine, WorkflowNodeTypes.Out, greater);
        Exec(numeric, greater, WorkflowNodeTypes.TruePort, hit);
        Data(numeric, ten, "value", greater, "left");
        Data(numeric, nine, "value", greater, "right");

        result = RunGraph(numeric);
        Check(result.Success && Output(result, hit) == "hit", "10 > 9 compares numerically");

        // A non-numeric operand is an error, never a silent dictionary compare.
        var textCompare = N("branch", 0, 0, ("left", "abc"), ("op", "greater"), ("right", "2"));
        var textCompareShortcut = NewShortcut(textCompare);
        Exec(textCompareShortcut, Start(textCompareShortcut), WorkflowNodeTypes.Out, textCompare);
        result = RunGraph(textCompareShortcut);
        Check(!result.Success && result.ErrorCode == "ComparisonNotNumeric",
            "an ordering comparison on text fails loudly");
    }

    // ---- switch -------------------------------------------------------------------

    private static void CheckSwitch()
    {
        var value = N("text", 0, 0, ("value", "b"));
        var sw = WorkflowNode.Create("switch", 300, 0);
        sw.Params["value"] = ShortcutValue.Literal("{{" + value.Id + "}}");
        sw.Cases =
        [
            new WorkflowCase { Id = "c1", Value = "a" },
            new WorkflowCase { Id = "c2", Value = "b" },
            new WorkflowCase { Id = "c3", Value = "c" },
        ];

        var a = N("setVar", 600, 0, ("name", "r"), ("value", "A"));
        var b = N("setVar", 600, 124, ("name", "r"), ("value", "B"));
        var c = N("setVar", 600, 248, ("name", "r"), ("value", "C"));

        var shortcut = NewShortcut(value, sw, a, b, c);
        value.Params["value"] = ShortcutValue.Literal("b");
        Exec(shortcut, Start(shortcut), WorkflowNodeTypes.Out, value);
        Exec(shortcut, value, WorkflowNodeTypes.Out, sw);
        Exec(shortcut, sw, "c1", a);
        Exec(shortcut, sw, "c2", b);
        Exec(shortcut, sw, "c3", c);

        var result = RunGraph(shortcut);
        Check(result.Success && Output(result, b) == "B", "switch routes by matching case");
        Check(Output(result, a) == null && Output(result, c) == null, "other cases do not run");

        // Editing a case value keeps its port id, so the existing link still works.
        sw.Cases![1].Value = "z";
        value.Params["value"] = ShortcutValue.Literal("z");
        result = RunGraph(shortcut);
        Check(result.Success && Output(result, b) == "B", "editing a case value keeps its link");
    }

    // ---- loops --------------------------------------------------------------------

    private static void CheckLoops()
    {
        // A loop body that collects, plus a done-path step that must run exactly once.
        var loop = N("forLoop", 0, 0, ("count", "3"));
        var collect = N("collect", 300, 0, ("value", "{{index}}"));
        var done = N("setVar", 300, 248, ("name", "done"), ("value", "once"));
        var after = N("setVar", 600, 248, ("name", "after"), ("value", "after"));

        var shortcut = NewShortcut(loop, collect, done, after);
        Exec(shortcut, Start(shortcut), WorkflowNodeTypes.Out, loop);
        Exec(shortcut, loop, WorkflowNodeTypes.BodyPort, collect);
        Exec(shortcut, loop, WorkflowNodeTypes.DonePort, done);
        Exec(shortcut, done, WorkflowNodeTypes.Out, after);

        var result = RunGraph(shortcut);
        Check(result.Success, "loop with collect succeeds");
        Check(Output(result, done) == "once" && Output(result, after) == "after",
            "the loop successor runs once, not once per iteration");

        var collected = result.Data.TryGetValue(loop.Id, out var nodeOutputs)
            ? nodeOutputs[WorkflowNodeTypes.CollectedPort]
            : null;
        Check(collected is { Type: DataType.List, Items.Count: 3 },
            "collect gathers one value per iteration");

        // Break stops the loop.
        var brk = N("branch", 300, 0, ("left", "{{index}}"), ("op", "equals"), ("right", "1"));
        var stop = WorkflowNode.Create("break", 600, 0);
        var touched = N("collect", 900, 0, ("value", "{{index}}"));
        var brkLoopNode = N("forLoop", 0, 0, ("count", "5"));
        var brkLoop = NewShortcut(brkLoopNode, brk, stop, touched);
        Exec(brkLoop, Start(brkLoop), WorkflowNodeTypes.Out, brkLoopNode);
        Exec(brkLoop, brkLoopNode, WorkflowNodeTypes.BodyPort, brk);
        Exec(brkLoop, brk, WorkflowNodeTypes.TruePort, stop);
        Exec(brkLoop, brk, WorkflowNodeTypes.FalsePort, touched);

        result = RunGraph(brkLoop);
        var breakCollected = result.Data.TryGetValue(brkLoopNode.Id, out var breakOutputs)
            ? breakOutputs[WorkflowNodeTypes.CollectedPort]
            : null;
        Check(result.Success && breakCollected?.Items.Select(v => v.ToText()).SequenceEqual(["0"]) == true,
            "break stops the loop at the first hit");

        // Per-iteration isolation: reading the previous iteration's output is an error.
        var staleLoopNode = N("forLoop", 0, 0, ("count", "2"));
        var setOnce = N("branch", 300, 0, ("left", "{{index}}"), ("op", "equals"), ("right", "0"));
        var onlyFirst = N("setVar", 600, 0, ("name", "only"), ("value", "first"));
        var readStale = N("setClipboard", 900, 0);
        readStale.Params["text"] = ShortcutValue.Variable(onlyFirst.Id, "value");
        var staleLoop = NewShortcut(staleLoopNode, setOnce, onlyFirst, readStale);
        Exec(staleLoop, Start(staleLoop), WorkflowNodeTypes.Out, staleLoopNode);
        Exec(staleLoop, staleLoopNode, WorkflowNodeTypes.BodyPort, setOnce);
        Exec(staleLoop, setOnce, WorkflowNodeTypes.TruePort, onlyFirst);
        Exec(staleLoop, onlyFirst, WorkflowNodeTypes.Out, readStale);
        // The second iteration skips onlyFirst and reads it anyway.
        Exec(staleLoop, setOnce, WorkflowNodeTypes.FalsePort, readStale);

        result = RunGraph(staleLoop);
        Check(!result.Success && result.ErrorCode is "SourceNotExecuted" or "InvalidGraph",
            "a later iteration cannot read the previous iteration's output");

        // Nested loops restore the outer index.
        var outer = N("forLoop", 0, 0, ("count", "2"));
        var inner = N("forLoop", 300, 0, ("count", "2"));
        var innerCapture = N("collect", 600, 0, ("value", "i{{index}}"));
        var outerCapture = N("collect", 600, 124, ("value", "o{{index}}"));
        var nested = NewShortcut(outer, inner, innerCapture, outerCapture);
        Exec(nested, Start(nested), WorkflowNodeTypes.Out, outer);
        Exec(nested, outer, WorkflowNodeTypes.BodyPort, inner);
        Exec(nested, inner, WorkflowNodeTypes.BodyPort, innerCapture);
        Exec(nested, inner, WorkflowNodeTypes.DonePort, outerCapture);

        result = RunGraph(nested);
        var innerCollected = result.Data.TryGetValue(inner.Id, out var innerOutputs)
            ? innerOutputs[WorkflowNodeTypes.CollectedPort]
            : null;
        var outerCollected = result.Data.TryGetValue(outer.Id, out var outerOutputs)
            ? outerOutputs[WorkflowNodeTypes.CollectedPort]
            : null;
        Check(result.Success && innerCollected is { Items.Count: 2 },
            "each inner-loop invocation collects its own iterations");
        Check(outerCollected?.Items.Select(v => v.ToText()).SequenceEqual(["o0", "o1"]) == true,
            "the outer index is restored after the inner loop");

        // while: reaching the cap with the condition still true is a failure.
        var whileLoop = N("whileLoop", 0, 0,
            ("left", "a"), ("op", "equals"), ("right", "a"), ("max", "3"));
        var whileBody = N("setVar", 300, 0, ("name", "n"), ("value", "1"));
        var whileShortcut = NewShortcut(whileLoop, whileBody);
        Exec(whileShortcut, Start(whileShortcut), WorkflowNodeTypes.Out, whileLoop);
        Exec(whileShortcut, whileLoop, WorkflowNodeTypes.BodyPort, whileBody);

        result = RunGraph(whileShortcut);
        Check(!result.Success && result.ErrorCode == "IterationLimitExceeded",
            "a while loop that never ends reports IterationLimitExceeded");

        // break outside a loop is a validator error.
        var strayBreak = WorkflowNode.Create("break", 300, 0);
        var stray = NewShortcut(strayBreak);
        Exec(stray, Start(stray), WorkflowNodeTypes.Out, strayBreak);
        var report = WorkflowValidator.Validate(stray.Graph!, stray, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code == "BreakOutsideLoop"),
            "break outside a loop is rejected by the validator");
    }

    // ---- data semantics -----------------------------------------------------------

    private static void CheckDataSemantics()
    {
        // setVar's value output feeds a data link; a reference resolves the same value.
        var source = N("setVar", 0, 0, ("name", "v"), ("value", "payload"));
        var linked = N("setClipboard", 300, 0);
        var referenced = N("setClipboard", 300, 124);
        referenced.Params["text"] = ShortcutValue.Variable(source.Id, "value");
        var shortcut = NewShortcut(source, linked, referenced);
        Exec(shortcut, Start(shortcut), WorkflowNodeTypes.Out, source);
        Exec(shortcut, source, WorkflowNodeTypes.Out, linked);
        Exec(shortcut, linked, WorkflowNodeTypes.Out, referenced);
        Data(shortcut, source, "value", linked, "text");

        var harness = new Harness();
        var result = RunGraph(shortcut, harness);
        Check(result.Success && harness.System.ClipboardWrites.Count == 2,
            "a data link and a reference both write the same value");
        Check(harness.System.ClipboardWrites.Distinct().Count() == 1,
            "reference and data link resolve identically");

        // A bound value is not template-expanded a second time; a literal is.
        var literalSource = N("text", 0, 0, ("value", "{{index}}"));
        var boundTarget = N("setClipboard", 300, 0);
        var literalTarget = N("setClipboard", 300, 124);
        literalTarget.Params["text"] = ShortcutValue.Literal("{{index}}");
        var templateShortcut = NewShortcut(literalSource, boundTarget, literalTarget);
        Exec(templateShortcut, Start(templateShortcut), WorkflowNodeTypes.Out, literalSource);
        Exec(templateShortcut, literalSource, WorkflowNodeTypes.Out, boundTarget);
        Exec(templateShortcut, boundTarget, WorkflowNodeTypes.Out, literalTarget);
        Data(templateShortcut, literalSource, "value", boundTarget, "text");

        harness = new Harness();
        RunGraph(templateShortcut, harness);
        Check(harness.System.ClipboardWrites.Contains("{{index}}"),
            "a bound value is not expanded again");

        // Empty vs null vs not provided.
        var emptyText = N("setClipboard", 0, 0, ("text", ""));
        var nullValue = N("setVar", 300, 0, ("name", "n"));
        var emptyShortcut = NewShortcut(emptyText, nullValue);
        Exec(emptyShortcut, Start(emptyShortcut), WorkflowNodeTypes.Out, emptyText);
        Exec(emptyShortcut, emptyText, WorkflowNodeTypes.Out, nullValue);

        harness = new Harness();
        result = RunGraph(emptyShortcut, harness);
        Check(result.Success && harness.System.ClipboardWrites.Contains(string.Empty),
            "an empty literal is written as an empty string");
        Check(result.Data[nullValue.Id]["value"].IsNull, "an unset value stays null, not empty");

        // An invalid number is a located error.
        var badNumber = N("wait", 0, 0, ("seconds", "abc"));
        var numberShortcut = NewShortcut(badNumber);
        Exec(numberShortcut, Start(numberShortcut), WorkflowNodeTypes.Out, badNumber);
        result = RunGraph(numberShortcut);
        Check(!result.Success, "a non-numeric value fails the run");

        // mergeValue: exactly one branch may produce a value.
        var yes = N("branch", 0, 0, ("left", "1"), ("op", "equals"), ("right", "1"));
        var left = N("text", 300, 0, ("value", "L"));
        var right = N("text", 300, 124, ("value", "R"));
        var merge = WorkflowNode.Create("mergeValue", 600, 0);
        var sink = N("setClipboard", 900, 0);
        var mergeShortcut = NewShortcut(yes, left, right, merge, sink);
        Exec(mergeShortcut, Start(mergeShortcut), WorkflowNodeTypes.Out, yes);
        Exec(mergeShortcut, yes, WorkflowNodeTypes.TruePort, left);
        Exec(mergeShortcut, yes, WorkflowNodeTypes.FalsePort, right);
        Exec(mergeShortcut, left, WorkflowNodeTypes.Out, merge);
        Exec(mergeShortcut, right, WorkflowNodeTypes.Out, merge);
        Exec(mergeShortcut, merge, WorkflowNodeTypes.Out, sink);
        Data(mergeShortcut, left, "value", merge, "a");
        Data(mergeShortcut, right, "value", merge, "b");
        Data(mergeShortcut, merge, "value", sink, "text");

        harness = new Harness();
        result = RunGraph(mergeShortcut, harness);
        Check(result.Success && harness.System.ClipboardWrites.SequenceEqual(["L"]),
            "merge takes the value of the branch that actually ran");

        // getProperty: a missing field and a JSON null are different outcomes.
        var json = N("parseJson", 0, 0, ("text", """{"a":null,"b":{"c":1}}"""));
        var nullField = N("getProperty", 300, 0, ("path", "a"));
        var nullShortcut = NewShortcut(json, nullField);
        Exec(nullShortcut, Start(nullShortcut), WorkflowNodeTypes.Out, json);
        Exec(nullShortcut, json, WorkflowNodeTypes.Out, nullField);
        Data(nullShortcut, json, "value", nullField, "value");
        result = RunGraph(nullShortcut);
        Check(result.Success, "getProperty run succeeds for the null field");
        Check(result.Success && result.Data[nullField.Id]["value"].IsNull,
            "a JSON null is returned as null");

        var jsonMissing = N("parseJson", 0, 0, ("text", """{"a":null}"""));
        var missingField = N("getProperty", 300, 0, ("path", "zz"));
        var missingShortcut = NewShortcut(jsonMissing, missingField);
        Exec(missingShortcut, Start(missingShortcut), WorkflowNodeTypes.Out, jsonMissing);
        Exec(missingShortcut, jsonMissing, WorkflowNodeTypes.Out, missingField);
        Data(missingShortcut, jsonMissing, "value", missingField, "value");
        result = RunGraph(missingShortcut);
        Check(!result.Success && result.ErrorCode == "FieldMissing",
            "a missing field is an error, not a null");

        // splitText / joinText round-trip with explicit options.
        var split = N("splitText", 0, 0, ("text", " a , b ,, c "), ("separator", "comma"));
        var join = N("joinText", 300, 0, ("separator", "custom"), ("custom", "|"));
        var listShortcut = NewShortcut(split, join);
        Exec(listShortcut, Start(listShortcut), WorkflowNodeTypes.Out, split);
        Exec(listShortcut, split, WorkflowNodeTypes.Out, join);
        Data(listShortcut, split, "list", join, "list");
        result = RunGraph(listShortcut);
        Check(Output(result, join) == "a|b|c", "splitText trims and drops empty items by default");

        // calculate: division by zero is an error.
        var divide = N("calculate", 0, 0, ("a", "1"), ("op", "divide"), ("b", "0"));
        var calcShortcut = NewShortcut(divide);
        Exec(calcShortcut, Start(calcShortcut), WorkflowNodeTypes.Out, divide);
        result = RunGraph(calcShortcut);
        Check(!result.Success && result.ErrorCode == "DivideByZero", "dividing by zero fails");
    }

    // ---- validation ---------------------------------------------------------------

    private static void CheckValidation()
    {
        // Data-only graph: no execution path, so it must not report success.
        var source = N("setVar", 0, 0, ("name", "v"), ("value", "x"));
        var target = N("setClipboard", 300, 0);
        var dataOnly = NewShortcut(source, target);
        Data(dataOnly, source, "value", target, "text");
        var result = RunGraph(dataOnly);
        Check(!result.Success && result.ErrorCode == "InvalidGraph",
            "a data-only graph does not report success");

        // An exec link into a parameter dot is rejected.
        var a = N("setVar", 0, 0, ("name", "a"), ("value", "1"));
        var b = N("setClipboard", 300, 0);
        var execIntoParam = NewShortcut(a, b);
        Exec(execIntoParam, Start(execIntoParam), WorkflowNodeTypes.Out, a);
        execIntoParam.Graph!.Links.Add(new WorkflowLink
        {
            From = a.Id,
            FromPort = WorkflowNodeTypes.Out,
            To = b.Id,
            ToPort = "text",
            Kind = LinkKinds.Exec,
        });
        var report = WorkflowValidator.Validate(execIntoParam.Graph!, execIntoParam, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code == "ExecIntoParam"),
            "an exec link cannot land on a parameter dot");

        // Duplicate data links into one parameter.
        var dupSource = N("text", 0, 0, ("value", "x"));
        var dupSource2 = N("text", 0, 124, ("value", "y"));
        var dupTarget = N("setClipboard", 300, 0);
        var duplicate = NewShortcut(dupSource, dupSource2, dupTarget);
        Data(duplicate, dupSource, "value", dupTarget, "text");
        duplicate.Graph!.Links.Add(WorkflowLink.Data(dupSource2.Id, "value", dupTarget.Id, "text"));
        report = WorkflowValidator.Validate(duplicate.Graph!, duplicate, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code == "DuplicateDataLink"),
            "two data links into one parameter are rejected");

        // Type mismatch: a list cannot feed a number.
        var listNode = N("createList", 0, 0, ("a", "1"));
        var numberNode = N("wait", 300, 0);
        var mismatch = NewShortcut(listNode, numberNode);
        mismatch.Graph!.Links.Add(WorkflowLink.Data(listNode.Id, "list", numberNode.Id, "seconds"));
        report = WorkflowValidator.Validate(mismatch.Graph!, mismatch, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code == "TypeMismatch"),
            "a list cannot be linked into a number parameter");

        // Exec fan-out.
        var fan = N("setVar", 0, 0, ("name", "f"), ("value", "1"));
        var fanA = N("setVar", 300, 0, ("name", "a"), ("value", "1"));
        var fanB = N("setVar", 300, 124, ("name", "b"), ("value", "1"));
        var fanShortcut = NewShortcut(fan, fanA, fanB);
        Exec(fanShortcut, Start(fanShortcut), WorkflowNodeTypes.Out, fan);
        Exec(fanShortcut, fan, WorkflowNodeTypes.Out, fanA);
        Exec(fanShortcut, fan, WorkflowNodeTypes.Out, fanB);
        report = WorkflowValidator.Validate(fanShortcut.Graph!, fanShortcut, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code == "ExecFanOut"),
            "one exec port cannot drive two successors");

        // Cycle without a loop.
        var cycleA = N("setVar", 0, 0, ("name", "a"), ("value", "1"));
        var cycleB = N("setVar", 300, 0, ("name", "b"), ("value", "1"));
        var cycle = NewShortcut(cycleA, cycleB);
        Exec(cycle, Start(cycle), WorkflowNodeTypes.Out, cycleA);
        Exec(cycle, cycleA, WorkflowNodeTypes.Out, cycleB);
        Exec(cycle, cycleB, WorkflowNodeTypes.Out, cycleA);
        result = RunGraph(cycle);
        Check(!result.Success, "a cycle is rejected instead of hanging");

        // No start node.
        var empty = new ShortcutDefinition { Name = "empty", Graph = new WorkflowGraph() };
        result = ShortcutRunner.RunAsync(empty, new RunServices()).GetAwaiter().GetResult();
        Check(!result.Success, "a graph without a start node fails loudly");

        // Unknown type.
        var unknownNode = new WorkflowNode { Type = "doesNotExist" };
        var unknown = NewShortcut(unknownNode);
        Exec(unknown, Start(unknown), WorkflowNodeTypes.Out, unknownNode);
        result = RunGraph(unknown);
        Check(!result.Success, "an unknown node type fails the run");

        // Required parameter missing.
        var notifyNode = N("notify", 0, 0);
        var missing = NewShortcut(notifyNode);
        Exec(missing, Start(missing), WorkflowNodeTypes.Out, notifyNode);
        result = RunGraph(missing);
        Check(!result.Success && result.ErrorCode is "RequiredMissing" or "RequiredEmpty",
            "a missing required parameter fails with a located error");

        // Reference to a node that never runs.
        var notRun = N("text", 300, 0, ("value", "x"));
        var reader = N("setClipboard", 600, 0);
        reader.Params["text"] = ShortcutValue.Variable(notRun.Id, "value");
        var orphan = NewShortcut(notRun, reader);
        Exec(orphan, Start(orphan), WorkflowNodeTypes.Out, reader);
        report = WorkflowValidator.Validate(orphan.Graph!, orphan, forRun: true);
        Check(report.Diagnostics.Any(d => d.Code is "SourceNotExecuted" or "SourceNotGuaranteed"),
            "a reference to an unreachable node is rejected");

        // Notes never execute.
        var note = N("note", 0, 0, ("note", "hello"));
        var counted = N("setVar", 300, 0, ("name", "x"), ("value", "1"));
        var noteShortcut = NewShortcut(note, counted);
        Exec(noteShortcut, Start(noteShortcut), WorkflowNodeTypes.Out, counted);
        result = RunGraph(noteShortcut);
        Check(result.Success && !result.Outputs.ContainsKey(note.Id), "a note is never executed");
    }

    // ---- lifecycle ----------------------------------------------------------------

    private static void CheckLifecycle()
    {
        // Cancelling the parent stops the child before its remaining steps run.
        var childWait = N("wait", 0, 0, ("seconds", "5"));
        var childWrite = N("setClipboard", 300, 0, ("text", "child-finished"));
        var child = NewShortcut(childWait, childWrite);
        Exec(child, Start(child), WorkflowNodeTypes.Out, childWait);
        Exec(child, childWait, WorkflowNodeTypes.Out, childWrite);
        child.Name = "child";
        ShortcutsStore.ResetForTesting(new ShortcutsDocument { Version = 3, Shortcuts = [child] });

        var parentRun = N("runShortcut", 0, 0, ("target", child.Id));
        var parent = NewShortcut(parentRun);
        Exec(parent, Start(parent), WorkflowNodeTypes.Out, parentRun);
        parent.Name = "parent";

        var harness = new Harness();
        using var cts = new CancellationTokenSource();
        var task = ShortcutRunner.RunAsync(parent, harness.Services,
            new RunOptions { CancellationToken = cts.Token });
        Thread.Sleep(250);
        cts.Cancel();
        var result = task.GetAwaiter().GetResult();
        Check(result.Cancelled, "cancelling the parent cancels the run");
        Check(harness.System.ClipboardWrites.Count == 0,
            "the child's remaining steps execute zero times after a parent cancel");

        // A step timeout cancels the operation instead of abandoning the wait.
        var slow = N("wait", 0, 0, ("seconds", "5"));
        var timeoutShortcut = NewShortcut(slow);
        Exec(timeoutShortcut, Start(timeoutShortcut), WorkflowNodeTypes.Out, slow);
        result = RunGraph(timeoutShortcut, new Harness(),
            new RunOptions { StepTimeout = TimeSpan.FromMilliseconds(120) });
        Check(!result.Success && result.ErrorCode == "Timeout", "a step timeout is reported as Timeout");

        // The graph is snapshotted: editing during a run cannot change it.
        var waitNode = N("wait", 0, 0, ("seconds", "0.4"));
        var valueNode = N("setVar", 300, 0, ("name", "v"), ("value", "before"));
        var snapshotShortcut = NewShortcut(waitNode, valueNode);
        Exec(snapshotShortcut, Start(snapshotShortcut), WorkflowNodeTypes.Out, waitNode);
        Exec(snapshotShortcut, waitNode, WorkflowNodeTypes.Out, valueNode);

        var snapshotTask = ShortcutRunner.RunAsync(snapshotShortcut, new Harness().Services);
        Thread.Sleep(120);
        valueNode.Params["value"] = ShortcutValue.Literal("after");
        result = snapshotTask.GetAwaiter().GetResult();
        Check(Output(result, valueNode) == "before", "editing during a run does not affect that run");

        // Confirmation: both switches are handled by the single entry point, and a run that
        // needs a dialog without a window fails instead of proceeding silently.
        var confirmNode = N("setVar", 0, 0, ("name", "c"), ("value", "1"));
        var confirmable = NewShortcut(confirmNode);
        Exec(confirmable, Start(confirmable), WorkflowNodeTypes.Out, confirmNode);
        confirmable.ConfirmBeforeRun = true;
        result = RunGraph(confirmable);
        Check(!result.Success && result.ErrorCode == "ConfirmUnavailable",
            "a confirmation without a window fails explicitly");

        confirmable.ConfirmBeforeRun = false;
        result = RunGraph(confirmable, new Harness(), new RunOptions { RequireConfirm = true });
        Check(!result.Success && result.ErrorCode == "ConfirmUnavailable",
            "the component-level switch behaves like the shortcut-level one");

        result = RunGraph(confirmable, new Harness(), new RunOptions { Confirmed = true });
        Check(result.Success, "an already-confirmed run proceeds without asking again");

        // Child inputs + return.
        var inputNode = N("calculate", 0, 0, ("op", "add"), ("b", "1"));
        inputNode.Params["a"] = ShortcutValue.Literal("{{n}}");
        var ret = N("return", 300, 0, ("value", "{{" + inputNode.Id + "}}"));
        var childWithReturn = NewShortcut(inputNode, ret);
        childWithReturn.Name = "calc-child";
        childWithReturn.InputFields =
        [
            new ShortcutInputField { Key = "n", Name = "n", DataType = DataType.Number, Required = true },
        ];
        Exec(childWithReturn, Start(childWithReturn), WorkflowNodeTypes.Out, inputNode);
        Exec(childWithReturn, inputNode, WorkflowNodeTypes.Out, ret);
        ShortcutsStore.ResetForTesting(new ShortcutsDocument { Version = 3, Shortcuts = [childWithReturn] });

        var callerNode = N("runShortcut", 0, 0, ("target", childWithReturn.Id), ("arguments", """{"n":41}"""));
        var callerShortcut = NewShortcut(callerNode);
        callerShortcut.Name = "caller";
        Exec(callerShortcut, Start(callerShortcut), WorkflowNodeTypes.Out, callerNode);

        result = RunGraph(callerShortcut);
        Check(result.Success, "runShortcut returns a value");
        Check(result.Data[callerNode.Id]["result"].ToText() == "42",
            "the child computes from its declared input and returns the result");

        // The shared step budget stops a run that would otherwise keep going.
        var budget = new RunBudget(3, TimeSpan.FromMinutes(1));
        var b1 = N("setVar", 0, 0, ("name", "a"), ("value", "1"));
        var b2 = N("setVar", 300, 0, ("name", "b"), ("value", "1"));
        var b3 = N("setVar", 600, 0, ("name", "c"), ("value", "1"));
        var b4 = N("setVar", 900, 0, ("name", "d"), ("value", "1"));
        var budgetShortcut = NewShortcut(b1, b2, b3, b4);
        Exec(budgetShortcut, Start(budgetShortcut), WorkflowNodeTypes.Out, b1);
        Exec(budgetShortcut, b1, WorkflowNodeTypes.Out, b2);
        Exec(budgetShortcut, b2, WorkflowNodeTypes.Out, b3);
        Exec(budgetShortcut, b3, WorkflowNodeTypes.Out, b4);

        result = RunGraph(budgetShortcut, new Harness(), new RunOptions { Budget = budget });
        Check(!result.Success && result.ErrorCode == "BudgetExhausted", "the shared step budget is enforced");
    }

    // ---- actions ------------------------------------------------------------------

    private static void CheckActions()
    {
        // httpRequest against a local server (TcpListener, so no URL ACL is needed).
        using var server = new TestHttpServer();
        var empty = N("httpRequest", 0, 0, ("url", server.Url + "empty"));
        var emptyShortcut = NewShortcut(empty);
        Exec(emptyShortcut, Start(emptyShortcut), WorkflowNodeTypes.Out, empty);
        var result = RunGraph(emptyShortcut);
        Check(result.Success && Output(result, empty) == string.Empty,
            "an empty HTTP response keeps an empty body");
        Check(result.Data[empty.Id]["statusCode"].ToText() == "200", "statusCode is reported");

        var missing = N("httpRequest", 0, 0, ("url", server.Url + "missing"));
        var missingShortcut = NewShortcut(missing);
        Exec(missingShortcut, Start(missingShortcut), WorkflowNodeTypes.Out, missing);
        result = RunGraph(missingShortcut);
        Check(!result.Success && result.ErrorCode == "HttpStatus", "a 404 is reported as an error");
        Check(result.Data.TryGetValue(missing.Id, out var notFound) &&
              notFound["body"]?.ToText() == "not found",
            "a non-2xx response keeps its body as data");

        var gbk = N("httpRequest", 0, 0, ("url", server.Url + "gbk"));
        var gbkShortcut = NewShortcut(gbk);
        Exec(gbkShortcut, Start(gbkShortcut), WorkflowNodeTypes.Out, gbk);
        result = RunGraph(gbkShortcut);
        Check(result.Success && Output(result, gbk) == "中文", "the response charset is honoured");

        var big = N("httpRequest", 0, 0, ("url", server.Url + "big"));
        var bigShortcut = NewShortcut(big);
        Exec(bigShortcut, Start(bigShortcut), WorkflowNodeTypes.Out, big);
        result = RunGraph(bigShortcut);
        Check(result.Data[big.Id]["truncated"].ToText() == "true" && Output(result, big)?.Length == 20000,
            "an oversized body is truncated and flagged");

        // File actions in a sandbox.
        var folder = Path.Combine(Path.GetTempPath(), "DeskCanvas-FileChecks-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "note.txt");

        var create = N("writeTextFile", 0, 0, ("path", file), ("text", "hello"), ("mode", "create"));
        var createShortcut = NewShortcut(create);
        Exec(createShortcut, Start(createShortcut), WorkflowNodeTypes.Out, create);
        result = RunGraph(createShortcut);
        Check(result.Success && File.Exists(file), "writeTextFile creates a file");
        Check(Output(result, create) == file, "writeTextFile returns the path it wrote");

        var again = N("writeTextFile", 0, 0, ("path", file), ("text", "again"), ("mode", "create"));
        var againShortcut = NewShortcut(again);
        Exec(againShortcut, Start(againShortcut), WorkflowNodeTypes.Out, again);
        result = RunGraph(againShortcut);
        Check(!result.Success && result.ErrorCode == "FileExists", "create refuses to overwrite");

        var append = N("writeTextFile", 0, 0, ("path", file), ("text", "!"), ("mode", "append"));
        var appendShortcut = NewShortcut(append);
        Exec(appendShortcut, Start(appendShortcut), WorkflowNodeTypes.Out, append);
        result = RunGraph(appendShortcut);
        Check(result.Success && File.ReadAllText(file) == "hello!", "append adds to the file");

        var read = N("readTextFile", 0, 0, ("path", file));
        var readShortcut = NewShortcut(read);
        Exec(readShortcut, Start(readShortcut), WorkflowNodeTypes.Out, read);
        result = RunGraph(readShortcut);
        Check(Output(result, read) == "hello!", "readTextFile round-trips the text");

        // System actions go through the injected adapter.
        var volume = N("setVolume", 0, 0, ("level", "30"));
        var volumeShortcut = NewShortcut(volume);
        Exec(volumeShortcut, Start(volumeShortcut), WorkflowNodeTypes.Out, volume);
        var harness = new Harness();
        result = RunGraph(volumeShortcut, harness);
        Check(result.Success && Math.Abs(harness.System.Volume - 30) < 0.01,
            "setVolume goes through the adapter");
        Check(Output(result, volume) == "30", "setVolume reports the actual level");

        var mute = N("muteToggle", 0, 0);
        var muteShortcut = NewShortcut(mute);
        Exec(muteShortcut, Start(muteShortcut), WorkflowNodeTypes.Out, mute);
        harness = new Harness();
        result = RunGraph(muteShortcut, harness);
        Check(result.Success && harness.System.Muted && Output(result, mute) == "true",
            "muteToggle reports the resulting state");

        var explicitMute = N("setMute", 0, 0, ("muted", "false"));
        var explicitShortcut = NewShortcut(explicitMute);
        Exec(explicitShortcut, Start(explicitShortcut), WorkflowNodeTypes.Out, explicitMute);
        harness = new Harness();
        harness.System.Muted = true;
        result = RunGraph(explicitShortcut, harness);
        Check(result.Success && !harness.System.Muted, "setMute(false) unmutes deterministically");

        var shot = N("screenshot", 0, 0, ("scope", "primary"));
        var shotShortcut = NewShortcut(shot);
        Exec(shotShortcut, Start(shotShortcut), WorkflowNodeTypes.Out, shot);
        harness = new Harness();
        result = RunGraph(shotShortcut, harness);
        Check(result.Success && Output(result, shot) == harness.System.LastScreenshot,
            "screenshot returns the file it actually wrote");

        // Typing while a hotkey is being recorded is refused.
        var typed = N("typeText", 0, 0, ("text", "hello"));
        var typedShortcut = NewShortcut(typed);
        Exec(typedShortcut, Start(typedShortcut), WorkflowNodeTypes.Out, typed);
        KeyboardInput.IsRecording = true;
        try
        {
            result = RunGraph(typedShortcut);
        }
        finally
        {
            KeyboardInput.IsRecording = false;
        }

        Check(!result.Success && result.ErrorCode == "RecordingHotkey",
            "typing is refused while a hotkey capture is recording");

        // Error routing: a failing step with onError=route continues through the error exit.
        var failing = N("openFolder", 0, 0, ("path", Path.Combine(folder, "missing")));
        failing.OnError = ErrorPolicy.Route;
        var recovered = N("setVar", 300, 0, ("name", "r"), ("value", "recovered"));
        var routed = NewShortcut(failing, recovered);
        Exec(routed, Start(routed), WorkflowNodeTypes.Out, failing);
        Exec(routed, failing, WorkflowNodeTypes.ErrorPort, recovered);
        result = RunGraph(routed);
        Check(result.Success && Output(result, recovered) == "recovered",
            "a routed error continues through the error exit");

        failing.OnError = null;
        result = RunGraph(routed);
        Check(!result.Success, "the default policy stops the run");

        try { Directory.Delete(folder, recursive: true); } catch { }
    }

    // ---- compatibility ------------------------------------------------------------

    private static void CheckCompatibility()
    {
        // The generated spec is importable and runnable.
        var spec = WorkflowSpecDocument.Build();
        var json = ExtractFirstJsonBlock(spec);
        Check(json != null, "the spec document contains a JSON example");

        if (json != null)
        {
            var imported = WorkflowImport.Parse(json);
            Check(imported.Success, "the spec example imports");
            if (imported.Success)
            {
                var report = WorkflowValidator.Validate(imported.Shortcuts[0].EffectiveGraph,
                    imported.Shortcuts[0], forRun: true);
                Check(!report.HasErrors, "the imported spec example validates");
            }
        }

        var missing = ActionCatalog.Visible
            .Where(d => !spec.Contains($"`{d.Type}`"))
            .Select(d => d.Type)
            .ToList();
        Check(missing.Count == 0,
            $"every visible action type appears in the spec (missing: {string.Join(",", missing)})");
        Check(spec.Contains("`error`"), "the spec documents the error exit");
        Check(spec.Contains("`body`") && spec.Contains("`done`"), "the spec documents loop ports");

        // Import: a linkless linear document is chained; a document with a data link must not be.
        var linear = WorkflowImport.Parse("""
        { "nodes": [ { "type": "start" }, { "type": "lockScreen" }, { "type": "minimizeAll" } ] }
        """);
        Check(linear.Success && linear.Shortcuts[0].EffectiveGraph.Links.Count == 2,
            "a linkless linear document is chained");

        var withData = WorkflowImport.Parse("""
        {
          "nodes": [
            { "id": "s", "type": "start" },
            { "id": "a", "type": "getClipboard" },
            { "id": "b", "type": "setClipboard" }
          ],
          "links": [ { "from": "a", "fromPort": "text", "to": "b", "toPort": "text" } ]
        }
        """);
        Check(!withData.Success, "a document with only a data link is rejected");
        Check(withData.Errors.Count > 0, "the rejection names the problem");

        // A legacy `value` data link is re-pointed at the real output port.
        var legacyPort = WorkflowImport.Parse("""
        {
          "nodes": [
            { "id": "s", "type": "start" },
            { "id": "a", "type": "getClipboard" },
            { "id": "b", "type": "setClipboard" }
          ],
          "links": [
            { "from": "s", "fromPort": "out", "to": "a", "toPort": "in" },
            { "from": "a", "fromPort": "out", "to": "b", "toPort": "in" },
            { "from": "a", "fromPort": "value", "to": "b", "toPort": "text" }
          ]
        }
        """);
        Check(legacyPort.Success, "a legacy value-port data link still imports");
        Check(legacyPort.Success && legacyPort.Shortcuts[0].EffectiveGraph.Links
                .Any(l => l.IsData && l.FromPort == "text"),
            "the legacy value port is re-pointed at the real output");

        // Serialization keeps nodes, links and cases.
        var shortcut = ShortcutTemplates.Screenshot();
        var text = JsonSerializer.Serialize(shortcut, DocumentJson.FileOptions);
        var back = JsonSerializer.Deserialize<ShortcutDefinition>(text, DocumentJson.FileOptions)!;
        Check(back.EffectiveGraph.Nodes.Count == shortcut.EffectiveGraph.Nodes.Count,
            "a graph survives a file round-trip");
        Check(back.EffectiveGraph.Links.Count == shortcut.EffectiveGraph.Links.Count,
            "links survive a file round-trip");

        var switchShortcut = ShortcutTemplates.Blank();
        var switchNode = WorkflowNode.Create("switch", 0, 0);
        switchNode.Cases = [new WorkflowCase { Id = "k1", Value = "one" }];
        switchShortcut.Graph = new WorkflowGraph { Nodes = [switchNode] };
        var switchText = JsonSerializer.Serialize(switchShortcut, DocumentJson.FileOptions);
        var switchBack = JsonSerializer.Deserialize<ShortcutDefinition>(switchText, DocumentJson.FileOptions)!;
        Check(switchBack.Graph!.Nodes[0].Cases is { Count: 1 } &&
              switchBack.Graph.Nodes[0].Cases![0].Value == "one",
            "switch cases survive a file round-trip");

        // The display language never changes machine data.
        var previous = Locale.Culture;
        try
        {
            Locale.Culture = new System.Globalization.CultureInfo("en");
            var english = RunGraph(NumericShortcut());
            Locale.Culture = new System.Globalization.CultureInfo("zh-Hans");
            var chinese = RunGraph(NumericShortcut());
            Check(english.Success && chinese.Success &&
                  english.Outputs.Values.SequenceEqual(chinese.Outputs.Values),
                "the display language does not change machine data");
        }
        finally
        {
            Locale.Culture = previous;
        }
    }

    private static ShortcutDefinition NumericShortcut()
    {
        var calc = N("calculate", 0, 0, ("a", "6"), ("op", "multiply"), ("b", "7"));
        var shortcut = NewShortcut(calc);
        Exec(shortcut, Start(shortcut), WorkflowNodeTypes.Out, calc);
        return shortcut;
    }

    /// <summary>First fenced block that is a complete workflow (the spec also shows templates).</summary>
    private static string? ExtractFirstJsonBlock(string spec)
    {
        var index = 0;
        while (true)
        {
            var start = spec.IndexOf("```json", index, StringComparison.Ordinal);
            if (start < 0) return null;
            start = spec.IndexOf('\n', start) + 1;
            var end = spec.IndexOf("```", start, StringComparison.Ordinal);
            if (end < 0) return null;

            var block = spec[start..end].Trim();
            index = end + 3;
            try
            {
                using var document = JsonDocument.Parse(block);
                if (document.RootElement.TryGetProperty("nodes", out _)) return block;
            }
            catch (JsonException)
            {
                // A skeleton with placeholders: keep looking.
            }
        }
    }

    // ---- shared data ---------------------------------------------------------------

    /// <summary>
    /// The shortcuts document is one shared file at the data root: every card, panel and editor
    /// reads it, switching 方案 never swaps it out, and removing the widget never takes it away.
    /// </summary>
    private static void CheckSharedData()
    {
        var path = ShortcutsStore.FilePath;
        Check(!path.StartsWith(Const.ProfilesFolder, StringComparison.OrdinalIgnoreCase),
            "the shortcuts file is outside any profile folder");
        Check(string.Equals(Path.GetDirectoryName(path), Const.DataFolder, StringComparison.OrdinalIgnoreCase),
            "the shortcuts file sits at the data root");

        // A card, a popup and an editor are three "components": all read the same document.
        var card = new ShortcutDefinition { Name = "shared-card" };
        ShortcutsStore.Upsert(card);

        ShortcutsStore.ResetForTesting(null);
        var asPanel = ShortcutsStore.Get();
        ShortcutsStore.ResetForTesting(null);
        var asEditor = ShortcutsStore.Get();
        Check(asPanel.Shortcuts.Any(s => s.Id == card.Id) && asEditor.Shortcuts.Any(s => s.Id == card.Id),
            "every component reads the same shared document");

        // Removing every widget only detaches listeners; the file and its data stay.
        ShortcutsStore.ResetForTesting(null);
        var afterRemoval = ShortcutsStore.Get();
        Check(afterRemoval.Shortcuts.Any(s => s.Id == card.Id), "the data survives removing the widget");
        Check(File.Exists(ShortcutsStore.FilePath), "the shared file is still on disk");

        // Switching profile must not re-point the file or drop the data.
        var original = Const.ActiveProfile;
        try
        {
            Const.SetActiveProfile("GraphChecksOther");
            Check(string.Equals(ShortcutsStore.FilePath, path, StringComparison.OrdinalIgnoreCase),
                "switching profile does not move the shortcuts file");
            Check(ShortcutsStore.Get().Shortcuts.Any(s => s.Id == card.Id),
                "switching profile keeps the same shortcuts");
        }
        finally
        {
            Const.SetActiveProfile(original);
            ShortcutsStore.ResetForTesting(null);
        }

        ShortcutsStore.Delete(card.Id);
    }

    // ---- end to end ---------------------------------------------------------------

    private static void CheckEndToEnd()
    {
        // 1) clipboard → textTransform → branch → setClipboard
        var harness = new Harness();
        harness.System.Clipboard = "  Mixed Case  ";

        var clip = N("getClipboard", 0, 0);
        var trim = N("textTransform", 300, 0, ("operation", "trim"));
        var emptyBranch = N("branch", 600, 0, ("op", "isEmpty"));
        var writeBack = N("setClipboard", 900, 0);
        var notifyEmpty = N("notify", 900, 124, ("message", "empty"));

        var flow = NewShortcut(clip, trim, emptyBranch, writeBack, notifyEmpty);
        Exec(flow, Start(flow), WorkflowNodeTypes.Out, clip);
        Exec(flow, clip, WorkflowNodeTypes.Out, trim);
        Exec(flow, trim, WorkflowNodeTypes.Out, emptyBranch);
        Exec(flow, emptyBranch, WorkflowNodeTypes.FalsePort, writeBack);
        Exec(flow, emptyBranch, WorkflowNodeTypes.TruePort, notifyEmpty);
        Data(flow, clip, "text", trim, "input");
        Data(flow, trim, "text", emptyBranch, "left");
        Data(flow, trim, "text", writeBack, "text");

        var result = RunGraph(flow, harness);
        Check(result.Success && harness.System.ClipboardWrites.SequenceEqual(["Mixed Case"]),
            "e2e: clipboard → trim → branch → write back");

        var trace = RunHistory.Latest(flow.Id);
        Check(trace != null && trace.Steps.Count >= 4, "the run history records every step");
        Check(trace != null && trace.Steps[0].NodeId == clip.Id, "the run history keeps execution order");
        Check(trace != null && trace.Steps.All(s => s.Inputs.Count > 0 || s.Outputs.Count > 0),
            "the run history records inputs and outputs");

        // 2) httpRequest → parseJson → forEach → collect → showResult
        using var server = new TestHttpServer();
        harness = new Harness();
        var http = N("httpRequest", 0, 0, ("url", server.Url + "items"));
        var parse = N("parseJson", 300, 0);
        var property = N("getProperty", 600, 0, ("path", "items"));
        var each = N("forEach", 900, 0, ("var", "item"));
        var collect = N("collect", 1200, 0, ("value", "{{item}}"));
        var show = N("showResult", 900, 248);

        var pipeline = NewShortcut(http, parse, property, each, collect, show);
        Exec(pipeline, Start(pipeline), WorkflowNodeTypes.Out, http);
        Exec(pipeline, http, WorkflowNodeTypes.Out, parse);
        Exec(pipeline, parse, WorkflowNodeTypes.Out, property);
        Exec(pipeline, property, WorkflowNodeTypes.Out, each);
        Exec(pipeline, each, WorkflowNodeTypes.BodyPort, collect);
        Exec(pipeline, each, WorkflowNodeTypes.DonePort, show);
        Data(pipeline, http, "body", parse, "text");
        Data(pipeline, parse, "value", property, "value");
        Data(pipeline, property, "value", each, "list");
        Data(pipeline, each, WorkflowNodeTypes.CollectedPort, show, "value");

        result = RunGraph(pipeline, harness);
        Check(result.Success, "e2e: HTTP → JSON → iterate → collect → show");
        Check(harness.Prompts.Shown.Count == 1, "showResult displayed the collected list");
        Check(harness.Prompts.Shown.FirstOrDefault()?.AsList().Count == 3,
            "the collected list holds one item per element");

        // 3) parent input → child compute → return
        var childCalc = N("calculate", 0, 0, ("op", "multiply"), ("b", "2"));
        childCalc.Params["a"] = ShortcutValue.Literal("{{amount}}");
        var childReturn = N("return", 300, 0, ("value", "{{" + childCalc.Id + "}}"));
        var child = NewShortcut(childCalc, childReturn);
        child.Name = "double";
        child.InputFields = [new ShortcutInputField { Key = "amount", Name = "amount", DataType = DataType.Number }];
        Exec(child, Start(child), WorkflowNodeTypes.Out, childCalc);
        Exec(child, childCalc, WorkflowNodeTypes.Out, childReturn);
        ShortcutsStore.ResetForTesting(new ShortcutsDocument { Version = 3, Shortcuts = [child] });

        var call = N("runShortcut", 0, 0, ("target", child.Id), ("arguments", """{"amount":21}"""));
        var caller = NewShortcut(call);
        Exec(caller, Start(caller), WorkflowNodeTypes.Out, call);
        result = RunGraph(caller);
        Check(result.Success && result.Data[call.Id]["result"].ToText() == "42",
            "e2e: parent passes an input, child computes, returns a value");

        // 4) An invalid graph executes zero steps and reports the validator's message.
        var broken = N("setVar", 0, 0, ("name", "x"), ("value", "1"));
        var unreachable = N("setClipboard", 300, 0);
        unreachable.Params["text"] = ShortcutValue.Variable(unreachable.Id, "text");
        var invalid = NewShortcut(broken, unreachable);
        Exec(invalid, Start(invalid), WorkflowNodeTypes.Out, broken);
        harness = new Harness();
        result = RunGraph(invalid, harness);
        Check(!result.Success && result.ErrorCode == "InvalidGraph",
            "an invalid graph fails before executing anything");
        Check(harness.System.ClipboardWrites.Count == 0, "no step ran for an invalid graph");
    }

    // ---- fakes --------------------------------------------------------------------

    /// <summary>Records what the workflow asked for; never touches the machine.</summary>
    private sealed class FakeSystem : ISystemAdapter
    {
        public string? Clipboard { get; set; }
        public List<string> ClipboardWrites { get; } = [];
        public double Volume { get; set; } = 50;
        public bool Muted { get; set; }
        public string LastScreenshot { get; set; } = @"C:\shots\shot.png";

        public void OpenTarget(string target, string? arguments) { }
        public void OpenFolder(string path) { }
        public void OpenUrl(string url) { }
        public string CaptureScreenshot(string? directory, ScreenshotScope scope) => LastScreenshot;
        public double GetVolume() => Volume;
        public void SetVolume(double percent) => Volume = percent;
        public bool GetMute() => Muted;
        public void SetMute(bool muted) => Muted = muted;
        public void LockWorkStation() { }
        public void Suspend() { }
        public void EmptyRecycleBin() { }
        public void MinimizeAll() { }
        public void ShowNotification(string message, string? title) { }
        public void TypeText(string text) { }
        public void SendKeySequence(IReadOnlyList<string> combinations) { }
        public void SendHotkey(string gesture, int holdMs) { }

        public long FindWindow(string title, WindowMatch match) => 0;

        public bool WaitForWindow(string title, WindowMatch match, TimeSpan timeout, CancellationToken token,
            out long handle)
        {
            handle = 0;
            return false;
        }

        public void ActivateWindow(long handle) { }
        public Task<bool> MediaTransportAsync(MediaAction action) => Task.FromResult(false);
        public bool HasClipboard => true;
        public Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);

        public Task SetClipboardTextAsync(string text)
        {
            Clipboard = text;
            ClipboardWrites.Add(text);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePrompts : IUserPromptService
    {
        public List<RunValue> Shown { get; } = [];

        public Task<RunValue?> AskAsync(string title, string? prompt, RunValue? defaultValue, DataType type,
            CancellationToken token) => Task.FromResult<RunValue?>(RunValue.FromText("answer"));

        public string? PickFile(string title, string? filter) => null;

        public string? PickFolder(string title) => null;

        public Task ShowResultAsync(string title, RunValue value)
        {
            Shown.Add(value);
            return Task.CompletedTask;
        }
    }

    /// <summary>Minimal HTTP/1.1 server on a loopback port (no URL ACL needed).</summary>
    private sealed class TestHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource cts = new();

        public TestHttpServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Url = $"http://127.0.0.1:{port}/";
            _ = Task.Run(AcceptLoopAsync);
        }

        public string Url { get; }

        private async Task AcceptLoopAsync()
        {
            while (!cts.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cts.Token);
                }
                catch
                {
                    return;
                }

                _ = Task.Run(() => ServeAsync(client));
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            {
                var stream = client.GetStream();
                var buffer = new byte[4096];
                var read = await stream.ReadAsync(buffer, cts.Token);
                var request = Encoding.ASCII.GetString(buffer, 0, read);
                var path = request.Split(' ').ElementAtOrDefault(1) ?? "/";

                var (status, contentType, body) = path switch
                {
                    "/empty" => ("200 OK", "text/plain; charset=utf-8", Array.Empty<byte>()),
                    "/missing" => ("404 Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("not found")),
                    "/gbk" => ("200 OK", "text/plain; charset=gbk", Gbk("中文")),
                    "/big" => ("200 OK", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(new string('x', 25000))),
                    "/items" => ("200 OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes("""{"items":["a","b","c"]}""")),
                    _ => ("404 Not Found", "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("?")),
                };

                var header = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                             $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                var head = Encoding.ASCII.GetBytes(header);
                await stream.WriteAsync(head, cts.Token);
                await stream.WriteAsync(body, cts.Token);
                await stream.FlushAsync(cts.Token);
            }
        }

        private static byte[] Gbk(string text)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GBK").GetBytes(text);
            }
            catch
            {
                return Encoding.UTF8.GetBytes(text);
            }
        }

        public void Dispose()
        {
            try { cts.Cancel(); } catch { }
            try { listener.Stop(); } catch { }
            cts.Dispose();
        }
    }
}
