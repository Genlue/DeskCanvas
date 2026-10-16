using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using DeskCanvas.Core;
using Shortcuts.Models;
using Shortcuts.Services;

namespace ShortcutsSmoke;

/// <summary>
/// Headless smoke checks for the Shortcuts plugin: glyph parsing, store round-trips, the
/// sequential runner, magic variables, control flow, cancellation, busy guard and failure
/// paths. Never touches live user data (the sandbox env var is set before Const is touched)
/// and never starts external processes.
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = [];

    [STAThread]
    private static int Main()
    {
        var sandbox = Path.Combine(Path.GetTempPath(),
            "DeskCanvas-ShortcutsSmoke-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        // The headless platform provides the render interface the geometry parser needs —
        // the same setup the repo's other check harnesses use.
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        CheckGlyphs();
        CheckDocumentRoundTrip();
        CheckStoreCrud();
        CheckViewsConstruct();

        // The runner is exercised off the dispatcher thread; the process has no running
        // message loop, and nothing here depends on one.
        var asyncFailures = Task.Run(RunAsyncChecks).GetAwaiter().GetResult();
        Failures.AddRange(asyncFailures);

        Console.WriteLine();
        if (Failures.Count > 0)
        {
            Console.WriteLine($"ShortcutsSmoke: {Failures.Count} failure(s)");
            foreach (var failure in Failures) Console.WriteLine("  - " + failure);
            return 1;
        }

        Console.WriteLine("ShortcutsSmoke: all checks passed");
        return 0;
    }

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
        if (!ok) Failures.Add(what);
    }

    // ---- glyphs --------------------------------------------------------------------

    private static void CheckGlyphs()
    {
        foreach (var definition in ActionCatalog.All)
            Check(GlyphLibrary.GetGeometry(definition.Glyph) != null,
                $"glyph '{definition.Glyph}' parses ({definition.Type})");

        foreach (var id in GlyphLibrary.PickerGlyphs)
            Check(GlyphLibrary.GetGeometry(id) != null, $"picker glyph '{id}' parses");

        Check(GlyphLibrary.GetGeometry("definitely-not-a-glyph") != null,
            "unknown glyph falls back to bolt");
    }

    // ---- document ------------------------------------------------------------------

    private static void CheckDocumentRoundTrip()
    {
        var document = new ShortcutsDocument { Shortcuts = [ShortcutTemplates.QuietFocus()] };
        var json = JsonSerializer.Serialize(document, DocumentJson.FileOptions);
        var back = JsonSerializer.Deserialize<ShortcutsDocument>(json, DocumentJson.FileOptions)!;
        Check(back.Shortcuts.Count == 1 && back.Shortcuts[0].Actions.Count == 2,
            "document round-trips");
        Check(back.Shortcuts[0].ActionCount == 2, "action count counts leaves");
    }

    private static void CheckStoreCrud()
    {
        ShortcutsStore.ResetForTesting(null);
        var seeded = ShortcutsStore.Get();
        Check(seeded.Shortcuts.Count == 3, "fresh store seeds three starter shortcuts");
        Check(File.Exists(ShortcutsStore.FilePath), "store file written to the sandbox");

        var firstId = seeded.Shortcuts[0].Id;
        var duplicate = ShortcutsStore.Duplicate(firstId);
        Check(duplicate != null && duplicate.Id != firstId, "duplicate creates a new id");
        Check(ShortcutsStore.Get().Shortcuts.Count == 4, "duplicate appends to the document");
        Check(ShortcutsStore.Delete(duplicate!.Id), "delete removes the duplicate");
        Check(ShortcutsStore.Get().Shortcuts.Count == 3, "document back to three");
    }

    // ---- view contracts ------------------------------------------------------------

    private static void CheckViewsConstruct()
    {
        try
        {
            var card = new Shortcuts.Views.ShortcutsView(null, new ShortcutsModel(), null, null);
            Check(card != null, "ShortcutsView constructs");
        }
        catch (Exception ex)
        {
            Check(false, "ShortcutsView constructs - " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            var popup = new Shortcuts.Views.ShortcutsPopupWindow();
            Check(popup != null, "ShortcutsPopupWindow constructs");
        }
        catch (Exception ex)
        {
            Check(false, "ShortcutsPopupWindow constructs - " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            var editor = new Shortcuts.Views.ShortcutsEditorWindow();
            Check(editor != null, "ShortcutsEditorWindow constructs");
        }
        catch (Exception ex)
        {
            Check(false, "ShortcutsEditorWindow constructs - " + ex.GetType().Name + ": " + ex.Message);
        }

        try
        {
            var settings = new Shortcuts.Views.Settings.ShortcutsSettings(
                new NullLayoutProvider(), null, null);
            Check(settings != null, "ShortcutsSettings constructs");
        }
        catch (Exception ex)
        {
            Check(false, "ShortcutsSettings constructs - " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private sealed class NullLayoutProvider : DeskCanvas.Core.Interfaces.IWidgetLayoutProvider
    {
        public string ScreenId { get; set; } = string.Empty;
        public DeskCanvas.Core.Models.WidgetLayout Get() => new(string.Empty, string.Empty, 0, 0, 0, 0, null);
        public void Save(DeskCanvas.Core.Models.WidgetLayout data) { }
        public void Remove() { }
        public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanging;
        public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.WidgetLayout>? DataChanged;

        private void Suppress() => (DataChanging, DataChanged) = (null, null);
    }

    // ---- runner --------------------------------------------------------------------

    private static async Task<List<string>> RunAsyncChecks()
    {
        var failures = new List<string>();
        void CheckAsync(bool ok, string what)
        {
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what);
            if (!ok) failures.Add(what);
        }

        // Linear + magic variables + control flow.
        var first = ShortcutAction.With("currentDate", ("format", "yyyy"));
        var upper = ShortcutAction.Create("textTransform");
        upper.Params["operation"] = ShortcutValue.Literal("upper");
        upper.Params["input"] = ShortcutValue.Variable(ValueKinds.LastResult);

        var repeat = ShortcutAction.Create("repeat");
        repeat.Params["count"] = ShortcutValue.Literal("2");
        var repeatChild = ShortcutAction.With("comment", ("note", "looped"));
        repeat.Children.Add(repeatChild);

        var ifAction = ShortcutAction.Create("if");
        ifAction.Params["left"] = ShortcutValue.Variable(repeatChild.Id);
        ifAction.Params["op"] = ShortcutValue.Literal("equals");
        ifAction.Params["right"] = ShortcutValue.Literal("looped");
        var thenChild = ShortcutAction.With("comment", ("note", "then-branch"));
        var elseChild = ShortcutAction.With("comment", ("note", "else-branch"));
        ifAction.Children.Add(thenChild);
        ifAction.ElseChildren.Add(elseChild);

        var deterministic = ShortcutAction.With("randomNumber", ("min", "1"), ("max", "1"));
        var shortWait = ShortcutAction.With("wait", ("seconds", "0.05"));

        var scripted = new ShortcutDefinition
        {
            Name = "smoke-runner",
            Actions = [first, upper, repeat, ifAction, deterministic, shortWait],
        };

        var events = new List<RunEvent>();
        void OnEvent(RunEvent runEvent) => events.Add(runEvent);
        ShortcutRunner.RunEvent += OnEvent;
        var result = await ShortcutRunner.RunAsync(scripted, new RunServices());
        ShortcutRunner.RunEvent -= OnEvent;

        CheckAsync(result.Success, "scripted run succeeds");
        CheckAsync(result.Outputs.GetValueOrDefault(first.Id)?.Length == 4,
            "currentDate honours the format");
        CheckAsync(result.Outputs.GetValueOrDefault(upper.Id) ==
                  result.Outputs.GetValueOrDefault(first.Id)?.ToUpperInvariant(),
            "magic variable resolves to the previous output");
        CheckAsync(result.Outputs.GetValueOrDefault(deterministic.Id) == "1",
            "random min==max is deterministic");
        CheckAsync(result.Outputs.GetValueOrDefault(thenChild.Id) == "then-branch",
            "if takes the then-branch");
        CheckAsync(!result.Outputs.ContainsKey(elseChild.Id), "else-branch is not executed");
        CheckAsync(events.Any(e => e.Kind == RunEventKind.Started) &&
                   events.Any(e => e.Kind == RunEventKind.Finished),
            "started/finished events fire");

        // Cancellation.
        var cancellable = new ShortcutDefinition
        {
            Name = "smoke-cancel",
            Actions = [ShortcutAction.With("wait", ("seconds", "5"))],
        };
        var cancelTask = ShortcutRunner.RunAsync(cancellable, new RunServices());
        await Task.Delay(150);
        CheckAsync(ShortcutRunner.IsRunning(cancellable.Id), "active run is reported running");
        CheckAsync(ShortcutRunner.Cancel(cancellable.Id), "cancel request accepted");
        var cancelled = await cancelTask;
        CheckAsync(cancelled.Cancelled, "cancelled run reports cancelled");
        CheckAsync(!ShortcutRunner.IsRunning(cancellable.Id), "run is released after cancel");

        // Busy guard.
        var busy = new ShortcutDefinition
        {
            Name = "smoke-busy",
            Actions = [ShortcutAction.With("wait", ("seconds", "1"))],
        };
        var firstRun = ShortcutRunner.RunAsync(busy, new RunServices());
        var secondRun = await ShortcutRunner.RunAsync(busy, new RunServices());
        CheckAsync(secondRun.Busy, "overlapping run returns busy");
        ShortcutRunner.Cancel(busy.Id);
        await firstRun;

        // Failure paths.
        var missingParam = new ShortcutDefinition
        {
            Name = "smoke-fail",
            Actions = [ShortcutAction.With("openApp", ("target", ""))],
        };
        var failedEvents = new List<RunEvent>();
        void OnFailEvent(RunEvent runEvent)
        {
            if (runEvent.Kind == RunEventKind.StepFailed) failedEvents.Add(runEvent);
        }

        ShortcutRunner.RunEvent += OnFailEvent;
        var failed = await ShortcutRunner.RunAsync(missingParam, new RunServices());
        ShortcutRunner.RunEvent -= OnFailEvent;
        CheckAsync(!failed.Success && !string.IsNullOrEmpty(failed.Error),
            "empty required parameter fails the run");
        CheckAsync(failedEvents.Count == 1, "step-failed event fired once");

        var unknownAction = new ShortcutDefinition
        {
            Name = "smoke-unknown",
            Actions = [new ShortcutAction { Type = "not-a-real-action" }],
        };
        var unknownResult = await ShortcutRunner.RunAsync(unknownAction, new RunServices());
        CheckAsync(!unknownResult.Success, "unknown action type fails the run");

        // Host-integration action that only touches the sandboxed data folder.
        var remindersShortcut = new ShortcutDefinition
        {
            Name = "smoke-reminder",
            Actions = [ShortcutAction.With("addReminder", ("title", "smoke-reminder"))],
        };
        var reminderResult = await ShortcutRunner.RunAsync(remindersShortcut, new RunServices());
        CheckAsync(reminderResult.Success, "addReminder succeeds");
        CheckAsync(File.Exists(Const.RemindersFile) &&
                   File.ReadAllText(Const.RemindersFile).Contains("smoke-reminder"),
            "reminder appended to reminders.json");

        return failures;
    }
}
