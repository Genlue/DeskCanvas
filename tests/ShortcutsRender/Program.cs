// Headless render + visual-tree harness for the Shortcuts card.
// Usage: ShortcutsRender [width] [height] [out.png] [scenario]
//   default: 140x140 (the live 2x2 content area), scenario "small"
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ShapePath = Avalonia.Controls.Shapes.Path;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var width = Arg(args, 0, 140);
        var height = Arg(args, 1, 140);
        var outPath = args.Length > 2 ? args[2] : Path.Combine(AppContext.BaseDirectory, "card.png");
        var scenario = args.Length > 3 ? args[3] : "small";

        var sandbox = Path.Combine(Path.GetTempPath(), "DeskCanvas-ShortcutsRender-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);
        // The harness must never touch the real OS hotkey table (Ctrl+Alt+1 may be taken).
        Environment.SetEnvironmentVariable("DESKCANVAS_SHORTCUTS_DISABLE_HOTKEYS", "1");

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        // Control themes (Button / ItemsControl / ProgressBar) come from Fluent, same as the
        // live app. Without them the ItemsControl renders no containers at all.
        Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        if (scenario == "probe")
        {
            var probeWindow = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Width = 120,
                Height = 40,
                Background = Brushes.White,
                Content = BuildIconProbe(),
            };
            probeWindow.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            probeWindow.CaptureRenderedFrame()?.Save(outPath);
            Console.WriteLine("saved probe: " + outPath);
            return;
        }

        ShortcutsStore.ResetForTesting(BuildDocument(scenario));

        if (scenario is "editor" or "graph")
        {
            var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id);
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            editor.CaptureRenderedFrame()?.Save(outPath);
            Console.WriteLine("saved editor: " + outPath);
            DumpEditorGeometry(editor);
            Dump(editor);
            return;
        }

        if (scenario == "editordump")
        {
            // Editor render + full visual-tree dump (rows geometry, chips, buttons).
            var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id);
            editor.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            DumpEditorGeometry(editor);
            Dump(editor);
            return;
        }

        if (scenario == "import")
        {
            // The AI-import dialog: verifies its XAML loads and the validation text lays out.
            var dialog = new WorkflowImportWindow();
            dialog.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            dialog.CaptureRenderedFrame()?.Save(outPath);
            Console.WriteLine("saved import dialog: " + outPath);
            return;
        }

        if (scenario == "browse")
        {
            RunBrowseProbe(outPath);
            return;
        }

        if (scenario == "urlprobe")
        {
            // Probe added by editor-bugs: reproduce "openUrl URL parameter cannot be edited".
            RunUrlProbe(outPath);
            return;
        }

        if (scenario == "hoverprobe")
        {
            // Probe added by editor-bugs: does the row "..." menu button appear on hover?
            RunHoverProbe(outPath);
            return;
        }

        if (scenario == "chevronprobe")
        {
            // Probe added by editor-bugs: does the invisible "..." button cover a container's chevron?
            RunChevronProbe(outPath);
            return;
        }

        if (scenario == "hotkeyparam")
        {
            RunHotkeyParamProbe(outPath);
            return;
        }

        if (scenario is "mono" or "mono-bw" or "mono-accent")
        {
            // Monochrome probe: every icon block must paint in the theme's single colour instead
            // of each shortcut's own colour. "mono-bw" is 黑白 (white on a dark card),
            // "mono-accent" is 强调色 with an explicit accent.
            var variant = scenario switch
            {
                "mono-bw" => DeskCanvas.Core.Models.Settings.MonochromeStyle.BlackWhite,
                "mono-accent" => DeskCanvas.Core.Models.Settings.MonochromeStyle.Accent,
                _ => (DeskCanvas.Core.Models.Settings.MonochromeStyle?)null,
            };
            var provider = new MonoProbeSettings(variant, scenario == "mono-accent" ? "#C0392B" : null);
            // The live report is a dark-mode card; the harness default is light.
            Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var monoView = new ShortcutsView(null, new ShortcutsModel(), provider, null);
            var monoWindow = new Window
            {
                WindowDecorations = WindowDecorations.None,
                Width = width,
                Height = height,
                Background = new SolidColorBrush(Color.Parse("#1E1E1E")),
                Content = monoView,
            };
            monoWindow.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();

            var card = (Shortcuts.ViewModels.ShortcutsCardViewModel)monoView.DataContext!;
            foreach (var tile in card.Tiles)
            {
                var color = tile.IconBrush is ISolidColorBrush solid ? solid.Color.ToString() : "?";
                Console.WriteLine($"  tile '{tile.Name}' icon={color} stroke=" +
                                  (tile.GlyphStrokeBrush is ISolidColorBrush s2 ? s2.Color.ToString() : "?"));
            }

            monoWindow.CaptureRenderedFrame()?.Save(outPath);
            Console.WriteLine("saved mono: " + outPath);
            return;
        }

        var view = new ShortcutsView(null, new ShortcutsModel(), null, null);
        if (scenario == "running")
        {
            var card = (Shortcuts.ViewModels.ShortcutsCardViewModel)view.DataContext!;
            card.Rebuild();
            card.Tiles.FirstOrDefault(t => !t.IsCreate)?.SetRunning(1, 3);
        }

        var window = new Window
        {
            WindowDecorations = WindowDecorations.None,
            Width = width,
            Height = height,
            Background = Brushes.White,
            Content = view,
        };
        window.Show();

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Dump(view);

        var frame = window.CaptureRenderedFrame();
        frame?.Save(outPath);
        Console.WriteLine("saved: " + outPath);
    }

    private static double Arg(string[] args, int index, double fallback) =>
        args.Length > index && double.TryParse(args[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : fallback;

    /// <summary>
    /// Replays the user's flow: chip click -> param flyout -> browse press/release, one fresh
    /// window per check. The picker is stubbed through FileDialogs.TestOverride (no real dialog).
    /// </summary>
    private static void RunBrowseProbe(string outPath)
    {
        var calls = new List<string>();
        const string picked = "C:/Demo/report.docx";

        // ---- check 1: the pick works, and the flyout survives the press ----------------
        Console.WriteLine("=== check 1: browse with focus in the param box");
        FileDialogs.TestOverride = (folder, title) =>
        {
            calls.Add((folder ? "folder" : "file") + "|" + (title ?? ""));
            return picked;
        };
        var editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        var browse = FindByContent(editor, "浏览");
        Console.WriteLine("  browse: " + Describe(browse));
        var box = (browse?.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault();
        box?.Focus(); // the real flyout hands keyboard focus to the box
        Pump(editor);
        if (Path.GetDirectoryName(outPath) is { Length: > 0 } dir)
            editor.CaptureRenderedFrame()?.Save(Path.Combine(dir, "browse_flyout.png"));

        var top = TopLevel.GetTopLevel(browse!);
        var browseButton = browse!;
        var pt = browseButton.TranslatePoint(
            new Point(browseButton.Bounds.Width / 2, browseButton.Bounds.Height / 2), top!);
        top!.MouseDown(pt!.Value, MouseButton.Left);
        Pump(editor);
        Console.WriteLine("  after press: flyout open=" + (FindByContent(editor, "浏览") != null)
                          + "  box focused=" + (box?.IsFocused ?? false)
                          + "  pickerCalls=" + calls.Count
                          + "  params=[" + string.Join(",", Params().Keys) + "]");
        top!.MouseUp(pt.Value, MouseButton.Left);
        Pump(editor);
        Console.WriteLine("  after release: pickerCalls=" + calls.Count + "  chip=" + Describe(FindChip(editor)));
        editor.Close();

        // ---- check 2: a cancelled pick leaves the model untouched ----------------------
        Console.WriteLine("=== check 2: cancel leaves the model untouched");
        calls.Clear();
        FileDialogs.TestOverride = (_, _) =>
        {
            calls.Add("cancel");
            return null;
        };
        editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        Click(editor, FindByContent(editor, "浏览")!);
        Pump(editor);
        Console.WriteLine("  after cancel: pickerCalls=" + calls.Count + "  chip=" + Describe(FindChip(editor))
                          + "  params=[" + string.Join(",", Params().Keys) + "]");
        editor.Close();

        // ---- check 3: the insert-variable menu still works -----------------------------
        Console.WriteLine("=== check 3: insert-variable menu");
        editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        var useVar = FindByContent(editor, "插入变量");
        Console.WriteLine("  useVar: " + Describe(useVar));
        ((useVar?.Parent as Panel)?.Children.OfType<TextBox>().FirstOrDefault())?.Focus();
        Pump(editor);
        Click(editor, useVar!);
        Pump(editor);
        var item = FindMenuItem(editor, "上一步的结果");
        Console.WriteLine("  menu item: " + Describe(item));
        if (item is not null)
        {
            Click(editor, item);
            Pump(editor);
            Console.WriteLine("  chip after: " + Describe(FindChip(editor)));
        }

        editor.CaptureRenderedFrame()?.Save(outPath);
        editor.Close();
        FileDialogs.TestOverride = null;
    }

    /// <summary>
    /// Reproduces the reported "openUrl URL parameter cannot be edited" flow: chip click ->
    /// flyout -> type -> Enter / focus-away. Reports the model value and the chip text after
    /// each path so the stale-commit suspicion can be confirmed or ruled out.
    /// </summary>
    private static void RunUrlProbe(string outPath)
    {
        static string UrlText() =>
            Params().TryGetValue("url", out var v)
                ? (v.IsVariable ? "<var:" + v.SourceActionId + ">" : (v.Text ?? "<null>"))
                : "<unset>";

        static string ChipText(Window e) =>
            FindChip(e)?.Content is TextBlock tb ? tb.Text ?? "" : "<no-chip>";

        // ---- check 0: pristine geometry (default "https://" chip) ---------------------
        Console.WriteLine("=== check 0: pristine openUrl row geometry (default \"https://\" chip)");
        var editor0 = OpenEditor();
        DumpEditorGeometry(editor0);
        {
            var c0 = FindChip(editor0)!;
            var m0 = editor0.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("toolbar-icon") && Math.Abs(b.Width - 26) < 0.01)!;
            var t0 = TopLevel.GetTopLevel(c0)!;
            var rc = RectInTop(c0, t0);
            var rm = RectInTop(m0, t0);
            Console.WriteLine("  chip=" + Fmt(rc) + " (w=" + rc.Width.ToString("0.#") + ")"
                              + " more=" + Fmt(rm) + " intersects=" + rc.Intersects(rm));
        }
        editor0.Close();

        // A/B on the pristine chip: click its centre, then (fresh editor) its right edge.
        editor0 = OpenEditor();
        var pc = FindChip(editor0)!;
        var pt0 = TopLevel.GetTopLevel(pc)!;
        var pcRect = RectInTop(pc, pt0);
        pt0.MouseDown(new Point(pcRect.Center.X, pcRect.Center.Y), MouseButton.Left);
        Pump(editor0);
        pt0.MouseUp(new Point(pcRect.Center.X, pcRect.Center.Y), MouseButton.Left);
        Pump(editor0);
        Console.WriteLine("  pristine chip CENTRE click -> paramEditorOpen=" + (FindFlyoutTextBox(editor0) != null));
        editor0.Close();

        editor0 = OpenEditor();
        var pc2 = FindChip(editor0)!;
        var pt2 = TopLevel.GetTopLevel(pc2)!;
        var pc2Rect = RectInTop(pc2, pt2);
        pt2.MouseDown(new Point(pc2Rect.Right - 2, pc2Rect.Center.Y), MouseButton.Left);
        Pump(editor0);
        pt2.MouseUp(new Point(pc2Rect.Right - 2, pc2Rect.Center.Y), MouseButton.Left);
        Pump(editor0);
        Console.WriteLine("  pristine chip RIGHT-EDGE click (under \"...\") -> paramEditorOpen="
                          + (FindFlyoutTextBox(editor0) != null));
        editor0.Close();

        // ---- check 1: type into the URL box and press Enter ---------------------------
        Console.WriteLine("=== check 1: openUrl URL box + Enter");
        var editor = OpenEditor();
        Console.WriteLine("  chip before: " + Describe(FindChip(editor)) + "  text='" + ChipText(editor) + "'  params.url=" + UrlText());
        Click(editor, FindChip(editor)!);
        Pump(editor);
        var box = FindFlyoutTextBox(editor);
        Console.WriteLine("  box after chip click: " + Describe(box));
        if (box != null)
        {
            box.Text = "https://example.com";
            box.Focus();
            Pump(editor);
            Console.WriteLine("  before Enter: box.Text='" + box.Text + "'  focused=" + box.IsFocused);
            TopLevel.GetTopLevel(box)!.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump(editor);
            Console.WriteLine("  after Enter: params.url=" + UrlText());
            Console.WriteLine("  chip after: text='" + ChipText(editor) + "'  " + Describe(FindChip(editor)));
            Console.WriteLine("  flyout box still present: " + (FindFlyoutTextBox(editor) != null));
        }
        editor.Close();

        // ---- check 2: type then move focus away (LostFocus commit path) ---------------
        Console.WriteLine("=== check 2: openUrl URL box + focus-away commit");
        editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        box = FindFlyoutTextBox(editor);
        if (box != null)
        {
            box.Text = "https://focus.example.com";
            box.Focus();
            Pump(editor);
            Console.WriteLine("  before blur: box.Text='" + box.Text + "'");
            var other = editor.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(t => !ReferenceEquals(t, box));
            other?.Focus();
            Pump(editor);
            Console.WriteLine("  after blur (focus -> " + Describe(other) + "): params.url=" + UrlText()
                              + "  chip text='" + ChipText(editor) + "'");
        }
        editor.Close();

        // ---- check 3: Enter then reopen the chip (stale value round-trip) --------------
        Console.WriteLine("=== check 3: Enter, then reopen the (rebuilt) chip");
        editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        box = FindFlyoutTextBox(editor);
        if (box != null)
        {
            box.Text = "https://first.example.com";
            box.Focus();
            Pump(editor);
            TopLevel.GetTopLevel(box)!.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Pump(editor);
            Console.WriteLine("  after Enter: params.url=" + UrlText() + "  chip text='" + ChipText(editor) + "'");
            Click(editor, FindChip(editor)!);
            Pump(editor);
            var box2 = FindFlyoutTextBox(editor);
            Console.WriteLine("  reopened box.Text='" + (box2?.Text ?? "<null>") + "'");
        }
        editor.Close();

        // ---- check 4: does the box get focus on open? does a mouse click keep it open? --
        Console.WriteLine("=== check 4: focus / click behaviour of the flyout box");
        editor = OpenEditor();
        Click(editor, FindChip(editor)!);
        Pump(editor);
        box = FindFlyoutTextBox(editor);
        Console.WriteLine("  right after chip click: box=" + Describe(box) + "  focused=" + (box?.IsFocused));
        if (Path.GetDirectoryName(outPath) is { Length: > 0 } dir1)
            editor.CaptureRenderedFrame()?.Save(Path.Combine(dir1, "url_flyout.png"));
        if (box != null)
        {
            var topB = TopLevel.GetTopLevel(box)!;
            var bp = box.TranslatePoint(new Point(box.Bounds.Width / 2, box.Bounds.Height / 2), topB);
            topB.MouseDown(bp!.Value, MouseButton.Left);
            Pump(editor);
            topB.MouseUp(bp.Value, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  after mouse click on box: focused=" + box.IsFocused
                              + "  flyoutStillOpen=" + (FindFlyoutTextBox(editor) != null));
            if (box.IsFocused)
            {
                box.CaretIndex = box.Text?.Length ?? 0;
                topB.KeyTextInput("example.com");
                Pump(editor);
                Console.WriteLine("  after KeyTextInput: box.Text='" + box.Text + "'");
                topB.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                Pump(editor);
                Console.WriteLine("  after Enter: params.url=" + UrlText() + "  chip='" + ChipText(editor) + "'");
                if (Path.GetDirectoryName(outPath) is { Length: > 0 } dir2)
                    editor.CaptureRenderedFrame()?.Save(Path.Combine(dir2, "url_after_enter.png"));
            }
        }
        editor.Close();

        // ---- check 5: row geometry — does the invisible "..." button overlap the chip? ----
        Console.WriteLine("=== check 5: row geometry (chip vs \"...\" button)");
        editor = OpenEditor();
        DumpEditorGeometry(editor);
        var chipNow = FindChip(editor);
        var moreBtn = editor.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("toolbar-icon") && Math.Abs(b.Width - 26) < 0.01);
        if (chipNow is not null && moreBtn is not null)
        {
            var topG = TopLevel.GetTopLevel(chipNow)!;
            var chipRect = RectInTop(chipNow, topG);
            var moreRect = RectInTop(moreBtn, topG);
            var overlap = chipRect.Intersect(moreRect);
            Console.WriteLine("  chip rect=" + Fmt(chipRect) + "  ... rect=" + Fmt(moreRect)
                              + "  overlap=" + Fmt(overlap) + " (intersects=" + chipRect.Intersects(moreRect) + ")");

            // A/B: chip centre vs chip right edge (where the invisible "..." button sits).
            // Use a fresh editor per click so the rebuilt rows/anchors do not cross windows.
            var center = new Point(chipRect.Center.X, chipRect.Center.Y);
            topG.MouseDown(center, MouseButton.Left);
            Pump(editor);
            topG.MouseUp(center, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  click chip CENTER -> paramEditorOpen=" + (FindFlyoutTextBox(editor) != null));
            editor.Close();

            editor = OpenEditor();
            var chipB = FindChip(editor)!;
            var topB2 = TopLevel.GetTopLevel(chipB)!;
            var chipRectB = RectInTop(chipB, topB2);
            var rightEdge = new Point(chipRectB.Right - 3, chipRectB.Center.Y);
            topB2.MouseDown(rightEdge, MouseButton.Left);
            Pump(editor);
            topB2.MouseUp(rightEdge, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  click chip RIGHT-EDGE -> paramEditorOpen=" + (FindFlyoutTextBox(editor) != null)
                              + "  chipRect=" + Fmt(chipRectB));
        }
        editor.Close();

        // ---- check 6: a long URL — is the chip clipped by ClipToBounds? ----------------
        Console.WriteLine("=== check 6: long URL chip clipping");
        Params()["url"] = ShortcutValue.Literal("https://example.com/" + new string('a', 160));
        editor = OpenEditor();
        DumpEditorGeometry(editor);
        var chipLong = FindChip(editor);
        if (chipLong?.Content is TextBlock inner)
        {
            Console.WriteLine("  chip bounds=" + Describe(chipLong)
                              + "  innerText desired=" + inner.DesiredSize.Width.ToString("0.#")
                              + "  innerText actual=" + inner.Bounds.Width.ToString("0.#"));
            var chipsStack = chipLong.Parent as StackPanel;
            Console.WriteLine("  chipsStack bounds=" + Describe(chipsStack)
                              + "  clip=" + chipsStack?.ClipToBounds);
        }
        if (Path.GetDirectoryName(outPath) is { Length: > 0 } dir3)
            editor.CaptureRenderedFrame()?.Save(Path.Combine(dir3, "url_long.png"));
        editor.Close();
    }

    /// <summary>Opens the sendHotkey chip and dumps the shared capture control geometry.</summary>
    private static void RunHotkeyParamProbe(string outPath)
    {
        Console.WriteLine("=== sendHotkey parameter probe ===");
        var editor = OpenEditor();
        DumpEditorGeometry(editor);

        var chip = FindChip(editor);
        Console.WriteLine("  chip: " + Describe(chip));
        if (chip == null)
        {
            editor.Close();
            return;
        }

        Click(editor, chip);
        Pump(editor);

        var captures = ProbeRoots(editor)
            .SelectMany(root => root.GetVisualDescendants().OfType<Shortcuts.Views.Controls.HotkeyCaptureBox>())
            .ToList();
        Console.WriteLine("  captures: " + captures.Count + " [" + string.Join(",", captures.Select(c => c.IsReadOnly)) + "]");
        var capture = captures.FirstOrDefault(c => !c.IsReadOnly);
        Console.WriteLine("  capture: " + Describe(capture));
        if (capture != null)
        {
            var text = capture.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            var record = capture.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Classes.Contains("hotkey-capture-action"));
            Console.WriteLine("  capture text='" + (text?.Text ?? "<null>") + "' readonly=" + (text?.IsReadOnly)
                              + " record=" + Describe(record));
        }

        editor.CaptureRenderedFrame()?.Save(outPath);

        if (capture != null)
        {
            var input = capture.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            Console.WriteLine("  capture input='" + (input?.Text ?? "<null>") + "' focused=" + (input?.IsFocused));
        }

        editor.Close();
    }

    private static Rect RectInTop(Control c, TopLevel top)
    {
        var p = c.TranslatePoint(new Point(0, 0), top) ?? new Point(0, 0);
        return new Rect(p, new Size(c.Bounds.Width, c.Bounds.Height));
    }

    /// <summary>
    /// A container row ("repeat"/"if") puts its collapse chevron in the block's last column —
    /// exactly where the invisible "..." button is clamped. Checks overlap + whether clicking
    /// the chevron still collapses the container.
    /// </summary>
    private static void RunChevronProbe(string outPath)
    {
        Console.WriteLine("=== chevron probe: container collapse vs \"...\" button ===");
        var editor = OpenEditor();
        DumpEditorGeometry(editor);

        var chevron = editor.GetVisualDescendants().OfType<Shortcuts.Views.Controls.GlyphView>()
            .FirstOrDefault(g => Math.Abs(g.Width - 12) < 0.01 && Math.Abs(g.Height - 12) < 0.01);
        var more = editor.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Classes.Contains("toolbar-icon") && Math.Abs(b.Width - 26) < 0.01);
        if (chevron is null || more is null)
        {
            Console.WriteLine("  chevron/more not found (chevron=" + (chevron != null) + ", more=" + (more != null) + ")");
            editor.Close();
            return;
        }

        var top = TopLevel.GetTopLevel(chevron)!;
        var rc = RectInTop(chevron, top);
        var rm = RectInTop(more, top);
        Console.WriteLine("  chevron=" + Fmt(rc) + "  more=" + Fmt(rm) + "  intersects=" + rc.Intersects(rm));
        Console.WriteLine("  visible rows before=" + VisibleRows(editor));

        // Which control is actually under the chevron's centre? (IsPointerOver follows real hit testing.)
        var c = new Point(rc.Center.X, rc.Center.Y);
        top.MouseMove(c);
        Pump(editor);
        Console.WriteLine("  pointer at chevron centre -> chevron.IsPointerOver=" + chevron.IsPointerOver
                          + "  more.IsPointerOver=" + more.IsPointerOver);

        top.MouseDown(c, MouseButton.Left);
        Pump(editor);
        top.MouseUp(c, MouseButton.Left);
        Pump(editor);
        Console.WriteLine("  after click chevron centre: visible rows=" + VisibleRows(editor));

        // Causality proof (test-side only): disable hit-testing on the invisible button and repeat.
        more.IsHitTestVisible = false;
        top.MouseMove(c);
        Pump(editor);
        Console.WriteLine("  with more.IsHitTestVisible=false -> chevron.IsPointerOver=" + chevron.IsPointerOver);
        top.MouseDown(c, MouseButton.Left);
        Pump(editor);
        top.MouseUp(c, MouseButton.Left);
        Pump(editor);
        Console.WriteLine("  after click again: visible rows=" + VisibleRows(editor));

        // Is the drag grip (also a GlyphView) hit-testable over its box?
        var grip = editor.GetVisualDescendants().OfType<Shortcuts.Views.Controls.GlyphView>()
            .FirstOrDefault(g => Math.Abs(g.Width - 12) < 0.01 && Math.Abs(g.Height - 14) < 0.01);
        if (grip is not null)
        {
            var gr = RectInTop(grip, top);
            var gp = new Point(gr.Center.X, gr.Center.Y);
            top.MouseMove(gp);
            Pump(editor);
            Console.WriteLine("  grip rect=" + Fmt(gr) + "  grip.IsPointerOver=" + grip.IsPointerOver);
            Console.WriteLine("  visuals under grip centre: " + string.Join(" > ",
                top.GetVisualsAt(gp).Take(6).Select(v => v.GetType().Name)));

            // Does a press on the grip start a drag? BeginDrag dims the row (Opacity 0.45).
            var rowRoot = editor.GetVisualDescendants().OfType<Grid>()
                .First(g => g.Children.OfType<Button>().Any(b => b.Classes.Contains("toolbar-icon")));
            top.MouseDown(gp, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  press on grip -> rowGrid.Opacity=" + rowRoot.Opacity + " (0.45 would mean drag started)");
            top.MouseUp(gp, MouseButton.Left);
            Pump(editor);

            // Sweep the grip box: is the GlyphView hit-testable anywhere, or only on its strokes?
            var hits = new List<string>();
            for (var sx = 0.0; sx <= gr.Width; sx += 2)
            for (var sy = 0.0; sy <= gr.Height; sy += 2)
            {
                top.MouseMove(new Point(gr.X + sx, gr.Y + sy));
                Pump(editor);
                if (grip.IsPointerOver) hits.Add($"{sx:0},{sy:0}");
            }
            Console.WriteLine("  grip hit points (local x,y of 12x14 box): "
                              + (hits.Count == 0 ? "<none>" : string.Join(" ", hits)));

            // Does a press on a reported "hit" point actually start a drag?
            var hp = new Point(gr.X + 4, gr.Y);
            top.MouseDown(hp, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  press at grip-edge hit point (4,0) -> rowGrid.Opacity=" + rowRoot.Opacity);
            top.MouseUp(hp, MouseButton.Left);
            Pump(editor);

            // And a press a little inside the box (the natural click point).
            var hp2 = new Point(gr.X + 4, gr.Y + 4);
            top.MouseDown(hp2, MouseButton.Left);
            Pump(editor);
            Console.WriteLine("  press just inside (4,4) -> rowGrid.Opacity=" + rowRoot.Opacity);
            top.MouseUp(hp2, MouseButton.Left);
            Pump(editor);
            top.MouseMove(new Point(5, 5));
            Pump(editor);
        }

        var chipForHit = FindChip(editor);
        if (chipForHit is not null)
        {
            var cp = new Point(RectInTop(chipForHit, top).Center.X, RectInTop(chipForHit, top).Center.Y);
            Console.WriteLine("  visuals under chip centre: " + string.Join(" > ",
                top.GetVisualsAt(cp).Take(6).Select(v => v.GetType().Name)));
        }

        editor.CaptureRenderedFrame()?.Save(outPath);
        editor.Close();
    }

    private static int VisibleRows(Window editor) => ProbeRoots(editor)
        .SelectMany(root => root.GetVisualDescendants().OfType<Grid>())
        .Count(g => g.IsEffectivelyVisible
                    && g.Children.OfType<Button>().Any(b => b.Classes.Contains("toolbar-icon")
                                                            && Math.Abs(b.Width - 26) < 0.01));

    private static string Fmt(Rect r) =>
        r.Width <= 0 || r.Height <= 0 ? "<empty>" : $"({r.X:0.#},{r.Y:0.#},{r.Width:0.#}x{r.Height:0.#})";

    /// <summary>Does the row's "..." overflow button appear on hover? Injects MouseMove in/out.</summary>
    private static void RunHoverProbe(string outPath)
    {
        Console.WriteLine("=== hover probe: row \"...\" button ===");
        var editor = OpenEditor();
        var rowGrid = editor.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(g => g.Children.OfType<Button>().Any(b => b.Classes.Contains("toolbar-icon")));
        var more = rowGrid?.Children.OfType<Button>().FirstOrDefault(b => b.Classes.Contains("toolbar-icon"));
        Console.WriteLine("  rowGrid: " + Describe(rowGrid) + "  moreButton: " + Describe(more)
                          + " opacity=" + more?.Opacity);
        if (rowGrid is null || more is null)
        {
            editor.Close();
            return;
        }

        var top = TopLevel.GetTopLevel(rowGrid)!;
        var center = rowGrid.TranslatePoint(new Point(rowGrid.Bounds.Width / 2, rowGrid.Bounds.Height / 2), top);
        top.MouseMove(center!.Value);
        Pump(editor);
        Console.WriteLine("  after move into row center: opacity=" + more.Opacity
                          + "  rowGrid.IsPointerOver=" + rowGrid.IsPointerOver);

        var blockColumn = rowGrid.Children.OfType<StackPanel>().FirstOrDefault();
        var block = blockColumn?.Children.OfType<Border>().FirstOrDefault();
        if (block is not null)
        {
            var bp = block.TranslatePoint(new Point(block.Bounds.Width / 2, block.Bounds.Height / 2), top);
            top.MouseMove(bp!.Value);
            Pump(editor);
            Console.WriteLine("  after move onto block child: opacity=" + more.Opacity
                              + "  rowGrid.IsPointerOver=" + rowGrid.IsPointerOver);
        }

        top.MouseMove(new Point(5, top.Bounds.Height - 5));
        Pump(editor);
        Console.WriteLine("  after move outside row: opacity=" + more.Opacity);

        editor.CaptureRenderedFrame()?.Save(outPath);
        editor.Close();
    }

    private static TextBox? FindFlyoutTextBox(Window editor)
    {
        if (Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(editor) is { } overlay)
            return overlay.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
        return null;
    }

    private static ShortcutsEditorWindow OpenEditor()
    {
        // Settle the platform between recycled windows: the headless popup host occasionally
        // keeps teardown work queued, and starting the next window mid-teardown can wedge.
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var editor = new ShortcutsEditorWindow(ShortcutsStore.Get().Shortcuts[0].Id);
        editor.Show();
        Pump(editor);
        return editor;
    }

    private static Dictionary<string, ShortcutValue> Params() =>
        ShortcutsStore.Get().Shortcuts[0].Actions[0].Params;

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static IEnumerable<Visual> ProbeRoots(Window window)
    {
        yield return window;
        if (Avalonia.Controls.Primitives.OverlayLayer.GetOverlayLayer(window) is { } overlay)
            yield return overlay;
        foreach (var owned in window.OwnedWindows)
            yield return owned;
    }

    private static Button? FindChip(Window editor)
    {
        foreach (var root in ProbeRoots(editor))
        {
            foreach (var button in root.GetVisualDescendants().OfType<Button>())
            {
                if (button.Classes.Contains("chip-btn")) return button;
            }
        }

        return null;
    }

    private static Button? FindByContent(Window editor, string text)
    {
        foreach (var root in ProbeRoots(editor))
        {
            foreach (var button in root.GetVisualDescendants().OfType<Button>())
            {
                if (button.Content as string == text) return button;
            }
        }

        return null;
    }

    private static MenuItem? FindMenuItem(Window editor, string header)
    {
        foreach (var root in ProbeRoots(editor))
        {
            foreach (var item in root.GetVisualDescendants().OfType<MenuItem>())
            {
                if (item.Header as string == header) return item;
            }
        }

        return null;
    }

    private static void Click(Window editor, Control control)
    {
        var top = TopLevel.GetTopLevel(control);
        var pt = top is null
            ? null
            : control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), top);
        if (top is null || pt is null)
        {
            Console.WriteLine("    click failed: no top level / no point");
            return;
        }

        top.MouseDown(pt.Value, MouseButton.Left);
        top.MouseUp(pt.Value, MouseButton.Left);
    }

    private static string Describe(Control? control)
    {
        if (control is null) return "null";
        var content = control switch
        {
            Button b => b.Content as string ?? (b.Content as TextBlock)?.Text,
            _ => null,
        };
        return $"{control.GetType().Name}('{content ?? "?"}') bounds={control.Bounds.X:0.#},{control.Bounds.Y:0.#},{control.Bounds.Width:0.#},{control.Bounds.Height:0.#}";
    }

    /// <summary>Three icon renderings side by side: A current, B normalized geometry, C viewbox.</summary>
    private static Control BuildIconProbe()
    {
        const string lockPath = "M6.5 10.5h11V20h-11zM9 10.5V8a3 3 0 0 1 6 0v2.5M12 14v3";

        static ShapePath StrokePath(Geometry geo) => new()
        {
            Data = geo,
            Stroke = Brushes.White,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
        };

        static ShapePath Stretched(Geometry geo) => new()
        {
            Data = geo,
            Stroke = Brushes.White,
            StrokeThickness = 1.7,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            Width = 15,
            Height = 15,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var raw = StreamGeometry.Parse(lockPath);

        Border Badge(Control child) => new()
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#185FA5")),
            Margin = new Thickness(4),
            Child = child,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(Badge(Stretched(raw))); // A: current behavior
        panel.Children.Add(Badge(new Viewbox { Width = 15, Height = 15, Child = StrokePath(StreamGeometry.Parse(lockPath)) })); // C: viewbox
        return panel;
    }

    private static ShortcutsDocument BuildDocument(string scenario)
    {
        // Different name lengths on purpose: with the old content-sized layout every
        // block came out a different width; the fixed layout must render them equal.
        var shortcuts = new List<ShortcutDefinition>
        {
            new() { Name = "一键锁屏", Glyph = "lock", Color = "#185FA5", Actions = [ShortcutAction.Create("lockScreen"), ShortcutAction.Create("screenshot")] },
            new() { Name = "新指令", Glyph = "bolt", Color = "#534AB7", Actions = [] },
            new() { Name = "打开工作文件夹", Glyph = "folder", Color = "#0F6E56", Actions = [ShortcutAction.Create("openFolder")] },
        };

        if (scenario is "small" or "running") shortcuts.RemoveAt(2); // the live card: two tiles + the "new" tile

        if (scenario == "graph")
        {
            // A branch + loop graph: the shapes the v1 list editor could not draw at all.
            var graph = new WorkflowGraph();
            var start = new WorkflowNode { Type = WorkflowNodeTypes.Start, X = 0, Y = 0 };
            var clip = WorkflowNode.Create("getClipboard", 300, 0);
            var branch = WorkflowNode.With("branch", 600, 0,
                ("left", "{{" + clip.Id + "}}"), ("op", "isEmpty"));
            var empty = WorkflowNode.With("notify", 900, 0, ("message", "剪贴板是空的"));
            var loop = WorkflowNode.With("forLoop", 900, 200, ("count", "3"));
            var body = WorkflowNode.Create("screenshot", 1200, 200);
            var done = WorkflowNode.With("notify", 1200, 360, ("message", "完成"));
            graph.Nodes.AddRange([start, clip, branch, empty, loop, body, done]);
            graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, clip.Id));
            graph.Links.Add(WorkflowLink.Exec(clip.Id, WorkflowNodeTypes.Out, branch.Id));
            graph.Links.Add(WorkflowLink.Exec(branch.Id, WorkflowNodeTypes.TruePort, empty.Id));
            graph.Links.Add(WorkflowLink.Exec(branch.Id, WorkflowNodeTypes.FalsePort, loop.Id));
            graph.Links.Add(WorkflowLink.Exec(loop.Id, WorkflowNodeTypes.BodyPort, body.Id));
            graph.Links.Add(WorkflowLink.Exec(loop.Id, WorkflowNodeTypes.DonePort, done.Id));
            graph.Links.Add(WorkflowLink.Data(clip.Id, empty.Id, "message"));

            return new ShortcutsDocument
            {
                Version = 2,
                Shortcuts = [new ShortcutDefinition { Name = "剪贴板工作流", Glyph = "branch", Color = "#534AB7", Graph = graph }],
            };
        }


        if (scenario == "browse")
        {
            // The user's case: an openFile action whose path parameter is still unset.
            return new ShortcutsDocument
            {
                Version = 1,
                Shortcuts =
                [
                    new()
                    {
                        Name = "打开文件测试", Glyph = "folder", Color = "#185FA5",
                        Actions = [ShortcutAction.Create("openFile")],
                    },
                ],
            };
        }

        if (scenario is "urlprobe" or "hoverprobe")
        {
            return new ShortcutsDocument
            {
                Version = 1,
                Shortcuts =
                [
                    new()
                    {
                        Name = "打开网页测试", Glyph = "globe", Color = "#185FA5",
                        Actions = [ShortcutAction.Create("openUrl")],
                    },
                ],
            };
        }

        if (scenario == "hotkey")
        {
            return new ShortcutsDocument
            {
                Version = 1,
                Shortcuts =
                [
                    new()
                    {
                        Name = "一键专注", Glyph = "moon", Color = "#534AB7", Hotkey = "Ctrl+Alt+1",
                        Actions = [ShortcutAction.Create("mediaPlayPause")],
                    },
                    new()
                    {
                        Name = "工作区启动", Glyph = "app", Color = "#185FA5", Hotkey = "Ctrl+Alt+2",
                        Actions = [ShortcutAction.Create("openFolder")],
                    },
                ],
            };
        }

        if (scenario == "chevronprobe")
        {
            var repeat = ShortcutAction.Create("repeat");
            repeat.Children.Add(ShortcutAction.With("comment", ("note", "hi")));
            return new ShortcutsDocument
            {
                Version = 1,
                Shortcuts = [new() { Name = "循环测试", Glyph = "repeat", Color = "#534AB7", Actions = [repeat] }],
            };
        }

        if (scenario == "hotkeyparam")
        {
            return new ShortcutsDocument
            {
                Version = 1,
                Shortcuts =
                [
                    new()
                    {
                        Name = "发送快捷键测试", Glyph = "keyboard", Color = "#534AB7",
                        Actions =
                        [
                            ShortcutAction.With("sendHotkey", ("hotkey", "ctrl + shift + s"), ("holdMs", "40")),
                        ],
                    },
                ],
            };
        }

        return new ShortcutsDocument { Version = 1, Shortcuts = shortcuts };
    }

    private static void DumpEditorGeometry(Window editor)
    {
        var rowsHost = editor.GetVisualDescendants().OfType<StackPanel>().FirstOrDefault(p => p.Name == "RowsHost");
        Console.WriteLine("=== editor rows geometry ===");
        Console.WriteLine("RowsHost: " + (rowsHost is null
            ? "null"
            : $"bounds={rowsHost.Bounds} hAlign={rowsHost.HorizontalAlignment} maxWidth={rowsHost.MaxWidth}"));

        if (rowsHost is not null)
        {
            foreach (var child in rowsHost.Children.OfType<Grid>())
            {
                Console.WriteLine($"-- rowGrid bounds={child.Bounds} cols={child.ColumnDefinitions.Count}");
                foreach (var rc in child.Children)
                {
                    var col = Grid.GetColumn(rc);
                    var span = Grid.GetColumnSpan(rc);
                    var extra = rc switch
                    {
                        Button b => $" opacity={b.Opacity} classes=[{string.Join(",", b.Classes)}]",
                        StackPanel sp => " children=[" + string.Join(", ", sp.Children.Select(c => c.GetType().Name + c.Bounds)) + "]",
                        _ => string.Empty,
                    };
                    Console.WriteLine($"   {rc.GetType().Name} col={col} span={span} bounds={rc.Bounds}{extra}");
                }
            }
        }
    }

    private static void Dump(Visual visual, int depth = 0)
    {
        if (depth > 14) return;
        foreach (var child in visual.GetVisualChildren())
        {
            if (child is not Visual v) continue;
            var line = new string(' ', depth * 2) + v.GetType().Name;
            if (v is Control c)
            {
                line += string.Create(CultureInfo.InvariantCulture, $" bounds={c.Bounds.X:0.#},{c.Bounds.Y:0.#},{c.Bounds.Width:0.#},{c.Bounds.Height:0.#}");
            }
            if (v is Layoutable l)
            {
                line += $" ha={l.HorizontalAlignment} va={l.VerticalAlignment}";
            }
            if (v is ContentPresenter cp)
            {
                line += $" hca={cp.HorizontalContentAlignment} vca={cp.VerticalContentAlignment}";
            }
            if (v is Border b && b.Background is ISolidColorBrush sb)
            {
                line += $" bg=#{sb.Color.A:X2}{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}";
            }
            Console.WriteLine(line);
            Dump(v, depth + 1);
        }
    }
}

/// <summary>
/// Settings provider for the monochrome render probes: one theme, one monochrome variant, and
/// nothing else the card needs. Mirrors the live wiring (the provider is what the tiles read).
/// </summary>
internal sealed class MonoProbeSettings(DeskCanvas.Core.Models.Settings.MonochromeStyle? variant, string? accent)
    : DeskCanvas.Core.Interfaces.IAppSettingsProvider
{
    private readonly DeskCanvas.Core.Models.Settings.AppSettings data = new(
        new DeskCanvas.Core.Models.Settings.Theme(
            DarkMode: true, AccentColor: null, OpacityLevel: 1.0, Monochrome: true,
            UseNativeFrame: false, FontFamily: "Segoe UI",
            Surface: DeskCanvas.Core.Models.Settings.SurfaceStyle.LiquidGlass,
            MonochromeVariant: variant),
        [],
        new DeskCanvas.Core.Models.Settings.Layout(DeskCanvas.Core.Models.Settings.GridMode.Manual, true, false, true, false),
        new DeskCanvas.Core.Models.Settings.Dimensions(160, 12, 24),
        new DeskCanvas.Core.Models.Settings.Region("en-US"),
        false,
        null);

    public DeskCanvas.Core.Models.Settings.AppSettings Get() =>
        data with { Theme = data.Theme with { AccentColor = accent } };

    public void Save(DeskCanvas.Core.Models.Settings.AppSettings value) { }

    public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.Settings.AppSettings>? DataChanging
    {
        add { } remove { }
    }

    public event DeskCanvas.Core.Interfaces.DataChangedEvent<DeskCanvas.Core.Models.Settings.AppSettings>? DataChanged
    {
        add { } remove { }
    }
}
