using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using StackWidgets.Models;
using StackWidgets.Views;
using StackWidgets.Views.Controls;
using Grid = DeskCanvas.Core.Models.Settings.Grid;
using WeatherForecast = Weather.Views.Forecast;
using WeatherFourDay = Weather.Views.FourDayForecast;
using ProgressGauge = Progress.Views.ProgressView;
using ClockAnalogI = Clock.Views.AnalogI;
using ClockAnalogIV = Clock.Views.AnalogIV;
using BatteriesWidgetView = Batteries.Views.BatteriesView;
using MonitorMultiDashboard = Monitor.Views.MultiDashboard;
using MonitorSingleMetric = Monitor.Views.SingleMetric;
using StorageWidgetView = Storage.Views.StorageView;
using MusicWidget = Music.Views.Music;
using PomodoroWidgetView = Pomodoro.Views.PomodoroView;
using HitokotoWidgetView = Hitokoto.Views.HitokotoView;
using NoteWidgetView = Notes.Views.Note;
using CalendarDate = Calendar.Views.Date;
using CalendarMonth = Calendar.Views.Month;
using ClockDigital = Clock.Views.Digital;
using ClockAnalogIII = Clock.Views.AnalogIII;
using ClockFlip = Clock.Views.FlipClock;
using TimetableView = Timetable.Views.Timetable;

namespace UiFoundationChecks;

/// <summary>
/// W10 宿主与插件解耦检查：
/// <list type="bullet">
/// <item><b>源码契约</b>：宿主 Widget 外观路径不再出现 Weather/Progress 类型名字符串，
/// Widget.axaml 无 StackIndicatorsHost 专属 XAML 残留（注释除外）。</item>
/// <item><b>IWidgetSurfaceHints</b>：Weather/Progress/表盘-1/表盘-2/表盘-3 视图声明各自资源键；
/// stub 内容实现接口时，宿主 Colorful 路径按明暗变体解析到声明键，未实现接口（或键解析不到）
/// 回退 WidgetBackground；表盘-1 经真实宿主解析到恒黑卡片（ClockCardBackground = #1C1C1E），
/// 表盘-2 与全新表盘-3 解析到亮色白卡、暗色黑卡（ClockAnalogIIICardBackground / ClockAnalogIVCardBackground）。</item>
/// <item><b>Stack 装饰下沉</b>：StackIndicatorStrip 位于 Stack 插件、绑定 IStackWidget 数据，
/// AutoHide 与滚轮/点击切页走新控件；宿主经 IWidgetOverlayProvider 消费；装饰单实例（引用相等）。</item>
/// <item><b>布局提示契约</b>：outside-card 右缘空间、原生 Acrylic 内移、Region 合并提示的
/// 字段与宿主实际放置行为。</item>
/// <item><b>Legacy 路径</b>：只实现 IStackWidget 的内容经 LegacyStackOverlayAdapter 仍产出分页点。</item>
/// <item><b>中键+滚轮手势</b>：经 headless 真实输入管道端到端保留。</item>
/// </list>
/// 人工验收（headless 做不了，不做假断言）：真实 Windows HWND Region/DWM 模糊下圆点条不被裁掉、
/// 不被糊成毛玻璃（Acrylic 实测）；旧版插件 DLL（未随包重新解包）在真机上的分页点回归。
/// </summary>
internal static partial class Program
{
    /// <summary>宿主与插件解耦 Weather/Progress 外观提示、Stack 装饰下沉(W10)</summary>
    private static void CheckHostDecoupling()
    {
        Console.WriteLine("--- 宿主与插件解耦 Weather/Progress 外观提示、Stack 装饰下沉(W10) ---");

        CheckHostAppearanceSourceContract();
        CheckSurfaceHintsDeclarations();
        CheckHostColorfulHintResolution();
        CheckStackIndicatorStripMigration();
        CheckOverlayLayoutHintsContract();
        CheckLegacyStackAdapterPath();
        CheckStackChordGesturePreserved();
    }

    // ---- 1. 源码契约：宿主无类型名外观分支、无 Stack 专属 XAML -------------------------------

    private static void CheckHostAppearanceSourceContract()
    {
        Console.WriteLine("--- 源码契约: 宿主外观路径无类型名分支 / 无 StackIndicatorsHost 残留 ---");

        var root = FindRepoRoot();
        var widgetCs = StripCSharpComments(File.ReadAllText(Path.Combine(root, "src", "DeskCanvas", "Views", "Widget.axaml.cs")));
        var widgetAxaml = StripXamlComments(File.ReadAllText(Path.Combine(root, "src", "DeskCanvas", "Views", "Widget.axaml")));

        // 外观路径的类型名字符串（Weather/Progress 两组）必须清零（注释除外）。
        var banned = Regex.Matches(widgetCs, @"\b(Forecast|FourDayForecast|Progress|ProgressView)\b");
        Check(banned.Count == 0,
            banned.Count == 0
                ? "Widget.axaml.cs（去注释后）不再出现 Forecast/FourDayForecast/Progress/ProgressView 类型名字符串"
                : $"Widget.axaml.cs 仍出现类型名字符串 {banned.Count} 处: " +
                  string.Join(" | ", banned.Select(m => $"L{m.Index}")));
        Check(!widgetAxaml.Contains("StackIndicatorsHost", StringComparison.Ordinal),
            "Widget.axaml 不再有 StackIndicatorsHost 专属 XAML 残留");
        Check(!widgetAxaml.Contains("OnStackIndicatorClicked", StringComparison.Ordinal),
            "Widget.axaml 不再挂接圆点点击宿主处理器");
    }

    private static string StripCSharpComments(string text) => Regex.Replace(
        Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline), @"//[^\r\n]*", " ");

    private static string StripXamlComments(string text) =>
        Regex.Replace(text, @"<!--.*?-->", " ", RegexOptions.Singleline);

    // ---- 2. IWidgetSurfaceHints 声明 ---------------------------------------------------------

    private static void CheckSurfaceHintsDeclarations()
    {
        Console.WriteLine("--- IWidgetSurfaceHints: Weather/Progress 视图声明正确的资源键 ---");

        var forecast = new WeatherForecast();
        Check(forecast is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "WeatherCardBackground" },
            $"Forecast implements IWidgetSurfaceHints with WeatherCardBackground ({(forecast as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var fourDay = new WeatherFourDay();
        Check(fourDay is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "WeatherCardBackground" },
            $"FourDayForecast implements IWidgetSurfaceHints with WeatherCardBackground ({(fourDay as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var progress = new ProgressGauge();
        Check(progress is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "ProgressCardBackground" },
            $"ProgressView implements IWidgetSurfaceHints with ProgressCardBackground ({(progress as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        // 表盘-1 / 表盘-3：多彩主题下的恒黑卡片（复刻 Apple 时钟小组件亮暗皆黑的卡片）。
        var analogI = new ClockAnalogI();
        Check(analogI is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "ClockCardBackground" },
            $"AnalogI (表盘-1) implements IWidgetSurfaceHints with ClockCardBackground ({(analogI as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var analogIV = new ClockAnalogIV();
        Check(analogIV is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "ClockAnalogIVCardBackground" },
            $"AnalogIV (表盘-3) implements IWidgetSurfaceHints with ClockAnalogIVCardBackground ({(analogIV as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var batteries = new BatteriesWidgetView();
        Check(batteries is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "BatteriesCardBackground" },
            $"BatteriesView implements IWidgetSurfaceHints with BatteriesCardBackground ({(batteries as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var monitorMulti = new MonitorMultiDashboard();
        Check(monitorMulti is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "MonitorCardBackground" },
            $"MultiDashboard implements IWidgetSurfaceHints with MonitorCardBackground ({(monitorMulti as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var monitorSingle = new MonitorSingleMetric();
        Check(monitorSingle is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "MonitorCardBackground" },
            $"SingleMetric implements IWidgetSurfaceHints with MonitorCardBackground ({(monitorSingle as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var storage = new StorageWidgetView();
        Check(storage is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "StorageCardBackground" },
            $"StorageView implements IWidgetSurfaceHints with StorageCardBackground ({(storage as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var music = new MusicWidget();
        Check(music is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "MusicCardBackground" },
            $"Music implements IWidgetSurfaceHints with MusicCardBackground ({(music as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var pomodoro = new PomodoroWidgetView();
        Check(pomodoro is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "PomodoroCardBackground" },
            $"PomodoroView implements IWidgetSurfaceHints with PomodoroCardBackground ({(pomodoro as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var hitokoto = new HitokotoWidgetView();
        Check(hitokoto is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "HitokotoCardBackground" },
            $"HitokotoView implements IWidgetSurfaceHints with HitokotoCardBackground ({(hitokoto as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var note = (NoteWidgetView)RuntimeHelpers.GetUninitializedObject(typeof(NoteWidgetView));
        Check(note is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "NotesCardBackground" },
            $"Note implements IWidgetSurfaceHints with NotesCardBackground ({(note as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var calDate = (CalendarDate)RuntimeHelpers.GetUninitializedObject(typeof(CalendarDate));
        Check(calDate is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "CalendarCardBackground" },
            $"Calendar Date implements IWidgetSurfaceHints with CalendarCardBackground ({(calDate as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var calMonth = (CalendarMonth)RuntimeHelpers.GetUninitializedObject(typeof(CalendarMonth));
        Check(calMonth is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "CalendarCardBackground" },
            $"Calendar Month implements IWidgetSurfaceHints with CalendarCardBackground ({(calMonth as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var digitalClock = (ClockDigital)RuntimeHelpers.GetUninitializedObject(typeof(ClockDigital));
        Check(digitalClock is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "ClockDigitalCardBackground" },
            $"Clock Digital implements IWidgetSurfaceHints with ClockDigitalCardBackground ({(digitalClock as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var analogIII = (ClockAnalogIII)RuntimeHelpers.GetUninitializedObject(typeof(ClockAnalogIII));
        Check(analogIII is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "ClockAnalogIIICardBackground" },
            $"Clock AnalogIII implements IWidgetSurfaceHints with ClockAnalogIIICardBackground ({(analogIII as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var flipClock = (ClockFlip)RuntimeHelpers.GetUninitializedObject(typeof(ClockFlip));
        Check(flipClock is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "FlipClockCardBackground" },
            $"Clock FlipClock implements IWidgetSurfaceHints with FlipClockCardBackground ({(flipClock as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");

        var timetable = (TimetableView)RuntimeHelpers.GetUninitializedObject(typeof(TimetableView));
        Check(timetable is IWidgetSurfaceHints { ColorfulBackgroundResourceKey: "TimetableCardBackground" },
            $"Timetable implements IWidgetSurfaceHints with TimetableCardBackground ({(timetable as IWidgetSurfaceHints)?.ColorfulBackgroundResourceKey})");
    }

    // ---- 3. 宿主 Colorful 路径按变体解析声明键 / 通用回退 -------------------------------------

    private static void CheckHostColorfulHintResolution()
    {
        Console.WriteLine("--- 宿主 Colorful 外观: 按变体解析声明键, 未声明回退 WidgetBackground ---");

        // 先把全局主题字典切到 Colorful：早前的 W1 检查把应用级主题字典切到了单色，
        // 这里的变体解析必须针对真实的 Colorful 主题字典断言。
        var colorful = ColorfulSettings().Theme;
        var themeService = new ThemeService(new StubSettings(ColorfulSettings()), new WallpaperThemeService());
        themeService.Apply(colorful);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 声明了 "WeatherCardBackground" 的 stub：light/dark 各解析到该变体的渐变。
        var light = BuildDecoupledWidget(() => new HintedContent(), ColorfulTheme());
        light.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(light.WidgetCardBackground is LinearGradientBrush lightGradient
              && lightGradient.GradientStops[0].Color == Color.Parse("#1B71BE"),
            $"light Colorful: hinted stub resolves the declared WeatherCardBackground gradient ({DescribeBrush(light.WidgetCardBackground)})");
        Check(!(light.WidgetCardBackground is ISolidColorBrush lightSolid && lightSolid.Color == Colors.White),
            "light Colorful: hinted resolution is NOT the WidgetBackground fallback");
        light.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var dark = BuildDecoupledWidget(() => new HintedContent(), ColorfulTheme());
        dark.RequestedThemeVariant = ThemeVariant.Dark;
        dark.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(dark.WidgetCardBackground is LinearGradientBrush darkGradient
              && darkGradient.GradientStops[0].Color == Color.Parse("#121E31"),
            $"dark Colorful: hinted stub resolves the DARK WeatherCardBackground gradient ({DescribeBrush(dark.WidgetCardBackground)})");
        dark.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 未实现接口的内容：回退 WidgetBackground（Colorful light = 纯白）。
        var plain = BuildDecoupledWidget(() => new PlainContent(), ColorfulTheme());
        plain.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(plain.WidgetCardBackground is ISolidColorBrush { Color: { } plainColor } && plainColor == Colors.White,
            $"light Colorful: plain content (no interface) falls back to WidgetBackground ({DescribeBrush(plain.WidgetCardBackground)})");
        plain.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 实现了接口但键解析不到：同样退回 WidgetBackground。
        var bogus = BuildDecoupledWidget(() => new BogusKeyContent(), ColorfulTheme());
        bogus.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(bogus.WidgetCardBackground is ISolidColorBrush { Color: { } bogusColor } && bogusColor == Colors.White,
            $"light Colorful: an unresolvable declared key falls back to WidgetBackground ({DescribeBrush(bogus.WidgetCardBackground)})");
        bogus.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 表盘-1：ClockCardBackground 在亮暗两档都解析到 #1C1C1E —— 多彩主题下的
        // 恒黑卡片（内圈有白盘）。
        var charcoal = Color.Parse("#1C1C1E");
        void CheckDialCardIsCharcoal(string label, Func<UserControl> make, ThemeVariant variant)
        {
            var host = BuildDecoupledWidget(make, ColorfulTheme());
            host.RequestedThemeVariant = variant;
            host.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(host.WidgetCardBackground is ISolidColorBrush { Color: { } dialCardColor }
                  && dialCardColor == charcoal,
                $"{label} {(variant == ThemeVariant.Dark ? "dark" : "light")}: Colorful card resolves the constant charcoal #1C1C1E ({DescribeBrush(host.WidgetCardBackground)})");
            host.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        CheckDialCardIsCharcoal("AnalogI (表盘-1)", () => new ClockAnalogI(), ThemeVariant.Light);
        CheckDialCardIsCharcoal("AnalogI (表盘-1)", () => new ClockAnalogI(), ThemeVariant.Dark);

        void CheckAnalogIVCard(ThemeVariant variant, Color expectedColor)
        {
            var host = BuildDecoupledWidget(() => new ClockAnalogIV(), ColorfulTheme());
            host.RequestedThemeVariant = variant;
            host.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(host.WidgetCardBackground is ISolidColorBrush { Color: { } cardColor }
                  && cardColor == expectedColor,
                $"AnalogIV (表盘-3) {(variant == ThemeVariant.Dark ? "dark" : "light")}: Colorful card resolves {expectedColor} ({DescribeBrush(host.WidgetCardBackground)})");
            host.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        CheckAnalogIVCard(ThemeVariant.Light, Colors.White);
        CheckAnalogIVCard(ThemeVariant.Dark, charcoal);

        var flipLightGray = Color.Parse("#EFEFF4");
        void CheckFlipClockCard(ThemeVariant variant, Color expectedColor)
        {
            var host = BuildDecoupledWidget(() => new FlipClockHintedContent(), ColorfulTheme());
            host.RequestedThemeVariant = variant;
            host.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(host.WidgetCardBackground is ISolidColorBrush { Color: { } cardColor }
                  && cardColor == expectedColor,
                $"FlipClock {(variant == ThemeVariant.Dark ? "dark" : "light")}: Colorful card resolves {expectedColor} ({DescribeBrush(host.WidgetCardBackground)})");
            host.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        CheckFlipClockCard(ThemeVariant.Light, flipLightGray);
        CheckFlipClockCard(ThemeVariant.Dark, charcoal);

        void CheckAnalogIIICard(ThemeVariant variant, Color expectedColor)
        {
            var host = BuildDecoupledWidget(() => new ClockAnalogIII(), ColorfulTheme());
            host.RequestedThemeVariant = variant;
            host.Show();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(host.WidgetCardBackground is ISolidColorBrush { Color: { } cardColor }
                  && cardColor == expectedColor,
                $"AnalogIII (表盘-2) {(variant == ThemeVariant.Dark ? "dark" : "light")}: Colorful card resolves {expectedColor} ({DescribeBrush(host.WidgetCardBackground)})");
            host.Close();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }

        CheckAnalogIIICard(ThemeVariant.Light, Colors.White);
        CheckAnalogIIICard(ThemeVariant.Dark, charcoal);
    }

    private static string DescribeBrush(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => $"solid {solid.Color}",
        LinearGradientBrush gradient => $"gradient {gradient.GradientStops.FirstOrDefault()?.Color}",
        null => "null",
        _ => brush.GetType().Name,
    };

    private sealed class HintedContent : UserControl, IWidgetSurfaceHints
    {
        public string? ColorfulBackgroundResourceKey => "WeatherCardBackground";
    }

    private sealed class FlipClockHintedContent : UserControl, IWidgetSurfaceHints
    {
        public string? ColorfulBackgroundResourceKey => "FlipClockCardBackground";
    }

    private sealed class BogusKeyContent : UserControl, IWidgetSurfaceHints
    {
        public string? ColorfulBackgroundResourceKey => "NoSuchBrushAnywhere";
    }

    private sealed class PlainContent : UserControl { }

    // ---- 4. Stack 装饰下沉: StackIndicatorStrip 绑定协议数据 + 单实例约束 ----------------------

    private static void CheckStackIndicatorStripMigration()
    {
        Console.WriteLine("--- Stack 装饰下沉: StackIndicatorStrip 绑定 IStackWidget + 单实例 ---");

        var model = new WidgetStackModel(
        [
            new StackedWidgetEntry("Weather", "Forecast", "weather"),
            new StackedWidgetEntry("Weather", "FourDayForecast", "four-day"),
            new StackedWidgetEntry("Progress", "ProgressView", "progress"),
        ]);
        var view = new WidgetStackView(model, new StubWidgetLayoutProvider(), new StubAssemblyProvider());
        var provider = (IWidgetOverlayProvider)view;

        // 单实例约束：同一实例重复取用不新建（引用相等）。
        var overlay1 = provider.Overlay;
        var overlay2 = provider.Overlay;
        Check(overlay1 is StackIndicatorStrip,
            $"WidgetStackView provides its decoration through StackIndicatorStrip ({overlay1?.GetType().Name})");
        Check(ReferenceEquals(overlay1, overlay2), "repeated provider.Overlay access returns the SAME instance");
        var strip = (StackIndicatorStrip)overlay1!;
        Check(ReferenceEquals(strip.BoundStack, view), "the strip is bound to its own IStackWidget (the view)");

        // 绑定既有 IStackWidget 数据：3 个指示项 → 可见、圆点数正确。
        Check(strip.IsVisible, "the strip is visible with more than one indicator item");
        var dots = strip.FindControl<ItemsControl>("DotsHost")!;
        Check((dots.ItemsSource as IReadOnlyList<StackWidgetIndicatorItem>)?.Count == 3,
            $"the strip binds the 3 IndicatorItems from IStackWidget ({(dots.ItemsSource as IReadOnlyList<StackWidgetIndicatorItem>)?.Count})");
        Check(!ReferenceEquals(strip, view), "the strip is a distinct control, not the view itself");

        // 挂进真实宿主：OverlayHost 承载同一实例；重复取用不重建。
        var host = BuildDecoupledWidget(() => view, SolidTheme());
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var overlayHost = OverlayHostOf(host);
        Check(ReferenceEquals(overlayHost.Content, strip),
            "the host's OverlayHost presents the provider's very strip instance");
        InvokePrivate(host, "AttachOverlay");
        Check(ReferenceEquals(overlayHost.Content, strip),
            "a repeated host attach keeps the same overlay instance (never rebuilt per access)");

        // 常态隐藏默认关闭：非 AutoHide 时圆点常显。
        Check(strip.Opacity == 1, $"AutoHideIndicators=false keeps the dots visible (opacity {strip.Opacity})");

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // AutoHideIndicators 走新控件：空闲隐藏 → 指针悬停显现（真实输入管道）。
        // 注：迁移前的“过渡期间显现”与“悬停显现”共用同一 Reveal 谓词（IsTransitionActive ||
        // pointer-over）；过渡路径需要真实子卡片与推进的 dispatcher 时钟（stub 装配下
        // SwitchToIndex 安全早退），headless 无法驱动，属人工验收。空闲 600ms 停留后的隐藏
        // 同样依赖 DispatcherTimer，留待真机验收。
        var autoHideModel = model with { AutoHideDots = true };
        var autoHideView = new WidgetStackView(autoHideModel, new StubWidgetLayoutProvider(), new StubAssemblyProvider());
        var autoHideStrip = (StackIndicatorStrip)((IWidgetOverlayProvider)autoHideView).Overlay!;
        Check(autoHideStrip.Opacity == 0, $"AutoHideIndicators=true keeps the dots hidden while idle (opacity {autoHideStrip.Opacity})");

        var revealHost = BuildDecoupledWidget(() => autoHideView, SolidTheme(), wrapPositionProxy: false);
        revealHost.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var revealStrip = (StackIndicatorStrip)OverlayHostOf(revealHost).Content!;
        var revealOrigin = revealStrip.TranslatePoint(default, revealHost) ?? default;
        revealHost.MouseMove(new Point(
            Math.Min(revealHost.Width - 2, revealOrigin.X + revealStrip.Bounds.Width / 2),
            Math.Min(revealHost.Height - 2, revealOrigin.Y + revealStrip.Bounds.Height / 2)));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(revealStrip.Opacity == 1,
            $"hovering the widget reveals the auto-hidden dots through the new control (opacity {revealStrip.Opacity})");
        revealHost.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 5. 布局提示契约: 放置 / 右缘空间 / Acrylic 内移 / Region 合并 -------------------------

    private static void CheckOverlayLayoutHintsContract()
    {
        Console.WriteLine("--- 布局提示契约: outside-card 右缘 / Acrylic 内移 / Region 合并 ---");

        // Stack 提供方的提示字段：outside-card + 右缘空间 + 内移 + 重新布局 + 隐藏可命中。
        var view = new WidgetStackView(
            new WidgetStackModel([new StackedWidgetEntry("Weather", "Forecast", "w"), new StackedWidgetEntry("Notes", "Note", "n")]),
            new StubWidgetLayoutProvider(), new StubAssemblyProvider());
        var hints = ((IWidgetOverlayProvider)view).OverlayHints;        Check(hints.Placement == WidgetOverlayPlacement.OutsideCardRight,
            $"stack decoration declares OutsideCardRight placement ({hints.Placement})");
        Check(hints.RightReservedWidth > 0,
            $"the decoration reserves right-edge space ({hints.RightReservedWidth} DIP)");
        Check(hints.InsetOnNativeBlur, "the decoration requires the native-acrylic inset");
        Check(hints.RelayoutOnBoundsChange, "the decoration requires relayout on widget move/resize");
        Check(hints.HitTestableWhenHidden, "the decoration stays hit-testable while hidden (Opacity reveal)");
        Check(!hints.MergeBoundsIntoWindowRegion,
            "the decoration relies on the inset (the card rect covers it) instead of a region merge");

        // 宿主实际放置行为：毛玻璃(UsesNativeBlur)下内移 → 右边距 = WidgetMargin.Right + 1。
        // 每个宿主用全新的 view 实例：同一装饰控件不可被多个窗口共享为 visual child。
        var acrylicHost = BuildDecoupledWidget(MakeStackHintsView, AcrylicTheme());
        acrylicHost.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var acrylicMargin = OverlayHostOf(acrylicHost).Margin;
        Check(acrylicMargin.Right == acrylicHost.WidgetMargin.Right + 1 && acrylicMargin.Left == 0,
            $"native blur: the host insets the overlay into the card (margin {acrylicMargin}, card right {acrylicHost.WidgetMargin.Right})");
        acrylicHost.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 非玻璃表面：outside-card → 右缘 1 DIP（网格线边距内）。
        var solidHost = BuildDecoupledWidget(MakeStackHintsView, SolidTheme());
        solidHost.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var solidMargin = OverlayHostOf(solidHost).Margin;
        Check(solidMargin.Right == 1 && solidMargin.Left == 0,
            $"non-blur surface: the overlay sits outside the card, 1 DIP off the grid line (margin {solidMargin})");
        solidHost.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // Region 合并提示：单一 GDI 圆角矩形无法 OR 第二个矩形 —— 声明了合并但仍停在卡片外的
        // 装饰，宿主在原生模糊裁剪下回退为内移放置，保证装饰不被 HWND Region 裁掉（报告 §13）。
        var mergeContent = new MergeHintContent();
        var mergeHost = BuildDecoupledWidget(() => mergeContent, AcrylicTheme());
        mergeHost.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var mergeOverlay = OverlayHostOf(mergeHost).Content;
        Check(mergeOverlay is Border mergeBorder && ReferenceEquals(mergeBorder, mergeContent.Overlay),
            "the host presents a custom provider's overlay control");
        Check(OverlayHostOf(mergeHost).Margin.Right == mergeHost.WidgetMargin.Right + 1,
            $"a merge-hinting out-of-card overlay is INSET under native-blur clipping instead of being clipped away (margin {OverlayHostOf(mergeHost).Margin})");
        mergeHost.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 非玻璃表面下同样的合并提示：维持 outside-card（无 Region 裁剪问题）。
        var mergeSolidHost = BuildDecoupledWidget(() => new MergeHintContent(), SolidTheme());
        mergeSolidHost.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(OverlayHostOf(mergeSolidHost).Margin.Right == 1,
            $"without native-blur clipping the merge-hinting overlay stays outside the card (margin {OverlayHostOf(mergeSolidHost).Margin})");
        mergeSolidHost.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    private static WidgetStackView MakeStackHintsView() => new(
        new WidgetStackModel([new StackedWidgetEntry("Weather", "Forecast", "w"), new StackedWidgetEntry("Notes", "Note", "n")]),
        new StubWidgetLayoutProvider(), new StubAssemblyProvider());

    private sealed class MergeHintContent : UserControl, IWidgetOverlayProvider
    {
        public Control? Overlay { get; } = new Border { Width = 8, Height = 8, Background = Brushes.Transparent };

        public WidgetOverlayHints OverlayHints { get; } = new()
        {
            Placement = WidgetOverlayPlacement.OutsideCardRight,
            InsetOnNativeBlur = false,
            MergeBoundsIntoWindowRegion = true,
        };
    }

    // ---- 6. Legacy 路径: 只实现 IStackWidget 的内容经适配器仍产出分页点 ------------------------

    private static void CheckLegacyStackAdapterPath()
    {
        Console.WriteLine("--- Legacy 路径: 旧 IStackWidget 经 LegacyStackOverlayAdapter 保留分页点 ---");

        var legacy = new LegacyStackContent();
        var host = BuildDecoupledWidget(() => legacy, SolidTheme());
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        var overlayHost = OverlayHostOf(host);
        Check(overlayHost.Content is LegacyStackIndicatorStrip,
            $"a content that only implements IStackWidget gets the host-side legacy strip ({overlayHost.Content?.GetType().Name})");
        Check(!ReferenceEquals(overlayHost.Content, legacy), "the content itself is not abused as the overlay");

        var strip = (LegacyStackIndicatorStrip)overlayHost.Content!;
        Check(strip.IsVisible, "the legacy strip is visible (3 indicator items)");
        var dots = (StackPanel)typeof(LegacyStackIndicatorStrip)
            .GetField("dots", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(strip)!;
        Check(dots.Children.Count == 3,
            $"the legacy strip rebuilds the 3 dots from IStackWidget.IndicatorItems ({dots.Children.Count})");
        Check(strip.Opacity == 1, "the legacy strip is visible for an always-show stack (AutoHide default false)");

        // 单实例约束同样适用于适配器。
        var adapter = new LegacyStackOverlayAdapter(legacy);
        Check(ReferenceEquals(adapter.Overlay, adapter.Overlay), "the legacy adapter returns the same strip instance on repeated access");

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// 旧版组件 DLL 的替身：只实现 IStackWidget（旧协议），不实现 IWidgetOverlayProvider
    /// 与 IWidgetSurfaceHints —— 正是已部署 bundle 未重新解包时的形状。
    /// </summary>
    private sealed class LegacyStackContent : UserControl, IStackWidget
    {
        public int CurrentIndex { get; private set; }
        public int SwitchCalls { get; private set; }

        // 指示项随 CurrentIndex 动态生成：切页手势（含中键和弦）依赖 IsActive 指向当前页。
        public IReadOnlyList<StackWidgetIndicatorItem> IndicatorItems =>
            [.. Enumerable.Range(0, 3).Select(i =>
                new StackWidgetIndicatorItem(i, $"page-{i}", i == CurrentIndex, i == CurrentIndex ? 1.0 : 0.3))];

        public bool IsTransitionActive => false;
        public bool AutoHideIndicators => false;
        public bool CanEditCurrentChild => false;
        public string? CurrentChildTitle => null;
        public void EditCurrentChild(object? ownerWindow = null) { }
        public event EventHandler? IndicatorItemsChanged;

        public void SwitchToIndex(int index)
        {
            SwitchCalls++;
            CurrentIndex = index;
            IndicatorItemsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    // ---- 7. 中键+滚轮切页手势保留（headless 真实输入管道端到端） --------------------------------

    private static void CheckStackChordGesturePreserved()
    {
        Console.WriteLine("--- 中键+滚轮切页: 经真实输入管道端到端保留 ---");

        var legacy = new LegacyStackContent();
        var host = BuildDecoupledWidget(() => legacy, SolidTheme(), wrapPositionProxy: false);
        host.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        var strip = (LegacyStackIndicatorStrip)OverlayHostOf(host).Content!;
        var bodyPoint = new Point(host.Width / 2, host.Height / 2);
        var stripOrigin = strip.TranslatePoint(default, host) ?? default;
        var stripPoint = new Point(
            Math.Min(host.Width - 2, stripOrigin.X + strip.Bounds.Width / 2),
            Math.Min(host.Height - 2, stripOrigin.Y + strip.Bounds.Height / 2));

        // 圆点条上的滚轮切页（无需中键）：向下滚 → 下一页。
        host.MouseWheel(stripPoint, new Vector(0, -120), RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(legacy.CurrentIndex == 1,
            $"a plain wheel over the dots strip pages forward (index {legacy.CurrentIndex})");

        // 中键按住 + 卡片主体上滚轮（隧道手势）：下一页。
        host.MouseDown(bodyPoint, MouseButton.Middle);
        host.MouseWheel(bodyPoint, new Vector(0, -120), RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(legacy.CurrentIndex == 2,
            $"the middle-button chord pages forward from the card body (index {legacy.CurrentIndex})");
        host.MouseUp(bodyPoint, MouseButton.Middle);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        // 中键释放后，主体上的滚轮不再切页（只有圆点条自己的滚轮处理器接手）。
        host.MouseWheel(bodyPoint, new Vector(0, -120), RawInputModifiers.None);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Check(legacy.CurrentIndex == 2,
            $"a wheel over the body without the chord does not page (index stays {legacy.CurrentIndex})");

        // 点击圆点切页（真实 Button 点击路径）。
        var dotButtons = strip.GetVisualDescendants().OfType<Button>().ToList();
        Check(dotButtons.Count == 3, $"the legacy strip builds 3 dot buttons ({dotButtons.Count})");
        if (dotButtons.Count == 3)
        {
            var dotCenter = dotButtons[0].TranslatePoint(
                new Point(dotButtons[0].Bounds.Width / 2, dotButtons[0].Bounds.Height / 2), host)!.Value;
            host.MouseDown(dotCenter, MouseButton.Left);
            host.MouseUp(dotCenter, MouseButton.Left);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Check(legacy.CurrentIndex == 0,
                $"clicking a dot switches to that child (index {legacy.CurrentIndex})");
        }

        host.Close();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    // ---- 共享 rig（Widget 宿主窗口，与 DragCommitChecks/WidgetAdaptiveChecks 同一套缝合法） ----

    private static ContentPresenter OverlayHostOf(Widget widget) =>
        (ContentPresenter)typeof(Widget).GetField("OverlayHost", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(widget)!;

    private static void InvokePrivate(object owner, string name) =>
        typeof(Widget).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!
            .Invoke(owner, null);

    private static AppSettings ColorfulSettings() => new(
        new Theme(false, null, 1.0, false, false, "Inter", SurfaceStyle.Colorful),
        [], new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null);

    private static IAppSettingsProvider ColorfulTheme() => new StubSettings(ColorfulSettings());

    private static IAppSettingsProvider AcrylicTheme() => new StubSettings(new AppSettings(
        new Theme(false, null, 0.8, false, false, "Inter", SurfaceStyle.Acrylic),
        [], new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));

    private static IAppSettingsProvider SolidTheme() => new StubSettings(new AppSettings(
        new Theme(false, null, 1.0, false, false, "Inter", SurfaceStyle.FullColor),
        [], new Layout(GridMode.Manual, true, false, true, false),
        new Dimensions(80, 12, 16), new Region("zh-Hans"), false, null));

    private static Widget BuildDecoupledWidget(Func<UserControl> content, IAppSettingsProvider settings, bool wrapPositionProxy = true)
    {
        var entry = new WidgetLayout("Test", "StubContent", 500, 333, 384, 384, null);
        var screenConfig = new ScreenLayout("screen-a", "A|1920x1080", null, null, new Grid(8, 6, 0, 0, 10), null, [entry]);
        var layout = new LayoutStore(new ScreensLayout([screenConfig]));
        var monitor = new DisplayMonitorService(layout);
        // 固定 anchor/refreshing：headless 下禁止真实拓扑观察翻转检查状态。
        typeof(DisplayMonitorService).GetField("anchor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, new Window());
        typeof(DisplayMonitorService).GetField("refreshing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(monitor, true);
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.Attached))!.SetValue(monitor,
            new List<AttachedScreen>
            {
                new(new Screen(1, new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040), true),
                    new ScreenIdentity("A", "A", 1920, 1080, true, 1), screenConfig)
            });
        typeof(DisplayMonitorService).GetProperty(nameof(DisplayMonitorService.IsTopologyChanging))!
            .SetValue(monitor, false);

        var provider = new WidgetLayoutProvider(layout, screenConfig.Id, entry);
        var profiles = (ProfileService)RuntimeHelpers.GetUninitializedObject(typeof(ProfileService));
        var widget = new Widget(settings, provider, new GridService(settings, monitor), layout, monitor, profiles,
            content, () => throw new InvalidOperationException());
        // headless 没有 HWND：OnActivated/OnOpened 里的原生互操作（Alt-Tab 移除、
        // 显示器 attach、Region 裁剪）必须摘除。交互检查（MouseWheel/MouseMove 扩展
        // 要求 headless 原生 PlatformImpl）不包 Position 代理；它们不触碰 Position/Move。
        DetachHeadlessLifecycleHandlers(widget);
        if (wrapPositionProxy) MakePositionAware(widget);
        return widget;
    }

    private static void DetachHeadlessLifecycleHandlers(Widget widget)
    {
        var activated = typeof(Widget).GetMethod("OnActivated", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        widget.Activated -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), widget, activated);
        var opened = typeof(Widget).GetMethod("OnOpened", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)!;
        widget.Opened -= (EventHandler)Delegate.CreateDelegate(typeof(EventHandler), widget, opened);
    }

    private sealed class LayoutStore(ScreensLayout initial) : ILayoutProvider
    {
        private ScreensLayout value = initial;
        public event DataChangedEvent<ScreensLayout>? DataChanging;
        public event DataChangedEvent<ScreensLayout>? DataChanged;
        public ScreensLayout Get() => value;
        public void Save(ScreensLayout next) { var old = value; DataChanging?.Invoke(this, old, next); value = next; DataChanged?.Invoke(this, old, next); }
    }

    private sealed class StubAssemblyProvider : IAssemblyProvider
    {
        // 指示项检查只需要圆点数据；子卡片装配失败会被 WidgetStackView 安全吞掉。
        public ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath) =>
            Array.Empty<AssemblyInfo>().ToLookup(_ => "");
        public Assembly LoadAssembly(string assemblyName) => null!;
        public void UnloadAssembly(string assemblyName) { }
        public object Activate(Type type, params object[] args) => null!;
        public System.Resources.ResourceManager? GetLocaleResourceManager(Assembly assembly) => null;
    }

    private static void MakePositionAware(Widget widget)
    {
        // Avalonia.Headless 的 Move 不保留坐标：除缺失的 move 缝合外全部转发给真实平台实现。
        var impl = widget.PlatformImpl!;
        var proxy = DispatchProxy.Create<IWindowImpl, PositionAwareWindow>();
        ((PositionAwareWindow)(object)proxy).Target = impl;
        for (var type = typeof(Widget); type != null; type = type.BaseType)
            foreach (var field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                if (ReferenceEquals(field.GetValue(widget), impl)) field.SetValue(widget, proxy);
    }

    public class PositionAwareWindow : DispatchProxy
    {
        public IWindowImpl Target = null!;
        private PixelPoint position;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method!.Name == "get_Position") return position;
            if (method.Name == "Move")
            {
                position = (PixelPoint)args![0]!;
                var changed = (Action<PixelPoint>?)typeof(IWindowBaseImpl)
                    .GetProperty("PositionChanged", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Target);
                changed?.Invoke(position);
                return null;
            }
            try { return method.Invoke(Target, args); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }
    }
}
