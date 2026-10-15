using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Tools.Views;

namespace UiFoundationChecks;

/// <summary>
/// W8 (Tools batch) checks: the Translator and Calculator popups migrated onto the shared
/// <see cref="SecondaryPanelShell"/>.
/// <list type="bullet">
/// <item><b>Migration contract</b>: each window's root IS the shell (exactly one in the tree),
/// with no local card / glass surface / close button left in the logical tree and no shell or
/// glass selectors in the local Styles. Both windows dropped their PanelCard override, so the
/// base class falls back to the connected shell's card at runtime.</item>
/// <item><b>Close single path</b>: a close-button click and a press whose source really is
/// PART_Backdrop each raise exactly one CloseRequested and the base enters its animated close
/// (counted with handledEventsToo — the base marks the event handled); a press inside the card
/// never requests a close.</item>
/// <item><b>Template re-application</b>: after a detach/reattach the card-scale subscription
/// count stays at exactly 1 (handlers never stack).</item>
/// <item><b>Business kept</b>: Translator keeps its input/output boxes, the language/engine
/// combos and the translate action; Calculator keeps the three mode workspaces, the expression
/// boxes, the sidebar and the toast — plus the header actions (mode tabs, angle toggle) sitting
/// in the shell's HeaderActions slot.</item>
/// <item><b>Drag contract</b>: the shell header is the drag handle, interactive business
/// controls (buttons, text boxes, combos) are excluded by PanelDragBehavior and the bare title
/// text is not — dragging never starts on a control.</item>
/// <item><b>Accessibility</b>: the shared close button carries the localized accessible name in
/// zh-Hans and en.</item>
/// </list>
/// Headless-environment limits (asserted by humans, never faked here): real window dragging via
/// BeginMoveDrag, native DWM transparency levels, live wallpaper sampling of the glass, the
/// Win32 outside-click hook and its Popup exclusion (a ComboBox dropdown must not dismiss the
/// panel — that needs a real Windows run), and multi-screen spawn positioning.
/// </summary>
internal static partial class Program
{
    private static void CheckPopupMigrationTools()
    {
        Console.WriteLine("--- Translator / Calculator 弹窗迁移(W8) ---");
        CheckTranslatorMigration();
        CheckCalculatorMigration();
        CheckToolsClosePath(() => new TranslatorPopupWindow(), "Translator");
        CheckToolsClosePath(() => new CalculatorPopupWindow(), "Calculator");
        CheckToolsTemplateReapplication(() => new TranslatorPopupWindow(), "Translator");
        CheckToolsTemplateReapplication(() => new CalculatorPopupWindow(), "Calculator");
        CheckToolsCloseButtonAccessibleName();
    }

    // ---- shared: the migration contract (one shell, no local chrome, base card fallback) ----

    private static void CheckMigratedToolsWindow(Window popup, string label, bool expectHeaderActions)
    {
        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The window's root is the shell — not an old Border wrapper, not a second shell.
        Check(popup.Content is SecondaryPanelShell, $"{label}: the window content root IS the shared shell");
        Check(popup.GetLogicalDescendants().OfType<SecondaryPanelShell>().Count() == 1,
            $"{label}: exactly one shell in the tree (no shell-in-shell, no wrapped old card)");

        // No local chrome: the old names must not exist anywhere, and no LiquidGlassSurface
        // outside the shell's template (template parts are visual, not logical children).
        var names = popup.GetLogicalDescendants().OfType<Control>()
            .Select(c => c.Name).Where(n => n is not null).ToHashSet();
        Check(!names.Contains("CardBorder"), $"{label}: no local CardBorder");
        Check(!names.Contains("LiquidGlassSurfaceControl"), $"{label}: no local LiquidGlassSurfaceControl");
        Check(!names.Contains("CloseButton"), $"{label}: no local CloseButton");
        Check(popup.GetLogicalDescendants().OfType<LiquidGlassSurface>().Count() == 0,
            $"{label}: no LiquidGlassSurface in the logical tree (the shell's template part owns the glass)");

        // No shell/glass/close-button styling left locally: every selector in the window's own
        // Styles is a business rule (pills, keypads, expression boxes, …), never shell chrome.
        // (The business template selectors like /template/ Border#PART_BorderElement on the
        // expression boxes are legit — only the SHELL part names must not appear.)
        Check(!HasLocalSelector(popup.Styles, "SecondaryPanelShell"), $"{label}: no local SecondaryPanelShell selector");
        Check(!HasLocalSelector(popup.Styles, "LiquidGlass"), $"{label}: no local glass selector");
        foreach (var shellPart in new[] { "PART_CloseButton", "PART_Backdrop", "PART_CardBorder", "PART_GlassSurface", "PART_HeaderDragArea", "PART_ContentPresenter" })
        {
            Check(!HasLocalSelector(popup.Styles, shellPart), $"{label}: no local {shellPart} selector");
        }

        // The window no longer overrides PanelCard: the base falls back to the shell's card.
        var panelCardGetter = popup.GetType()
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetGetMethod(true)!;
        Check(panelCardGetter.DeclaringType == typeof(SecondaryPanelWindow),
            $"{label}: no longer overrides PanelCard (the base shell fallback is used)");
        var connectedCard = ReadField<SecondaryPanelShell?>(popup, "connectedShell")?.CardBorder;
        var panelCard = typeof(SecondaryPanelWindow)
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(popup);
        Check(ReferenceEquals(panelCard, connectedCard) && connectedCard is not null,
            $"{label}: the base shell fallback resolved the connected card at runtime");

        // The shared drag contract: the header strip is the handle, business interactive
        // controls never start a drag, the bare title text does not block it.
        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderDragArea is not null && PanelDragBehavior.GetIsDragHandle(shell.HeaderDragArea),
            $"{label}: PART_HeaderDragArea is marked as the shared W2 drag handle");
        if (shell.HeaderDragArea is { } dragArea)
        {
            var titleText = (shell.HeaderContent as Control)?.GetLogicalDescendants().OfType<TextBlock>().FirstOrDefault();
            Check(titleText is not null && !PanelDragBehavior.IsDragExcluded(titleText, dragArea),
                $"{label}: the title text inside the header does not block the drag");
            foreach (var interactive in popup.GetLogicalDescendants()
                         .OfType<Control>()
                         .Where(c => c.Name is "InputBox" or "SciExpressionBox" or "ProgExpressionBox"
                             or "SourceCombo" or "TargetCombo" or "EngineCombo"
                             or "TranslateButton" or "TabScientificBtn" or "TabProgrammerBtn"
                             or "TabConverterBtn" or "AngleModeBtn"))
            {
                Check(PanelDragBehavior.IsDragExcluded(interactive, dragArea),
                    $"{label}: {interactive.Name} is excluded from dragging (interactive control)");
            }
        }

        // Header actions (Calculator only) live in the shell's HeaderActions slot next to the
        // shared close button.
        if (expectHeaderActions)
        {
            Check(shell.HeaderActions is not null, $"{label}: the business header actions are in HeaderActions");
        }

        FrameworkClose(popup);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 1. Translator: shell migration + business kept ------------------------------------

    private static void CheckTranslatorMigration()
    {
        Console.WriteLine("--- Translator popup: one shell, no local chrome, business intact ---");

        var popup = new TranslatorPopupWindow();
        CheckMigratedToolsWindow(popup, "Translator", expectHeaderActions: false);

        // Re-open for the business assertions (CheckMigratedToolsWindow closed its instance).
        popup = new TranslatorPopupWindow();
        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(popup.FindControl<TextBox>("InputBox") is not null, "Translator: InputBox is still there");
        Check(popup.FindControl<TextBox>("OutputBox") is not null, "Translator: OutputBox is still there");
        Check(popup.FindControl<ComboBox>("SourceCombo") is not null
              && popup.FindControl<ComboBox>("TargetCombo") is not null
              && popup.FindControl<ComboBox>("EngineCombo") is not null,
            "Translator: the language/engine ComboBoxes are still there");
        Check(popup.FindControl<Button>("TranslateButton") is not null,
            "Translator: the translate action is still there");
        Check(popup.FindControl<Button>("CopyButton") is not null,
            "Translator: the copy action is still there");
        Check(popup.FindControl<TextBlock>("CharCountText") is not null
              && popup.FindControl<TextBlock>("StatusText") is not null,
            "Translator: the char counter and status line are still there");

        // The dynamic icon+title block lives in the shell's HeaderContent slot.
        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderContent is Control headerContent
              && headerContent.GetLogicalDescendants().OfType<TextBlock>()
                  .Any(t => t.Text == Tools.Locales.Locale.Tools_Translator_Title),
            "Translator: the localized title block lives in the shell's HeaderContent slot");

        FrameworkClose(popup);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 2. Calculator: shell migration + business kept --------------------------------------

    private static void CheckCalculatorMigration()
    {
        Console.WriteLine("--- Calculator popup: one shell, no local chrome, business intact ---");

        var popup = new CalculatorPopupWindow();
        CheckMigratedToolsWindow(popup, "Calculator", expectHeaderActions: true);

        popup = new CalculatorPopupWindow();
        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Mode switching (the three workspaces) survived.
        Check(popup.FindControl<Button>("TabScientificBtn") is not null
              && popup.FindControl<Button>("TabProgrammerBtn") is not null
              && popup.FindControl<Button>("TabConverterBtn") is not null,
            "Calculator: the three mode tabs are still there");
        Check(popup.FindControl<Panel>("ScientificPanel") is not null
              && popup.FindControl<Panel>("ProgrammerPanel") is not null
              && popup.FindControl<Panel>("ConverterPanel") is not null,
            "Calculator: the three mode workspaces are still there");

        // Expression editing / keypad / results / converter / sidebar / toast survived.
        Check(popup.FindControl<TextBox>("SciExpressionBox") is not null
              && popup.FindControl<TextBlock>("SciResultText") is not null,
            "Calculator: the scientific expression box and result are still there");
        Check(popup.FindControl<TextBox>("ProgExpressionBox") is not null
              && popup.FindControl<TextBlock>("ProgDecText") is not null,
            "Calculator: the programmer expression box and base matrix are still there");
        Check(popup.FindControl<TextBox>("ConvInputBox") is not null
              && popup.FindControl<TextBlock>("ConvResultText") is not null
              && popup.FindControl<ComboBox>("ConvFromCombo") is not null,
            "Calculator: the unit converter inputs are still there");
        Check(popup.FindControl<StackPanel>("SidebarItemsPanel") is not null,
            "Calculator: the history/memory sidebar is still there");
        Check(popup.FindControl<Border>("ToastBanner") is not null,
            "Calculator: the floating toast is still there");
        Check(popup.FindControl<Button>("AngleModeBtn") is not null,
            "Calculator: the angle mode toggle is still there");

        // Header actions really sit in the shell's HeaderActions slot (mode tabs + angle toggle).
        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderActions is Control actions
              && actions.GetLogicalDescendants().OfType<Button>()
                  .Any(b => ReferenceEquals(b, popup.FindControl<Button>("TabScientificBtn")))
              && actions.GetLogicalDescendants().OfType<Button>()
                  .Any(b => ReferenceEquals(b, popup.FindControl<Button>("AngleModeBtn"))),
            "Calculator: the mode tabs and the angle toggle live in the shell's HeaderActions slot");
        Check(shell.HeaderContent is Control header
              && header.GetLogicalDescendants().OfType<TextBlock>()
                  .Any(t => t.Text == Tools.Locales.Locale.Tools_Calculator_Title),
            "Calculator: the localized title block lives in the shell's HeaderContent slot");

        // A mode switch still works end-to-end (business path, no shell interference).
        popup.FindControl<Button>("TabProgrammerBtn")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(popup.FindControl<Panel>("ProgrammerPanel")!.IsVisible
              && !popup.FindControl<Panel>("ScientificPanel")!.IsVisible,
            "Calculator: switching to the programmer mode still swaps the workspaces");

        FrameworkClose(popup);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 3. The single close path through the base class (both popups) ----------------------

    private static void CheckToolsClosePath(Func<Window> create, string label)
    {
        Console.WriteLine($"--- {label}: CloseRequested collapses into the shared animated close ---");

        // NOTE: the exit animation's completion timer (180 ms + 40 ms pad) is a DispatcherTimer
        // and this headless harness runs no dispatcher loop, so the final base.Close() never
        // fires here (the same W7 caveat). What IS verifiable headlessly is the routing
        // contract — exactly one CloseRequested per interaction and the base entering its
        // animated-close state (closeAnimationRunning) without closing on the spot.
        //
        // The close-button click above pumps the open transition out first (see
        // CompleteOpenTransition); the backdrop and inside-card sections below open FRESH
        // panels whose card still sits at the spawn scale — either way the (2,2) corner press
        // resolves to PART_Backdrop.

        // 1) Close-button click → exactly one request, the base runs the exit animation.
        var panel = create();
        var shell = (SecondaryPanelShell)panel.Content!;
        panel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Run the open transition out first: frozen mid-zoom, the click hit-tests against the
        // unscaled layout and the projected button center lands on the content underneath
        // (0 requests for the wrong reason). Same stand-in as the W7 close-path checks.
        CompleteOpenTransition(panel);
        // The renderer's hit tester only exists after a render pass.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var closeRequests = 0;
        // The base handler marks CloseRequested handled — count with handledEventsToo.
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => closeRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var closeButton = shell.CloseButton!;
        var center = closeButton.TranslatePoint(
            new Point(closeButton.Bounds.Width / 2, closeButton.Bounds.Height / 2), panel)!.Value;
        panel.MouseDown(center, MouseButton.Left);
        panel.MouseUp(center, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(closeRequests == 1, $"{label}: one close-button click raises exactly one CloseRequested ({closeRequests})");
        Check(ReadField<bool>(panel, "closeAnimationRunning"),
            $"{label}: the base class took over and is running the exit animation");
        Check(panel.IsVisible, $"{label}: the window is still open while the exit animation plays");
        FrameworkClose(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 2) A press on the in-window area outside the card (PART_Backdrop only) closes too.
        var backdropPanel = create();
        backdropPanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var backdropShell = (SecondaryPanelShell)backdropPanel.Content!;
        var backdropRequests = 0;
        backdropShell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => backdropRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        backdropPanel.MouseDown(new Point(2, 2), MouseButton.Left);
        backdropPanel.MouseUp(new Point(2, 2), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(backdropRequests == 1
              && ReadField<bool>(backdropPanel, "closeAnimationRunning"),
            $"{label}: a press whose source really is PART_Backdrop requests the shared close ({backdropRequests})");
        FrameworkClose(backdropPanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 3) A press INSIDE the card must not request a close.
        var insidePanel = create();
        insidePanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var insideShell = (SecondaryPanelShell)insidePanel.Content!;
        var insideRequests = 0;
        insideShell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => insideRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var contentSpot = insideShell.ContentHost!.TranslatePoint(new Point(10, 10), insidePanel)!.Value;
        insidePanel.MouseDown(contentSpot, MouseButton.Left);
        insidePanel.MouseUp(contentSpot, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(insideRequests == 0, $"{label}: a press inside the card never requests a close ({insideRequests})");
        Check(!ReadField<bool>(insidePanel, "closeAnimationRunning"), $"{label}: the panel stays open");
        FrameworkClose(insidePanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 4. Template re-application reconnects without stacking subscriptions (both) ---------

    private static void CheckToolsTemplateReapplication(Func<Window> create, string label)
    {
        Console.WriteLine($"--- {label}: template re-application reconnects, never stacks handlers ---");

        var panel = create();
        var shell = (SecondaryPanelShell)panel.Content!;
        panel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var firstScale = ReadField<ScaleTransform?>(panel, "cardScale");
        Check(firstScale is not null && PropertyChangedHandlerCount(firstScale!) == 1,
            $"{label}: the initial connection attaches exactly one scale listener");

        // Force a template re-application by detaching the shell and re-attaching it: the
        // visual is rebuilt, the base must reconnect the NEW card and the old transform
        // subscription must be released first.
        panel.Content = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        panel.Content = shell;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(shell.IsTemplateApplied, $"{label}: the shell re-applied its template after re-attaching");
        var reconnectedScale = ReadField<ScaleTransform?>(panel, "cardScale");
        Check(reconnectedScale is not null,
            $"{label}: the base reconnected the card transform after re-application");
        Check(PropertyChangedHandlerCount(reconnectedScale!) == 1,
            $"{label}: the reconnect replaced (not stacked) the scale listener");

        // The shell's own part handlers must not stack either.
        var requests = 0;
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => requests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Array.Empty<Type>(), null)
                      ?? typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RoutedEventArgs)], null)!;
        onClick.Invoke(shell.CloseButton, onClick.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : [new RoutedEventArgs(Button.ClickEvent)]);
        Check(requests == 1,
            $"{label}: after a template re-run a close click still raises exactly one request ({requests})");

        FrameworkClose(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 5. The shared close button's localized accessible name (both popups, both cultures) --

    private static void CheckToolsCloseButtonAccessibleName()
    {
        Console.WriteLine("--- Tools popups: close button accessible name in both cultures ---");

        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { new CultureInfo("zh-Hans"), new CultureInfo("en") })
            {
                CultureInfo.CurrentUICulture = culture;
                foreach (var (label, create) in new (string, Func<Window>)[]
                {
                    ("Translator", () => new TranslatorPopupWindow()),
                    ("Calculator", () => new CalculatorPopupWindow()),
                })
                {
                    var panel = create();
                    panel.Show();
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    var shell = (SecondaryPanelShell)panel.Content!;

                    var name = shell.CloseButton is null ? null : AutomationProperties.GetName(shell.CloseButton);
                    Check(!string.IsNullOrWhiteSpace(name)
                          && name == DeskCanvas.Locales.Locale.SecondaryPanel_Close,
                        $"{label} {culture.Name}: the close button's accessible name is the localized " +
                        $"\"Locale.SecondaryPanel_Close\" (\"{name}\")");

                    FrameworkClose(panel);
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                }
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }
}
