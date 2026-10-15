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
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Folders.Views;
using Notes.Views;
using Weather.Views;

namespace UiFoundationChecks;

/// <summary>
/// W8 heavy checks: the three highest-risk popup migrations onto the shared
/// <see cref="SecondaryPanelShell"/> — Notes, Weather and BigFolder.
/// <list type="bullet">
/// <item><b>Per popup</b>: headless instantiation succeeds, the tree declares exactly one
/// shell, no local card / glass surface / close button / close-button style remains, the base
/// <c>PanelCard</c> shell fallback resolves the connected card at runtime, the header drag
/// area carries the shared handle with the business interactive controls excluded, the close
/// button and the backdrop each collapse into exactly one CloseRequested handled by the base
/// (a press inside the card never requests one), and a template re-application reconnects the
/// card without stacking <c>cardScale.PropertyChanged</c> handlers.</item>
/// <item><b>Business intact</b>: Notes keeps title/body editor and markdown preview; Weather
/// keeps the hourly strip, 7-day list, the four metric cards and the plugin-owned palette; in
/// the 多彩 theme the weather-blue card is asserted over the shared macOS baseline via an
/// injected settings provider. BigFolder keeps the drawer, the file grid, and its three
/// pinned specialisations (conditional <c>TransformAnimationEnabled</c>, the 150 ms focus
/// grace, Escape-closes-drawer-first).</item>
/// </list>
/// Headless-environment limits (asserted by humans on a real run, never faked here): real
/// BeginMoveDrag window dragging, native DWM transparency levels, the SetWindowRgn native
/// blur clip of BigFolder, the Win32 outside-click hook and the focus-loss auto-close.
/// </summary>
internal static partial class Program
{
    private static void CheckPopupMigrationHeavy()
    {
        Console.WriteLine("--- Notes / Weather / BigFolder 弹窗迁移(W8 heavy) ---");
        CheckHeavyNote();
        CheckHeavyWeather();
        CheckHeavyWeatherColorfulCard();
        CheckHeavyBigFolder();
    }

    private static void RunJobs() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    // ---- shared migration assertions -------------------------------------------------------

    private static void CheckHeavyShellBasics(SecondaryPanelWindow popup, string label)
    {
        Check(popup.Content is SecondaryPanelShell, $"{label}: the window content root IS the shared shell");
        Check(popup.GetLogicalDescendants().OfType<SecondaryPanelShell>().Count() == 1,
            $"{label}: exactly one shell in the tree (no shell-in-shell, no wrapped old card)");

        // No local chrome: the old template names must not exist anywhere, and the shell's
        // glass surface lives only in its template (visual, not a logical child).
        var names = popup.GetLogicalDescendants().OfType<Control>()
            .Select(c => c.Name).Where(n => n is not null).ToHashSet();
        Check(!names.Contains("CardBorder"), $"{label}: no local CardBorder");
        Check(!names.Contains("LiquidGlassSurfaceControl"), $"{label}: no local LiquidGlassSurfaceControl");
        Check(!names.Contains("CloseButton"), $"{label}: no local CloseButton");
        Check(popup.GetLogicalDescendants().OfType<LiquidGlassSurface>().Count() == 0,
            $"{label}: no LiquidGlassSurface in the logical tree (the shell's template part owns the glass)");
        foreach (var fragment in new[] { "close-btn", "icon-btn", "CardBorder", "LiquidGlass" })
        {
            Check(!HasLocalSelector(popup.Styles, fragment),
                $"{label}: no local \"{fragment}\" style selector remains");
        }

        // The window no longer overrides PanelCard: the base falls back to the shell's card.
        var panelCardGetter = popup.GetType()
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetGetMethod(true)!;
        Check(panelCardGetter.DeclaringType == typeof(SecondaryPanelWindow),
            $"{label}: PanelCard no longer overridden (the base shell fallback is used)");
        var connectedCard = ReadField<SecondaryPanelShell?>(popup, "connectedShell")?.CardBorder;
        var panelCard = typeof(SecondaryPanelWindow)
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(popup);
        Check(ReferenceEquals(panelCard, connectedCard) && connectedCard is not null,
            $"{label}: the base shell fallback resolved the connected card at runtime");

        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderDragArea is not null && PanelDragBehavior.GetIsDragHandle(shell.HeaderDragArea),
            $"{label}: the header strip is marked as the shared drag handle");
    }

    private static void CheckHeavyCloseButtonPath(SecondaryPanelWindow popup, string label)
    {
        var shell = (SecondaryPanelShell)popup.Content!;
        var closeRequests = 0;
        // The base marks CloseRequested handled — count with handledEventsToo.
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => closeRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);

        var closeButton = shell.CloseButton!;
        var center = closeButton.TranslatePoint(
            new Point(closeButton.Bounds.Width / 2, closeButton.Bounds.Height / 2), popup)!.Value;
        popup.MouseDown(center, MouseButton.Left);
        popup.MouseUp(center, MouseButton.Left);
        RunJobs();

        Check(closeRequests == 1, $"{label}: one close-button click raises exactly one CloseRequested ({closeRequests})");
        Check(ReadField<bool>(popup, "closeAnimationRunning"),
            $"{label}: the base class took over and is running the exit animation");
        Check(popup.IsVisible, $"{label}: the window is still open while the exit animation plays");
        FrameworkClose(popup);
        RunJobs();
        Check(!popup.IsVisible, $"{label}: the framework close finished the dismissal");
    }

    private static void CheckHeavyBackdropAndInsideCard(Func<SecondaryPanelWindow> create, string label)
    {
        // A press on the in-window area outside the card (PART_Backdrop only — the rounded
        // corner at (2,2) leaves the card's hit region) dismisses the panel.
        var popup = create();
        popup.Show();
        RunJobs();
        // The renderer's hit tester only exists after a render pass.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var shell = (SecondaryPanelShell)popup.Content!;
        var backdropRequests = 0;
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => backdropRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        popup.MouseDown(new Point(2, 2), MouseButton.Left);
        popup.MouseUp(new Point(2, 2), MouseButton.Left);
        RunJobs();
        Check(backdropRequests == 1 && ReadField<bool>(popup, "closeAnimationRunning"),
            $"{label}: a press whose source really is PART_Backdrop requests the shared close ({backdropRequests})");
        FrameworkClose(popup);
        RunJobs();

        // A press INSIDE the card must not request a close (the old BigFolder
        // "e.Source is Panel or Grid" whole-root handler is gone).
        var inside = create();
        inside.Show();
        RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var insideShell = (SecondaryPanelShell)inside.Content!;
        var insideRequests = 0;
        insideShell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => insideRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var contentSpot = insideShell.ContentHost!.TranslatePoint(new Point(10, 10), inside)!.Value;
        inside.MouseDown(contentSpot, MouseButton.Left);
        inside.MouseUp(contentSpot, MouseButton.Left);
        RunJobs();
        Check(insideRequests == 0, $"{label}: a press inside the card never requests a close ({insideRequests})");
        Check(!ReadField<bool>(inside, "closeAnimationRunning"), $"{label}: the panel stays open");
        FrameworkClose(inside);
        RunJobs();
    }

    private static void CheckHeavyTemplateReapplication(SecondaryPanelWindow popup, string label)
    {
        var shell = (SecondaryPanelShell)popup.Content!;
        var firstScale = ReadField<ScaleTransform?>(popup, "cardScale");
        Check(firstScale is not null, $"{label}: the panel connected the card scale transform");
        Check(firstScale is null || PropertyChangedHandlerCount(firstScale) == 1,
            $"{label}: the initial connection attaches exactly one scale listener");

        // Force a template re-application: the base must reconnect the NEW card without
        // stacking the transform subscription.
        popup.Content = null;
        RunJobs();
        popup.Content = shell;
        RunJobs();

        Check(shell.IsTemplateApplied, $"{label}: the shell re-applied its template after re-attaching");
        var reconnectedScale = ReadField<ScaleTransform?>(popup, "cardScale");
        Check(reconnectedScale is not null && PropertyChangedHandlerCount(reconnectedScale!) == 1,
            $"{label}: the reconnect replaced (not stacked) the scale listener");
        Check(shell.CardBorder!.Opacity == 1.0,
            $"{label}: a live panel's rebuilt card is revealed immediately (not stuck at Opacity=0)");
    }

    private static void CheckHeavyCloseButtonAccessibleName(Func<SecondaryPanelWindow> create, string label)
    {
        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { new CultureInfo("zh-Hans"), new CultureInfo("en") })
            {
                CultureInfo.CurrentUICulture = culture;
                var popup = create();
                popup.Show();
                RunJobs();
                var shell = (SecondaryPanelShell)popup.Content!;
                var name = shell.CloseButton is null ? null : AutomationProperties.GetName(shell.CloseButton);
                Check(!string.IsNullOrWhiteSpace(name) && name == DeskCanvas.Locales.Locale.SecondaryPanel_Close,
                    $"{label} {culture.Name}: the shell close button's accessible name is the localized \"{name}\"");
                FrameworkClose(popup);
                RunJobs();
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // ---- Notes (次序 7) ----------------------------------------------------------------------

    private static void CheckHeavyNote()
    {
        Console.WriteLine("--- Notes popup: shell migration, editor intact ---");

        var popup = new NotePopupWindow();
        popup.Show();
        RunJobs();

        CheckHeavyShellBasics(popup, "Notes");

        var title = popup.FindControl<TextBox>("TitleBox");
        Check(title is not null, "Notes: the editable title box is still there");
        Check(popup.FindControl<TextBox>("BodyEditor") is not null, "Notes: the body editor is still there");
        Check(popup.FindControl<StackPanel>("PreviewHost") is not null, "Notes: the markdown preview host is still there");
        Check(popup.FindControl<TextBlock>("FooterText") is not null, "Notes: the footer status line is still there");

        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderContent is Control header
              && header.GetLogicalDescendants().OfType<TextBox>().Any(t => ReferenceEquals(t, title)),
            "Notes: the title editor lives in the shell HeaderContent slot");
        Check(title is not null && shell.HeaderDragArea is not null
              && PanelDragBehavior.IsDragExcluded(title, shell.HeaderDragArea),
            "Notes: the title TextBox is excluded from header dragging (typing never moves the window)");
        Check(shell.FooterContent is not null, "Notes: the footer divider lives in the shell FooterContent slot");

        CheckHeavyCloseButtonAccessibleName(() => new NotePopupWindow(), "Notes");
        CheckHeavyTemplateReapplication(popup, "Notes");
        CheckHeavyBackdropAndInsideCard(() => new NotePopupWindow(), "Notes");
        CheckHeavyCloseButtonPath(popup, "Notes");
    }

    // ---- Weather (次序 8) --------------------------------------------------------------------

    private static void CheckHeavyWeather()
    {
        Console.WriteLine("--- Weather popup: shell migration, multi-block content + palette intact ---");

        var popup = new WeatherPopupWindow();
        popup.Show();
        RunJobs();

        CheckHeavyShellBasics(popup, "Weather");

        Check(popup.FindControl<TextBlock>("CityText") is not null
              && popup.FindControl<TextBlock>("TempText") is not null,
            "Weather: the current-conditions header is still there");
        Check(popup.FindControl<ScrollViewer>("HourlyScroll") is not null
              && popup.FindControl<ItemsControl>("HourlyList") is not null,
            "Weather: the hourly strip (with its wheel-scroll surface) is still there");
        Check(popup.FindControl<ItemsControl>("DailyList") is not null,
            "Weather: the 7-day forecast list is still there");
        Check(popup.GetLogicalDescendants().OfType<Border>().Count(b => b.Classes.Contains("metric-card")) == 4,
            "Weather: the four environment metric cards are still there");

        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderContent is Control header
              && header.GetLogicalDescendants().OfType<TextBlock>()
                  .Any(t => ReferenceEquals(t, popup.FindControl<TextBlock>("CityText"))),
            "Weather: the conditions block lives in the shell HeaderContent slot");
        Check(shell.CloseButton is not null && shell.HeaderDragArea is not null
              && PanelDragBehavior.IsDragExcluded(shell.CloseButton, shell.HeaderDragArea),
            "Weather: the close button in the header is excluded from dragging");
        // The palette is plugin-owned: it follows the historic weather rule against whatever
        // variant this window actually runs in (headless has no provider → the fallback theme,
        // the same source the binder resolves the shell with).
        var fallback = PanelAppearanceResolver.ResolveEffectiveTheme(null);
        bool isDark = PanelAppearanceResolver.IsDark(fallback, popup.ActualThemeVariant);
        var expectedText = (fallback.IsColorful || isDark) ? Colors.White : Color.FromRgb(30, 30, 30);
        Check(popup.Resources["WeatherForegroundBrush"] is ISolidColorBrush fg
              && fg.Color == expectedText,
            "Weather: the plugin-owned palette was applied by the window (not by a local material branch)");

        CheckHeavyCloseButtonAccessibleName(() => new WeatherPopupWindow(), "Weather");
        CheckHeavyTemplateReapplication(popup, "Weather");
        CheckHeavyBackdropAndInsideCard(() => new WeatherPopupWindow(), "Weather");
        CheckHeavyCloseButtonPath(popup, "Weather");
    }

    /// <summary>
    /// 多彩 is the one theme where Weather's card differs from the shared resolver baseline
    /// (#1C1C1E/#FFFFFF): the weather blue must be asserted on top of every shell refresh.
    /// Injected through App.Services so the real appearance binder drives the refresh.
    /// </summary>
    private static void CheckHeavyWeatherColorfulCard()
    {
        Console.WriteLine("--- Weather 多彩: the weather-blue card overrides the shell baseline ---");

        var stub = new StubSettings(new AppSettings(
            new Theme(false, null, 0.9, false, false, "Inter", SurfaceStyle.Colorful),
            [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        var services = typeof(DeskCanvas.App).GetProperty("Services")!;
        var previous = services.GetValue(null);
        services.GetSetMethod(true)!.Invoke(null, [new SingleServiceProvider(stub)]);
        try
        {
            var popup = new WeatherPopupWindow();
            popup.Show();
            RunJobs();

            var shell = (SecondaryPanelShell)popup.Content!;
            Check(BrushColor(shell.CardBackground) == Color.Parse("#2B6CB0"),
                $"Weather 多彩 light: the card keeps the weather blue over the shared baseline ({BrushColor(shell.CardBackground)})");
            Check(BrushColor(shell.CardBorderBrush) == Color.FromArgb(40, 255, 255, 255),
                "Weather 多彩 light: the hairline keeps the historic white 40-alpha border");
            Check(popup.Resources["WeatherForegroundBrush"] is ISolidColorBrush fg && fg.Color == Colors.White,
                "Weather 多彩 light: the palette stays white-on-blue regardless of the variant");

            FrameworkClose(popup);
            RunJobs();
        }
        finally
        {
            services.GetSetMethod(true)!.Invoke(null, [previous]);
        }
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType)
            => serviceType.IsInstanceOfType(service) ? service : null;
    }

    // ---- BigFolder (次序 9, 最后迁移, 风险最高) -------------------------------------------------

    private static void CheckHeavyBigFolder()
    {
        Console.WriteLine("--- BigFolder popup: shell migration, drawer + pinned overrides ---");

        var popup = new BigFolderPopupWindow();
        popup.Show();
        RunJobs();

        CheckHeavyShellBasics(popup, "BigFolder");

        var drawer = popup.FindControl<Border>("SettingsDrawer");
        Check(drawer is not null, "BigFolder: the settings drawer is still there");
        Check(popup.FindControl<ScrollViewer>("ItemsScrollViewer") is not null
              && popup.FindControl<ItemsControl>("PopupItemsList") is not null,
            "BigFolder: the file grid is still there");
        Check(popup.FindControl<TextBox>("FolderNameBox") is not null
              && popup.FindControl<NumericUpDown>("ColumnsNum") is not null,
            "BigFolder: the drawer inputs are still there");

        var shell = (SecondaryPanelShell)popup.Content!;
        Check(!string.IsNullOrWhiteSpace(shell.Title),
            $"BigFolder: the dynamic title was written into the shell (\"{shell.Title}\")");
        var settingsButton = popup.FindControl<Button>("SettingsButton");
        Check(settingsButton is not null && ReferenceEquals(shell.HeaderActions, settingsButton),
            "BigFolder: the settings toggle lives in the shell HeaderActions slot");
        Check(settingsButton is not null && shell.HeaderDragArea is not null
              && PanelDragBehavior.IsDragExcluded(settingsButton, shell.HeaderDragArea),
            "BigFolder: the settings button is excluded from header dragging");

        // The three risk-carrying specialisations stay pinned (headless: no provider → the
        // fallback theme is not native-blur → the transform animation stays ON).
        var transform = typeof(SecondaryPanelWindow)
            .GetProperty("TransformAnimationEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(popup);
        Check(transform is true,
            "BigFolder: TransformAnimationEnabled stays conditional and is true without the native-blur theme");
        var grace = typeof(SecondaryPanelWindow)
            .GetProperty("DeactivateCloseGraceMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(popup);
        Check(grace is 150, $"BigFolder: the 150 ms focus-loss grace is pinned ({grace})");
        var escape = typeof(BigFolderPopupWindow)
            .GetMethod("OnPanelEscape", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Check(!(bool)escape.Invoke(popup, null)!,
            "BigFolder: Escape with the drawer hidden does not consume the panel escape (the panel closes)");
        drawer!.IsVisible = true;
        Check((bool)escape.Invoke(popup, null)!,
            "BigFolder: the first Escape consumes the panel escape while the drawer is open");
        Check(!drawer.IsVisible,
            "BigFolder: OnPanelEscape closed the settings drawer instead of the panel");

        CheckHeavyCloseButtonAccessibleName(() => new BigFolderPopupWindow(), "BigFolder");
        CheckHeavyTemplateReapplication(popup, "BigFolder");
        CheckHeavyBackdropAndInsideCard(() => new BigFolderPopupWindow(), "BigFolder");
        CheckHeavyCloseButtonPath(popup, "BigFolder");
    }
}
