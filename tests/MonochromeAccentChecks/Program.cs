using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.ViewModels;

namespace MonochromeAccentChecks;

/// <summary>
/// Pins the 单色 accent contracts through the real ThemeService + the real Accent /
/// MonochromeBlackWhite / MonochromeBackground style dictionaries:
/// <list type="bullet">
/// <item><b>黑白</b>: the accent is strictly achromatic — <b>black in light mode, white in
/// dark mode</b> — while the text family stays pure black/white;</item>
/// <item><b>背景色</b>: text AND accent lock to the inverted background — light mode renders
/// the dark-mode background color, dark mode the light-mode one;</item>
/// <item><b>强调色</b>: the accent stays the picked color (no inversion, no locking).</item>
/// </list>
/// Uses non-default background colors on purpose (#112233 vs #FEDCBA), so any mix-up between
/// the variants shows up as a swapped or tinted value instead of two equal whites.
/// </summary>
internal class Program
{
    private static int failed;

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {message}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {message}");
            failed++;
        }
    }

    private static Color Resource(string key) =>
        (Color)(Application.Current!.Resources[key]
            ?? throw new InvalidOperationException($"Resource {key} is missing"));

    /// <summary>
    /// Variant-scoped brush lookup through the real style chain (same path the widgets use).
    /// The probe control lives inside a real (never shown) Window: a fully detached control's
    /// resource walk does not reach the Application.Styles theme dictionaries.
    /// </summary>
    private static readonly Window ProbeWindow = new();

    private static SolidColorBrush BrushResource(string key, ThemeVariant variant)
    {
        var probe = new Control();
        ProbeWindow.Content = probe;
        if (!probe.TryFindResource(key, variant, out var value) || value is not SolidColorBrush brush)
            throw new InvalidOperationException($"{key} ({variant}) did not resolve to a brush");
        return brush;
    }

    private static Color BrushOf(string key, ThemeVariant variant) =>
        BrushResource(key, variant).Color;

    private static double OpacityOf(string key, ThemeVariant variant) =>
        BrushResource(key, variant).Opacity;

    public static int Main(string[] args)
    {
        // Portable probe data stays beside this test executable, away from user profiles.
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(AppContext.BaseDirectory, "Widgets"));
        AppBuilder.Configure<ProbeApp>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        Console.WriteLine("=== Monochrome Accent Checks ===");

        var settings = new StubSettings(new AppSettings(
            new Theme(false, "#007AFF", 1, false, false, "Inter", SurfaceStyle.Solid),
            [], new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));
        var themeService = new ThemeService(settings, new WallpaperThemeService());
        Assert(DeskCanvas.App.Services is null, "Probe skips host startup and user settings services");

        // Non-default background colors make any variant mix-up obvious (#112233 vs #FEDCBA).
        const string darkBg = "#112233";
        const string lightBg = "#FEDCBA";
        var darkBgColor = Color.Parse(darkBg);
        var lightBgColor = Color.Parse(lightBg);

        static Theme Monochrome(MonochromeStyle variant, bool? darkMode, string darkBackground, string lightBackground, string? accentColor = null) => new(
            DarkMode: darkMode,
            AccentColor: accentColor,
            OpacityLevel: 1.0,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid,
            SolidBackgroundDark: darkBackground,
            SolidBackgroundLight: lightBackground,
            MonochromeVariant: variant);

        Console.WriteLine("\n--- 黑白, dark mode (explicit): accent = white, text = white ---");
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, true, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == Colors.White,
            "SystemAccentColor == white (imperative readers)");
        Assert(Resource("SystemAccentColorDark1") == Colors.Black && Resource("SystemAccentColorDark2") == Colors.Black,
            "Dark ramp (Dark1/2) == black — feeds the LIGHT dictionaries' accent");
        Assert(Resource("SystemAccentColorLight1") == Colors.White && Resource("SystemAccentColorLight2") == Colors.White,
            "Light ramp (Light1/2) == white — feeds the DARK dictionaries' accent");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == Colors.White,
            "Dark-variant accent brush == white");
        Assert(BrushOf("CalendarHeaderBrush", ThemeVariant.Dark) == Colors.White,
            "Dark-variant semantic accent brush (CalendarHeaderBrush) == white");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Dark) == Colors.White,
            "Text family stays pure white (dark variant)");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Light) == Colors.Black,
            "Text family stays pure black (light variant)");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Dark) == Colors.Black,
            "Dark-variant AccentContrastForegroundBrush == black (readable on the white accent)");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Light) == Colors.White,
            "Light-variant AccentContrastForegroundBrush == white (readable on the black accent)");

        Console.WriteLine("\n--- 黑白, light mode (explicit): accent = black, text = black ---");
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, false, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == Colors.Black,
            "SystemAccentColor == black (imperative readers)");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Light) == Colors.Black,
            "Light-variant accent brush == black");

        Console.WriteLine("\n--- A hand-picked accent color must NOT leak through 黑白 ---");
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, true, darkBg, lightBg, accentColor: "#FF0000"));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == Colors.White,
            "Dark-variant accent brush still == white (the red accent is ignored)");
        Assert(Resource("SystemAccentColor") == Colors.White,
            "SystemAccentColor still == white (the red accent is ignored)");

        Console.WriteLine("\n--- 背景色, dark mode (explicit): text AND accent = LIGHT-mode background ---");
        themeService.Apply(Monochrome(MonochromeStyle.BackgroundColor, true, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == lightBgColor,
            $"SystemAccentColor == light-mode bg {lightBg} (got {Resource("SystemAccentColor")})");
        Assert(Resource("SystemAccentColorLight1") == lightBgColor && Resource("SystemAccentColorLight2") == lightBgColor,
            "Light ramp (SystemAccentColorLight1/2) == light-mode bg");
        Assert(Resource("SystemAccentColorDark1") == darkBgColor,
            "Dark ramp (SystemAccentColorDark1) == dark-mode bg");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush == light-mode bg");
        Assert(BrushOf("CalendarHeaderBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant semantic accent brush (CalendarHeaderBrush) == light-mode bg");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Dark) == lightBgColor,
            "Text family locks to the inverted background (dark variant)");
        Assert(BrushOf("NotesTextBrush", ThemeVariant.Dark) == lightBgColor,
            "Notes text locks to the inverted background (dark variant)");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Light) == darkBgColor,
            "Text family locks to the inverted background (light variant)");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Dark) == Colors.Black,
            "Dark-variant AccentContrastForegroundBrush == black (readable on the light-ish accent)");
        Assert(BrushOf("AccentContrastForegroundBrush", ThemeVariant.Light) == Colors.White,
            "Light-variant AccentContrastForegroundBrush == white (readable on the dark-ish accent)");

        Console.WriteLine("\n--- 背景色, light mode (explicit): text AND accent = DARK-mode background ---");
        themeService.Apply(Monochrome(MonochromeStyle.BackgroundColor, false, darkBg, lightBg));

        Assert(Resource("SystemAccentColor") == darkBgColor,
            $"SystemAccentColor == dark-mode bg {darkBg} (got {Resource("SystemAccentColor")})");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant accent brush == dark-mode bg");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant text brush == dark-mode bg");

        Console.WriteLine("\n--- A hand-picked accent color must NOT leak through 背景色 ---");
        themeService.Apply(Monochrome(MonochromeStyle.BackgroundColor, true, darkBg, lightBg, accentColor: "#FF0000"));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush still == light-mode bg (the red accent is ignored)");
        Assert(Resource("SystemAccentColor") == lightBgColor,
            "SystemAccentColor still == light-mode bg (the red accent is ignored)");

        Console.WriteLine("\n--- 强调色 variant stays accent-based (no inversion, no locking) ---");
        themeService.Apply(new Theme(
            DarkMode: true,
            AccentColor: "#FF0000",
            OpacityLevel: 1.0,
            Monochrome: true,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid,
            MonochromeVariant: MonochromeStyle.Accent));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == Colors.Red,
            "Dark-variant accent brush == the picked accent (red)");
        Assert(BrushOf("SystemControlForegroundBaseHighBrush", ThemeVariant.Dark) == Colors.Red,
            "Dark-variant text brush == the accent too (text becomes the accent)");

        Console.WriteLine("\n--- Follow-system (DarkMode = null, 背景色): per-variant brushes stay correct ---");
        themeService.Apply(Monochrome(MonochromeStyle.BackgroundColor, null, darkBg, lightBg));

        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Dark) == lightBgColor,
            "Dark-variant accent brush == light-mode bg");
        Assert(BrushOf("SystemControlForegroundAccentBrush", ThemeVariant.Light) == darkBgColor,
            "Light-variant accent brush == dark-mode bg");
        Console.WriteLine($"  [INFO] Application.ActualThemeVariant under follow-system: {Application.Current!.ActualThemeVariant}");
        Console.WriteLine($"  [INFO] Base SystemAccentColor picked: {Resource("SystemAccentColor")} (dark bg {darkBgColor} / light bg {lightBgColor})");

        Console.WriteLine("\n--- Widget-style lookups (the way imperative widget code MUST resolve) ---");
        // The variant-less TryFindResource overload resolves theme dictionaries against
        // ThemeVariant.Default — i.e. it ALWAYS returns the LIGHT dictionary's brush, even
        // while the widget renders dark. The widgets therefore resolve with the variant
        // explicitly (Month.ResolveAccentBrush, AggregateViewModel.ResolveAccentBrush,
        // AnalogWorldSingle.BuildTicks, MarkdownRenderer.ResolveThemed). 背景色 is used here
        // because its two variants carry clearly different colors (#112233 vs #FEDCBA);
        // the variant-less result is reported as INFO.
        themeService.Apply(Monochrome(MonochromeStyle.BackgroundColor, true, darkBg, lightBg));

        var hostWindow = new Window { RequestedThemeVariant = ThemeVariant.Dark, Width = 10, Height = 10 };
        var attached = new Control();
        hostWindow.Content = attached;
        Console.WriteLine($"  [INFO] attached probe.ActualThemeVariant = {attached.ActualThemeVariant}");
        Console.WriteLine($"  [INFO] variant-less TryFindResource (the trap) -> " +
            (attached.TryFindResource("CalendarTodayBrush", out var trap) && trap is SolidColorBrush t ? t.Color.ToString() : "(unresolved)"));

        Assert(attached.TryFindResource("CalendarTodayBrush", attached.ActualThemeVariant, out var darkDot)
               && darkDot is SolidColorBrush ddb && ddb.Color == lightBgColor,
            "Attached-in-dark-window variant-aware lookup of CalendarTodayBrush == light-mode bg");
        Assert(((IResourceHost)Application.Current!).TryGetResource("CalendarTodayBrush", Application.Current.ActualThemeVariant, out var appLevel)
               && appLevel is SolidColorBrush alb && alb.Color == lightBgColor,
            "Application-host variant-aware lookup of CalendarTodayBrush == light-mode bg");
        Assert(((IResourceHost)Application.Current!).TryGetResource("SystemControlForegroundBaseHighBrush", Application.Current.ActualThemeVariant, out var baseHigh)
               && baseHigh is SolidColorBrush bhb && bhb.Color == lightBgColor,
            "Application-host variant-aware lookup of SystemControlForegroundBaseHighBrush == light-mode bg (text locked)");

        var lightWindow = new Window { RequestedThemeVariant = ThemeVariant.Light, Width = 10, Height = 10 };
        var attachedLight = new Control();
        lightWindow.Content = attachedLight;
        Assert(attachedLight.TryFindResource("CalendarTodayBrush", attachedLight.ActualThemeVariant, out var lightDot)
               && lightDot is SolidColorBrush ldb && ldb.Color == darkBgColor,
            "Attached-in-light-window variant-aware lookup of CalendarTodayBrush == dark-mode bg");

        // 黑白 keeps its per-variant lookups achromatic through the same dictionaries.
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, true, darkBg, lightBg));
        Assert(hostWindow.Content is Control blackProbe
               && blackProbe.TryFindResource("CalendarTodayBrush", blackProbe.ActualThemeVariant, out var blackDot)
               && blackDot is SolidColorBrush bdb && bdb.Color == Colors.White,
            "Attached-in-dark-window lookup of CalendarTodayBrush == white in 黑白");

        // --- 单色关闭必须把强调色渐变交还出去（不得残留黑白档的纯白/纯黑） ---
        // 回归场景：暗色 + 黑白 → 关掉黑白 → "全局元素阴影"开关(ON)的圆点由黑变白却看不见。
        // 根因不在圆点，而在轨道取色源 SystemAccentColor 还停在黑白写下的纯白：渐变键活在
        // Application.Resources 里、跨 Apply 存活，所以"什么都不写"等于保留上一次的值。
        Console.WriteLine("\n--- 单色关闭：强调色渐变必须归还（不得残留） ---");
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, true, darkBg, lightBg));
        Assert(Resource("SystemAccentColor") == Colors.White,
            "前置：黑白已把 SystemAccentColor 覆写成纯白");

        themeService.Apply(new Theme(
            DarkMode: true,
            AccentColor: null,
            OpacityLevel: 1.0,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid));

        Assert(!Application.Current!.Resources.ContainsKey("SystemAccentColor"),
            "关闭单色后 SystemAccentColor 的覆写已移除（归还 Fluent/系统强调色）");
        Assert(!Application.Current.Resources.ContainsKey("SystemAccentColorLight1")
               && !Application.Current.Resources.ContainsKey("SystemAccentColorDark1"),
            "关闭单色后整个 Light/Dark 渐变覆写同步移除（不残留半套）");

        var restoredTrack = BrushOf("SystemControlHighlightAccentBrush", ThemeVariant.Dark);
        var restoredKnob = BrushOf("AccentContrastForegroundBrush", ThemeVariant.Dark);
        Assert(restoredTrack != Colors.White,
            $"关闭单色后 ON 轨道取色源不再是残留的纯白（got {restoredTrack}）");
        Assert(restoredTrack != restoredKnob,
            $"关闭单色后 ON 轨道 ({restoredTrack}) != 圆点对比色 ({restoredKnob}) — 开关可见");

        // 切回亮色同理（用户场景 3：关掉黑白后切亮色，开关全白看不见）。
        var restoredTrackLight = BrushOf("SystemControlHighlightAccentBrush", ThemeVariant.Light);
        var restoredKnobLight = BrushOf("AccentContrastForegroundBrush", ThemeVariant.Light);
        Assert(restoredTrackLight != restoredKnobLight,
            $"（亮色）关闭单色后 ON 轨道 ({restoredTrackLight}) != 圆点对比色 ({restoredKnobLight}) — 开关可见");
        Assert(BrushOf("ToggleSwitchFillOnPointerOver", ThemeVariant.Light) != restoredKnobLight,
            "（亮色）关闭单色后 hover 的 ON 轨道 != 圆点对比色 — hover 不再变白");

        Console.WriteLine("\n--- 单色关闭：手选强调色必须回来（而不是留在黑白） ---");
        themeService.Apply(Monochrome(MonochromeStyle.BlackWhite, true, darkBg, lightBg));
        themeService.Apply(new Theme(
            DarkMode: true,
            AccentColor: "#FF0000",
            OpacityLevel: 1.0,
            Monochrome: false,
            UseNativeFrame: false,
            FontFamily: "Inter",
            Surface: SurfaceStyle.Solid));
        Assert(Resource("SystemAccentColor") == Colors.Red,
            "关闭单色且手选强调色 → SystemAccentColor == 手选红（黑白残留被覆盖）");
        Assert(BrushOf("SystemControlHighlightAccentBrush", ThemeVariant.Dark) == Colors.Red,
            "关闭单色后 ON 轨道取色源 == 手选红");

        // --- Fluent 选中态控件的可读性契约（开关圆点白上白的修复） ---
        // ON 轨道取色源 SystemControlHighlightAccentBrush = SystemAccentColor 基键；
        // 圆点经 App.axaml 部件样式改绑 AccentContrastForegroundBrush。四种单色态下
        // 两者必须是不同颜色——同色即"轨道圆点白成一片"回归。
        Console.WriteLine("\n--- Fluent selection-control glyph contrast (ToggleSwitch/CheckBox/RadioButton) ---");
        foreach (var (variantName, monoVariant, dark) in new[]
        {
            ("黑白 dark", MonochromeStyle.BlackWhite, true),
            ("黑白 light", MonochromeStyle.BlackWhite, false),
            ("背景色 dark", MonochromeStyle.BackgroundColor, true),
            ("背景色 light", MonochromeStyle.BackgroundColor, false),
        })
        {
            themeService.Apply(Monochrome(monoVariant, dark, darkBg, lightBg));
            var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            var track = BrushOf("SystemControlHighlightAccentBrush", variant);
            var glyph = BrushOf("AccentContrastForegroundBrush", variant);
            Assert(track != glyph, $"{variantName}: ON track ({track}) != knob/glyph ({glyph}) — readable");

            // hover/pressed 必须停在同一极性上。Fluent 把这两个键硬接在
            // SystemAccentColorLight1 / Dark1 上，而本应用的渐变是交叉写入的：单色档下
            // "亮一点的强调色"就是反极性的纯色，hover 一下轨道与圆点撞成一片（亮色黑白：
            // 黑轨道 hover 变白 + 白圆点 = 开关消失）。
            var hover = BrushOf("ToggleSwitchFillOnPointerOver", variant);
            var pressed = BrushOf("ToggleSwitchFillOnPressed", variant);
            Assert(hover != glyph,
                $"{variantName}: hovered ON track ({hover}) != knob/glyph ({glyph}) — readable");
            Assert(pressed != glyph,
                $"{variantName}: pressed ON track ({pressed}) != knob/glyph ({glyph}) — readable");
            Assert(OpacityOf("ToggleSwitchFillOnPointerOver", variant) < 1.0,
                $"{variantName}: hovered ON track keeps a feedback opacity");
            Assert(OpacityOf("ToggleSwitchFillOnPressed", variant)
                   < OpacityOf("ToggleSwitchFillOnPointerOver", variant),
                $"{variantName}: pressed ON track is deeper than hovered");

            // 复选/单选的选中底色同族（同一批键、同一根因），一并对齐。
            Assert(BrushOf("CheckBoxCheckBackgroundFillCheckedPointerOver", variant) != glyph,
                $"{variantName}: hovered checked CheckBox background != glyph — readable");
            Assert(BrushOf("RadioButtonOuterEllipseCheckedFillPointerOver", variant) != glyph,
                $"{variantName}: hovered checked RadioButton fill != glyph — readable");

            // 模板级断言（暗色两档即可，模板化路径相同）：真实 ToggleSwitch 挂进窗口、
            // 应用模板后读圆点的 Fill，证明 App.axaml 的部件样式真的压过了 Fluent
            // 模板的 ToggleSwitchKnobFillOn 资源，而不是只改了某个中间资源键。
            if (dark)
            {
                hostWindow.Content = new ToggleSwitch { IsChecked = true };
                hostWindow.Show();
                var toggle = (ToggleSwitch)hostWindow.Content!;
                var knob = toggle.GetVisualDescendants()
                    .OfType<Avalonia.Controls.Shapes.Ellipse>()
                    .FirstOrDefault(e => e.Name == "SwitchKnobOn");
                Assert(knob != null, $"{variantName}: ToggleSwitch SwitchKnobOn part found");
                if (knob != null)
                {
                    var knobColor = knob.Fill is SolidColorBrush kb ? kb.Color : default(Color?);
                    Assert(knobColor == glyph,
                        $"{variantName}: templated SwitchKnobOn fill == {glyph} (got {knobColor})");
                }
                hostWindow.Hide();
                hostWindow.Content = null;
            }
        }

        Console.WriteLine("\n--- Real Profiles accent-button templates ---");
        var profileService = new ProfileService(settings, new LayoutProvider(), themeService,
            new LocaleService(settings), null!, null!);
        var profilesPage = new DeskCanvas.Views.Pages.Profiles(profileService);
        var profilesViewModel = (ProfilesViewModel)profilesPage.DataContext!;
        profilesViewModel.Profiles.Clear();
        profilesViewModel.Profiles.Add(new ProfileItemViewModel("Button contrast probe", false, 2));
        hostWindow.Width = 1200;
        hostWindow.Height = 400;
        hostWindow.Position = new PixelPoint(-30000, 100);
        hostWindow.Content = profilesPage;

        foreach (var (variantName, monoVariant, dark) in new[]
        {
            ("BlackWhite dark", (MonochromeStyle?)MonochromeStyle.BlackWhite, true),
            ("BlackWhite light", (MonochromeStyle?)MonochromeStyle.BlackWhite, false),
            ("BackgroundColor dark", (MonochromeStyle?)MonochromeStyle.BackgroundColor, true),
            ("BackgroundColor light", (MonochromeStyle?)MonochromeStyle.BackgroundColor, false),
            ("Restored accent dark", (MonochromeStyle?)null, true),
            ("Restored accent light", (MonochromeStyle?)null, false),
        })
        {
            themeService.Apply(monoVariant is { } monochrome
                ? Monochrome(monochrome, dark, darkBg, lightBg)
                : settings.Get().Theme with { DarkMode = dark, Monochrome = false, AccentColor = "#007AFF" });
            hostWindow.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            hostWindow.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var buttons = profilesPage.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Classes.Contains("accent") && button.IsVisible).ToArray();
            Assert(buttons.Length == 2, $"{variantName}: New and Switch accent buttons are templated");
            foreach (var button in buttons)
            {
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>()
                    .Single(part => part.Name == "PART_ContentPresenter");
                foreach (var (state, opacity) in new[]
                {
                    ("normal", 1.0),
                    ("hover", 0.85),
                    ("pressed", 0.7),
                    ("disabled", 1.0),
                })
                {
                    var pseudoClasses = (IPseudoClasses)button.Classes;
                    pseudoClasses.Set(":pointerover", state is "hover" or "pressed");
                    pseudoClasses.Set(":pressed", state == "pressed");
                    button.IsEnabled = state != "disabled";
                    Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                    var foreground = presenter.Foreground as SolidColorBrush;
                    var background = presenter.Background as SolidColorBrush;
                    Assert(foreground != null && background != null,
                        $"{variantName}/{button.Content}/{state}: presenter brushes resolved");
                    if (foreground == null || background == null) continue;
                    Assert(foreground.Color != background.Color || foreground.Opacity != background.Opacity,
                        $"{variantName}/{button.Content}/{state}: actual foreground/background remain distinct");
                    if (state == "disabled")
                    {
                        button.TryFindResource("AccentButtonForegroundDisabled", button.ActualThemeVariant, out var disabledForeground);
                        button.TryFindResource("AccentButtonBackgroundDisabled", button.ActualThemeVariant, out var disabledBackground);
                        Assert(foreground.Equals(disabledForeground) && background.Equals(disabledBackground),
                            $"{variantName}/{button.Content}: disabled retains Fluent muted brushes");
                    }
                    else
                    {
                        var expectedForeground = BrushOf("AccentContrastForegroundBrush", button.ActualThemeVariant);
                        Assert(foreground.Color == expectedForeground && background.Color == Resource("SystemAccentColor")
                               && Math.Abs(background.Opacity - opacity) < 0.001,
                            $"{variantName}/{button.Content}/{state}: template uses theme contrast and state feedback");
                    }
                }
                button.IsEnabled = true;
                ((IPseudoClasses)button.Classes).Set(":pointerover", false);
                ((IPseudoClasses)button.Classes).Set(":pressed", false);
            }
            hostWindow.Hide();
        }
        hostWindow.Content = null;

        if (failed == 0)
        {
            Console.WriteLine("\nALL CHECKS PASSED!");
        }
        else
        {
            Console.WriteLine($"\n{failed} CHECK(S) FAILED!");
        }

        return failed;
    }

    private sealed class StubSettings(AppSettings value) : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging { add { } remove { } }
        public event DataChangedEvent<AppSettings>? DataChanged { add { } remove { } }
        public AppSettings Get() => value;
        public void Save(AppSettings data) => value = data;
    }

    private sealed class ProbeApp : DeskCanvas.App
    {
        public override void OnFrameworkInitializationCompleted() { }
    }
}
