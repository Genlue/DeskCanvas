// Independent UI probe for the Shortcuts widget (owned by ui-sweep).
// Usage: _uiprobe <scenario> [out.png] [width] [height]
//   editor       rich document editor render + row/chip geometry dump
//   editorlib    editor with the action-library drawer open
//   editorempty  editor on a shortcut with zero actions (empty hint)
//   card         ShortcutsView at the given size (tier auto)
//   popup        ShortcutsPopupWindow render + row geometry dump
//   settings     ShortcutsSettings render
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Views;
using Shortcuts.Views.Settings;
using ShortcutsCardViewModel = Shortcuts.ViewModels.ShortcutsCardViewModel;

internal static class Program
{
    private static string outDir = ".";
    private static bool initialized;

    [STAThread]
    private static void Main(string[] args)
    {
        var scenario = args.Length > 0 ? args[0] : "editor";
        var outPath = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, scenario + ".png");
        var width = Arg(args, 2, 800);
        var height = Arg(args, 3, 660);
        var dark = args.Length > 4 && args[4] == "dark";
        outDir = Path.GetDirectoryName(Path.GetFullPath(outPath)) ?? ".";
        Directory.CreateDirectory(outDir);

        var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-uiprobe-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        Setup();
        if (dark) Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        switch (scenario)
        {
            case "editor":
                RenderEditor(outPath, width, height, openLibrary: false, empty: false);
                break;
            case "editorlib":
                RenderEditor(outPath, width, height, openLibrary: true, empty: false);
                break;
            case "editorempty":
                RenderEditor(outPath, width, height, openLibrary: false, empty: true);
                break;
            case "editorhover":
                RenderEditorHover(outPath, width, height);
                break;
            case "editorenabled":
                RenderEditorEnabledToolbar(outPath, width, height);
                break;
            case "card":
                RenderCard(outPath, width, height, running: false);
                break;
            case "cardrun":
                RenderCard(outPath, width, height, running: true);
                break;
            case "popup":
                RenderPopup(outPath, empty: false);
                break;
            case "popupempty":
                RenderPopup(outPath, empty: true);
                break;
            case "settings":
                RenderSettings(outPath, width, height);
                break;
            default:
                Console.WriteLine("unknown scenario: " + scenario);
                break;
        }
    }

    private static double Arg(string[] args, int index, double fallback) =>
        args.Length > index && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    // ---- platform ----------------------------------------------------------------

    private static void Setup()
    {
        if (initialized) return;
        initialized = true;

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Light;

        // The live app loads these via App.axaml; without them the popup's dc-icon-button and
        // the shell chrome fall back to bare Fluent (a false-positive source), so load them.
        foreach (var style in new[]
                 {
                     "Controls/Input.axaml", "Controls/IconButton.axaml",
                     "Controls/SettingsSectionHeader.axaml", "Controls/SecondaryPanelShell.axaml",
                 })
        {
            try
            {
                app.Styles.Add(new StyleInclude(new Uri("avares://DeskCanvas/"))
                { Source = new Uri("avares://DeskCanvas/Styles/" + style) });
            }
            catch (Exception ex) { Console.WriteLine("style load failed: " + style + " -> " + ex.Message); }
        }

        foreach (var res in new[] { "DesignTokens.axaml", "InteractionResources.axaml" })
        {
            try
            {
                app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://DeskCanvas/"))
                { Source = new Uri("avares://DeskCanvas/Styles/" + res) });
            }
            catch (Exception ex) { Console.WriteLine("resource load failed: " + res + " -> " + ex.Message); }
        }

        app.Resources["SystemAccentColor"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorDark1"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorLight1"] = Color.Parse("#0A84FF");
        // Chrome surfaces must be theme-aware (mirroring App.axaml's theme dictionaries): with a
        // fixed light value the dark render showed white text on a white surface, i.e. everything
        // default-foreground looked "missing" — a pure probe artifact.
        var lightChrome = new ResourceDictionary
        {
            ["SettingsBackground"] = new SolidColorBrush(Color.Parse("#F5F5F7")),
            ["MenuSurfaceBackground"] = new SolidColorBrush(Color.Parse("#FFFFFF")) { Opacity = 0.88 },
            ["MenuSurfaceBorder"] = new SolidColorBrush(Color.Parse("#E5E5EA")) { Opacity = 0.8 },
        };
        var darkChrome = new ResourceDictionary
        {
            ["SettingsBackground"] = new SolidColorBrush(Color.Parse("#1C1C1E")),
            ["MenuSurfaceBackground"] = new SolidColorBrush(Color.Parse("#1C1C1E")) { Opacity = 0.88 },
            ["MenuSurfaceBorder"] = new SolidColorBrush(Color.Parse("#38383A")) { Opacity = 0.8 },
        };
        var chrome = new ResourceDictionary();
        chrome.ThemeDictionaries[ThemeVariant.Light] = lightChrome;
        chrome.ThemeDictionaries[ThemeVariant.Dark] = darkChrome;
        app.Resources.MergedDictionaries.Add(chrome);
        app.Resources["HostSurfaceBorderThickness"] = new Thickness(1);
        app.Resources["HostSurfaceCornerRadius"] = new CornerRadius(16);
    }

    private static void Pump(Window w)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window w, string path)
    {
        Pump(w);
        w.CaptureRenderedFrame()?.Save(path);
        Console.WriteLine("saved: " + path);
    }

    // ---- documents ---------------------------------------------------------------

    private static ShortcutsDocument RichDocument()
    {
        var openUrl = ShortcutAction.With("openUrl",
            ("url", "https://example.com/a/very/long/path/with?query=1234567890&more=abcdefghijklmnop"));
        var transform = ShortcutAction.With("textTransform",
            ("input", "一段需要转换的长输入文本 content"), ("operation", "replace"),
            ("find", "abc"), ("replace_with", "xyz"));
        var openApp = ShortcutAction.With("openApp",
            ("target", @"C:\Program Files\SomeVeryLongApplicationFolder\应用主程序.exe"),
            ("args", "--verbose --output=full-report"));
        var volume = ShortcutAction.Create("setVolume");
        var branch = ShortcutAction.With("if", ("op", "equals"), ("right", "expected-value"));
        branch.Params["left"] = ShortcutValue.Variable(ValueKinds.LastResult);
        branch.Children.Add(ShortcutAction.Create("lockScreen"));
        branch.ElseChildren.Add(ShortcutAction.Create("screenshot"));
        var loop = ShortcutAction.Create("repeat");
        loop.Children.Add(ShortcutAction.With("notify", ("message", "循环里的提示消息")));

        var shortcuts = new List<ShortcutDefinition>
        {
            new()
            {
                Name = "一个相当长的快捷指令名称用于观察省略与截断效果",
                Glyph = "bolt", Color = "#185FA5",
                Actions = [openUrl, transform, openApp, volume, branch, loop],
            },
            new() { Name = "空指令", Glyph = "folder", Color = "#0F6E56", Actions = [] },
        };
        return new ShortcutsDocument { Version = 1, Shortcuts = shortcuts };
    }

    private static ShortcutsDocument CardDocument()
    {
        return new ShortcutsDocument
        {
            Version = 1,
            Shortcuts =
            [
                new() { Name = "一键锁屏", Glyph = "lock", Color = "#185FA5", Actions = [ShortcutAction.Create("lockScreen"), ShortcutAction.Create("screenshot")] },
                new() { Name = "较长名称的工作文件夹快捷打开方式", Glyph = "folder", Color = "#0F6E56", Actions = [ShortcutAction.Create("openFolder")] },
                new() { Name = "锁屏", Glyph = "bolt", Color = "#534AB7", Actions = [] },
                new() { Name = "音乐控制", Glyph = "play", Color = "#993556", Actions = [ShortcutAction.Create("mediaPlayPause")] },
                new() { Name = "测试", Glyph = "bell", Color = "#3B6D11", Actions = [] },
            ],
        };
    }

    // ---- editor ------------------------------------------------------------------

    private static void RenderEditor(string outPath, double width, double height, bool openLibrary, bool empty)
    {
        var doc = RichDocument();
        if (empty)
        {
            var only = doc.Shortcuts[1]; // "空指令" has no actions
            ShortcutsStore.ResetForTesting(new ShortcutsDocument { Version = 1, Shortcuts = [only] });
        }
        else ShortcutsStore.ResetForTesting(doc);

        var subject = empty ? ShortcutsStore.Get().Shortcuts[0] : ShortcutsStore.Get().Shortcuts[0];
        var editor = new ShortcutsEditorWindow(subject.Id)
        {
            WindowDecorations = WindowDecorations.None,
            Width = width,
            Height = height,
        };
        editor.Show();
        Pump(editor);

        if (openLibrary)
        {
            var add = editor.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AddActionButton");
            if (add != null) { Click(editor, add); Pump(editor); }
            else Console.WriteLine("AddActionButton not found");
        }

        Save(editor, outPath);
        if (!empty) DumpEditor(editor, width, height);
        DumpTopLevel(editor);
    }

    private static void RenderEditorHover(string outPath, double width, double height)
    {
        ShortcutsStore.ResetForTesting(RichDocument());
        var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id)
        {
            WindowDecorations = WindowDecorations.None,
            Width = width, Height = height,
        };
        editor.Show();
        Pump(editor);

        // Simulate the row-hover reveal of the per-row "…" overflow button.
        var rowsHost = editor.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "RowsHost");
        if (rowsHost != null)
            foreach (var b in rowsHost.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("toolbar-icon")))
                b.Opacity = 0.7;
        Pump(editor);
        Save(editor, outPath);
        DumpEditor(editor, width, height);
    }

    private static void RenderEditorEnabledToolbar(string outPath, double width, double height)
    {
        ShortcutsStore.ResetForTesting(RichDocument());
        var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id)
        {
            WindowDecorations = WindowDecorations.None,
            Width = width, Height = height,
        };
        editor.Show();
        Pump(editor);

        // Force the toolbar into the enabled state to judge the enabled icon contrast.
        foreach (var name in new[] { "UndoButton", "RedoButton" })
        {
            var b = editor.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => x.Name == name);
            if (b != null) b.IsEnabled = true;
        }
        Pump(editor);
        Save(editor, outPath);
    }

    private static void DumpEditor(Window editor, double width, double height)
    {
        Console.WriteLine($"=== editor geometry (window {width}x{height}) ===");
        var rowsHost = editor.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "RowsHost");
        Console.WriteLine("RowsHost: " + Describe(rowsHost));

        var libraryPanel = editor.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "LibraryPanel");
        var bottomStack = libraryPanel?.Parent as StackPanel;
        Console.WriteLine("bottomStack: " + Describe(bottomStack));
        Console.WriteLine("LibraryPanel: " + Describe(libraryPanel));

        var addBar = editor.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "AddActionButton");
        Console.WriteLine("AddActionButton: " + Describe(addBar));

        if (rowsHost != null)
        {
            var i = 0;
            foreach (var rowGrid in rowsHost.Children.OfType<Grid>())
            {
                var absY = rowGrid.TranslatePoint(new Point(0, 0), rowsHost)?.Y;
                Console.WriteLine($"-- rowGrid[{i}] bounds={Fmt(rowGrid.Bounds)} absY={absY:0.#} cols={rowGrid.ColumnDefinitions.Count}");
                foreach (var rc in rowGrid.Children)
                {
                    var col = Grid.GetColumn(rc);
                    var span = Grid.GetColumnSpan(rc);
                    if (rc is StackPanel sp)
                    {
                        Console.WriteLine($"   StackPanel(col={col},span={span}) bounds={Fmt(rc.Bounds)}");
                        foreach (var c in sp.Children)
                            Console.WriteLine($"      child {c.GetType().Name} bounds={Fmt(c.Bounds)}");
                    }
                    else
                    {
                        Console.WriteLine($"   {rc.GetType().Name}(col={col},span={span}) bounds={Fmt(rc.Bounds)} classes=[{string.Join(",", (rc as StyledElement)?.Classes ?? [])}]");
                    }
                }

                var blockCol = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
                var block = blockCol?.Children.OfType<Border>().FirstOrDefault();
                var more = rowGrid.Children.OfType<Button>().FirstOrDefault();

                var chips = block?.GetVisualDescendants().OfType<StackPanel>()
                    .FirstOrDefault(sp => sp.Children.Count > 0 && sp.Children.All(c => c is Button b && b.Classes.Contains("chip-btn")));
                if (chips != null)
                {
                    Console.WriteLine($"   chips bounds={Fmt(chips.Bounds)} clip={chips.ClipToBounds}");
                    foreach (var chip in chips.Children.OfType<Button>())
                    {
                        var text = (chip.Content as TextBlock)?.Text ?? "?";
                        var overflow = chip.Bounds.Right - chips.Bounds.Width;
                        Console.WriteLine($"      chip '{Trunc(text,28)}' bounds={Fmt(chip.Bounds)} right={chip.Bounds.Right:0.#} overflow={overflow:0.#}");
                    }
                }

                if (block != null && more != null && blockCol != null)
                {
                    var gap = more.Bounds.X - (blockCol.Bounds.X + blockCol.Bounds.Width);
                    Console.WriteLine($"   block right={blockCol.Bounds.Right:0.#} more.X={more.Bounds.X:0.#} gap={gap:0.#} " +
                                      $"blockW={block.Bounds.Width:0.#} title?= see chips");
                }
                i++;
            }

            var hint = editor.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(t => t.Name == "EmptyHint");
            Console.WriteLine("EmptyHint: " + Describe(hint) + " visible=" + hint?.IsVisible);

            Console.WriteLine($"RowsHost Spacing={rowsHost.Spacing} childCount={rowsHost.Children.Count}");
            foreach (var body in rowsHost.GetVisualDescendants().OfType<StackPanel>().Where(sp => Math.Abs(sp.Spacing - 6) < 0.01))
            {
                Console.WriteLine($"-- nested body StackPanel spacing={body.Spacing} bounds={Fmt(body.Bounds)}");
                for (var j = 0; j < body.Children.Count; j++)
                {
                    var c = body.Children[j];
                    var abs = c.TranslatePoint(new Point(0, 0), rowsHost);
                    Console.WriteLine($"   child[{j}] {c.GetType().Name} bounds={Fmt(c.Bounds)} absY={abs?.Y:0.#}");
                }
            }
        }

        // toolbar
        foreach (var name in new[] { "UndoButton", "RedoButton", "RunButton", "NameBox", "ActionCountText" })
        {
            var c = editor.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == name);
            Console.WriteLine($"{name}: {Describe(c)}" +
                              (c is Button bu ? $" enabled={bu.IsEnabled}" : string.Empty));
        }
    }

    // ---- card --------------------------------------------------------------------

    private static void RenderCard(string outPath, double width, double height, bool running)
    {
        ShortcutsStore.ResetForTesting(CardDocument());
        var view = new ShortcutsView(null, new ShortcutsModel(), null, null);
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = width, Height = height,
            Background = HostBackground(),
            Content = view,
        };
        window.Show();
        Pump(window);

        var card = (ShortcutsCardViewModel)view.DataContext!;
        if (running)
        {
            card.Rebuild();
            card.Tiles.FirstOrDefault(t => !t.IsCreate)?.SetRunning(1, 3);
            Pump(window);
        }

        Console.WriteLine($"=== card {width}x{height} columns={card.Columns} tiles={card.Tiles.Count} running={running} ===");
        foreach (var t in card.Tiles)
            Console.WriteLine($"   tile name='{t.Name}' create={t.IsCreate} sub='{t.ActionCountText}' showSubtitle={t.ShowSubtitle}");

        Save(window, outPath);
        DumpTiles(view);
    }

    private static void DumpTiles(Visual root)
    {
        foreach (var btn in root.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("shortcut-tile")))
        {
            Console.WriteLine($"   tile btn bounds={Fmt(btn.Bounds)}");
            var texts = btn.GetVisualDescendants().OfType<TextBlock>().ToList();
            foreach (var t in texts)
                Console.WriteLine($"      text '{Trunc(t.Text ?? "", 24)}' fs={t.FontSize} lh={t.LineHeight} bounds={Fmt(t.Bounds)} visible={t.IsVisible}");
            var bar = btn.GetVisualDescendants().OfType<ProgressBar>().FirstOrDefault();
            if (bar != null)
                Console.WriteLine($"      progress bounds={Fmt(bar.Bounds)} visible={bar.IsVisible} value={bar.Value}");
        }
    }

    // ---- popup -------------------------------------------------------------------

    private static void RenderPopup(string outPath, bool empty)
    {
        if (empty)
            ShortcutsStore.ResetForTesting(new ShortcutsDocument { Version = 1, Shortcuts = [] });
        else
            ShortcutsStore.ResetForTesting(CardDocument());
        try
        {
            var popup = new ShortcutsPopupWindow();
            popup.Show();
            Pump(popup);
            Save(popup, outPath);
            DumpPopup(popup);
        }
        catch (Exception ex)
        {
            Console.WriteLine("popup Show failed: " + ex);
            // Fallback: render the shell content inside a plain window with the card revealed.
            ShortcutsStore.ResetForTesting(CardDocument());
            var popup = new ShortcutsPopupWindow();
            var shell = popup.Content as Control;
            if (shell != null)
            {
                popup.Content = null;
                var win = new Window
                {
                    WindowDecorations = WindowDecorations.None,
                    Width = 400, Height = 540,
                    Background = Brushes.White,
                    Content = shell,
                };
                win.Show();
                Pump(win);
                Save(win, outPath);
                DumpPopup(win);
            }
        }
    }

    private static void DumpPopup(Window win)
    {
        Console.WriteLine("=== popup geometry ===");
        foreach (var name in new[] { "SearchBox", "ListControl", "EmptyPanel", "AddButton", "SubtitleText" })
        {
            var c = win.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Name == name);
            Console.WriteLine($"{name}: {Describe(c)}");
        }

        foreach (var btn in win.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("dc-icon-button")))
        {
            var auto = Avalonia.Automation.AutomationProperties.GetName(btn);
            Console.WriteLine($"   dc-icon-button bounds={Fmt(btn.Bounds)} name='{auto}' tip='{ToolTip.GetTip(btn)}'");
        }

        // Rows
        foreach (var border in win.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("popup-row")))
            Console.WriteLine($"   popup-row bounds={Fmt(border.Bounds)}");
    }

    // ---- settings ----------------------------------------------------------------

    private static void RenderSettings(string outPath, double width, double height)
    {
        ShortcutsStore.ResetForTesting(CardDocument());
        var provider = new StubLayoutProvider();
        var settings = new ShortcutsSettings(provider);
        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = width, Height = height,
            Background = Brushes.White,
            Content = settings,
        };
        window.Show();
        Pump(window);
        Save(window, outPath);
        Console.WriteLine("=== settings geometry ===");
        foreach (var combo in window.GetVisualDescendants().OfType<ComboBox>())
            Console.WriteLine($"   ComboBox '{combo.Name}' bounds={Fmt(combo.Bounds)} minW={combo.MinWidth}");
        foreach (var sw in window.GetVisualDescendants().OfType<ToggleSwitch>())
            Console.WriteLine($"   ToggleSwitch '{sw.Name}' bounds={Fmt(sw.Bounds)}");
    }

    // ---- shared helpers ----------------------------------------------------------

    private static void Click(Window window, Control control)
    {
        var top = TopLevel.GetTopLevel(control);
        var pt = top is null ? null : control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        if (top is null || pt is null) { Console.WriteLine("    click failed"); return; }
        top.MouseDown(pt.Value, MouseButton.Left);
        top.MouseUp(pt.Value, MouseButton.Left);
    }

    private static void DumpTopLevel(Visual visual, int depth = 0)
    {
        if (depth > 10) return;
        foreach (var child in visual.GetVisualChildren())
        {
            if (child is not Visual v) continue;
            if (v is Control c && c.Name != null)
                Console.WriteLine($"{new string(' ', depth * 2)}{v.GetType().Name} name={c.Name} bounds={Fmt(c.Bounds)}");
            DumpTopLevel(v, depth + 1);
        }
    }

    private static IBrush HostBackground() =>
        Application.Current!.ActualThemeVariant == ThemeVariant.Dark
            ? new SolidColorBrush(Color.Parse("#1C1C1E"))
            : Brushes.White;

    private static string Fmt(Rect r) =>
        string.Create(CultureInfo.InvariantCulture, $"{r.X:0.#},{r.Y:0.#},{r.Width:0.#},{r.Height:0.#}");

    private static string Describe(Control? c) =>
        c is null ? "null" : $"{c.GetType().Name} bounds={Fmt(c.Bounds)} ha={((Layoutable)c).HorizontalAlignment} visible={c.IsVisible}";

    private static string Trunc(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private sealed class StubLayoutProvider : IWidgetLayoutProvider
    {
        private WidgetLayout layout = new("Shortcuts", "ShortcutsView", 0, 0, 300, 300, null);
        public string ScreenId { get; set; } = "screen";
        public WidgetLayout Get() => layout;
        public void Save(WidgetLayout data) => layout = data;
        public void Remove() { }
        public event DataChangedEvent<WidgetLayout>? DataChanging;
        public event DataChangedEvent<WidgetLayout>? DataChanged;
    }
}
