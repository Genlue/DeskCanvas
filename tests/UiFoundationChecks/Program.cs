using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Grid = Avalonia.Controls.Grid;
// The shared row/layout engine and the input enums moved to DeskCanvas.UI (ADR-P3); the host
// facades keep the old type names. Alias the moved types so the existing assertions keep
// running against the one shared implementation.
using SettingLayoutPanel = DeskCanvas.UI.Controls.SettingLayoutPanel;
using SettingLayoutSlot = DeskCanvas.UI.Controls.SettingLayoutSlot;
using SettingLayoutMode = DeskCanvas.UI.Controls.SettingLayoutMode;
using SettingEffectiveLayout = DeskCanvas.UI.Controls.SettingEffectiveLayout;
using ColorInputFormat = DeskCanvas.UI.Controls.ColorInputFormat;
using ColorInputAlphaPolicy = DeskCanvas.UI.Controls.ColorInputAlphaPolicy;

namespace UiFoundationChecks;

/// <summary>
/// UI foundation checks — W1 batch: the text-selection palette.
/// <list type="bullet">
/// <item><b>Contrast algorithm</b>: pure WCAG math over the boundary accents (black/white/
/// yellow/light cyan/deep blue/red/grey) — the higher-contrast of black/white wins and the
/// resulting pair is always ≥ 4.5:1; alpha is normalised to opaque.</item>
/// <item><b>Real resolution + writes</b>: with the real App styles loaded (ProbeApp runs the
/// real App.axaml), ThemeService.Apply must write the COMPUTED pair into the Default and Dark
/// theme dictionaries of the merged InteractionResources dictionary — per variant, never one
/// global value.</item>
/// <item><b>Template coverage</b>: an open NumericUpDown's PART_TextBox / TextPresenter must
/// resolve the paired brushes through the shared rules, and must follow a live accent change
/// without being reopened.</item>
/// <item><b>GridEditor local exception</b>: the fixed light tool surface redeclares the pair
/// locally; its inputs must keep the dark-selection/white-text pair no matter the app accent.</item>
/// <item><b>Idempotent rewrite</b>: re-writing identical pairs raises no ResourcesChanged (the
/// compare-before-write guard), while a real change does.</item>
/// <item><b>EditWidget (W2)</b>: the header strip is hit-testable as a whole, the shared
/// drag handle excludes buttons and their contents, Save still closes the window, legal
/// negative coordinates survive the first show, and later activations never re-place the
/// window (once-only geometry guard).</item>
/// </list>
/// Usage: dotnet run --project tests/UiFoundationChecks -c Release
/// </summary>
/// <summary>
/// Split across several files so several work packages can extend the suite without editing
/// the same file: <c>Program.cs</c> owns Main and the shared helpers, every W-package owns its
/// own <c>Check…</c> section file.
/// </summary>
internal static partial class Program
{
    private static int failures;

    private static void Check(bool condition, string message)
    {
        Console.WriteLine(condition ? $"  [PASS] {message}" : $"  [FAIL] {message}");
        if (!condition) failures++;
    }

    private static readonly ThemeVariant[] Variants = [ThemeVariant.Light, ThemeVariant.Dark];

    public static int Main()
    {
        // Keep every path the app derives from Const inside the build output (portable mode),
        // away from %LocalAppData%\DeskCanvas.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Widgets"));

        AppBuilder.Configure<ProbeApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .WithInterFont()
            .SetupWithoutStarting();

        Console.WriteLine("=== UI foundation checks (W1: text selection) ===");

        CheckContrastAlgorithm();
        CheckResolutionAndVariantWrites();
        CheckTemplateCoverageAndLiveUpdate();
        CheckGridEditorLocalException();
        CheckIdempotentRewrite();
        CheckEditWidgetDragAndGeometry();
        CheckSettingAdaptiveLayout();
        CheckScrollOwnership();
        CheckWindowGeometry();
        CheckIconButtonsAndSectionHeaders();
        CheckCompoundInputs();
        CheckPanelShell();
        CheckGlassSamplingOrigin();
        CheckGlassSettleProbing();
        CheckPopupMigrationTools();
        CheckPopupMigrationInfo();
        CheckPopupMigrationHeavy();
        CheckSettingsPagesAndLocale();
        CheckSettingsPagesBatchOne();
        CheckSettingsPagesBatchTwo();
        CheckHostDecoupling();
        CheckSpecularRim();
        CheckMotion();
        CheckAnimationFrameProbe();
        CheckStretchArrangementProbe();
        CheckResultHighlightFlight();
        CheckGlassShapePort();
        CheckRedundancyGuard();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL UI FOUNDATION CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // ---- 1. Pure contrast algorithm -------------------------------------------------------

    private static void CheckContrastAlgorithm()
    {
        Console.WriteLine("--- contrast algorithm (WCAG relative luminance) ---");

        Check(Math.Abs(TextSelectionPaletteResolver.RelativeLuminance(Colors.Black)) < 1e-9,
            "luminance(black) = 0");
        Check(Math.Abs(TextSelectionPaletteResolver.RelativeLuminance(Colors.White) - 1.0) < 1e-9,
            "luminance(white) = 1");
        Check(Math.Abs(TextSelectionPaletteResolver.ContrastRatio(Colors.Black, Colors.White) - 21.0) < 0.01,
            "contrast(black, white) = 21:1");

        var cases = new (string Name, string Accent, Color Expected)[]
        {
            ("black", "#000000", Colors.White),
            ("white", "#FFFFFF", Colors.Black),
            ("yellow", "#FFD700", Colors.Black),      // the historic white-on-yellow defect
            ("light cyan", "#7FDBFF", Colors.Black),
            ("deep blue", "#0B2A5B", Colors.White),
            ("red", "#FF0000", Colors.Black),
            ("mid grey", "#808080", Colors.Black),
        };
        foreach (var (name, accentText, expected) in cases)
        {
            var palette = TextSelectionPaletteResolver.Compute(Color.Parse(accentText));
            var contrast = TextSelectionPaletteResolver.ContrastRatio(palette.Background, palette.Foreground);
            Check(palette.Foreground == expected,
                $"{name} {accentText}: picks {(expected == Colors.Black ? "black" : "white")} text");
            Check(contrast >= 4.5,
                $"{name} {accentText}: contrast {contrast:F2}:1 >= 4.5");
        }

        var translucent = TextSelectionPaletteResolver.Compute(Color.Parse("#80123456"));
        Check(translucent.Background == Color.Parse("#FF123456"),
            "translucent accent #80123456 normalises to opaque #FF123456 (RGB kept, alpha = 255)");
    }

    // ---- 2. Resolution through the real theme + per-variant writes ------------------------

    private static IThemeService? themeService;

    private static void CheckResolutionAndVariantWrites()
    {
        Console.WriteLine("--- accent resolution + per-variant writes (real App styles) ---");

        var settings = new StubSettings(new AppSettings(
            new Theme(false, "#FFD700", 1, false, false, "Inter", SurfaceStyle.Acrylic),
            [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        themeService = new ThemeService(settings, new WallpaperThemeService());
        themeService.Apply(settings.Get().Theme);

        Check(TextSelectionPaletteResolver.TryResolveAccentColor(ThemeVariant.Light) == Color.Parse("#FFD700"),
            "resolver reads the applied custom accent (gold) with a variant-aware lookup");
        foreach (var variant in Variants)
        {
            Check(PairBackground(variant) == Color.Parse("#FFD700") && PairForeground(variant) == Colors.Black,
                $"{variant}: applied gold accent writes gold background + black text into the pair");
        }

        // Per-variant routing: write clearly different pairs through the real helper and read
        // each variant back — the light result must never bleed into the dark dictionary.
        InvokeUpdateTextSelectionResources(
            new TextSelectionPalette(Color.Parse("#112233"), Colors.White),
            new TextSelectionPalette(Color.Parse("#FEDCBA"), Colors.Black));
        Check(PairBackground(ThemeVariant.Light) == Color.Parse("#112233")
              && PairForeground(ThemeVariant.Light) == Colors.White,
            "light variant reads its own dictionary (#112233 / white)");
        Check(PairBackground(ThemeVariant.Dark) == Color.Parse("#FEDCBA")
              && PairForeground(ThemeVariant.Dark) == Colors.Black,
            "dark variant reads its own dictionary (#FEDCBA / black)");

        // 黑白: dark mode = white accent; the pair must never be white-on-white.
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, dark: true));
        Check(PairBackground(ThemeVariant.Dark) == Colors.White && PairForeground(ThemeVariant.Dark) == Colors.Black,
            "黑白 dark: dark variant pair = white background + black text");
        Check(PairBackground(ThemeVariant.Light) == Colors.White && PairForeground(ThemeVariant.Light) == Colors.Black,
            "黑白 dark: light variant pair stays readable (white background + black text)");

        // 黑白: light mode = black accent.
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, dark: false));
        Check(PairBackground(ThemeVariant.Light) == Colors.Black && PairForeground(ThemeVariant.Light) == Colors.White,
            "黑白 light: light variant pair = black background + white text");
    }

    // ---- 3. Template coverage + live update ----------------------------------------------

    private static void CheckTemplateCoverageAndLiveUpdate()
    {
        Console.WriteLine("--- NumericUpDown / TextBox template coverage + live updates ---");

        var window = new Window { RequestedThemeVariant = ThemeVariant.Light, Width = 400, Height = 300 };
        var number = new NumericUpDown { Width = 150, Value = 42 };
        window.Content = number;
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var inner = number.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(t => t.Name == "PART_TextBox");
        Check(inner is not null, "NumericUpDown template built its PART_TextBox");
        if (inner is null) return;

        var expectedBackground = PairBackground(ThemeVariant.Light);
        var expectedForeground = PairForeground(ThemeVariant.Light);
        Check(BrushColor(inner.SelectionBrush) == expectedBackground,
            $"PART_TextBox SelectionBrush resolves the computed pair ({expectedBackground})");
        Check(BrushColor(inner.SelectionForegroundBrush) == expectedForeground,
            $"PART_TextBox SelectionForegroundBrush resolves the computed pair ({expectedForeground})");
        var presenter = inner.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        Check(presenter is not null && BrushColor(presenter.SelectionBrush) == expectedBackground,
            "TextPresenter renders the same selection background (template parts agree)");

        // Live update on an OPEN window: switch the accent, the same instance must follow.
        themeService!.Apply(new Theme(
            DarkMode: false,
            AccentColor: "#FFD700",
            OpacityLevel: 1.0,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Acrylic));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(BrushColor(inner.SelectionBrush) == Color.Parse("#FFD700")
              && BrushColor(inner.SelectionForegroundBrush) == Colors.Black,
            "open NumericUpDown follows a live accent change (gold + black text, no reopen)");

        // Plain TextBox shares the same rule.
        var textBox = new TextBox { Width = 150, Text = "selection probe" };
        window.Content = textBox;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(BrushColor(textBox.SelectionBrush) == Color.Parse("#FFD700")
              && BrushColor(textBox.SelectionForegroundBrush) == Colors.Black,
            "plain TextBox resolves the same computed pair");
        var textPresenter = textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        Check(textPresenter is not null && BrushColor(textPresenter.SelectionBrush) == Color.Parse("#FFD700"),
            "plain TextBox TextPresenter renders the computed pair");

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 4. GridEditor local exception ---------------------------------------------------

    private static void CheckGridEditorLocalException()
    {
        Console.WriteLine("--- GridEditor fixed light surface (local paired-resource exception) ---");

        // Keep the app in its worst case for this window: a white monochrome accent, which the
        // old hard-coded exception existed for.
        themeService!.Apply(Monochrome(MonochromeStyle.BlackWhite, dark: true));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var layout = new StubLayout();
        var editor = new GridEditor(new StubSettings(new AppSettings(
            new Theme(false, "#FFD700", 1, false, false, "Inter", SurfaceStyle.Acrylic),
            [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null)),
            layout, new DisplayMonitorService(layout));
        editor.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var columns = editor.FindControl<NumericUpDown>("ColumnsInput");
        Check(columns is not null, "GridEditor parameters panel is built");
        if (columns is not null)
        {
            var inner = columns.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            Check(inner is not null && BrushColor(inner.SelectionBrush) == Color.Parse("#1C1C1E"),
                "GridEditor NumericUpDown keeps the local dark selection background (#1C1C1E)");
            Check(inner is not null && BrushColor(inner.SelectionForegroundBrush) == Colors.White,
                "GridEditor NumericUpDown keeps the local white selected text (#FFFFFF)");
        }

        editor.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 5. Idempotent rewrite (no feedback loop) ----------------------------------------

    private static void CheckIdempotentRewrite()
    {
        Console.WriteLine("--- idempotent rewrite (compare-before-write) ---");

        var changes = 0;
        EventHandler<ResourcesChangedEventArgs> handler = (_, _) => changes++;
        Application.Current!.ResourcesChanged += handler;
        try
        {
            // Resolve the CURRENT pair and write it again: nothing may be raised.
            var light = TextSelectionPaletteResolver.Resolve(ThemeVariant.Light, TextSelectionPaletteResolver.LightFallback);
            var dark = TextSelectionPaletteResolver.Resolve(ThemeVariant.Dark, TextSelectionPaletteResolver.DarkFallback);
            InvokeUpdateTextSelectionResources(light, dark);
            Check(changes == 0, $"identical rewrite raises no ResourcesChanged (raised {changes})");

            // A real change must raise — proving the counter observes actual writes.
            changes = 0;
            InvokeUpdateTextSelectionResources(
                new TextSelectionPalette(Color.Parse("#123456"), Colors.White),
                new TextSelectionPalette(Color.Parse("#654321"), Colors.Black));
            Check(changes > 0, $"a changed pair raises ResourcesChanged (raised {changes})");
        }
        finally
        {
            Application.Current!.ResourcesChanged -= handler;
        }
    }

    // ---- 6. EditWidget title drag + geometry (W2) ----------------------------------------

    private static void CheckEditWidgetDragAndGeometry()
    {
        Console.WriteLine("--- EditWidget: title drag handle + negative coordinate rescue ---");

        // 1) The whole header strip must be hit-testable (Background=Transparent), not only
        //    the glyphs: a point in the empty title column resolves to the handle strip.
        var widget = new EditWidget(new StubWidgetLayoutProvider(), new UserControl());
        widget.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // The renderer's hit tester only exists after a render pass.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var header = widget.GetVisualDescendants().OfType<Grid>()
            .FirstOrDefault(PanelDragBehavior.GetIsDragHandle);
        Check(header is not null, "header strip is marked as the shared drag handle");
        if (header is not null)
        {
            var origin = header.TranslatePoint(default, widget) ?? default;
            var emptySpot = new Point(origin.X + header.Bounds.Width * 0.6, origin.Y + header.Bounds.Height / 2);
            var hit = widget.GetVisualAt(emptySpot);
            Check(hit is not null && hit.GetSelfAndVisualAncestors().Contains(header),
                $"empty title space hits the header strip (hit {hit?.GetType().Name ?? "null"})");

            // A real left press on the empty area runs the drag path (BeginMoveDrag) and must
            // not throw or close anything — no button may intercept it.
            widget.MouseDown(emptySpot, MouseButton.Left);
            widget.MouseUp(emptySpot, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(widget.IsVisible, "a left press on the empty header area runs the drag path cleanly");
        }

        var save = widget.GetVisualDescendants().OfType<Button>().FirstOrDefault();
        Check(save is not null, "Save button exists in the header");
        if (save is not null && header is not null)
        {
            Check(PanelDragBehavior.IsDragExcluded(save, header),
                "a press on the Save button is excluded from dragging");
            var buttonText = save.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
            if (buttonText is not null)
            {
                Check(PanelDragBehavior.IsDragExcluded(buttonText, header),
                    "content inside the Save button is excluded through the ancestor walk");
            }

            Check(!PanelDragBehavior.IsDragExcluded(header, header),
                "the bare handle itself may start a drag");
            var titleText = widget.GetVisualDescendants().OfType<TextBlock>()
                .FirstOrDefault(t => !ReferenceEquals(t, buttonText));
            if (titleText is not null)
            {
                Check(!PanelDragBehavior.IsDragExcluded(titleText, header),
                    "non-interactive title text does not block the drag");
            }

            // Real pointer input on the Save button: the press must reach the button (the drag
            // handler must not swallow it) and the click must close the window.
            var saveOrigin = save.TranslatePoint(default, widget) ?? default;
            var saveCenter = new Point(saveOrigin.X + save.Bounds.Width / 2, saveOrigin.Y + save.Bounds.Height / 2);
            widget.MouseDown(saveCenter, MouseButton.Left);
            widget.MouseUp(saveCenter, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(!widget.IsVisible, "a real click on Save still closes the window");
        }

        // 2) Pure visibility rule with synthetic screen layouts: legal negative coordinates of
        //    monitors left of / above the primary survive; a fully lost window is pulled into
        //    the primary working area.
        var leftOfPrimary = new PixelRect(-1920, 0, 1920, 1080);
        var primaryArea = new PixelRect(0, 0, 1920, 1080);
        var workingAreas = new[] { leftOfPrimary, primaryArea };
        var windowSize = new PixelSize(560, 640);

        Check(EditWidget.ResolveVisiblePosition(new PixelPoint(-300, -200), windowSize, workingAreas, primaryArea)
              == new PixelPoint(-300, -200),
            "negative coordinates on a left/top monitor are kept EXACTLY");
        Check(EditWidget.ResolveVisiblePosition(new PixelPoint(-1860, 300), windowSize, workingAreas, primaryArea)
              == new PixelPoint(-1860, 300),
            "a window deep into the left monitor is kept");

        var rescued = EditWidget.ResolveVisiblePosition(new PixelPoint(9000, 9000), windowSize, workingAreas, primaryArea);
        Check(primaryArea.Contains(new PixelRect(rescued, windowSize)),
            $"a fully lost window is clamped inside the primary working area (rescued {rescued})");

        // A removed screen (only the primary left) also rescues into the primary.
        var singleScreen = new[] { primaryArea };
        var rescuedSingle = EditWidget.ResolveVisiblePosition(new PixelPoint(-1900, 100), windowSize, singleScreen, primaryArea);
        Check(primaryArea.Contains(new PixelRect(rescuedSingle, windowSize)),
            $"a window on a removed screen is clamped into the remaining screen (rescued {rescuedSingle})");

        // 3) Window-level sanity: the first-show rescue runs once and leaves an on-screen
        //    position alone; later activations never re-place the window.
        var firstShow = new EditWidget(new StubWidgetLayoutProvider(), new UserControl());
        firstShow.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var guard = typeof(EditWidget).GetField("geometryChecked", BindingFlags.NonPublic | BindingFlags.Instance);
        Check(guard is not null && (bool)guard.GetValue(firstShow)!,
            "the first show consumed the once-only geometry guard");
        typeof(EditWidget)
            .GetMethod("OnOpened", BindingFlags.NonPublic | BindingFlags.Instance, null,
                [typeof(object), typeof(EventArgs)], null)!
            .Invoke(firstShow, [null, EventArgs.Empty]);
        Check((bool)guard!.GetValue(firstShow)!,
            "later openings do not re-run the rescue (window stays where the user left it)");

        firstShow.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 7. Setting adaptive layout (W3) --------------------------------------------------

    private static void CheckSettingAdaptiveLayout()
    {
        Console.WriteLine("--- Setting: adaptive layout, wrapping, hints (W3) ---");

        const string longTitle = "一个相当长的设置标题必须完整换行显示而不是被省略号截断";
        var setting = new Setting
        {
            Title = longTitle,
            Subtitle = "副标题同样允许换行",
            AdminRequired = true,
        };
        var datePicker = new DatePicker();
        setting.Content = datePicker;

        var host = new Border { Child = setting };
        var window = new Window { Width = 600, Height = 400 };
        window.Content = host;
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var panel = setting.GetVisualDescendants().OfType<SettingLayoutPanel>().SingleOrDefault();
        Check(panel is not null, "the template builds exactly one SettingLayoutPanel");
        if (panel is null) { window.Close(); return; }

        var editorPresenter = setting.GetVisualDescendants().OfType<ContentPresenter>()
            .SingleOrDefault(p => SettingLayoutPanel.GetSlot(p) == SettingLayoutSlot.Editor);
        var labelGroup = setting.GetVisualDescendants().OfType<StackPanel>()
            .FirstOrDefault(s => SettingLayoutPanel.GetSlot(s) == SettingLayoutSlot.Label);
        Check(editorPresenter is not null && labelGroup is not null, "editor presenter and label group exist");

        // Narrow viewport: the wide DatePicker must drop onto its own row.
        host.Width = 400;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Stacked,
            $"a DatePicker row at 400 DIP stacks (threshold {panel.LastHorizontalThreshold:F0})");
        Check(setting.EffectiveLayout == SettingEffectiveLayout.Stacked,
            "the Setting exposes the measured layout too");
        if (editorPresenter is not null && labelGroup is not null)
        {
            Check(editorPresenter.Bounds.Top >= labelGroup.Bounds.Bottom - 0.5,
                $"the stacked editor sits below the label group (editor top {editorPresenter.Bounds.Top:F0}, label bottom {labelGroup.Bounds.Bottom:F0})");
            Check(editorPresenter.Bounds.Width >= 290,
                $"the stacked DatePicker keeps its usable width ({editorPresenter.Bounds.Width:F0})");
        }

        // Title wraps instead of truncating.
        var titleBlock = setting.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == longTitle);
        Check(titleBlock is not null, "the title TextBlock is reachable");
        if (titleBlock is not null)
        {
            Check(ReferenceEquals(titleBlock.TextTrimming, TextTrimming.None), "the title never truncates (TextTrimming=None)");
            Check(titleBlock.Bounds.Height > 25,
                $"the long title wraps to multiple lines (height {titleBlock.Bounds.Height:F0})");
        }

        // Wide viewport: back to one line.
        host.Width = 640;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Horizontal,
            "the same row at 640 DIP returns to the horizontal arrangement");

        // Hysteresis: near the threshold the arrangement must not flap. The panel measures
        // against the row width INSIDE its padding, so resize in panel space.
        var threshold = panel.LastHorizontalThreshold;
        var chrome = host.Width - panel.Bounds.Width;
        void ResizeToPanelWidth(double panelWidth)
        {
            host.Width = panelWidth + chrome;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        ResizeToPanelWidth(threshold - 4);
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Stacked, "just below the threshold: stacked");
        ResizeToPanelWidth(threshold + 8);
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Stacked,
            "inside the hysteresis band: still stacked (needs threshold+16 to flip back)");
        ResizeToPanelWidth(threshold + 20);
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Horizontal, "past the band: horizontal again");
        ResizeToPanelWidth(threshold - 2);
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Stacked, "shrinking below the threshold flips immediately");

        // Exactly one editor instance ever (no stale copy from a previous arrangement).
        Check(setting.GetVisualDescendants().OfType<DatePicker>().Count() == 1
              && ReferenceEquals(editorPresenter?.Content, datePicker),
            "the single editor presenter holds the very content instance");
        Check(setting.GetVisualDescendants().Count(v => ReferenceEquals(v, datePicker)) == 1,
            "the editor control exists once in the tree across re-arrangements");

        // Hints: visible space for Admin/Restart requirement notes, live toggling.
        var adminHint = setting.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == DeskCanvas.Locales.Locale.Settings_AdminRequired);
        var restartHint = setting.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == DeskCanvas.Locales.Locale.Settings_RestartRequired);
        Check(adminHint is { IsVisible: true }, "AdminRequired shows its localized hint");
        Check(restartHint is { IsVisible: false }, "RestartRequired hint stays hidden when unset");
        if (adminHint is not null && restartHint is not null)
        {
            Check(adminHint.Bounds.Height > 0 && adminHint.Bounds.Bottom <= setting.Bounds.Height,
                "the hint takes real layout space inside the row (never overlapping the editor)");
        }

        setting.RestartRequired = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(restartHint?.IsVisible == true, "RestartRequired=true reveals the hint at runtime");
        setting.AdminRequired = false;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(adminHint?.IsVisible == false, "AdminRequired=false hides the hint at runtime");

        // Inline mode keeps one line even when narrow (short labels / small switches).
        setting.LayoutMode = SettingLayoutMode.Inline;
        host.Width = 240;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(panel.EffectiveLayout == SettingEffectiveLayout.Horizontal,
            "LayoutMode=Inline keeps the single-line arrangement at 240 DIP");

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 8. Scroll ownership (W4) ----------------------------------------------------------

    private static void CheckScrollOwnership()
    {
        Console.WriteLine("--- scroll ownership: single vertical scroll owner (W4) ---");

        // LegacySelf (default, no marker): the host viewport must NOT scroll — the page keeps
        // its own scroller and only receives the finite box.
        var legacyPage = new UserControl
        {
            Content = new ScrollViewer { Content = new TextBlock { Text = "legacy" } },
        };
        Check(SettingsPage.GetScrollOwnership(legacyPage) == SettingsPageScrollOwnership.LegacySelf,
            "an unmarked page defaults to LegacySelf");

        var legacyWidget = new EditWidget(new StubWidgetLayoutProvider(), legacyPage);
        legacyWidget.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var legacyViewport = FindViewport(legacyWidget);
        Check(legacyViewport?.VerticalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "LegacySelf: the host viewport supplies a finite box only (vertical scroll disabled)");
        Check(legacyViewport?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "LegacySelf: horizontal scrolling is disabled on the viewport too");
        legacyWidget.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Host marker: the viewport becomes the single scroll owner (Auto vertical).
        var tallStack = new StackPanel();
        for (var i = 0; i < 40; i++)
            tallStack.Children.Add(new Border { Height = 40 });
        var hostPage = new UserControl { Content = tallStack };
        SettingsPage.SetScrollOwnership(hostPage, SettingsPageScrollOwnership.Host);

        var hostWidget = new EditWidget(new StubWidgetLayoutProvider(), hostPage);
        hostWidget.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var hostViewport = FindViewport(hostWidget);
        Check(hostViewport?.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "Host: the viewport owns the vertical scroll (Auto)");
        Check(hostViewport is not null && hostViewport.Extent.Height > hostViewport.Viewport.Height + 100,
            $"Host: tall content is actually scrollable in the viewport (extent {hostViewport?.Extent.Height:F0} > viewport {hostViewport?.Viewport.Height:F0})");
        hostWidget.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The REAL migrated pages: marker present, no outer ScrollViewer of their own.
        CheckMigratedPage("Timetable", new Timetable.Views.Settings.TimetableSettings());
        CheckMigratedPage("FramelessClock", new Clock.Views.Settings.FramelessClockSettings(new StubWidgetLayoutProvider()));
        CheckMigratedPage("Picture", new Picture.Views.Settings.PictureSettings());

        // Full EditWidget integration on the real, most complex page (Timetable): exactly ONE
        // vertical scroll owner across the whole window.
        var timetablePage = new Timetable.Views.Settings.TimetableSettings();
        var timetableWidget = new EditWidget(new StubWidgetLayoutProvider(), timetablePage);
        timetableWidget.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // "Scroll owner" = a vertical scroller that ACTUALLY scrolls: controls like the ComboBox
        // carry internal micro scrollers (extent == viewport, never active) — those are not page
        // scrolling. The migrated page must leave exactly ONE real scrollable container: the
        // EditWidget viewport.
        var realScrollers = timetableWidget.GetVisualDescendants().OfType<ScrollViewer>()
            .Where(s => s.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
            .Where(s => s.Extent.Height > s.Viewport.Height + 1)
            .ToArray();
        foreach (var s in realScrollers)
        {
            Console.WriteLine($"  [INFO] scrolling container: name={s.Name} extent={s.Extent.Height:F0} viewport={s.Viewport.Height:F0}");
        }
        Check(realScrollers.Length == 1 && realScrollers[0].Name == "ContentViewport",
            $"exactly one real vertical scroll owner, the viewport ({realScrollers.Length})");
        Check(FindViewport(timetableWidget)?.VerticalScrollBarVisibility == ScrollBarVisibility.Auto,
            "Timetable scrolls through the EditWidget viewport");
        timetableWidget.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void CheckMigratedPage(string name, UserControl page)
    {
        Check(SettingsPage.GetScrollOwnership(page) == SettingsPageScrollOwnership.Host,
            $"{name} settings declares ScrollOwnership=Host");
        Check(!page.GetVisualDescendants().OfType<ScrollViewer>().Any(),
            $"{name} settings keeps no outer ScrollViewer of its own");
    }

    private static ScrollViewer? FindViewport(EditWidget widget)
        => widget.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.Name == "ContentViewport");

    // ---- 9. Window geometry store (W4) ----------------------------------------------------

    private static void CheckWindowGeometry()
    {
        Console.WriteLine("--- window geometry memory (W4) ---");

        // 1) End-to-end through EditWidget: close stores, a new window of the same settings
        //    type restores. (The isolated ui-state.json lives next to this check's exe.)
        var path = WindowGeometryStore.FilePath;
        if (File.Exists(path)) File.Delete(path);

        var first = new EditWidget(new StubWidgetLayoutProvider(), new StubSettingsPage())
        {
            Width = 500,
            Height = 600,
        };
        first.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        first.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var key = WindowGeometryStore.ComposeKey(typeof(StubSettingsPage));
        var stored = WindowGeometryStore.TryGet(key);
        Check(stored is not null && Math.Abs(stored.WidthDip - 500) < 1 && Math.Abs(stored.HeightDip - 600) < 1,
            $"closing stores the normal DIP size ({stored?.WidthDip:F0}x{stored?.HeightDip:F0})");
        Check(stored?.MonitorId == "primary", "the screen signature is stored");

        var second = new EditWidget(new StubWidgetLayoutProvider(), new StubSettingsPage());
        var preShowRestored = Math.Abs(second.Width - 500) < 1 && Math.Abs(second.Height - 600) < 1;
        second.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(second.Width - 500) < 1 && Math.Abs(second.Height - 600) < 1,
            $"a new window of the same settings type restores the stored size (pre-show restore: {preShowRestored})");
        second.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 2) Store robustness: roundtrip, corrupt file, invalid entry.
        if (File.Exists(path)) File.Delete(path);
        Check(WindowGeometryStore.TryGet(key) is null, "no stored geometry initially");

        WindowGeometryStore.Save(key, new WindowGeometryEntry
        {
            WidthDip = 560, HeightDip = 640, MonitorId = "primary", CenterXRatio = 0.25, CenterYRatio = 0.75,
        });
        var roundtrip = WindowGeometryStore.TryGet(key);
        Check(roundtrip is { WidthDip: 560, HeightDip: 640, CenterXRatio: 0.25, CenterYRatio: 0.75 },
            "save/load roundtrip keeps every field");

        File.WriteAllText(path, "{ this is not json");
        Check(WindowGeometryStore.TryGet(key) is null, "a corrupt ui-state.json falls back to 'no geometry'");

        File.WriteAllText(path, "{\"schemaVersion\":1,\"editWidgetWindows\":{\"" + key.Replace("\\", "\\\\") + "\":{\"widthDip\":0,\"heightDip\":640}}}");
        Check(WindowGeometryStore.TryGet(key) is null, "a zero-size stored entry is rejected");

        WindowGeometryStore.Save(key, new WindowGeometryEntry { WidthDip = 100, HeightDip = 100 });
        Check(WindowGeometryStore.TryGet(key) is not null, "saving repairs the file after corruption");

        // 3) Pure restore math with synthetic screens (negative monitors, scaling, tiny areas).
        var primary = new PixelRect(0, 0, 1920, 1080);
        var entry = new WindowGeometryEntry { WidthDip = 560, HeightDip = 640, CenterXRatio = 0.5, CenterYRatio = 0.5 };
        var centered = EditWidget.ResolveRestoredGeometry(entry, primary, 1.0, 440, 360);
        Check(centered.Width == 560 && centered.Height == 640, "normal restore keeps the stored DIP size");
        Check(centered.Position == new PixelPoint(680, 220),
            $"the normalized center maps to the physical top-left ({centered.Position})");

        var leftMonitor = new PixelRect(-1920, 0, 1920, 1080);
        var onLeft = EditWidget.ResolveRestoredGeometry(entry, leftMonitor, 1.0, 440, 360);
        Check(onLeft.Position.X < 0, $"a monitor left of the primary keeps negative coordinates ({onLeft.Position.X})");

        var hidpi = EditWidget.ResolveRestoredGeometry(entry, new PixelRect(0, 0, 3840, 2160), 2.0, 440, 360);
        Check(hidpi.Width == 560 && hidpi.Position == new PixelPoint(1360, 440),
            $"200% scaling converts the center with the screen scaling (position {hidpi.Position})");

        var tiny = EditWidget.ResolveRestoredGeometry(entry, new PixelRect(0, 0, 320, 240), 1.0, 440, 360);
        Check(tiny.MinWidth == 320 && tiny.MinHeight == 240 && tiny.Width <= 320 && tiny.Height <= 240,
            $"a tiny working area lowers the effective minimum size ({tiny.MinWidth}x{tiny.MinHeight})");
        Check(tiny.Position.X >= 0 && tiny.Position.Y >= 0
              && tiny.Position.X + 320 <= 320 && tiny.Position.Y + 240 <= 240,
            "the tiny-area window stays reachable inside it");

        var belowMin = EditWidget.ResolveRestoredGeometry(
            new WindowGeometryEntry { WidthDip = 200, HeightDip = 200, CenterXRatio = 0.5, CenterYRatio = 0.5 },
            primary, 1.0, 440, 360);
        Check(belowMin.Width == 440 && belowMin.Height == 360,
            "a stored size below the design minimum is raised to the minimum");

        // Geometry must live in its own file, never in the theme configuration document.
        Check(File.Exists(path) && path.EndsWith("ui-state.json"),
            $"stored in ui-state.json ({path})");
    }

    // ---- 10. Icon buttons + section headers (W5) -----------------------------------------

    private static void CheckIconButtonsAndSectionHeaders()
    {
        Console.WriteLine("--- icon buttons + section headers (W5) ---");

        var window = new Window { Width = 420, Height = 320 };
        var stack = new StackPanel();
        var circle = new Button { Content = "✕" };
        circle.Classes.Add("dc-icon-button");
        var aliasCircle = new Button { Content = "✕" };
        aliasCircle.Classes.Add("icon-btn");
        var toolbar = new Button { Content = "T" };
        toolbar.Classes.Add("dc-toolbar-button");
        var compactToolbar = new Button { Content = "C" };
        compactToolbar.Classes.Add("icon-button");
        var compactCircle = new Button { Content = "c" };
        compactCircle.Classes.Add("dc-icon-button");
        compactCircle.Classes.Add("compact");
        var header = new TextBlock { Text = "Group" };
        header.Classes.Add("group-header");
        var headerAlias = new TextBlock { Text = "Section" };
        headerAlias.Classes.Add("section-header");

        foreach (var control in new Control[] { circle, aliasCircle, toolbar, compactToolbar, compactCircle, header, headerAlias })
            stack.Children.Add(control);
        window.Content = stack;
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(circle.Width == 28 && circle.Height == 28
              && circle.CornerRadius == new CornerRadius(14)
              && circle.Background is ISolidColorBrush baseBrush && baseBrush.Color == Colors.Transparent,
            $"dc-icon-button resolves the canonical 28 DIP circle (bg {circle.Background})");
        Check(aliasCircle.Width == 28 && aliasCircle.CornerRadius == new CornerRadius(14),
            "the icon-btn transition alias resolves the same circle contract");
        Check(double.IsNaN(toolbar.Width) && toolbar.CornerRadius.TopLeft == 6,
            "dc-toolbar-button keeps the page's natural size with radius 6");
        Check(compactToolbar.Width == 24 && compactToolbar.Height == 24 && compactToolbar.CornerRadius.TopLeft == 6,
            "the icon-button transition alias is the 24 DIP compact toolbar");
        Check(compactCircle.Width == 24 && compactCircle.CornerRadius == new CornerRadius(12),
            "dc-icon-button.compact is the 24 DIP circle");

        // Hover must reach the RENDERED template part, not just the control property.
        Check(circle.TryFindResource("ControlHoverBrush", circle.ActualThemeVariant, out var hoverValue)
              && hoverValue is ISolidColorBrush hoverBrush,
            "ControlHoverBrush resolves through the shared dictionary");
        ((IPseudoClasses)circle.Classes).Set(":pointerover", true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var hoverMatches = hoverValue is ISolidColorBrush expected
                           && circle.Background is ISolidColorBrush bg
                           && bg.Color == expected.Color && Math.Abs(bg.Opacity - expected.Opacity) < 0.001;
        Check(hoverMatches, $"hover state uses the shared hover brush ({circle.Background})");
        var presenter = circle.GetVisualDescendants().OfType<ContentPresenter>()
            .FirstOrDefault(p => p.Name == "PART_ContentPresenter");
        var expectedHover = (ISolidColorBrush)hoverValue!;
        Check(presenter?.Background is ISolidColorBrush painted
              && painted.Color == expectedHover.Color && Math.Abs(painted.Opacity - expectedHover.Opacity) < 0.001,
            "the rendered template part actually paints the shared hover background");
        ((IPseudoClasses)circle.Classes).Set(":pointerover", false);

        ((IPseudoClasses)circle.Classes).Set(":focus-visible", true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(circle.BorderThickness == new Thickness(1),
            "keyboard focus draws the shared focus ring on transparent material");
        ((IPseudoClasses)circle.Classes).Set(":focus-visible", false);

        Check(header.FontSize == 13 && header.FontWeight == FontWeight.SemiBold,
            "group-header resolves the shared type (13 / SemiBold)");
        Check(headerAlias.FontSize == 13 && headerAlias.FontWeight == FontWeight.SemiBold,
            "section-header alias shares the same definition");
        Check(header.Margin == new Thickness(0, 12, 0, 4),
            "the shared SettingsSectionHeaderMargin drives the group spacing");
        Check(header.TryFindResource("SystemControlForegroundAccentBrush", header.ActualThemeVariant, out var accentValue)
              && accentValue is ISolidColorBrush accent && header.Foreground is ISolidColorBrush headerBrush
              && headerBrush.Color == accent.Color,
            "the header foreground follows the accent brush");

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The migrated files must carry NO local base styles any more (source contract).
        var calculator = new Tools.Views.CalculatorView();
        Check(!HasLocalSelector(calculator.Styles, "icon-btn"),
            "CalculatorView keeps no local icon-btn base style");
        Check(calculator.GetLogicalDescendants().OfType<Button>()
                .Any(b => b.Classes.Contains("dc-toolbar-button")),
            "CalculatorView buttons moved onto the shared toolbar variant");
        Check(calculator.GetLogicalDescendants().OfType<Button>()
                .Where(b => b.Classes.Contains("dc-toolbar-button"))
                .All(b => AutomationProperties.GetName(b) is not (null or "")),
            "CalculatorView icon buttons carry accessible names");

        var music = new Music.Views.Settings.MusicSettings();
        Check(!HasLocalSelector(music.Styles, "icon-button") && !HasLocalSelector(music.Styles, "group-header"),
            "MusicSettings keeps no local icon-button / group-header styles");

        var search = new Search.Views.Settings.SearchSettings();
        Check(!HasLocalSelector(search.Styles, "icon-button") && !HasLocalSelector(search.Styles, "group-header"),
            "SearchSettings keeps no local icon-button / group-header styles");

        var noteWindow = new Notes.Views.NotePopupWindow();
        Check(!HasLocalSelector(noteWindow.Styles, "icon-btn"),
            "NotePopupWindow keeps no local icon-btn base style");
        noteWindow.Close();

        var timetableWindow = new Timetable.Views.TimetablePanelWindow();
        Check(!HasLocalSelector(timetableWindow.Styles, "icon-btn"),
            "TimetablePanelWindow keeps no local icon-btn base style");
        timetableWindow.Close();

        var timetableSettings = new Timetable.Views.Settings.TimetableSettings();
        Check(!HasLocalSelector(timetableSettings.Styles, "section-header"),
            "TimetableSettings keeps no local section-header style");
    }

    private static bool HasLocalSelector(Avalonia.Styling.Styles styles, string fragment)
        => styles.OfType<Avalonia.Styling.Style>()
            .Any(s => s.Selector?.ToString().Contains(fragment, StringComparison.Ordinal) == true);

    // ---- 11. Compound inputs (W6) ---------------------------------------------------------

    private static void CheckCompoundInputs()
    {
        Console.WriteLine("--- compound inputs: LabeledSlider + ColorInput (W6) ---");

        var window = new Window { Width = 520, Height = 360 };
        var stack = new StackPanel();

        var slider = new LabeledSlider();
        var percent = new LabeledSlider { Minimum = 5, Maximum = 95, Step = 5, Unit = "%" };
        var editable = new LabeledSlider { Minimum = 0, Maximum = 80, Step = 1, ShowEditableValue = true, DisplayFormat = "F0" };
        var color = new ColorInput();
        var rgbColor = new ColorInput { ColorFormat = ColorInputFormat.Rgb, AlphaPolicy = ColorInputAlphaPolicy.ForceOpaque };

        foreach (var control in new Control[] { slider, percent, editable, color, rgbColor })
            stack.Children.Add(control);
        window.Content = stack;
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        CheckLabeledSlider(slider, percent, editable);
        CheckColorInput(color, rgbColor);

        window.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        CheckCompoundInputPages();
    }

    private static void CheckLabeledSlider(LabeledSlider slider, LabeledSlider percent, LabeledSlider editable)
    {
        var sliderPart = Part<Slider>(slider, "SliderPart");
        var numericPart = Part<NumericUpDown>(slider, "NumericPart");
        var textPart = Part<TextBlock>(slider, "ValueTextPart");

        Check(slider.Minimum == 0 && slider.Maximum == 100 && slider.Step == 1,
            "LabeledSlider defaults to 0–100 step 1");
        Check(textPart.IsVisible && !numericPart.IsVisible,
            "the default value column is the read-only formatted text");
        Check(Part<NumericUpDown>(editable, "NumericPart").IsVisible
              && !Part<TextBlock>(editable, "ValueTextPart").IsVisible,
            "ShowEditableValue swaps the text for the numeric box");

        // ---- load mirrors into every part, and never commits ----
        var commits = 0;
        slider.ValueCommitted += (_, _) => commits++;

        slider.Value = 25;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(textPart.Text == "25", $"the value column renders the current value (\"{textPart.Text}\")");
        Check(Part<TextBlock>(percent, "ValueTextPart").Text?.EndsWith("%", StringComparison.Ordinal) == true,
            "Unit is appended to the rendered value");
        Check(commits == 0, "loading a value from the model raises no ValueCommitted");

        // ---- user edit: snap + clamp, mirrored to the sibling part ----
        Part<NumericUpDown>(percent, "NumericPart").Value = 33m;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(percent.Value - 35) < 1e-9,
            $"a user edit snaps to the step grid anchored at Minimum (33 → {percent.Value})");

        percent.Value = 200;                       // loaded as-is …
        percent.Minimum = 10;                      // … and a range/step change re-snaps it
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(percent.Value - 95) < 1e-9,
            $"an out-of-range value is clamped to Maximum ({percent.Value})");

        // ---- invalid configuration normalises instead of leaking ----
        var broken = new LabeledSlider { Minimum = 100, Maximum = 0, Step = 0, Value = 40 };
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Part<Slider>(broken, "SliderPart").Maximum == 100 && Part<Slider>(broken, "SliderPart").Minimum == 0,
            "an inverted range falls back to 0–100");
        Check(Part<Slider>(broken, "SliderPart").TickFrequency == 1,
            "a non-positive step falls back to 1");

        var nan = new LabeledSlider { Minimum = 20, Maximum = 60, Value = double.NaN };
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Part<TextBlock>(nan, "ValueTextPart").Text == "20",
            "a non-finite value renders as the range start instead of poisoning the slider");

        // ---- commit: once per completed interaction, never on a no-op ----
        slider.Value = 40;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(commits == 0, "an external update (another editor / profile switch) raises no commit");

        sliderPart.Value = 44;                     // the drag itself
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(slider.Value - 44) < 1e-9, $"a slider drag moves Value ({slider.Value})");
        Check(commits == 0, "the drag itself does not commit — only its completion does");

        RaiseDragCompleted(sliderPart);
        Check(commits == 1, "releasing the thumb commits once");
        RaiseDragCompleted(sliderPart);
        Check(commits == 1, "a release without a value change stays silent (no duplicate save)");

        slider.Value = 44;                         // same value pushed back by the binding
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        RaiseDragCompleted(sliderPart);
        Check(commits == 1, "the binding echoing the committed value back raises no second commit");

        sliderPart.Value = 50;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        RaiseDragCompleted(sliderPart);
        Check(commits == 2 && Math.Abs(slider.Value - 50) < 1e-9,
            $"the next completed drag commits again ({commits} commits, value {slider.Value})");

        // ---- SetCurrentValue keeps a TwoWay binding alive ----
        var host = new SliderBindingHost { Level = 30 };
        var bound = new LabeledSlider { Minimum = 0, Maximum = 100, Step = 1 };
        bound.Bind(LabeledSlider.ValueProperty, new Avalonia.Data.Binding(nameof(SliderBindingHost.Level))
        {
            Source = host,
            Mode = Avalonia.Data.BindingMode.TwoWay,
        });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(bound.Value - 30) < 1e-9, "the control picks up the bound source value");
        Part<Slider>(bound, "SliderPart").Value = 72;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(host.Level - 72) < 1e-9,
            $"a user edit writes through the binding instead of replacing it (source {host.Level})");
        host.Level = 18;                           // source still drives the control
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Math.Abs(bound.Value - 18) < 1e-9,
            $"the binding survives the edit and still pushes source changes ({bound.Value})");

        // ---- the accessible name reaches the interactive parts ----
        AutomationProperties.SetName(slider, "模糊");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(AutomationProperties.GetName(sliderPart) == "模糊"
              && AutomationProperties.GetName(numericPart) == "模糊",
            "the page-level accessible name is forwarded to the slider and the numeric box");
    }

    private static void CheckColorInput(ColorInput color, ColorInput rgbColor)
    {
        var picker = Part<ColorPicker>(color, "PickerPart");
        var hexBox = Part<TextBox>(color, "HexBoxPart");
        var error = Part<TextBlock>(color, "ErrorTextPart");

        // ---- load ----
        var commits = new System.Collections.Generic.List<string?>();
        color.ValueCommitted += (_, e) => commits.Add(e.Value);

        color.Value = "#400078D4";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(hexBox.Text == "#400078D4", $"the draft renders the stored value (\"{hexBox.Text}\")");
        Check(!color.HasValidationError && !error.IsVisible, "a valid stored value shows no error");
        Check(commits.Count == 0, "loading a value from the model never commits");

        // ---- intermediate states never reach the model ----
        Type(hexBox, "#");
        Blur(hexBox);
        Check(color.Value == "#400078D4" && commits.Count == 0,
            "an intermediate \"#\" is never written back");
        Check(color.HasValidationError && error.IsVisible,
            $"an invalid draft is reported in place ({color.ValidationMessage})");

        Type(hexBox, "nonsense");
        Blur(hexBox);
        Check(color.Value == "#400078D4" && commits.Count == 0, "an invalid draft keeps the model value");

        // ---- Escape restores the draft without committing ----
        Type(hexBox, "#123456");
        Press(hexBox, Key.Escape);
        Check(hexBox.Text == "#400078D4" && color.Value == "#400078D4" && commits.Count == 0,
            "Escape restores the last valid draft and commits nothing");

        // ---- a valid Hex commit goes through the same normalisation as the picker ----
        Type(hexBox, "#7B61FF");
        Press(hexBox, Key.Enter);
        Check(color.Value == "#407B61FF",
            $"a 6-digit commit keeps the previous alpha (Preserve): {color.Value}");
        Check(commits.Count == 1 && commits[0] == "#407B61FF", "exactly one commit per accepted edit");

        Type(hexBox, "#80112233");
        Blur(hexBox);
        Check(color.Value == "#80112233" && commits.Count == 2,
            $"an 8-digit commit edits alpha explicitly: {color.Value}");

        // ---- IsAlphaVisible=false must not silently rewrite alpha to FF ----
        Check(!picker.IsAlphaVisible, "the row declares alpha is not edited here");
        picker.Color = Color.Parse("#FFABCDEF");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(color.Value == "#80ABCDEF",
            $"a picker move with hidden alpha preserves the stored alpha (#80…, got {color.Value})");

        // ---- ForceOpaque + Rgb: the row that hands alpha to another field ----
        var rgbCommits = 0;
        rgbColor.ValueCommitted += (_, _) => rgbCommits++;
        rgbColor.Value = "#400078D4";
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Part<TextBox>(rgbColor, "HexBoxPart").Text == "#0078D4",
            "Rgb renders #RRGGBB even while the model carries an alpha");
        Type(Part<TextBox>(rgbColor, "HexBoxPart"), "#FF0000");
        Blur(Part<TextBox>(rgbColor, "HexBoxPart"));
        Check(rgbColor.Value == "#FF0000" && rgbCommits == 1,
            $"ForceOpaque + Rgb writes a plain #RRGGBB ({rgbColor.Value})");

        // ---- empty: legal only when the field says so ----
        Type(hexBox, "");
        Blur(hexBox);
        Check(color.HasValidationError && color.Value == "#80ABCDEF",
            "clearing a field that does not allow empty is rejected");

        color.AllowEmpty = true;
        Type(hexBox, "");
        Blur(hexBox);
        Check(color.Value is null && commits.Count == 4 && commits[3] is null,
            $"AllowEmpty: clearing commits null, the business \"automatic\" state ({commits.Count} commits)");
        Check(!color.HasValidationError, "the empty state is not an error");

        // ---- read-only keeps the colour visible but not editable ----
        color.AllowEmpty = false;
        color.Value = "#FF7B61FF";
        color.IsReadOnly = true;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(!picker.IsEnabled && hexBox.IsReadOnly,
            "IsReadOnly disables both the picker and the draft box");
        color.IsReadOnly = false;
    }

    private static void CheckCompoundInputPages()
    {
        Console.WriteLine("--- migrated pages use the compound editors (W6) ---");

        var progress = new Progress.Views.Settings.ProgressSettings(new StubWidgetLayoutProvider());
        Check(progress.GetLogicalDescendants().OfType<ColorInput>().Count() == 3,
            $"Progress settings: three colour rows on the shared ColorInput " +
            $"({progress.GetLogicalDescendants().OfType<ColorInput>().Count()})");
        Check(CountStray<ColorPicker, ColorInput>(progress) == 0,
            "Progress settings: no bare ColorPicker left outside ColorInput");

        var clock = new Clock.Views.Settings.FramelessClockSettings(new StubWidgetLayoutProvider());
        Check(clock.GetLogicalDescendants().OfType<LabeledSlider>().Count() == 2,
            "FramelessClock settings: the two opacity rows are shared LabeledSliders");
        Check(clock.GetLogicalDescendants().OfType<ColorInput>().Count() == 1,
            "FramelessClock settings: the overlay colour is a shared ColorInput");
        Check(CountStray<ColorPicker, ColorInput>(clock) == 0,
            "FramelessClock settings: no bare ColorPicker left");
        // The font-weight row is a *labelled* slider (thin … thick), not a slider+box pair:
        // it is the one row this work package deliberately leaves as it is.
        Check(CountStray<Slider, LabeledSlider>(clock) == 1,
            $"FramelessClock settings: only the labelled font-weight slider stays bare " +
            $"({CountStray<Slider, LabeledSlider>(clock)})");

        var appearance = new DeskCanvas.Views.Pages.Appearance(new StubSettings(
            new AppSettings(new Theme(false, null, 1, false, false, "Inter", SurfaceStyle.LiquidGlassV2),
                [], new Layout(GridMode.Manual, true, false, true, false),
                new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null)));
        var sliders = appearance.GetLogicalDescendants().OfType<LabeledSlider>().Count();
        var colors = appearance.GetLogicalDescendants().OfType<ColorInput>().Count();
        // 8 optics rows + opacity + 强调色偏移. The accent shift joined the slider family on
        // purpose: it is a percentage of a colour distance, i.e. the same kind of control as the
        // optics rows, and it commits on release like them rather than writing the settings file
        // once per pixel of the drag.
        Check(sliders == 10, $"Appearance: 8 optics rows, opacity and 强调色偏移 are LabeledSliders ({sliders})");
        // 描边颜色 was removed with the 描边 feature; the remaining colour rows are
        // 强调色 and the two 背景颜色 pickers (dark + light).
        Check(colors == 3, $"Appearance: the three remaining colour rows are ColorInputs ({colors})");
        Check(CountStray<Slider, LabeledSlider>(appearance) == 0 && CountStray<ColorPicker, ColorInput>(appearance) == 0,
            "Appearance: no bare Slider / ColorPicker left");
        // The remaining numeric boxes are real single-value rows (interval / delay),
        // not the value column of a slider — they must not have been absorbed. 描边粗细 was
        // removed together with the outline ring.
        Check(appearance.GetLogicalDescendants().OfType<NumericUpDown>()
                .Count(n => !n.GetLogicalAncestors().OfType<LabeledSlider>().Any()) == 2,
            "Appearance: the two remaining non-slider numeric rows are untouched");
    }

    // ---- W6 helpers ----------------------------------------------------------------------

    private static T Part<T>(object owner, string name) where T : class
    {
        var type = owner.GetType();
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException($"{type.Name}.{name} not found");
        return (T)field.GetValue(owner)!;
    }

    private static void RaiseDragCompleted(Slider slider) =>
        slider.RaiseEvent(new VectorEventArgs { RoutedEvent = Thumb.DragCompletedEvent, Vector = default });

    /// <summary>Simulate typing: the control must see this as a user edit.</summary>
    private static void Type(TextBox box, string text)
    {
        box.Text = text;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Blur(TextBox box)
    {
        box.RaiseEvent(new FocusChangedEventArgs(InputElement.LostFocusEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static void Press(TextBox box, Key key)
    {
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Leaf controls on the page that are NOT wrapped in the shared compound editor.</summary>
    private static int CountStray<TLeaf, TWrapper>(Control root)
        where TLeaf : Control where TWrapper : Control
        => root.GetLogicalDescendants().OfType<TLeaf>()
            .Count(leaf => !leaf.GetLogicalAncestors().OfType<TWrapper>().Any());

    private sealed class SliderBindingHost : System.ComponentModel.INotifyPropertyChanged
    {
        private double level;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public double Level
        {
            get => level;
            set
            {
                if (Math.Abs(level - value) < 1e-9) return;
                level = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Level)));
            }
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    /// <summary>Contract key names (the helper that writes them is internal to the app).</summary>
    private const string SelectionBackgroundKey = "TextSelectionBackgroundBrush";
    private const string SelectionForegroundKey = "TextSelectionForegroundBrush";

    private static Theme Monochrome(MonochromeStyle variant, bool dark) => new(
        DarkMode: dark,
        AccentColor: null,
        OpacityLevel: 1.0,
        Monochrome: true,
        UseNativeFrame: false,
        FontFamily: "Inter",
        Surface: SurfaceStyle.Acrylic,
        SolidBackgroundDark: "#112233",
        SolidBackgroundLight: "#FEDCBA",
        MonochromeVariant: variant);

    private static object? ReadResource(string key, ThemeVariant variant)
        => ((IResourceHost)Application.Current!).TryGetResource(key, variant, out var value) ? value : null;

    private static Color PairBackground(ThemeVariant variant) =>
        BrushColor(ReadResource(SelectionBackgroundKey, variant));

    private static Color PairForeground(ThemeVariant variant) =>
        BrushColor(ReadResource(SelectionForegroundKey, variant));

    private static Color BrushColor(object? value) => value switch
    {
        ISolidColorBrush brush => brush.Color,
        Color color => color,
        _ => default,
    };

    /// <summary>The helper is internal to the app assembly; the checks invoke it as-is.</summary>
    private static void InvokeUpdateTextSelectionResources(TextSelectionPalette light, TextSelectionPalette dark)
    {
        var helper = typeof(ThemeService).Assembly.GetType("DeskCanvas.Services.ThemeResourceHelper")
            ?? throw new InvalidOperationException("ThemeResourceHelper not found");
        var update = helper.GetMethod("UpdateTextSelectionResources", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException("UpdateTextSelectionResources not found");
        update.Invoke(null, [light, dark]);
    }

    private sealed class StubSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged { add { } remove { } }
        public AppSettings Get() => value;
        public void Save(AppSettings data) => value = data;
    }

    private sealed class StubWidgetLayoutProvider : IWidgetLayoutProvider
    {        private WidgetLayout value = new("Probe", "ProbeWidget", 0, 0, 2, 2, null);
        private DataChangedEvent<WidgetLayout>? dataChangingHandlers;
        private DataChangedEvent<WidgetLayout>? dataChangedHandlers;

        public string ScreenId { get; set; } = "probe-screen";

        public event DataChangedEvent<WidgetLayout> DataChanging
        {
            add => dataChangingHandlers += value;
            remove => dataChangingHandlers -= value;
        }

        public event DataChangedEvent<WidgetLayout> DataChanged
        {
            add => dataChangedHandlers += value;
            remove => dataChangedHandlers -= value;
        }

        public WidgetLayout Get() => value;

        public void Save(WidgetLayout next)
        {
            var old = value;
            dataChangingHandlers?.Invoke(this, old, next);
            value = next;
            dataChangedHandlers?.Invoke(this, old, next);
        }

        public void Remove() { }
    }

    private sealed class StubLayout : ILayoutProvider
    {
        private ScreensLayout value = new([]);
        private DataChangedEvent<ScreensLayout>? dataChangingHandlers;
        private DataChangedEvent<ScreensLayout>? dataChangedHandlers;

        public event DataChangedEvent<ScreensLayout> DataChanging
        {
            add => dataChangingHandlers += value;
            remove => dataChangingHandlers -= value;
        }

        public event DataChangedEvent<ScreensLayout> DataChanged
        {
            add => dataChangedHandlers += value;
            remove => dataChangedHandlers -= value;
        }

        public ScreensLayout Get() => value;

        public void Save(ScreensLayout next)
        {
            var old = value;
            dataChangingHandlers?.Invoke(this, old, next);
            value = next;
            dataChangedHandlers?.Invoke(this, old, next);
        }
    }

    /// <summary>Distinct settings-page type so the geometry checks get their own slot.</summary>
    private sealed class StubSettingsPage : UserControl;

    private sealed class ProbeApp : DeskCanvas.App
    {
        public override void OnFrameworkInitializationCompleted() { }
    }
}
