using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Services.Executors;
using Shortcuts.ViewModels;

namespace ShortcutsSmoke;

/// <summary>
/// Headless smoke checks for the Shortcuts plugin: glyph parsing, store round-trips, the
/// sequential runner, magic variables, control flow, cancellation, busy guard and failure
/// paths. Never touches live user data (the sandbox env var is set before Const is touched).
/// The only external processes started are the read-only shells behind the runCommand checks.
/// </summary>
internal static class Program
{
    private static readonly List<string> Failures = [];

    [STAThread]
    private static int Main()
    {
        var sandbox = Path.Combine(Path.GetTempPath(),
            "DeskCanvas-ShortcutsSmoke-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        // The headless platform provides the render interface the geometry parser needs —
        // the same setup the repo's other check harnesses use.
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        CheckGlyphs();
        CheckDocumentRoundTrip();
        CheckHotkeyModel();
        CheckCornerRadius();
        CheckNewActionsAndTemplates();
        CheckStoreCrud();
        CheckViewsConstruct();
        // After the view checks: this one publishes a monochrome theme into the plugin runtime,
        // and the runtime only ever takes the first publisher.
        CheckMonochromeTiles();
        CheckProcessGlobalRuntimes();

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
        Check(back.Shortcuts.Count == 1 && back.Shortcuts[0].EffectiveGraph.Nodes.Count == 3,
            "document round-trips (start + two actions)");
        Check(back.Shortcuts[0].ActionCount == 2, "action count counts executable nodes");
        Check(back.Shortcuts[0].Actions.Count == 0, "the legacy action list is emptied by migration");
    }

    // ---- hotkey model --------------------------------------------------------------

    private static void CheckHotkeyModel()
    {
        Check(HotkeyGesture.TryParse("Ctrl+Alt+1", requireModifier: true, out var one, out _)
              && one.Text == "Ctrl+Alt+1" && one.VirtualKey == 0x31
              && one.Modifiers == (HotkeyModifiers.Control | HotkeyModifiers.Alt),
            "hotkey parses Ctrl+Alt+1");

        Check(HotkeyGesture.TryParse("alt+ctrl+shift+win+f12", requireModifier: true, out var all, out _)
              && all.Text == "Ctrl+Alt+Shift+Win+F12",
            "hotkey canonicalizes modifier order");

        Check(!HotkeyGesture.TryParse("A", requireModifier: true, out _, out var bareError)
              && bareError == HotkeyParseError.ModifierRequired,
            "bare letter needs a modifier");

        Check(HotkeyGesture.TryParse("F5", requireModifier: true, out _, out _),
            "F5 can stand alone");

        Check(!HotkeyGesture.TryParse("Ctrl+Alt", requireModifier: true, out _, out var missingKey)
              && missingKey == HotkeyParseError.MissingKey,
            "modifier-only text is rejected");

        Check(!HotkeyGesture.TryParse("Ctrl+Alt+NoSuchKey", requireModifier: true, out _, out _),
            "unknown key is rejected");

        Check(HotkeyGesture.TryParse("ctrl + alt + k", requireModifier: false, out var typed, out _)
              && typed.Text == "Ctrl+Alt+K",
            "hand-typed lowercase/spaced combo canonicalizes");

        Check(HotkeyGesture.TryParse("ctrl+shift+-", requireModifier: false, out var symbol, out _)
              && symbol.Text == "Ctrl+Shift+-",
            "punctuation keys parse");

        Check(HotkeyGesture.TryParse("Ctrl+Alt+Space", requireModifier: true, out var space, out _)
              && space.Text == "Ctrl+Alt+Space",
            "named keys keep canonical casing");

        const string oldJson = """
        {"Version":1,"Shortcuts":[{"Id":"old","Name":"Old","Glyph":"bolt","Color":"#534AB7","ConfirmBeforeRun":false,"Actions":[]}]}
        """;
        var oldDocument = JsonSerializer.Deserialize<ShortcutsDocument>(oldJson, DocumentJson.FileOptions)!;
        Check(oldDocument.Shortcuts[0].Hotkey == null, "old JSON without Hotkey loads as null");

        var serialized = JsonSerializer.Serialize(
            new ShortcutDefinition { Name = "no-hotkey" }, DocumentJson.Options);
        Check(!serialized.Contains("Hotkey", StringComparison.Ordinal),
            "unbound hotkey is omitted from JSON");

        var duplicateSource = new ShortcutDefinition
        {
            Name = "duplicate-source", Glyph = "bolt", Hotkey = "Ctrl+Alt+8",
        };
        ShortcutsStore.ResetForTesting(new ShortcutsDocument
        {
            Version = 1,
            Shortcuts = [duplicateSource],
        });
        var duplicate = ShortcutsStore.Duplicate(duplicateSource.Id);
        Check(duplicate != null && duplicate.Hotkey == null,
            "duplicating a shortcut does not copy its hotkey");

        // Leave a fresh sandbox for the store CRUD checks: Duplicate wrote the test document.
        try { File.Delete(ShortcutsStore.FilePath); } catch { }
        ShortcutsStore.ResetForTesting(null);
    }

    // ---- corner geometry -----------------------------------------------------------

    private static void CheckCornerRadius()
    {
        var tile = new ShortcutTileViewModel(new ShortcutDefinition { Name = "corner", Glyph = "bolt" });
        tile.SetCardRadius(16);
        tile.SetTileSize(140, 45);

        var outer = tile.TileCornerRadius.TopLeft;
        var inner = tile.IconCornerRadius.TopLeft;
        Check(outer is >= 8 and <= 16, $"tile radius follows card radius (got {outer:0.##})");
        Check(Math.Abs(inner - Math.Max(4, outer - 6)) < 0.01,
            $"icon radius is outer - 6 (outer {outer:0.##}, inner {inner:0.##})");

        tile.SetTileSize(140, 20);
        Check(tile.TileCornerRadius.TopLeft <= outer, "tile radius shrinks with a short tile");
        Check(tile.IconCornerRadius.TopLeft >= 4, "icon radius never drops below 4");

        tile.SetCardRadius(0);
        Check(tile.TileCornerRadius.TopLeft == 0 && tile.IconCornerRadius.TopLeft == 0,
            "square host keeps square tiles");

        tile.SetCardRadius(24);
        tile.SetTileSize(200, 80);
        Check(tile.TileCornerRadius.TopLeft > outer, "larger card radius grows the tile radius");

        var hotkeyTile = new ShortcutTileViewModel(new ShortcutDefinition
        {
            Name = "hotkey-tile", Glyph = "bolt", Hotkey = "Ctrl+Alt+1",
        });
        hotkeyTile.SetTileSize(100, 40);
        Check(!hotkeyTile.HasHotkeyBadge, "very narrow tile hides the hotkey badge");
        hotkeyTile.SetTileSize(130, 40);
        Check(hotkeyTile.HasHotkeyBadge && !hotkeyTile.ShowActionCountText,
            "2x2-width tile shows the badge instead of the action count");
        hotkeyTile.SetTileSize(220, 40);
        Check(hotkeyTile.HasHotkeyBadge && hotkeyTile.ShowActionCountText,
            "roomy tile shows both the badge and the action count");

        // The card and the quick panel must read their geometry from the same view-model
        // properties: a row on the panel and a tile on the card are the same rectangle with
        // the same radius, whatever the widget's own radius is.
        var cardXaml = ReadSource("Views", "ShortcutsView.axaml");
        var panelXaml = ReadSource("Views", "ShortcutsPopupWindow.axaml");
        var panelCs = ReadSource("Views", "ShortcutsPopupWindow.axaml.cs");

        Check(cardXaml.Contains("Classes=\"shortcut-tile\"") && cardXaml.Contains("CornerRadius=\"{Binding TileCornerRadius}\""),
            "the card's tile button carries the tile radius (not the Fluent default)");
        Check(panelXaml.Contains("CornerRadius=\"{Binding TileCornerRadius}\""),
            "the panel row carries the tile radius too");
        Check(panelXaml.Contains("CornerRadius=\"{Binding IconCornerRadius}\""),
            "the panel's icon block stays concentric with its row");
        Check(panelCs.Contains("tile.SetCardRadius(cardCornerRadius)"),
            "the panel pushes the host card radius into its rows");
    }

    /// <summary>
    /// 单色模式下指令也要是单色的：图标块、字形描边、标题与副标题都得换成单色分支的画笔。
    /// 断言的是「两种模式下的画笔必须是不同实例」——具体色值取决于主题设置，不该被这条检查钉死。
    /// </summary>
    private static void CheckMonochromeTiles()
    {
        // Colored first: the monochrome branch is the one that has to differ from it.
        var colored = new ShortcutTileViewModel(new ShortcutDefinition { Name = "colored", Glyph = "bolt", Color = "#3366CC" });
        colored.SetCardRadius(16);
        colored.ApplyTheme(true);
        var coloredIcon = colored.IconBrush;
        var coloredStroke = colored.GlyphStrokeBrush;
        var coloredName = colored.NameBrush;

        ShortcutsRuntime.Publish(new MonochromeSettingsProvider(), null);

        var mono = new ShortcutTileViewModel(new ShortcutDefinition { Name = "mono", Glyph = "bolt", Color = "#3366CC" });
        mono.SetCardRadius(16);
        mono.ApplyTheme(true);

        Check(!ReferenceEquals(mono.IconBrush, coloredIcon),
            "monochrome repaints the tile's icon block");
        Check(!ReferenceEquals(mono.GlyphStrokeBrush, coloredStroke),
            "monochrome gives the glyph a contrasting stroke");
        Check(!ReferenceEquals(mono.NameBrush, coloredName),
            "monochrome repaints the name and subtitle");

        // 黑白 is the variant the report was about: the icon block has to become white in dark
        // mode, NOT the system accent. Resolving the generic accent resource first (the old
        // order) painted every tile in the accent colour and looked like "monochrome does
        // nothing" even when the monochrome branch was reached.
        MonochromeSettingsProvider.Variant = MonochromeStyle.BlackWhite;
        var blackWhite = new ShortcutTileViewModel(new ShortcutDefinition { Name = "bw", Glyph = "bolt", Color = "#3366CC" });
        blackWhite.SetCardRadius(16);
        blackWhite.ApplyTheme(true);
        Check(blackWhite.IconBrush is ISolidColorBrush { Color: var bwColor } && bwColor == Colors.White,
            "黑白 in dark mode paints the icon block white (not the accent)");
        Check(blackWhite.GlyphStrokeBrush is ISolidColorBrush { Color: var strokeColor } && strokeColor == Colors.Black,
            "黑白 keeps the glyph readable: black stroke on the white block");

        // 强调色 with an explicit accent must use THAT accent, not the app-level ramp.
        MonochromeSettingsProvider.Variant = MonochromeStyle.Accent;
        MonochromeSettingsProvider.Accent = "#C0392B";
        var accented = new ShortcutTileViewModel(new ShortcutDefinition { Name = "accent", Glyph = "bolt", Color = "#3366CC" });
        accented.SetCardRadius(16);
        accented.ApplyTheme(false);
        Check(accented.IconBrush is ISolidColorBrush { Color: var accentColor } && accentColor == Color.Parse("#C0392B"),
            "强调色 uses the configured accent colour");
        MonochromeSettingsProvider.Accent = null;
    }

    /// <summary>Reads a file from the Shortcuts plugin folder.</summary>
    private static string ReadSource(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts")))
            dir = dir.Parent!;

        return File.ReadAllText(Path.Combine(new[] { dir!.FullName, "src", "Widgets", "Shortcuts" }.Concat(parts).ToArray()));
    }

    /// <summary>Reads a repo-relative file (the plugin-loading rule lives outside this widget).</summary>
    private static string ReadRepo(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "scripts")))
            dir = dir.Parent!;

        return File.ReadAllText(Path.Combine(new[] { dir!.FullName }.Concat(parts).ToArray()));
    }

    // ---- plugin loading ------------------------------------------------------------

    /// <summary>
    /// The WinRT runtime must be one instance per process.
    /// <para>
    /// Every widget lives in its own collectible <c>AssemblyLoadContext</c>, so a per-context copy
    /// of <c>WinRT.Runtime</c> means a second <c>ComWrappers.RegisterForMarshalling</c>, which
    /// throws <c>InvalidOperationException: Attempt to update previously set global instance.</c>
    /// That is the red toast every notification/media action produced once another widget had
    /// already touched WinRT (reproduced with two load contexts over the same widget folder).
    /// Pinning the rule here keeps a future refactor of the loader from reintroducing it.
    /// </para>
    /// </summary>
    private static void CheckProcessGlobalRuntimes()
    {
        var loader = ReadRepo("src", "DeskCanvas.Core", "Services", "AssemblyProvider.cs");

        Check(loader.Contains("\"WinRT.Runtime\"") && loader.Contains("\"Microsoft.Windows.SDK.NET\""),
            "WinRT.Runtime and the WinRT projection are registered as process-global");
        Check(loader.Contains("SharedRuntime.Load(sharedName, sharedPath)"),
            "plugin contexts resolve a process-global assembly through the one shared loader");
        Check(loader.Contains("class SharedRuntimeLoadContext"),
            "the shared loader is a dedicated, never-collected load context");
    }

    // ---- ecosystem -----------------------------------------------------------------

    private static void CheckNewActionsAndTemplates()
    {
        foreach (var type in new[] { "openFolder", "minimizeAll", "typeText", "sendKeySequence", "sendHotkey", "runCommand", "runShortcut", "httpRequest" })
        {
            Check(ActionCatalog.Get(type) != null, $"catalog defines {type}");
            Check(ActionExecutors.Find(type) != null, $"executor exists for {type}");
        }

        Check(ActionCatalog.Get("sendHotkey")!.Params.Any(p => p.Kind == ParamKind.Hotkey),
            "sendHotkey exposes a Hotkey parameter");

        // The "AI 帮写" spec is generated from the catalog; it must cover every visible action,
        // so a new action can never ship without appearing in the prompt document.
        var spec = WorkflowSpecDocument.Build();
        Check(ActionCatalog.Visible.All(d => spec.Contains($"`{d.Type}`", StringComparison.Ordinal)),
            "AI spec document covers every visible action");

        // The spec must demand a security review pass (sub-agent when available) and allow a
        // short safety notice before the JSON.
        Check(spec.Contains("安全审查", StringComparison.Ordinal) &&
              spec.Contains("子代理", StringComparison.Ordinal) &&
              spec.Contains("⚠️ 安全提示", StringComparison.Ordinal),
            "AI spec document requires a security review and user-facing safety notices");

        // Import must extract the JSON out of a full model reply (fenced block or prose-wrapped).
        var sampleJson = """{"name":"t","nodes":[{"id":"s","type":"start"}],"links":[]}""";
        Check(WorkflowImport.ExtractJson(sampleJson) == sampleJson, "extract keeps verbatim JSON");
        Check(WorkflowImport.ExtractJson("说明文字\n```json\n" + sampleJson + "\n```\n结尾") == sampleJson,
            "extract pulls the fenced json block");
        Check(WorkflowImport.ExtractJson("⚠️ 安全提示：……\n" + sampleJson + "\n（完）") == sampleJson,
            "extract pulls prose-wrapped JSON");
        Check(WorkflowImport.Parse(WorkflowImport.ExtractJson(
                "⚠️ 安全提示：示例\n```json\n" + sampleJson + "\n```")) is { Success: true },
            "import succeeds on a full model reply");

        Check(ActionCatalog.Get("openFolder")!.Params[0].Kind == ParamKind.FolderPath,
            "openFolder uses the folder picker");

        Check(ShortcutTemplates.Menu.Count >= 7,
            $"at least seven new-shortcut templates are offered (got {ShortcutTemplates.Menu.Count})");

        foreach (var (labelKey, create) in ShortcutTemplates.Menu)
        {
            var shortcut = create();
            Check(!string.IsNullOrWhiteSpace(Loc.Get(labelKey)) && !string.IsNullOrWhiteSpace(shortcut.Name),
                $"template '{labelKey}' has a localized name");
            Check(shortcut.EffectiveGraph.Nodes.All(n => ActionCatalog.Get(n.Type) != null),
                $"template '{labelKey}' only uses catalog actions");
            var report = WorkflowValidator.Validate(shortcut.EffectiveGraph, shortcut, forRun: true);
            Check(!report.HasErrors, $"template '{labelKey}' validates");
        }

        Check(ShortcutTemplates.ClockOut().ConfirmBeforeRun,
            "clock-out template requires confirmation");
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
        var first = ShortcutAction.Create("currentDate");
        var upper = ShortcutAction.Create("textTransform");
        upper.Params["operation"] = ShortcutValue.Literal("upper");
        upper.Params["input"] = ShortcutValue.Variable(ValueKinds.LastResult);

        var flag = ShortcutAction.With("setVar", ("name", "flag"), ("value", "looped"));

        var repeat = ShortcutAction.Create("repeat");
        repeat.Params["count"] = ShortcutValue.Literal("2");
        var repeatChild = ShortcutAction.With("setVar", ("name", "looped"), ("value", "looped"));
        repeat.Children.Add(repeatChild);

        var ifAction = ShortcutAction.Create("if");
        ifAction.Params["left"] = ShortcutValue.Variable(flag.Id);
        ifAction.Params["op"] = ShortcutValue.Literal("equals");
        ifAction.Params["right"] = ShortcutValue.Literal("looped");
        var thenChild = ShortcutAction.With("setVar", ("name", "branch"), ("value", "then-branch"));
        var elseChild = ShortcutAction.With("setVar", ("name", "branch"), ("value", "else-branch"));
        ifAction.Children.Add(thenChild);
        ifAction.ElseChildren.Add(elseChild);

        var deterministic = ShortcutAction.With("randomNumber", ("min", "1"), ("max", "1"));
        var shortWait = ShortcutAction.With("wait", ("seconds", "0.05"));

        var scripted = new ShortcutDefinition
        {
            Name = "smoke-runner",
            Actions = [first, upper, flag, repeat, ifAction, deterministic, shortWait],
        };

        var events = new List<RunEvent>();
        void OnEvent(RunEvent runEvent) => events.Add(runEvent);
        ShortcutRunner.RunEvent += OnEvent;
        var result = await ShortcutRunner.RunAsync(scripted, new RunServices());
        ShortcutRunner.RunEvent -= OnEvent;

        if (!result.Success)
            Console.WriteLine($"      scripted error [{result.ErrorCode}] {result.Error}");
        CheckAsync(result.Success, "scripted run succeeds");
        CheckAsync(!string.IsNullOrEmpty(result.Outputs.GetValueOrDefault(first.Id)),
            "currentDate produces a date value");
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
        if (failedEvents.Count != 1)
            Console.WriteLine($"      failed run [{failed.ErrorCode}] events={failedEvents.Count} {failed.Error}");
        CheckAsync(failedEvents.Count == 1, "step-failed event fired once");

        // Built directly as a graph: the v1 migration deliberately tolerates an unknown action
        // by turning it into a note, so the runtime guard is what is under test here.
        var unknownStart = new WorkflowNode { Type = WorkflowNodeTypes.Start };
        var unknownNode = new WorkflowNode { Type = "not-a-real-action" };
        var unknownAction = new ShortcutDefinition
        {
            Name = "smoke-unknown",
            Graph = new WorkflowGraph
            {
                Nodes = [unknownStart, unknownNode],
                Links = [WorkflowLink.Exec(unknownStart.Id, WorkflowNodeTypes.Out, unknownNode.Id)],
            },
        };
        var unknownResult = await ShortcutRunner.RunAsync(unknownAction, new RunServices());
        CheckAsync(unknownResult.ErrorCode is "InvalidGraph" or "UnknownType",
            "the unknown type is named by the run result");
        CheckAsync(!unknownResult.Success, "unknown action type fails the run");

        // New executors: safe negative paths only (no windows are minimized / no keys are sent).
        var missingFolder = new ShortcutDefinition
        {
            Name = "smoke-missing-folder",
            Actions =
            [
                ShortcutAction.With("openFolder",
                    ("path", Path.Combine(Path.GetTempPath(), "definitely-missing-" + Guid.NewGuid().ToString("N")))),
            ],
        };
        var missingFolderResult = await ShortcutRunner.RunAsync(missingFolder, new RunServices());
        CheckAsync(!missingFolderResult.Success && !string.IsNullOrEmpty(missingFolderResult.Error),
            "openFolder rejects a missing path");

        var badScheme = new ShortcutDefinition
        {
            Name = "smoke-http-scheme",
            Actions = [ShortcutAction.With("httpRequest", ("url", "file:///C:/"))],
        };
        var badSchemeResult = await ShortcutRunner.RunAsync(badScheme, new RunServices());
        CheckAsync(!badSchemeResult.Success, "httpRequest rejects non-http schemes");

        var badKeys = new ShortcutDefinition
        {
            Name = "smoke-bad-keys",
            Actions = [ShortcutAction.With("sendKeySequence", ("keys", "Ctrl+Alt+NoSuchKey"))],
        };
        var badKeysResult = await ShortcutRunner.RunAsync(badKeys, new RunServices());
        CheckAsync(!badKeysResult.Success, "sendKeySequence rejects an unknown key before SendInput");

        // sendHotkey: injectable sender, exact press/release order, finally release, capture guard.
        var previousSender = KeyboardInput.Sender;
        try
        {
            var keyboard = new FakeKeyboardInput();
            KeyboardInput.Sender = keyboard;

            var hotkeyShortcut = new ShortcutDefinition
            {
                Name = "smoke-send-hotkey",
                Actions =
                [
                    ShortcutAction.With("sendHotkey", ("hotkey", "ctrl + shift + k"), ("holdMs", "10")),
                ],
            };
            var hotkeyResult = await ShortcutRunner.RunAsync(hotkeyShortcut, new RunServices());
            CheckAsync(hotkeyResult.Success, "sendHotkey succeeds with an injected sender");
            CheckAsync(keyboard.Batches.Count == 2, "sendHotkey sends a down batch and an up batch");
            CheckAsync(keyboard.Batches.Count == 2 &&
                       DescribeStrokes(keyboard.Batches[0]) == "11↓,10↓,4B↓",
                "sendHotkey presses modifiers before the main key");
            CheckAsync(keyboard.Batches.Count == 2 &&
                       DescribeStrokes(keyboard.Batches[1]) == "4B↑,10↑,11↑",
                "sendHotkey releases in reverse order");

            var failing = new FakeKeyboardInput { FailOnCall = 1 };
            KeyboardInput.Sender = failing;
            var failingResult = await ShortcutRunner.RunAsync(hotkeyShortcut, new RunServices());
            CheckAsync(!failingResult.Success, "sendHotkey surfaces a sender failure");
            CheckAsync(failing.Batches.Count >= 2 &&
                       failing.Batches[1].Count > 0 &&
                       failing.Batches[1].All(s => s.IsKeyUp),
                "sendHotkey releases all keys in finally after a press failure");

            KeyboardInput.Sender = keyboard;
            ShortcutHotkeyService.BeginCapture();
            try
            {
                var blockedResult = await ShortcutRunner.RunAsync(hotkeyShortcut, new RunServices());
                CheckAsync(!blockedResult.Success && keyboard.Batches.Count == 2,
                    "sendHotkey refuses to inject while a capture box is recording");
            }
            finally
            {
                ShortcutHotkeyService.EndCapture();
            }
        }
        finally
        {
            KeyboardInput.Sender = previousSender;
        }

        var cycleA = new ShortcutDefinition
        {
            Id = ShortcutDefinition.NewId(),
            Name = "smoke-cycle-a",
            Actions = [ShortcutAction.With("runShortcut", ("target", "smoke-cycle-b"))],
        };
        var cycleB = new ShortcutDefinition
        {
            Id = ShortcutDefinition.NewId(),
            Name = "smoke-cycle-b",
            Actions = [ShortcutAction.With("runShortcut", ("target", "smoke-cycle-a"))],
        };
        ShortcutsStore.ResetForTesting(new ShortcutsDocument
        {
            Version = 1,
            Shortcuts = [cycleA, cycleB],
        });
        var cycleResult = await ShortcutRunner.RunAsync(cycleA, new RunServices());
        CheckAsync(!cycleResult.Success, "runShortcut blocks indirect recursion");

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

        // runCommand: real but read-only shells (echo / exit codes / a killed sleep) — they
        // verify stream capture, code-page handling and the timeout path end to end.
        var echoPs = new ShortcutDefinition
        {
            Name = "smoke-runcommand-ps",
            Actions = [ShortcutAction.With("runCommand",
                ("shell", "powershell"), ("command", "Write-Output '你好 world'"), ("timeout", "20"))],
        };
        var echoPsResult = await ShortcutRunner.RunAsync(echoPs, new RunServices());
        if (!echoPsResult.Success)
            Console.WriteLine($"      runCommand ps [{echoPsResult.ErrorCode}] {echoPsResult.Error}");
        CheckAsync(echoPsResult.Success, "runCommand runs PowerShell");
        CheckAsync(echoPsResult.Outputs.GetValueOrDefault(echoPs.Actions[0].Id)?.Contains("你好 world") == true,
            "runCommand captures PowerShell output with non-ASCII text intact");

        var cmdScript = new ShortcutDefinition
        {
            Name = "smoke-runcommand-cmd",
            Actions = [ShortcutAction.With("runCommand",
                ("shell", "cmd"),
                ("command", "echo 你好 hello\r\ncmd /c exit 7"),
                ("failOnExit", "false"), ("timeout", "20"))],
        };
        var cmdResult = await ShortcutRunner.RunAsync(cmdScript, new RunServices());
        if (!cmdResult.Success)
            Console.WriteLine($"      runCommand cmd [{cmdResult.ErrorCode}] {cmdResult.Error}");
        CheckAsync(cmdResult.Success, "runCommand runs a multi-line CMD script");
        CheckAsync(cmdResult.Outputs.GetValueOrDefault(cmdScript.Actions[0].Id)?.Contains("hello") == true,
            "runCommand captures CMD stdout across lines");
        CheckAsync(cmdResult.Outputs.GetValueOrDefault(cmdScript.Actions[0].Id)?.Contains("你好") == true,
            "runCommand keeps non-ASCII text intact through the cmd code page");

        var exitCode = new ShortcutDefinition
        {
            Name = "smoke-runcommand-exit",
            Actions = [ShortcutAction.With("runCommand",
                ("shell", "cmd"), ("command", "cmd /c exit 7"), ("timeout", "20"))],
        };
        var exitResult = await ShortcutRunner.RunAsync(exitCode, new RunServices());
        CheckAsync(!exitResult.Success && exitResult.ErrorCode == "CommandFailed",
            "runCommand fails the step on a non-zero exit code");

        var slowCommand = new ShortcutDefinition
        {
            Name = "smoke-runcommand-timeout",
            Actions = [ShortcutAction.With("runCommand",
                ("shell", "powershell"), ("command", "Start-Sleep -Seconds 60"), ("timeout", "1"))],
        };
        var slowStart = DateTime.UtcNow;
        var slowResult = await ShortcutRunner.RunAsync(slowCommand, new RunServices());
        var slowElapsed = DateTime.UtcNow - slowStart;
        CheckAsync(!slowResult.Success && slowResult.ErrorCode == "CommandTimeout",
            "runCommand reports a timeout");
        CheckAsync(slowElapsed < TimeSpan.FromSeconds(20),
            $"runCommand timeout actually kills the child ({slowElapsed.TotalSeconds:0.#} s)");

        // Hotkey registration: fake registrar, no real OS table.
        var fake = new FakeRegistrar();
        fake.RejectKeys.Add(0x39); // Ctrl+Alt+9 is "taken" by another app

        var validId = ShortcutDefinition.NewId();
        var duplicateId = ShortcutDefinition.NewId();
        var invalidId = ShortcutDefinition.NewId();
        var conflictId = ShortcutDefinition.NewId();

        var hotkeyDocument = new ShortcutsDocument
        {
            Version = 1,
            Shortcuts =
            [
                new()
                {
                    Id = validId, Name = "valid", Hotkey = "Ctrl+Alt+1",
                    Actions = [ShortcutAction.With("comment", ("note", "hotkey-ran"))],
                },
                new() { Id = duplicateId, Name = "duplicate", Hotkey = "Ctrl+Alt+1" },
                new() { Id = invalidId, Name = "invalid", Hotkey = "Ctrl+Alt" },
                new() { Id = conflictId, Name = "conflict", Hotkey = "Ctrl+Alt+9" },
            ],
        };

        ShortcutsStore.ResetForTesting(hotkeyDocument);
        ShortcutHotkeyService.ResetForTesting();
        ShortcutHotkeyService.RegistrarFactory = () => fake;
        ShortcutHotkeyService.EnsureStarted();

        CheckAsync(ShortcutHotkeyService.IsStarted, "hotkey service starts with the injected registrar");
        CheckAsync(fake.RegisterCalls == 2, "only parseable, non-duplicate hotkeys reach the registrar");
        CheckAsync(fake.ActiveRegistrations.Count == 1, "one hotkey registered");

        CheckAsync(ShortcutHotkeyService.TryGetBinding(validId, out var validBinding)
                   && validBinding.Status == HotkeyRegistrationStatus.Registered,
            "valid hotkey is registered");
        CheckAsync(ShortcutHotkeyService.TryGetBinding(duplicateId, out var duplicateBinding)
                   && duplicateBinding.Status == HotkeyRegistrationStatus.Duplicate
                   && duplicateBinding.Detail == "valid",
            "duplicate hotkey is reported with the owner's name");
        CheckAsync(ShortcutHotkeyService.TryGetBinding(invalidId, out var invalidBinding)
                   && invalidBinding.Status == HotkeyRegistrationStatus.Invalid,
            "unparseable hotkey is reported as invalid");
        CheckAsync(ShortcutHotkeyService.TryGetBinding(conflictId, out var conflictBinding)
                   && conflictBinding.Status == HotkeyRegistrationStatus.Conflict,
            "registrar failure is reported as a conflict");

        // Document change: unregister everything, then register the new table.
        hotkeyDocument.Shortcuts[0].Hotkey = "Ctrl+Alt+3";
        ShortcutsStore.ResetForTesting(hotkeyDocument);
        ShortcutHotkeyService.Refresh(hotkeyDocument);
        CheckAsync(fake.UnregisterCalls >= 1, "refresh unregisters the previous table first");
        CheckAsync(ShortcutHotkeyService.TryGetBinding(validId, out var moved)
                   && moved.Hotkey == "Ctrl+Alt+3"
                   && moved.Status == HotkeyRegistrationStatus.Registered,
            "refresh moves the binding to the new gesture");

        // Recording: suspend removes registrations; resume puts them back.
        ShortcutHotkeyService.Suspend();
        CheckAsync(fake.ActiveRegistrations.Count == 0, "suspend unregisters every hotkey");
        CheckAsync(ShortcutHotkeyService.TryGetBinding(validId, out var suspendedBinding)
                   && suspendedBinding.Status == HotkeyRegistrationStatus.Suspended,
            "suspended binding is marked while recording");

        ShortcutHotkeyService.Resume();
        // The old "Ctrl+Alt+1" duplicate became unique when the first shortcut moved to
        // Ctrl+Alt+3, so two bindings are live again (the conflict stays rejected).
        CheckAsync(fake.ActiveRegistrations.Count == 2, "resume re-registers the document");

        // Trigger path: execute by id without a real keyboard event.
        var triggered = await ShortcutHotkeyService.ExecuteShortcutAsync(validId);
        CheckAsync(triggered, "hotkey trigger path runs the shortcut");

        ShortcutHotkeyService.Shutdown();
        ShortcutHotkeyService.Shutdown();
        CheckAsync(fake.Disposed, "shutdown disposes the registrar exactly once");
        CheckAsync(!ShortcutHotkeyService.IsStarted, "shutdown clears the started flag");
        ShortcutHotkeyService.ResetForTesting();

        // Opt-in real interop smoke (manual run: set DESKCANVAS_REAL_HOTKEY_TEST=1). The
        // default gate never touches the OS hotkey table.
        if (Environment.GetEnvironmentVariable("DESKCANVAS_REAL_HOTKEY_TEST") == "1")
        {
            var realId = ShortcutDefinition.NewId();
            var realDocument = new ShortcutsDocument
            {
                Version = 1,
                Shortcuts = [new() { Id = realId, Name = "real", Hotkey = "Ctrl+Alt+F24" }],
            };

            ShortcutsStore.ResetForTesting(realDocument);
            ShortcutHotkeyService.EnsureStarted();
            CheckAsync(ShortcutHotkeyService.TryGetBinding(realId, out var realBinding)
                       && realBinding.Status == HotkeyRegistrationStatus.Registered,
                "real Win32 registrar accepts Ctrl+Alt+F24");
            ShortcutHotkeyService.Shutdown();
        }

        return failures;
    }

    private static string DescribeStrokes(IEnumerable<KeyStroke> strokes) =>
        string.Join(",", strokes.Select(s => s.VirtualKey.ToString("X2") + (s.IsKeyUp ? "↑" : "↓")));

    /// <summary>
    /// A read-only settings provider whose theme is monochrome: the tile has to read the
    /// monochrome branch from it.
    /// </summary>
    private sealed class MonochromeSettingsProvider : IAppSettingsProvider
    {
        /// <summary>
        /// Which monochrome variant the provider reports. Static because the plugin runtime takes
        /// the first publisher only (<c>ShortcutsRuntime.Publish</c> is last-writer-loses by
        /// design), so the checks switch variants on the one provider instead of republishing.
        /// </summary>
        public static MonochromeStyle? Variant { get; set; }

        /// <summary>Explicit accent colour; null means "follow the app accent".</summary>
        public static string? Accent { get; set; }

        private readonly AppSettings data = new(
            new Theme(false, null, 1.0, true, false, "Segoe UI"),
            [],
            new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(160, 12, 24),
            new Region("en-US"),
            false,
            null);

        public AppSettings Get() => data with
        {
            Theme = data.Theme with { MonochromeVariant = Variant, AccentColor = Accent }
        };

        public void Save(AppSettings data) { }

        // Explicit accessors rather than a field-like event: a field-like event in a private
        // nested class is never invoked, which the compiler reports (CS0067).
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }

        public event DataChangedEvent<AppSettings>? DataChanged { add { } remove { } }
    }

    private sealed class FakeKeyboardInput : IKeyboardInputSender
    {
        public List<IReadOnlyList<KeyStroke>> Batches { get; } = [];

        public int Calls { get; private set; }
        public int FailOnCall { get; init; }

        public void Send(IReadOnlyList<KeyStroke> strokes)
        {
            Calls++;
            Batches.Add(strokes.ToArray());
            if (Calls == FailOnCall)
                throw new InvalidOperationException("fake sender failure");
        }
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public event Action<int>? Pressed;

        public readonly HashSet<uint> RejectKeys = [];
        public readonly List<int> ActiveRegistrations = [];

        public int RegisterCalls { get; private set; }
        public int UnregisterCalls { get; private set; }
        public bool Disposed { get; private set; }

        public bool Register(int registrationId, HotkeyModifiers modifiers, uint virtualKey)
        {
            RegisterCalls++;
            if (Disposed || RejectKeys.Contains(virtualKey)) return false;
            ActiveRegistrations.Add(registrationId);
            return true;
        }

        public void Unregister(int registrationId)
        {
            UnregisterCalls++;
            ActiveRegistrations.Remove(registrationId);
        }

        public void Press(int registrationId) => Pressed?.Invoke(registrationId);

        public void Dispose()
        {
            Disposed = true;
            ActiveRegistrations.Clear();
        }
    }
}
