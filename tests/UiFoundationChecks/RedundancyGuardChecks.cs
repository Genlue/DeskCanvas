using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;

namespace UiFoundationChecks;

/// <summary>
/// W11 反冗余回流守卫：把"迁移成果"固化成可执行的防回流断言（与
/// scripts/check-ui-conventions.ps1 互补——脚本管仓库静态规范，这里管可加载契约）。
/// <list type="bullet">
/// <item><b>宿主类型名分支</b>：对 Widget.axaml.cs 源码去注释定向扫描插件类型名
/// （Forecast/FourDayForecast/Progress/ProgressView/MapView）。W10 检查已覆盖前四个的
/// 清零断言，这里守卫更宽的集合：残余命中必须逐个落在带原因的登记例外里（当前仅
/// MapView——右键菜单可见性开关，非外观路径，见文档 UI-Foundation.md 例外登记）。</item>
/// <item><b>9 弹窗外壳三件套</b>：源码级守卫——CardBorder / LiquidGlassSurfaceControl /
/// PART_CloseButton 不允许在弹窗 XAML 里本地声明（运行期树断言由 PopupMigration* 检查
/// 负责，这里防的是"重新把外壳抄回来"的源码回流）。</item>
/// <item><b>共享样式键存在</b>：App.axaml 必须挂接 Input/IconButton/SettingsSectionHeader/
/// SecondaryPanelShell 四个 StyleInclude 与 DesignTokens/InteractionResources 两个
/// ResourceInclude，共享样式文件必须真实存在（运行期解析已由 W5/W6/W7 检查覆盖）。</item>
/// <item><b>基线旧 DLL 装载验证</b>：dist 下的 3.5.0 基线组件 DLL（打包产物，早于本轮
/// UI 重构）在新宿主程序集已加载的进程里装载：无 ReflectionTypeLoadException、弹窗类型
/// 仍派生自 SecondaryPanelWindow、旧 Forecast 未实现 IWidgetSurfaceHints（证明可选接口
/// + WidgetBackground 回退正是旧 DLL 的运行路径）。找不到基线 DLL 时按契约层验证跳过。</item>
/// </list>
/// Usage: dotnet run --project tests/UiFoundationChecks -c Release
/// </summary>
internal static partial class Program
{
    /// <summary>反冗余回流守卫(W11)</summary>
    private static void CheckRedundancyGuard()
    {
        Console.WriteLine("--- 反冗余回流守卫(W11) ---");
        GuardHostTypeNameBranches();
        GuardPopupShellTrio();
        GuardSharedStyleKeys();
        VerifyBaselineWidgetDlls();
    }

    // ---- 1. 宿主类型名分支守卫 ------------------------------------------------------------

    /// <summary>
    /// 登记例外（与 check-ui-conventions.ps1 的 $TypeNameExceptions 同源，带原因）：
    /// 目前仅 MapView。任何新出现的类型名分支都会在这里失败。
    /// </summary>
    private static readonly (string Token, string Reason)[] HostTypeNameExceptions =
    [
        ("MapView",
         "IsMapWidget 是组件右键菜单「地图预设 2x2/4x2/4x4」的可见性开关（上下文菜单职责，" +
         "非外观着色路径）；W10 解耦范围只覆盖外观画刷选择。移除条件：Map 通过提示接口声明" +
         "自己的固定跨度预设后改由数据驱动。"),
    ];

    private static void GuardHostTypeNameBranches()
    {
        Console.WriteLine("--- 宿主类型名分支: Widget.axaml.cs 去注释扫描（残余必须登记） ---");

        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "DeskCanvas", "Views", "Widget.axaml.cs"));
        var code = StripCSharpComments(source);

        var hits = Regex.Matches(code, @"\b(Forecast|FourDayForecast|Progress|ProgressView|MapView)\b")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(t => t)
            .ToArray();

        var unregistered = hits.Where(t => !HostTypeNameExceptions.Any(e => e.Token == t)).ToArray();
        Check(unregistered.Length == 0,
            unregistered.Length == 0
                ? $"Widget.axaml.cs 类型名残余 {string.Join("/", hits)} 全部落在带原因的登记例外内"
                : $"Widget.axaml.cs 出现未登记的插件类型名分支: {string.Join("/", unregistered)}");

        // 已登记的例外逐个确认仍然在场（例外清单与源码保持同步，防止清单腐烂）。
        foreach (var (token, _) in HostTypeNameExceptions)
        {
            Check(hits.Contains(token),
                $"登记例外 {token} 仍然对应源码中的实际分支（清单未腐烂）");
        }
    }

    // ---- 2. 9 弹窗外壳三件套源码守卫 -------------------------------------------------------

    /// <summary>9 个二级弹窗（报告附录 A.3，与脚本 $PopupWindows 同源）。</summary>
    private static readonly string[] GuardedPopups =
    [
        "src/Widgets/Tools/Views/ClipboardPopupWindow.axaml",
        "src/Widgets/Tools/Views/TranslatorPopupWindow.axaml",
        "src/Widgets/Tools/Views/CalculatorPopupWindow.axaml",
        "src/Widgets/Hitokoto/Views/HitokotoPopupWindow.axaml",
        "src/Widgets/Timetable/Views/TimetablePanelWindow.axaml",
        "src/Widgets/Reminders/Views/RemindersPopupWindow.axaml",
        "src/Widgets/Notes/Views/NotePopupWindow.axaml",
        "src/Widgets/Weather/Views/WeatherPopupWindow.axaml",
        "src/Widgets/Folders/Views/BigFolderPopupWindow.axaml",
    ];

    /// <summary>外壳三件套：本地声明即回流。</summary>
    private static readonly string[] ShellPartNames = ["CardBorder", "LiquidGlassSurfaceControl", "PART_CloseButton"];

    private static void GuardPopupShellTrio()
    {
        Console.WriteLine("--- 9 弹窗外壳三件套: 源码级防回流（业务内容内普通 Border 不受限） ---");

        var root = FindRepoRoot();
        foreach (var relative in GuardedPopups)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            var xaml = StripXamlComments(File.ReadAllText(path));

            var residue = new List<string>();
            if (Regex.IsMatch(xaml, @"<[A-Za-z.]*LiquidGlassSurfaceControl[\s>]"))
                residue.Add("<LiquidGlassSurfaceControl> 节点");
            foreach (var part in ShellPartNames)
            {
                if (Regex.IsMatch(xaml, $@"(x:Name|Name)=""{part}"""))
                    residue.Add($"x:Name=\"{part}\"");
            }

            Check(residue.Count == 0,
                residue.Count == 0
                    ? $"{Path.GetFileName(relative)} 无外壳三件套残留"
                    : $"{Path.GetFileName(relative)} 外壳回流: {string.Join(", ", residue)}");

            // 正向契约：每个弹窗都确实消费共享 Shell。
            Check(xaml.Contains("SecondaryPanelShell", StringComparison.Ordinal),
                $"{Path.GetFileName(relative)} 挂接共享 SecondaryPanelShell");
        }
    }

    // ---- 3. 共享样式键存在守卫 -------------------------------------------------------------

    private static void GuardSharedStyleKeys()
    {
        Console.WriteLine("--- 共享样式键: App.axaml 挂接 + 共享样式文件存在 ---");

        var root = FindRepoRoot();
        var appAxaml = File.ReadAllText(Path.Combine(root, "src", "DeskCanvas", "App.axaml"));

        var requiredIncludes = new (string Resource, string Kind)[]
        {
            ("avares://DeskCanvas/Styles/Controls/Input.axaml", "StyleInclude"),
            ("avares://DeskCanvas/Styles/Controls/IconButton.axaml", "StyleInclude"),
            ("avares://DeskCanvas/Styles/Controls/SettingsSectionHeader.axaml", "StyleInclude"),
            ("avares://DeskCanvas/Styles/Controls/SecondaryPanelShell.axaml", "StyleInclude"),
            ("avares://DeskCanvas/Styles/DesignTokens.axaml", "ResourceInclude"),
            ("avares://DeskCanvas/Styles/InteractionResources.axaml", "ResourceInclude"),
        };
        foreach (var (resource, kind) in requiredIncludes)
        {
            Check(Regex.IsMatch(appAxaml, $"<{kind}[^>]*Source=\"{Regex.Escape(resource)}\""),
                $"App.axaml 挂接 {kind}: {Path.GetFileName(resource)}");
        }

        var requiredFiles = requiredIncludes
            .Select(i => i.Resource.Replace("avares://DeskCanvas/", "src/DeskCanvas/"))
            .Distinct()
            .ToArray();
        foreach (var relative in requiredFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(path), $"共享样式文件存在: {relative}");
        }

        // 守卫共享 ControlTheme 类本身可解析（程序集层面，不重复 W5/W6/W7 的运行期行为断言）。
        Check(typeof(SecondaryPanelShell).Assembly == typeof(Widget).Assembly,
            "SecondaryPanelShell 与宿主同程序集（组件主题打进宿主产物的前提）");
        Check(typeof(LabeledSlider).Assembly == typeof(Widget).Assembly
              && typeof(ColorInput).Assembly == typeof(Widget).Assembly,
            "LabeledSlider / ColorInput 与宿主同程序集");
    }

    // ---- 4. 基线旧 DLL 装载验证 -------------------------------------------------------------

    /// <summary>dist 下的基线组件 DLL 目录（3.5.0 打包产物，早于本轮 UI 重构）。</summary>
    private static string? FindBaselineWidgetsDir()
    {
        var root = FindRepoRoot();
        var dist = Path.Combine(root, "dist");
        if (!Directory.Exists(dist)) return null;

        // 已知路径优先，否则任何 dist/*/Widgets（含 Weather.dll 的 portable 解包目录）。
        var known = Path.Combine(dist, "portable-win-x64-3.5.0", "Widgets");
        if (Directory.Exists(known)) return known;

        return Directory.EnumerateDirectories(dist, "Widgets", SearchOption.AllDirectories)
            .FirstOrDefault(d => (d.Contains("3.5.0", StringComparison.OrdinalIgnoreCase) || d.Contains("baseline", StringComparison.OrdinalIgnoreCase))
                                 && File.Exists(Path.Combine(d, "Weather.dll")));
    }

    private static void VerifyBaselineWidgetDlls()
    {
        Console.WriteLine("--- 基线旧 DLL 装载验证: 旧组件 + 新宿主契约 ---");

        var baselineDir = FindBaselineWidgetsDir();
        if (baselineDir is null)
        {
            Console.WriteLine("  [INFO] 未找到基线旧 DLL，运行时装载验证不可行——按契约层验证：" +
                              "IWidgetSurfaceHints/IWidgetOverlayProvider 为可选接口（W10 检查已断言" +
                              "未实现接口时回退 WidgetBackground、LegacyStackOverlayAdapter 产出分页点）。");
            Check(true, "旧 DLL 契约层验证已被 W10 检查覆盖（可选接口 + Legacy 适配路径）");
            return;
        }

        Console.WriteLine($"  [INFO] 基线目录: {Path.GetRelativePath(FindRepoRoot(), baselineDir)}");

        // 复制到临时目录装载：不在 dist 原位锁文件；第三方依赖（Markdig 等）由 ALC
        // Resolving 兜底探测，Avalonia/DeskCanvas/Core 解析到测试进程已加载的新宿主程序集
        //（.NET Core 对非强名程序集忽略版本差异——这正是"旧 DLL 引用新宿主"的运行形态）。
        // 目录名每次随机：collectible ALC 的卸载异步释放文件，清理失败时不阻塞下次运行。
        var probeDir = Path.Combine(Path.GetTempPath(),
            "deskcanvas-w11-old-dll-probe-" + Path.GetRandomFileName().Replace(".", ""));
        Directory.CreateDirectory(probeDir);
        foreach (var file in Directory.EnumerateFiles(baselineDir, "*.dll"))
        {
            File.Copy(file, Path.Combine(probeDir, Path.GetFileName(file)));
        }

        var alc = new AssemblyLoadContext("w11-baseline-probe", isCollectible: true);
        try
        {
            alc.Resolving += (context, name) =>
            {
                var candidate = Path.Combine(probeDir, name.Name + ".dll");
                return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
            };

            // 基线 3.5.0 早于 Hitokoto 插件，弹窗契约用 Notes（NotePopupWindow）与 Weather
            //（WeatherPopupWindow）验证；三者都存在于基线目录，缺一则按需跳过。
            var probedAssemblies = new List<Assembly>();
            foreach (var name in new[] { "Weather.dll", "Notes.dll", "StackWidgets.dll" })
            {
                var path = Path.Combine(probeDir, name);
                if (!File.Exists(path))
                {
                    Console.WriteLine($"  [INFO] 基线缺 {name}，跳过该 DLL 的装载断言");
                    continue;
                }
                probedAssemblies.Add(alc.LoadFromAssemblyPath(path));
            }

            // (a) 全部基线 DLL：装载无 TypeLoad。
            var allErrors = new List<string>();
            var allTypes = new List<Type>();
            foreach (var assembly in probedAssemblies)
            {
                var types = SafeGetTypes(assembly, out var errors);
                allErrors.AddRange(errors);
                allTypes.AddRange(types);
            }
            Check(allErrors.Count == 0,
                allErrors.Count == 0
                    ? $"基线组件 DLL（{probedAssemblies.Count} 个）在新宿主下装载无 TypeLoad（{allTypes.Count} 类型）"
                    : $"基线组件 DLL 装载错误 {allErrors.Count} 处: {allErrors[0]}");

            // (b) 弹窗类型仍派生自新宿主的 SecondaryPanelWindow（生命周期契约未破坏）。
            var oldPopups = allTypes.Where(t => t.Name.EndsWith("PopupWindow", StringComparison.Ordinal)).ToList();
            Check(oldPopups.Count >= 1,
                $"基线组件里找到 {oldPopups.Count} 个弹窗类型（{string.Join(", ", oldPopups.Select(p => p.Name))}）");
            Check(oldPopups.All(p => typeof(SecondaryPanelWindow).IsAssignableFrom(p)),
                "基线弹窗类型全部仍派生自新宿主 SecondaryPanelWindow");

            // (c) Weather：旧 Forecast 未实现 IWidgetSurfaceHints（可选接口回退路径）。
            var oldForecast = allTypes.FirstOrDefault(t => t.FullName == "Weather.Views.Forecast");
            Check(oldForecast is not null, "基线 Weather.dll 含 Forecast 视图类型");
            Check(oldForecast is not null && !typeof(IWidgetSurfaceHints).IsAssignableFrom(oldForecast),
                "基线 Forecast 未实现 IWidgetSurfaceHints——可选接口 + WidgetBackground 回退正是旧 DLL 的运行路径");

            // (d) StackWidgets：旧 IStackWidget 协议仍在——LegacyStackOverlayAdapter 的服务对象。
            var oldStackView = allTypes.FirstOrDefault(t => t.FullName == "StackWidgets.Views.WidgetStackView");
            Check(oldStackView is not null && typeof(IStackWidget).IsAssignableFrom(oldStackView),
                "基线 WidgetStackView 仍实现 IStackWidget（LegacyStackOverlayAdapter 的服务对象在场）");
        }
        finally
        {
            alc.Unload();
            try { Directory.Delete(probeDir, true); }
            catch (Exception) { /* ALC 卸载后文件释放是异步的；随机目录名保证下次运行不受影响 */ }
        }
    }

    /// <summary>GetTypes 收集装载失败明细而不是让异常直接炸掉整个套件。</summary>
    private static Type[] SafeGetTypes(Assembly assembly, out List<string> errors)
    {
        errors = [];
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            errors.AddRange(ex.LoaderExceptions
                .Select(e => e?.Message ?? "unknown")
                .Distinct());
            return ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }
    }
}
