// Headless regression suite for the v2 workflow canvas.
// Covers the incremental-editing contract that replaced the v1 "rebuild everything" model:
// card lifecycle events, node add/duplicate/delete without a full reload, link selection and
// deletion, run-state and diagnostics badges, minimap view state, the layered layout engine
// and the AI settings store round-trip.
// Usage: dotnet run --project tests/ShortcutsCanvasChecks -c Release
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Services.Ai;
using Shortcuts.Views;
using Shortcuts.Views.Controls;

namespace ShortcutsCanvasChecks;

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
            "DeskCanvas-CanvasChecks-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESKCANVAS_SHORTCUTS_DISABLE_HOTKEYS", "1");

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        Console.WriteLine("=== Shortcuts canvas checks ===");

        // 一切都在主（UI）线程上同步执行 —— 与 ShortcutsRender 相同的模式；
        // 从池线程 Invoke 回 UI 线程会与 Main 的等待互相卡死。
        RunAll();

        Console.WriteLine($"\nShortcutsCanvasChecks: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    private static void RunAll()
    {
        CheckCardLifecycle();
        CheckIncrementalEdits();
        CheckLinkSelectionAndDeletion();
        CheckDiagnosticsBadges();
        CheckMinimapViewState();
        CheckLayoutEngine();
        CheckEditorSmoke();
        CheckAiSettingsStore();
        CheckAiServiceValidation();
        CheckRunnerUiAffinity();
    }

    private static Window ShowWindow(Control content)
    {
        var window = new Window { Width = 900, Height = 660, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static WorkflowGraph BuildGraph()
    {
        var graph = new WorkflowGraph();
        var start = new WorkflowNode { Type = WorkflowNodeTypes.Start, X = 0, Y = 0 };
        var notify = WorkflowNode.With("notify", 300, 0, ("message", "hello"));
        graph.Nodes.AddRange([start, notify]);
        graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, notify.Id));
        return graph;
    }

    // ---- 1. card lifecycle ---------------------------------------------------------

    private static void CheckCardLifecycle()
    {
        Console.WriteLine("--- card lifecycle ---");
        var canvas = new WorkflowCanvas();
        var created = new List<string>();
        canvas.NodeCardCreated += card => created.Add(card.Node.Id);

        var graph = BuildGraph();
        canvas.Load(graph);
        Check(canvas.Graph == graph, "Load keeps the graph reference");
        Check(created.Count == 2, "NodeCardCreated fires once per card on Load");
        Check(canvas.CardOf(graph.Start.Id) != null, "CardOf resolves the start card");

        var window = ShowWindow(canvas);
        Dispatcher.UIThread.RunJobs();
        Check(canvas.CardOf(graph.Start.Id)!.Bounds.Height > 0, "cards are laid out inside a window");
        window.Close();
    }

    // ---- 2. incremental edits ------------------------------------------------------

    private static void CheckIncrementalEdits()
    {
        Console.WriteLine("--- incremental edits ---");
        var canvas = new WorkflowCanvas();
        var graph = BuildGraph();
        canvas.Load(graph);
        var window = ShowWindow(canvas);
        Dispatcher.UIThread.RunJobs();

        var cardCountBefore = canvas.Graph!.Nodes.Count;
        var definition = ActionCatalog.Get("notify")!;
        var added = canvas.AddNode(definition);
        Check(added != null && canvas.CardOf(added.Id) != null, "AddNode creates a card without a reload");
        Check(canvas.Graph!.Nodes.Count == cardCountBefore + 1, "AddNode appends exactly one node");

        // Free the start's out port first, then: selected start + AddNode auto-wires the
        // exec continuation.
        graph.Links.Clear();
        canvas.InvalidateLinks();
        canvas.Select(graph.Start.Id);
        var chained = canvas.AddNode(ActionCatalog.Get("notify")!);
        Check(chained != null && graph.Links.Count == 1, "AddNode auto-connects from a selected node with a free out port");

        var duplicates = canvas.DuplicateSelected();
        Check(duplicates == 1, "DuplicateSelected copies the selection");
        Check(canvas.SelectedNodeIds.Count == 1, "the copy becomes the selection");

        var before = canvas.Graph.Nodes.Count;
        var deleted = canvas.DeleteSelected();
        Check(deleted == 1 && canvas.Graph.Nodes.Count == before - 1, "DeleteSelected removes the selected node");
        Check(canvas.CardOf(canvas.SelectedNodeId ?? "") == null, "the deleted card is gone");

        // Start node is not deletable.
        canvas.Select(graph.Start.Id);
        Check(canvas.DeleteSelected() == 0, "the start node cannot be deleted");

        // Multi-delete: everything except start, with the touching links.
        canvas.SelectAll();
        var selectedCount = canvas.SelectedNodeIds.Count;
        var multiDeleted = canvas.DeleteSelected();
        Check(multiDeleted == selectedCount, "DeleteSelected removes every selected node");
        Check(graph.Links.Count == 0, "links touching removed nodes are pruned");
        Check(graph.Nodes.All(n => n.Type == WorkflowNodeTypes.Start), "only the start node survives the full delete");

        window.Close();
    }

    // ---- 3. link selection + deletion ---------------------------------------------

    private static void CheckLinkSelectionAndDeletion()
    {
        Console.WriteLine("--- link selection + deletion ---");
        var canvas = new WorkflowCanvas();
        var graph = BuildGraph();
        var link = graph.Links[0];
        canvas.Load(graph);
        var window = ShowWindow(canvas);
        Dispatcher.UIThread.RunJobs();

        var selectionEvents = 0;
        canvas.SelectionChanged += _ => selectionEvents++;

        canvas.SelectLink(link.Id);
        Check(canvas.SelectedLinkId == link.Id, "SelectLink selects an existing link");
        Check(canvas.SelectedNodeId == null && canvas.SelectedNodeIds.Count == 0, "link selection clears node selection");
        Check(selectionEvents > 0, "SelectionChanged fires for link selection");

        canvas.Select(graph.Start.Id);
        Check(canvas.SelectedLinkId == null, "node selection clears link selection");

        canvas.SelectLink(link.Id);
        var deleted = canvas.DeleteSelected();
        Check(deleted == 1, "DeleteSelected removes the selected link");
        Check(graph.Links.Count == 0, "the graph no longer holds the deleted link");
        Check(canvas.SelectedLinkId == null, "link selection resets after deletion");

        // Re-create the link through the model and delete it again (idempotent path).
        var recreated = WorkflowLink.Exec(graph.Start.Id, WorkflowNodeTypes.Out, graph.Nodes[1].Id);
        graph.Links.Add(recreated);
        canvas.SelectLink(recreated.Id);
        Check(canvas.DeleteSelected() == 1, "a re-created link deletes the same way");

        window.Close();
    }

    // ---- 4. diagnostics badges ------------------------------------------------------

    private static void CheckDiagnosticsBadges()
    {
        Console.WriteLine("--- diagnostics badges ---");
        var canvas = new WorkflowCanvas();
        var graph = BuildGraph();
        canvas.Load(graph);
        var window = ShowWindow(canvas);
        Dispatcher.UIThread.RunJobs();

        var node = graph.Nodes[1];
        canvas.SetDiagnostics(
        [
            new WorkflowDiagnostic("RequiredEmpty", DiagnosticSeverity.Warning, "Shortcuts_Diag_RequiredEmpty", node.Id, "message"),
            new WorkflowDiagnostic("NotConnected", DiagnosticSeverity.Error, "Shortcuts_Diag_NotConnected", node.Id),
        ]);

        var card = canvas.CardOf(node.Id)!;
        Check(card.ErrorBadgeCount == 2, "the card badge counts the node's diagnostics");

        canvas.SetDiagnostics([]);
        Check(canvas.CardOf(node.Id)!.ErrorBadgeCount == 0, "an empty report clears the badge");

        // RefreshNode keeps the badge that belongs to the node.
        canvas.SetDiagnostics([new WorkflowDiagnostic("NotConnected", DiagnosticSeverity.Error, "Shortcuts_Diag_NotConnected", node.Id)]);
        canvas.RefreshNode(node.Id);
        Check(canvas.CardOf(node.Id)!.ErrorBadgeCount == 1, "RefreshNode restores the badge");
        Check(canvas.CardOf(node.Id)!.Classes.Contains("selected") == false, "RefreshNode keeps an unselected card unselected");

        canvas.Select(node.Id);
        canvas.RefreshNode(node.Id);
        Check(canvas.CardOf(node.Id)!.Classes.Contains("selected"), "RefreshNode restores the selected state");

        window.Close();
    }

    // ---- 5. minimap view state ------------------------------------------------------

    private static void CheckMinimapViewState()
    {
        Console.WriteLine("--- minimap view state ---");
        var canvas = new WorkflowCanvas();
        var graph = BuildGraph();
        canvas.Load(graph);
        var window = ShowWindow(canvas);
        Dispatcher.UIThread.RunJobs();

        var minimap = new WorkflowMinimap { Source = canvas, Width = 200, Height = 130 };
        var host = new Window { Width = 400, Height = 300, Content = minimap };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        Check(canvas.ContentBounds() is { }, "ContentBounds covers the loaded graph");
        var viewEvents = 0;
        canvas.ViewChanged += () => viewEvents++;
        canvas.CenterOnGraphPoint(new Point(150, 40));
        Check(viewEvents > 0, "CenterOnGraphPoint raises ViewChanged");
        var viewport = canvas.ViewportGraphRect();
        Check(viewport.Contains(new Point(150, 40)), "the viewport contains the centered graph point");

        var scaleBefore = canvas.Scale;
        canvas.ZoomBy(1.2);
        Check(Math.Abs(canvas.Scale - scaleBefore * 1.2) < 0.01, "ZoomBy scales around the viewport centre");

        // Minimap rendering must not throw headless.
        minimap.InvalidateVisual();
        Dispatcher.UIThread.RunJobs();
        Check(true, "minimap renders without exceptions");

        host.Close();
        window.Close();
    }

    // ---- 6. layout engine -----------------------------------------------------------

    private static void CheckLayoutEngine()
    {
        Console.WriteLine("--- layered layout ---");
        var graph = new WorkflowGraph();
        var start = new WorkflowNode { Type = WorkflowNodeTypes.Start };
        var a = WorkflowNode.Create("getClipboard", 0, 0);
        var branch = WorkflowNode.With("branch", 0, 0, ("left", "{{" + a.Id + "}}"), ("op", "isEmpty"));
        var empty = WorkflowNode.With("notify", 0, 0, ("message", "empty"));
        var loop = WorkflowNode.With("forLoop", 0, 0, ("count", "3"));
        var body = WorkflowNode.Create("screenshot", 0, 0);
        var note = new WorkflowNode { Type = WorkflowNodeTypes.Note };
        graph.Nodes.AddRange([start, a, branch, empty, loop, body, note]);
        graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, a.Id));
        graph.Links.Add(WorkflowLink.Exec(a.Id, WorkflowNodeTypes.Out, branch.Id));
        graph.Links.Add(WorkflowLink.Exec(branch.Id, WorkflowNodeTypes.TruePort, empty.Id));
        graph.Links.Add(WorkflowLink.Exec(branch.Id, WorkflowNodeTypes.FalsePort, loop.Id));
        graph.Links.Add(WorkflowLink.Exec(loop.Id, WorkflowNodeTypes.BodyPort, body.Id));

        WorkflowLayout.Arrange(graph);

        Check(graph.Nodes.All(n => double.IsFinite(n.X) && double.IsFinite(n.Y)), "every node lands on finite coordinates");
        Check(start.X < a.X, "start sits left of its successor");
        Check(a.X < branch.X, "the chain advances to the right");
        Check(branch.X < loop.X || branch.X < empty.X, "branches move right of the branch node");
        Check(note.Y > Math.Max(empty.Y, Math.Max(loop.Y, body.Y)), "notes park below the main graph");

        var overlap = graph.Nodes
            .Where(n => n.Type != WorkflowNodeTypes.Note)
            .GroupBy(n => (X: n.X, Y: n.Y))
            .Any(g => g.Count() > 1);
        Check(!overlap, "no two nodes share the same position");

        // Determinism: a second arrange produces identical coordinates.
        var snapshot = graph.Nodes.Select(n => (n.Id, n.X, n.Y)).ToList();
        WorkflowLayout.Arrange(graph);
        Check(graph.Nodes.Select(n => (n.Id, n.X, n.Y)).SequenceEqual(snapshot), "the layout is deterministic");

        // Migration's AutoLayout delegates to the same engine.
        var migrated = BuildGraph();
        WorkflowMigration.AutoLayout(migrated);
        Check(migrated.Nodes[1].X > migrated.Nodes[0].X, "WorkflowMigration.AutoLayout still arranges left-to-right");
    }

    // ---- 7. editor smoke ------------------------------------------------------------

    private static void CheckEditorSmoke()
    {
        Console.WriteLine("--- editor window smoke ---");
        try
        {
            ShortcutsStore.ResetForTesting(new ShortcutsDocument
            {
                Version = 3,
                Shortcuts = [new ShortcutDefinition { Name = "画布检查", Glyph = "bolt", Color = "#534AB7", Graph = BuildGraph() }],
            });

            var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id);
            editor.Show();
            Dispatcher.UIThread.RunJobs();

            var canvas = editor.GetVisualDescendants().OfType<WorkflowCanvas>().FirstOrDefault();
            Check(canvas != null, "the editor hosts the v2 canvas");
            Check(canvas!.Graph?.Nodes.Count >= 2, "the editor loaded the graph");

            editor.Close();
            Dispatcher.UIThread.RunJobs();
            Check(true, "the editor constructs, renders and closes headless");
        }
        catch (Exception ex)
        {
            Check(false, "editor smoke - " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    // ---- 8. AI settings store ---------------------------------------------------------

    private static void CheckAiSettingsStore()
    {
        Console.WriteLine("--- AI settings store ---");
        var settings = new AiSettings
        {
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKey = "sk-test-123",
            Model = "deepseek-chat",
            Temperature = 0.7,
        };

        AiSettingsStore.Save(settings);
        var loaded = AiSettingsStore.Load();
        Check(loaded.BaseUrl == settings.BaseUrl, "the base URL round-trips");
        Check(loaded.ApiKey == settings.ApiKey, "the API key round-trips");
        Check(loaded.Model == settings.Model, "the model round-trips");
        Check(Math.Abs(loaded.Temperature - settings.Temperature) < 1e-9, "the temperature round-trips");

        // A corrupt file falls back to defaults instead of throwing.
        File.WriteAllText(AiSettingsStore.FilePath, "{ not json");
        var fallback = AiSettingsStore.Load();
        Check(fallback.ApiKey.Length == 0, "a corrupt file falls back to defaults");

        // A missing file behaves the same way.
        File.Delete(AiSettingsStore.FilePath);
        Check(AiSettingsStore.Load().Model.Length > 0, "a missing file yields defaults");
    }

    // ---- 9. AI service validation ------------------------------------------------------

    private static void CheckAiServiceValidation()
    {
        Console.WriteLine("--- AI service validation ---");
        var result = AiWorkflowService.GenerateAsync(
            new AiSettings { ApiKey = "" },
            "帮我做一个提醒",
            currentGraph: null,
            currentOwner: null).GetAwaiter().GetResult();

        Check(!result.Success, "a missing API key fails fast");
        Check(!string.IsNullOrEmpty(result.Error), "the failure carries a message");

        var noPrompt = AiWorkflowService.GenerateAsync(
            new AiSettings { ApiKey = "sk-x", BaseUrl = "https://127.0.0.1:1/v1", Model = "x" },
            "test",
            currentGraph: null,
            currentOwner: null).GetAwaiter().GetResult();
        Check(!noPrompt.Success, "an unreachable endpoint fails without throwing");
    }

    // ---- 10. runner UI affinity ---------------------------------------------------------

    private static ShortcutDefinition BuildVarShortcut()
    {
        var graph = new WorkflowGraph();
        var start = new WorkflowNode { Type = WorkflowNodeTypes.Start };
        var setVar = WorkflowNode.With("setVar", 100, 0, ("name", "x"), ("value", "1"));
        graph.Nodes.AddRange([start, setVar]);
        graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, setVar.Id));
        return new ShortcutDefinition { Name = "线程亲和检查", Graph = graph };
    }

    private static void CheckRunnerUiAffinity()
    {
        Console.WriteLine("--- runner UI affinity ---");

        // 场景 A：无 TopLevel（无头测试的老路径）→ 在调用线程直接执行，行为不变。
        var pooled = ShortcutRunner.RunAsync(BuildVarShortcut(), new RunServices())
            .GetAwaiter().GetResult();
        Check(pooled.Success, "no TopLevel: the run completes on the calling thread");

        // 场景 B：有 TopLevel + 从池线程调用（等价于全局热键入口）→ 必须经 UI 线程派发，
        // 完成且成功 —— 这是 v2 修掉的"动作生效了却报跨线程错误"的回归防线。
        var window = new Window { Width = 200, Height = 120 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var services = new RunServices { TopLevelAccessor = () => window };
            var dispatched = Task.Run(() => ShortcutRunner.RunAsync(BuildVarShortcut(), services));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!dispatched.IsCompleted && sw.Elapsed < TimeSpan.FromSeconds(10))
            {
                // 无头环境没有运行中的消息循环，需要手动泵 dispatcher 让派发任务执行。
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }

            Check(dispatched.IsCompleted, "TopLevel + background entry: the dispatch completes");
            Check(dispatched.Status == TaskStatus.RanToCompletion && dispatched.Result.Success,
                "TopLevel + background entry: the run succeeds via the UI thread");
        }
        finally
        {
            window.Close();
        }
    }
}
