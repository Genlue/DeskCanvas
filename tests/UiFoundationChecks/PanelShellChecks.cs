using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Tools.Views;

namespace UiFoundationChecks;

/// <summary>
/// W7 checks: the shared secondary-panel shell and the shared material resolver.
/// <list type="bullet">
/// <item><b>PanelAppearanceResolver</b>: the five materials (Solid / Acrylic / LiquidGlass /
/// LiquidGlassV2 / Colorful) × both variants resolve the exact baseline the nine popups
/// carried in their own <c>ApplyTheme</c> — transparency hints with a None fallback, card
/// fill/hairline alphas, the rendered-glass flag with the theme as material, the semantic
/// title/subtitle/icon brushes, and the propagated corner radius. Pure functions, asserted
/// without any window.</item>
/// <item><b>Shell template</b>: all six parts resolve under the real App.axaml, the card
/// starts at Opacity=0, the drag area is hit-testable (transparent background) and really
/// carries the shared W2 drag handle, and the close button resolves the shared
/// dc-icon-button theme with a non-empty localized accessible name in both cultures.</item>
/// <item><b>Close path</b>: a close-button click and a backdrop press both bubble the single
/// CloseRequested into the base class's animated close (one close path, no second one).</item>
/// <item><b>Re-application</b>: re-applying the shell template reconnects the card without
/// stacking <c>cardScale.PropertyChanged</c> handlers.</item>
/// <item><b>Lifetime</b>: the appearance binder unsubscribes on Close, the active-panel map
/// drops the instance and no live panel is kept by the shared services.</item>
/// <item><b>Clipboard pilot</b>: the migrated window declares exactly one shell, no local
/// card / glass surface / close button, and keeps its business controls.</item>
/// </list>
/// Headless-environment limits (asserted by humans, never faked here): real window dragging
/// via BeginMoveDrag, native DWM transparency levels, live wallpaper sampling of the glass,
/// the Win32 outside-click hook and multi-screen spawn positioning.
/// </summary>
internal static partial class Program
{
    private static void CheckPanelShell()
    {
        Console.WriteLine("--- secondary panel shell + material resolver (W7) ---");

        CheckResolverMatrix();
        CheckShellTemplateParts();
        CheckGlassSamplingAnchor();
        CheckCloseButtonAccessibleName();
        CheckShellClosePaths();
        CheckTemplateReapplication();
        CheckBinderLifetime();
        CheckClipboardMigration();
    }

    // ---- 1. Resolver matrix: five materials × two variants --------------------------------

    private static void CheckResolverMatrix()
    {
        Console.WriteLine("--- PanelAppearanceResolver: 5 materials × 2 variants ---");

        const double radius = 22.0;
        var darkFollow = new Theme(DarkMode: null, AccentColor: null, OpacityLevel: 0.8, Monochrome: false,
            UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Solid);

        // "Follow system" (DarkMode == null) must resolve to the LIVE variant, not to dark.
        Check(!PanelAppearanceResolver.IsDark(darkFollow, ThemeVariant.Light),
            "follow-system theme resolves to LIGHT on a light panel variant");
        Check(PanelAppearanceResolver.IsDark(darkFollow, ThemeVariant.Dark),
            "follow-system theme resolves to DARK on a dark panel variant");
        Check(PanelAppearanceResolver.IsDark(darkFollow with { DarkMode = true }, ThemeVariant.Light),
            "an explicit DarkMode=true wins over the live variant");

        foreach (var isDark in new[] { true, false })
        {
            var suffix = isDark ? "dark" : "light";

            // 液态玻璃 / 新液态玻璃: transparent card + live surface, hint Transparent + None fallback.
            foreach (var surface in new[] { SurfaceStyle.LiquidGlass, SurfaceStyle.LiquidGlassV2 })
            {
                var theme = ThemeOf(surface, isDark);
                var a = PanelAppearanceResolver.Resolve(theme, isDark, radius);
                Check(a.UseRenderedGlass && ReferenceEquals(a.GlassMaterial, theme),
                    $"{surface} {suffix}: rendered glass ON with the theme itself as material");
                Check(BrushColor(a.CardBackground).A == 0,
                    $"{surface} {suffix}: the card stays transparent under the glass");
                Check(IsColor(a.CardBorderBrush, Color.FromArgb(90, 255, 255, 255)),
                    $"{surface} {suffix}: hairline = #5AFFFFFF (the popups' shared glass border)");
                CheckLevels(a, WindowTransparencyLevel.Transparent, $"{surface} {suffix}");
                CheckSemanticBrushes(a, isDark, $"{surface} {suffix}");
                Check(a.CornerRadius == radius, $"{surface} {suffix}: radius propagates to the card");
            }

            // 多彩: flat opaque macOS card.
            var colorful = PanelAppearanceResolver.Resolve(ThemeOf(SurfaceStyle.Colorful, isDark), isDark, radius);
            Check(!colorful.UseRenderedGlass && colorful.GlassMaterial is null,
                "Colorful: no rendered glass");
            Check(IsColor(colorful.CardBackground, Color.Parse(isDark ? "#1C1C1E" : "#FFFFFF")),
                $"Colorful {suffix}: card fill is the opaque {(isDark ? "#1C1C1E" : "#FFFFFF")} baseline");
            Check(IsColor(colorful.CardBorderBrush, isDark
                      ? Color.FromArgb(60, 255, 255, 255)
                      : Color.FromArgb(40, 0, 0, 0)),
                $"Colorful {suffix}: hairline matches the popup baseline");
            CheckLevels(colorful, WindowTransparencyLevel.Transparent, $"Colorful {suffix}");
            CheckSemanticBrushes(colorful, isDark, $"Colorful {suffix}");

            // 纯色: user colour × the opacity slider (alpha clamped to 40–255).
            var solidTheme = new Theme(
                DarkMode: isDark, AccentColor: null, OpacityLevel: 0.5, Monochrome: false,
                UseNativeFrame: false, FontFamily: "Inter", Surface: SurfaceStyle.Solid,
                SolidBackgroundDark: "#334455", SolidBackgroundLight: "#DDEEFF");
            var solid = PanelAppearanceResolver.Resolve(solidTheme, isDark, radius);
            var solidBase = Color.Parse(isDark ? "#334455" : "#DDEEFF");
            Check(IsColor(solid.CardBackground, Color.FromArgb(128, solidBase.R, solidBase.G, solidBase.B)),
                $"Solid {suffix}: fill = base colour at round(0.5×255)=128 alpha");
            Check(IsColor(solid.CardBorderBrush, isDark
                      ? Color.FromArgb(40, 255, 255, 255)
                      : Color.FromArgb(30, 0, 0, 0)),
                $"Solid {suffix}: hairline matches the popup baseline");
            CheckLevels(solid, WindowTransparencyLevel.Transparent, $"Solid {suffix}");

            var solidSaturated = PanelAppearanceResolver.Resolve(solidTheme with { OpacityLevel = 0.1 }, isDark, radius);
            Check(BrushColor(solidSaturated.CardBackground).A == 40,
                $"Solid {suffix}: the slider never pushes the fill below the alpha-40 floor");
            var solidOpaque = PanelAppearanceResolver.Resolve(solidTheme with { OpacityLevel = 1.0 }, isDark, radius);
            Check(BrushColor(solidOpaque.CardBackground).A == 255,
                $"Solid {suffix}: opacity 1.0 keeps the fully opaque fill");

            // 毛玻璃: native acrylic blur + translucent fill (alpha clamped to 40–240).
            var acrylicTheme = ThemeOf(SurfaceStyle.Acrylic, isDark) with { OpacityLevel = 0.7 };
            var acrylic = PanelAppearanceResolver.Resolve(acrylicTheme, isDark, radius);
            Check(!acrylic.UseRenderedGlass, $"Acrylic {suffix}: no rendered glass");
            Check(IsColor(acrylic.CardBackground, isDark
                      ? Color.FromArgb(154, 28, 28, 32)
                      : Color.FromArgb(154, 245, 245, 248)),
                $"Acrylic {suffix}: fill = the popups' base at round(0.7×220)=154 alpha");
            Check(IsColor(acrylic.CardBorderBrush, isDark
                      ? Color.FromArgb(55, 255, 255, 255)
                      : Color.FromArgb(35, 0, 0, 0)),
                $"Acrylic {suffix}: hairline matches the popup baseline");
            CheckLevels(acrylic, WindowTransparencyLevel.AcrylicBlur, $"Acrylic {suffix}");
            CheckSemanticBrushes(acrylic, isDark, $"Acrylic {suffix}");
        }

        // The resolver never throws without a settings provider (headless / early startup).
        // W10: the fallback theme is the NON-GLASS baseline (OpacityLevel 1.0 → derived Solid
        // → UsesNativeBlur=false), aligned with the old plugins' catch branch that never
        // enabled native blur; 0.8 used to derive Acrylic — the opposite semantics.
        Check(PanelAppearanceResolver.ResolveEffectiveTheme(null) == PanelAppearanceResolver.FallbackTheme,
            "no provider → the fallback theme (dark, non-glass baseline)");
        Check(PanelAppearanceResolver.IsDark(PanelAppearanceResolver.FallbackTheme, ThemeVariant.Light),
            "the fallback theme is dark, like every popup's catch block");
        Check(!PanelAppearanceResolver.FallbackTheme.UsesNativeBlur,
            "the fallback theme never implies native blur (UsesNativeBlur=false, W10)");

        // The single effective-theme rule: with 失焦自动切换 OFF (or in a headless app without
        // a coordinator) every panel resolves the SHARED theme — the same theme the binder
        // paints the shell with, so the plugin content and the shell can never split states.
        var sharedTheme = ThemeOf(SurfaceStyle.LiquidGlassV2, isDark: false);
        var stubProvider = new StubEffectiveThemeSettings(new AppSettings(
            sharedTheme, [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        Check(ReferenceEquals(PanelAppearanceResolver.ResolveEffectiveTheme(stubProvider), sharedTheme),
            "focus link disabled → the shared theme");
        var focusedStateLink = new FocusLinkSettings(
            Enabled: true,
            Focused: new FocusMode(true, false),
            Unfocused: new FocusMode(false, false),
            FocusedSurface: SurfaceStyle.Colorful,
            UnfocusedSurface: SurfaceStyle.LiquidGlass);
        stubProvider.Save(stubProvider.Get() with { FocusLink = focusedStateLink });
        Check(ReferenceEquals(PanelAppearanceResolver.ResolveEffectiveTheme(stubProvider), sharedTheme),
            "focus link enabled but headless (no coordinator service) → the shared theme fallback");

        // Pure state resolution (no coordinator instance needed): the focused state carries the
        // colorful material + dark mode, the unfocused one the liquid-glass material + light mode
        // — ResolveStateTheme is what both the binder and ResolveEffectiveTheme consume.
        var focusedTheme = FocusThemeCoordinator.ResolveStateTheme(focusedStateLink, focused: true,
            new AppSettings(sharedTheme, [], new Layout(GridMode.Manual, true, false, true, false),
                new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        Check(focusedTheme.EffectiveSurface == SurfaceStyle.Colorful && focusedTheme.DarkMode == true,
            "the focused state resolves its own material + mode (colorful, dark)");
        var unfocusedTheme = FocusThemeCoordinator.ResolveStateTheme(focusedStateLink, focused: false,
            new AppSettings(sharedTheme, [], new Layout(GridMode.Manual, true, false, true, false),
                new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        Check(unfocusedTheme.EffectiveSurface == SurfaceStyle.LiquidGlass && unfocusedTheme.DarkMode == false,
            "the unfocused state resolves its own material + mode (liquid glass, light)");
    }

    /// <summary>Minimal <see cref="IAppSettingsProvider"/> stub for the effective-theme rule.</summary>
    private sealed class StubEffectiveThemeSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged { add { } remove { } }
        public AppSettings Get() => value;
        public void Save(AppSettings data) => value = data;
    }

    private static Theme ThemeOf(SurfaceStyle surface, bool isDark) => new(
        DarkMode: isDark,
        AccentColor: null,
        OpacityLevel: 0.8,
        Monochrome: false,
        UseNativeFrame: false,
        FontFamily: "Inter",
        Surface: surface);

    private static void CheckLevels(PanelAppearance appearance, WindowTransparencyLevel primary, string label)
    {
        Check(appearance.PrimaryTransparencyLevel == primary,
            $"{label}: primary hint = {primary}");
        Check(appearance.TransparencyLevels.Length == 2
              && appearance.TransparencyLevels[^1] == WindowTransparencyLevel.None,
            $"{label}: the None fallback follows the primary hint");
    }

    private static void CheckSemanticBrushes(PanelAppearance a, bool isDark, string label)
    {
        Check(IsColor(a.TitleBrush, isDark ? Colors.White : Color.FromRgb(30, 30, 30)),
            $"{label}: title brush follows the variant");
        Check(IsColor(a.IconBrush, isDark
                  ? Color.FromArgb(200, 255, 255, 255)
                  : Color.FromArgb(180, 0, 0, 0)),
            $"{label}: icon/close brush follows the variant");
    }

    private static bool IsColor(IBrush? brush, Color expected)
        => brush is ISolidColorBrush solid && solid.Color == expected;

    // ---- 2. Shell template parts under the real App.axaml ---------------------------------

    private static void CheckShellTemplateParts()
    {
        Console.WriteLine("--- SecondaryPanelShell: template parts + initial state ---");

        // A bare shell (no panel window): the ControlTheme/Style must build all six parts and
        // keep the card invisible until the panel window reveals it.
        var shell = new SecondaryPanelShell { Title = "Probe", Subtitle = "sub" };
        shell.Content = new TextBox();
        var host = new Window { Width = 420, Height = 320, SystemDecorations = SystemDecorations.None };
        host.Content = shell;
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(shell.IsTemplateApplied, "the shell template applied under the real App.axaml");
        Check(shell.Backdrop is not null, "PART_Backdrop resolves");
        Check(shell.CardBorder is not null, "PART_CardBorder resolves");
        Check(shell.GlassSurface is not null, "PART_GlassSurface resolves");
        Check(shell.HeaderDragArea is not null, "PART_HeaderDragArea resolves");
        Check(shell.CloseButton is not null, "PART_CloseButton resolves");
        Check(shell.ContentHost is not null, "PART_ContentPresenter resolves");
        Check(shell.GlassSurface is { IgnoresAncestorTransform: true },
            "PART_GlassSurface ignores ancestor render transforms (the card zoom must not shift the wallpaper sampling origin)");
        if (shell.CardBorder is { } card)
        {
            Check(card.Opacity == 0.0,
                "the card starts at Opacity=0 (a late template application cannot flash an unstyled card)");
            Check(card.CornerRadius == new CornerRadius(18),
                "the shell default radius is the shared SecondaryPanelCornerRadius (18)");
        }
        Check(shell.ContentPadding == new Thickness(16, 14, 16, 12),
            "the shell default ContentPadding is the shared token (16,14,16,12)");
        Check(shell.HeaderDragArea is Avalonia.Controls.Grid { Background: ISolidColorBrush hit }
              && hit.Color.A == 0,
            "PART_HeaderDragArea has a transparent (hit-testable) background");
        Check(shell.HeaderDragArea is not null && PanelDragBehavior.GetIsDragHandle(shell.HeaderDragArea),
            "PART_HeaderDragArea is marked as the shared W2 drag handle");
        Check(shell.CloseButton is not null && shell.CloseButton.Classes.Contains("dc-icon-button"),
            "PART_CloseButton resolves the shared dc-icon-button theme");
        Check(ReferenceEquals(shell.ContentHost?.Content, shell.Content),
            "PART_ContentPresenter presents the business content exactly once");

        // The surface appearance decomposes into the paintable parts, and only into those:
        // business slots (header content, actions, footer, content) are never touched.
        var titleBrush = new SolidColorBrush(Colors.HotPink);
        shell.TitleForeground = titleBrush;               // a plugin-specific colour…
        shell.HeaderActions = new Button { Content = "act" };
        shell.FooterContent = new TextBlock { Text = "foot" };
        var appearance = PanelAppearanceResolver.Resolve(ThemeOf(SurfaceStyle.Solid, isDark: true), true, 18);
        shell.SurfaceAppearance = appearance;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(ReferenceEquals(shell.CardBackground, appearance.CardBackground)
              && ReferenceEquals(shell.CardBorderBrush, appearance.CardBorderBrush)
              && ReferenceEquals(shell.GlassMaterial, appearance.GlassMaterial),
            "SurfaceAppearance decomposes into the card paint properties");
        Check(!ReferenceEquals(shell.TitleForeground, titleBrush),
            "a resolver refresh rewrites the semantic title brush (plugins re-apply theirs on top)");
        Check(shell.HeaderActions is not null && shell.FooterContent is not null,
            "the resolver never touches the business header/footer slots");

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 2b. Glass sampling anchor vs the card zoom ----------------------------------------

    /// <summary>
    /// The panel window asks the glass for its first frame while the card is still at the 0.6
    /// spawn scale (Loaded order: connect → request → play). The sampling origin must be the
    /// AT-REST rect — the zoom is re-parameterised per tick through SetAnimationFrameScale —
    /// so the transform-free position walk must return the same point at any card scale, while
    /// the raw PointToScreen (which composes render transforms) demonstrably would not. This is
    /// the regression test for the reported 二级面板玻璃采样错位（偏上）.
    /// </summary>
    private static void CheckGlassSamplingAnchor()
    {
        Console.WriteLine("--- PART_GlassSurface: sampling origin ignores the card zoom ---");

        var shell = new SecondaryPanelShell { Title = "Anchor" };
        shell.Content = new TextBox();
        var host = new Window { Width = 460, Height = 580, SystemDecorations = SystemDecorations.None };
        host.Content = shell;
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var surface = shell.GlassSurface!;
        var card = shell.CardBorder!;
        var walk = typeof(LiquidGlassSurface).GetMethod(
            "DesktopPositionIgnoringRenderTransforms", BindingFlags.Instance | BindingFlags.NonPublic);
        Check(walk is not null, "the transform-free position walk exists");

        PixelPoint Invoke() => walk!.Invoke(surface, Array.Empty<object?>()) is PixelPoint p ? p : default;
        PixelPoint RawPointToScreen() => surface.PointToScreen(default);

        var restRaw = RawPointToScreen();

        // The pre-animation state InitializeVisualCard arms: 0.6 around the card center.
        var spawnScale = new ScaleTransform(0.6, 0.6);
        card.RenderTransform = spawnScale;
        card.RenderTransformOrigin = RelativePoint.Center;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var zoomedRaw = RawPointToScreen();
        Check(zoomedRaw != restRaw,
            "sanity: the raw PointToScreen really shifts under the 0.6 card zoom (the bug the flag guards)");

        var zoomedWalk = Invoke();
        Check(zoomedWalk == restRaw,
            "the sampling walk returns the at-rest origin while the card is mid-zoom");

        // Any scale around any origin must stay transparent to the walk.
        card.RenderTransformOrigin = new RelativePoint(0.2, 0.9, RelativeUnit.Relative);
        spawnScale.ScaleX = spawnScale.ScaleY = 0.75;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Invoke() == restRaw, "the walk stays at-rest under a different origin and scale");

        card.RenderTransform = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(Invoke() == restRaw && RawPointToScreen() == restRaw,
            "at rest the walk and the raw path agree again");

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 3. Localized close-button name ----------------------------------------------------

    private static void CheckCloseButtonAccessibleName()
    {
        Console.WriteLine("--- PART_CloseButton: localized accessible name in both cultures ---");

        var original = CultureInfo.CurrentUICulture;
        try
        {
            foreach (var culture in new[] { new CultureInfo("zh-Hans"), new CultureInfo("en") })
            {
                CultureInfo.CurrentUICulture = culture;
                var shell = new SecondaryPanelShell();
                var host = new Window { Width = 320, Height = 240, SystemDecorations = SystemDecorations.None };
                host.Content = shell;
                host.Show();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();

                var name = shell.CloseButton is null ? null : AutomationProperties.GetName(shell.CloseButton);
                Check(!string.IsNullOrWhiteSpace(name)
                      && name == DeskCanvas.Locales.Locale.SecondaryPanel_Close,
                    $"{culture.Name}: the close button's accessible name is the localized \"Locale.SecondaryPanel_Close\" (\"{name}\")");
                Check(!string.IsNullOrWhiteSpace(ToolTip.GetTip(shell.CloseButton!) as string),
                    $"{culture.Name}: the close button carries a tooltip as well");

                host.Close();
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            }
        }
        finally
        {
            CultureInfo.CurrentUICulture = original;
        }
    }

    // ---- 4. The single close path through the base class -----------------------------------

    private sealed class TestPanelWindow : SecondaryPanelWindow
    {
        public TestPanelWindow()
        {
            SystemDecorations = SystemDecorations.None;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Width = 420;
            Height = 320;
            Content = new SecondaryPanelShell { Title = "probe" };
            InitializePanel();
        }

        protected override Point? SpawnScreenCenter => null;
    }

    private static void CheckShellClosePaths()
    {
        Console.WriteLine("--- CloseRequested collapses into the shared animated close ---");

        // NOTE: the exit animation's completion timer (180 ms + 40 ms pad) is a
        // DispatcherTimer and this headless harness runs no dispatcher loop, so the final
        // base.Close() never fires here. What IS verifiable headlessly is the routing
        // contract — exactly one CloseRequested per interaction and the base entering its
        // animated-close state (closeAnimationRunning) without closing the window on the
        // spot. The timer completion itself needs human acceptance on a real run.
        var panel = new TestPanelWindow();
        var shell = (SecondaryPanelShell)panel.Content!;
        panel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Read the REVEALED state, not the open transition's t=0 frame: the reveal assertion
        // below is about the panel having opened, so run the transition out first.
        CompleteOpenTransition(panel);

        var card = shell.CardBorder!;
        Check(card.Opacity > 0, "the panel window revealed the card after load (opacity > 0)");

        // Close-button click → CloseRequested → the base class's animated Close. Exactly one
        // request per click (the base marks the event handled, so count with handledEventsToo).
        // W10: the click runs Button's REAL OnClick path via reflection — a synthetic MouseDown
        // used to land on PART_Backdrop headlessly (the intro scale animation's clock does not
        // advance, the card stays at 60% and the hit tester misses the button), a false positive
        // for the wrong reason. Same approach as the W8 popup-migration checks.
        var closeRequests = 0;
        shell.AddHandler(
            SecondaryPanelShell.CloseRequestedEvent, (_, _) => closeRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Array.Empty<Type>(), null)
                      ?? typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RoutedEventArgs)], null)!;
        onClick.Invoke(shell.CloseButton, onClick.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : [new RoutedEventArgs(Button.ClickEvent)]);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(closeRequests == 1, $"one close-button click raises exactly one CloseRequested ({closeRequests})");
        Check(ReadField<bool>(panel, "closeAnimationRunning"),
            "the base class took over and is running the exit animation");
        Check(panel.IsVisible, "the window is still open while the exit animation plays");

        // Skip past the animation the way the real timer would: close the window through the
        // framework path (Window.Close is not virtual — this bypasses the `new` Close).
        FrameworkClose(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(!panel.IsVisible, "the framework close finished the dismissal");
        Check(!ActivePanelMap().ContainsKey(typeof(TestPanelWindow)),
            "the active-panel map dropped the closed instance");

        // A press on the in-window area outside the card (PART_Backdrop only) closes too.
        var backdropPanel = new TestPanelWindow();
        backdropPanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // The renderer's hit tester only exists after a render pass (same as the W2 checks).
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        backdropPanel.MouseDown(new Point(2, 2), MouseButton.Left);
        backdropPanel.MouseUp(new Point(2, 2), MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(ReadField<bool>(backdropPanel, "closeAnimationRunning"),
            "a press whose source really is PART_Backdrop requests the shared close");
        FrameworkClose(backdropPanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // A press INSIDE the card must not request a close.
        var insidePanel = new TestPanelWindow();
        insidePanel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var insideShell = (SecondaryPanelShell)insidePanel.Content!;
        var insideRequests = 0;
        insideShell.AddHandler(
            SecondaryPanelShell.CloseRequestedEvent, (_, _) => insideRequests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        var contentSpot = insideShell.ContentHost!.TranslatePoint(
            new Point(10, 10), insidePanel)!.Value;
        insidePanel.MouseDown(contentSpot, MouseButton.Left);
        insidePanel.MouseUp(contentSpot, MouseButton.Left);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(insideRequests == 0, $"a press inside the card never requests a close ({insideRequests})");
        Check(!ReadField<bool>(insidePanel, "closeAnimationRunning"), "the panel stays open");
        FrameworkClose(insidePanel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// Close a test panel through the Window (framework) path, bypassing the base's
    /// <c>new</c> animated Close — the headless stand-in for the exit animation's timer.
    /// </summary>
    private static void FrameworkClose(Window panel)
        => typeof(Window).GetMethod(nameof(Window.Close), Array.Empty<Type>())!
            .Invoke(panel, null);

    /// <summary>
    /// Headless stand-in for the real 320 ms open clock — the twin of
    /// <see cref="FrameworkClose"/>. Armed Transitions freeze at the current animation frame
    /// in the headless run (a property write only retargets them, it never completes them),
    /// which leaves the card mid-zoom: a real close-button click then hit-tests against the
    /// UNSCALED layout and misses (the projected center resolves to the content underneath),
    /// and mid-flight reads are wall-clock-dependent. Each forced render tick commits one
    /// ~8 ms animation step; pump until the transition has really COMPLETED (the animation is
    /// removed and the effective values land on the exact base values — a mid-flight frame
    /// never reaches 1.0 within 1e-9).
    /// </summary>
    private static void CompleteOpenTransition(Window panel)
    {
        if (panel.Content is not SecondaryPanelShell shell) return;
        for (var i = 0; i < 120; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var card = shell.CardBorder;
            var scale = ReadField<ScaleTransform?>(panel, "cardScale");
            if (card is null || scale is null) return;
            if (Math.Abs(card.Opacity - 1.0) < 1e-9
                && Math.Abs(scale.ScaleX - 1.0) < 1e-9
                && Math.Abs(scale.ScaleY - 1.0) < 1e-9)
            {
                return;
            }
            System.Threading.Thread.Sleep(10);
        }
    }

    // ---- 5. Template re-application reconnects without stacking subscriptions --------------

    private static void CheckTemplateReapplication()
    {
        Console.WriteLine("--- template re-application: reconnect, never stack handlers ---");

        var panel = new TestPanelWindow();
        var shell = (SecondaryPanelShell)panel.Content!;
        panel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        // Let the open transition really finish BEFORE the detach: the transform's armed
        // transition must be gone (base value = exact 1.0), or the re-connected transform
        // below would still read a frozen mid-zoom frame.
        CompleteOpenTransition(panel);

        var firstScale = ReadField<ScaleTransform?>(panel, "cardScale");
        Check(firstScale is not null, "the panel connected the card scale transform");
        var handlerCount = PropertyChangedHandlerCount(firstScale!);
        Check(handlerCount == 1,
            $"the initial connection attaches exactly one scale listener ({handlerCount})");

        // Force a template re-application by detaching the shell and re-attaching it: the
        // visual is rebuilt, the base must reconnect the NEW card (not early-return on the
        // same shell instance) and the old transform subscription must be released first.
        panel.Content = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        panel.Content = shell;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(shell.IsTemplateApplied, "the shell re-applied its template after re-attaching");
        Check(!ReferenceEquals(shell.CardBorder, null), "the re-applied template rebuilt the card part");
        var reconnectedScale = ReadField<ScaleTransform?>(panel, "cardScale");
        Check(reconnectedScale is not null,
            "the base reconnected the card transform after re-application");
        Check(PropertyChangedHandlerCount(reconnectedScale!) == 1,
            "the reconnect replaced (not stacked) the scale listener");
        Check(shell.CardBorder!.Opacity == 1.0,
            "a live panel's rebuilt card is revealed immediately (not stuck at Opacity=0)");
        Check(Math.Abs(reconnectedScale!.ScaleX - 1.0) < 1e-9
              && Math.Abs(reconnectedScale.ScaleY - 1.0) < 1e-9,
            "the rebuilt card sits at its final scale");

        // The shell's own part handlers must not stack either: template runs N times → a
        // close-button click raises exactly one request.
        panel.Content = null;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        panel.Content = shell;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var requests = 0;
        // The base handler marks CloseRequested handled — count with handledEventsToo.
        shell.AddHandler(SecondaryPanelShell.CloseRequestedEvent, (_, _) => requests++,
            RoutingStrategies.Bubble, handledEventsToo: true);
        // Invoke the real click path (a bare RaiseEvent(ClickEvent) only routes the event,
        // it never runs Button.OnClick). Accept either OnClick() or OnClick(e) signatures.
        var onClick = typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, Array.Empty<Type>(), null)
                      ?? typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic, null, [typeof(RoutedEventArgs)], null)!;
        onClick.Invoke(shell.CloseButton, onClick.GetParameters().Length == 0
            ? Array.Empty<object?>()
            : [new RoutedEventArgs(Button.ClickEvent)]);
        Check(requests == 1,
            $"after two template runs a close click still raises exactly one request ({requests})");

        FrameworkClose(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static int PropertyChangedHandlerCount(ScaleTransform transform)
    {
        // `cardScale.PropertyChanged += …` binds to AvaloniaObject's typed
        // PropertyChanged event (EventHandler<AvaloniaPropertyChangedEventArgs>), whose
        // backing field is `_propertyChanged` — not the INPC `_inpcChanged` field.
        var field = typeof(AvaloniaObject).GetField("_propertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("AvaloniaObject._propertyChanged backing field not found");
        return field.GetValue(transform) is Delegate d ? d.GetInvocationList().Length : 0;
    }

    // ---- 6. Lifetime: subscriptions detached, no service holds a dead panel -----------------

    private static void CheckBinderLifetime()
    {
        Console.WriteLine("--- panel lifetime: binder detaches, active map drains ---");

        // 1) Direct binder contract: a settings change refreshes while attached, nothing
        //    refreshes after Detach, and the process-wide attached count returns to zero.
        var before = PanelAppearanceBinder.ActiveCount;
        var settings = new LiveStubSettings(new AppSettings(
            new Theme(true, null, 0.8, false, false, "Inter", SurfaceStyle.LiquidGlass),
            [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));

        var shell = new SecondaryPanelShell();
        var host = new Window { Width = 300, Height = 200, SystemDecorations = SystemDecorations.None };
        host.Content = shell;
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var binder = new PanelAppearanceBinder(host, shell, settings);
        binder.Attach();
        Check(PanelAppearanceBinder.ActiveCount == before + 1,
            $"Attach registers the binder (count {before} → {PanelAppearanceBinder.ActiveCount})");
        var initial = binder.RefreshCount;
        Check(initial >= 1 && binder.Current is not null,
            "Attach resolved an initial appearance");
        Check(ReferenceEquals(binder.Current!.GlassMaterial, settings.Get().Theme),
            "the resolved appearance carries the liquid-glass material from the shared settings");

        settings.Save(settings.Get() with { });   // a settings change while open
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(binder.RefreshCount == initial + 1,
            "a settings change while the panel is open refreshes the appearance");

        var hostVariantChanges = 0;
        host.ActualThemeVariantChanged += (_, _) => hostVariantChanges++;
        host.RequestedThemeVariant = ThemeVariant.Light;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(hostVariantChanges > 0, "the variant switch was observed by the window");
        Check(binder.RefreshCount >= initial + 2,
            "a panel theme variant change refreshes the appearance too");

        binder.Detach();
        Check(PanelAppearanceBinder.ActiveCount == before,
            "Detach releases the binder registration");
        settings.Save(settings.Get() with { });
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(binder.RefreshCount == initial + 2,
            "a settings change after Detach never refreshes (subscription removed)");

        // The settings provider itself must be free of handlers after Detach.
        var dataChanged = typeof(LiveStubSettings)
            .GetField("dataChangedHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(settings) as Delegate;
        Check(dataChanged is null || dataChanged.GetInvocationList().Length == 0,
            "the stub settings provider holds no DataChanged handler after Detach");

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 2) End-to-end: a real panel window whose settings come from a provider stub —
        //    closing must unsubscribe everything and drain the active-panel map.
        Check(PanelAppearanceResolver.TryResolveSettingsProvider() is null,
            "the headless app exposes no shared settings provider (optional dependency stays null)");

        var panel = new TestPanelWindow();
        var panelShell = (SecondaryPanelShell)panel.Content!;
        panel.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(ReadStaticField("ActivePanels") is Dictionary<Type, SecondaryPanelWindow> map
              && map.ContainsKey(typeof(TestPanelWindow)),
            "an open panel is registered in the active map");
        var liveBinder = ReadField<PanelAppearanceBinder?>(panel, "appearanceBinder");
        Check(liveBinder is not null && liveBinder.IsAttached,
            "the panel window attached its appearance binder");

        FrameworkClose(panel);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Check(!ActivePanelMap().ContainsKey(typeof(TestPanelWindow)),
            "the closed panel is gone from the active map (a later open creates a fresh one)");
        Check(liveBinder is not null && !liveBinder.IsAttached,
            "the closed panel's binder is detached");
        Check(panelShell.GlassSurface is { IsVisible: false },
            "the closed panel's shell glass surface is hidden (nothing paints after the window is gone)");
    }

    private static Dictionary<Type, SecondaryPanelWindow> ActivePanelMap()
        => (Dictionary<Type, SecondaryPanelWindow>)ReadStaticField("ActivePanels")!;

    /// <summary>Like <see cref="StubSettings"/> but Save actually raises DataChanged — the
    /// shared provider's live contract the binder listens to.</summary>
    private sealed class LiveStubSettings(AppSettings value) : IAppSettingsProvider
    {
        private DataChangedEvent<AppSettings>? dataChangedHandlers;

        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged
        {
            add => dataChangedHandlers += value;
            remove => dataChangedHandlers -= value;
        }

        public AppSettings Get() => value;

        public void Save(AppSettings data)
        {
            var old = value;
            value = data;
            dataChangedHandlers?.Invoke(this, old, data);
        }
    }

    private static object? ReadStaticField(string name) => typeof(SecondaryPanelWindow)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?.GetValue(null);

    private static T? ReadField<T>(object owner, string name) => (T?)typeof(SecondaryPanelWindow)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
        ?.GetValue(owner);

    // ---- 7. The Clipboard pilot --------------------------------------------------------------

    private static void CheckClipboardMigration()
    {
        Console.WriteLine("--- Clipboard pilot: one shell, no local chrome, business intact ---");

        var popup = new ClipboardPopupWindow();
        popup.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // The window's root is the shell — not an old Border wrapper, not a second shell.
        Check(popup.Content is SecondaryPanelShell, "the window content root IS the shared shell");
        Check(popup.GetLogicalDescendants().OfType<SecondaryPanelShell>().Count() == 1,
            "exactly one shell in the tree (no shell-in-shell, no wrapped old card)");

        // No local chrome: the old names must not exist anywhere, and no LiquidGlassSurface
        // outside the shell's template (template parts are visual, not logical children).
        var names = popup.GetLogicalDescendants().OfType<Control>()
            .Select(c => c.Name).Where(n => n is not null).ToHashSet();
        Check(!names.Contains("CardBorder"), "no local CardBorder");
        Check(!names.Contains("LiquidGlassSurfaceControl"), "no local LiquidGlassSurfaceControl");
        Check(!names.Contains("CloseButton"), "no local CloseButton");
        Check(popup.GetLogicalDescendants().OfType<LiquidGlassSurface>().Count() == 0,
            "no LiquidGlassSurface in the logical tree (the shell's template part owns the glass)");

        // The window no longer overrides PanelCard: the base falls back to the shell's card.
        var panelCardGetter = typeof(ClipboardPopupWindow)
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetGetMethod(true)!;
        Check(panelCardGetter.DeclaringType == typeof(SecondaryPanelWindow),
            "Clipboard no longer overrides PanelCard (the base shell fallback is used)");
        var connectedCard = ReadField<SecondaryPanelShell?>(popup, "connectedShell")?.CardBorder;
        var panelCard = typeof(SecondaryPanelWindow)
            .GetProperty("PanelCard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(popup);
        Check(ReferenceEquals(panelCard, connectedCard) && connectedCard is not null,
            "the base shell fallback resolved the connected card at runtime");

        // Business controls survive: search, category tabs, the list and the counter badge.
        Check(popup.FindControl<TextBox>("SearchBox") is not null, "SearchBox is still there");
        Check(popup.FindControl<Button>("TabAll") is not null
              && popup.FindControl<Button>("TabFiles") is not null,
            "the category tabs are still there");
        Check(popup.FindControl<ItemsControl>("ItemsControl") is not null,
            "the history ItemsControl is still there");
        Check(popup.FindControl<TextBlock>("CountBadge") is not null,
            "the dynamic counter badge is still there");

        // The shell's header hosts the dynamic business title (title + badge).
        var shell = (SecondaryPanelShell)popup.Content!;
        Check(shell.HeaderContent is Control headerContent
              && headerContent.GetLogicalDescendants().OfType<TextBlock>()
                  .Any(t => ReferenceEquals(t, popup.FindControl<TextBlock>("CountBadge"))),
            "the dynamic title block lives in the shell's HeaderContent slot");

        FrameworkClose(popup);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
