using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using DeskCanvas.Services;
using DeskCanvas.WinUI.Host;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// 内置组件程序集登记表与预加载器（报告 §3 第一步 5/6、§0.2）。
/// <para>
/// 新入口用 ProjectReference 引入了 src/Widgets 下的 26 个组件工程（报告 §3 第一步 5 的限定例外）。
/// 于是同一份 Notes/Clock 在进程里可能有两个来源：GUI 引用的 exe 目录 DLL，和
/// <c>DeskCanvas.Core.Services.AssemblyProvider</c> 从 <c>Const.WidgetsFolder</c> 扫描后用插件上下文加载的 DLL。
/// 两个 <see cref="Assembly"/> 实例意味着 <see cref="Type"/> 不相等——组件里的 static 状态、
/// DI 注册、WinRT marshaller 都会分裂。
/// </para>
/// <para>
/// 解决办法只有一步：把随版本发布的组件程序集<b>提前加载到 <see cref="System.Runtime.Loader.AssemblyLoadContext.Default"/>
/// 上下文</b>。之后 <c>AssemblyProvider.LoadAssembly</c> 会先命中 Default，内置包只剩一份实例；
/// 第三方包不在 Default 里，继续走原来的可收集插件上下文。
/// </para>
/// <para>
/// 这里<b>只</b>用 <c>Assembly.Load</c>：不 new 任何组件类型、不读组件设置、不碰 Avalonia/WinUI 对象。
/// 加载程序集只把元数据带进进程，不会运行组件里的静态构造函数。
/// </para>
/// </summary>
public static class WidgetAssemblyRegistry
{
    private static readonly object Gate = new();

    /// <summary>
    /// 内置组件程序集名（有序、去重）：与 src/Widgets 下的 26 个组件工程一一对应，顺序是工程名的字母序。
    /// <para>
    /// 这里<b>是程序集名</b>，不是工程目录名。<c>Fixed</c>/<c>Stack</c> 两个工程的 AssemblyName 分别是
    /// <c>FixedWidgets</c>/<c>StackWidgets</c>（layout.json 的 <c>Type</c> 和 AssemblyProvider 的查找键
    /// 都用程序集名）。新增组件工程时同步改这里和 <c>DeskCanvas.WinUI.csproj</c> 的 ProjectReference。
    /// </para>
    /// </summary>
    private static readonly string[] builtInNames =
    [
        "AudioVisualizer",
        "Batteries",
        "Calendar",
        "Clock",
        "ControlCenter",
        "Countdown",
        "FixedWidgets", // 工程 src/Widgets/Fixed，AssemblyName=FixedWidgets
        "Folders",
        "Habits",
        "Hitokoto",
        "Map",
        "Monitor",
        "Music",
        "Notes",
        "Picture",
        "Pomodoro",
        "Progress",
        "Reminders",
        "Search",
        "Shortcuts",
        "StackWidgets", // 工程 src/Widgets/Stack，AssemblyName=StackWidgets
        "Storage",
        "TickClock",
        "Timetable",
        "Tools",
        "Weather",
    ];

    private static readonly HashSet<string> loaded = new(StringComparer.Ordinal);
    private static readonly HashSet<string> failed = new(StringComparer.Ordinal);
    private static readonly HashSet<string> originLogged = new(StringComparer.Ordinal);
    private static bool stageLogged;

    /// <summary>内置组件程序集名（有序、去重）。<see cref="Preload"/> 只加载这些名字。</summary>
    public static IReadOnlyList<string> BuiltInAssemblyNames => builtInNames;

    /// <summary>当前进程里已经进入 Default 上下文的内置程序集名（按登记顺序）。</summary>
    public static IReadOnlyList<string> LoadedAssemblyNames => Snapshot(loaded);

    /// <summary>预加载失败的内置程序集名（按登记顺序）。失败只记录，不抛异常。</summary>
    public static IReadOnlyList<string> FailedAssemblyNames => Snapshot(failed);

    /// <summary>26 个内置程序集是否都已进入 Default 上下文。</summary>
    public static bool IsFullyLoaded
    {
        get
        {
            lock (Gate) return loaded.Count == builtInNames.Length;
        }
    }

    /// <summary>
    /// 把内置组件程序集加载到 Default 上下文。
    /// <para>
    /// <b>幂等、可重复调用</b>：已经加载的名字直接跳过（因此第二次调用只是 26 次 O(Default 程序集数)
    /// 的名字查找）；失败的名字下次调用会重试。单个程序集加载失败只写日志并记录
    /// <see cref="FailedAssemblyNames"/>，绝不让启动失败——少一个组件包也好过整个软件起不来。
    /// </para>
    /// <para>
    /// 宿主在构建 DI 之前调用一次即可（App.xaml.cs）。另外 <see cref="PreloadOnModuleLoad"/> 会在
    /// 入口程序集加载时兜底调用一次，所以即使那一行被移动/遗漏，内置包也不会退化成两份实例。
    /// </para>
    /// </summary>
    public static void Preload()
    {
        lock (Gate)
        {
            EnsureDiagnosticsBridge();

            foreach (var name in builtInNames)
            {
                var existing = FindLoadedInDefault(name);
                if (existing is not null)
                {
                    MarkLoaded(name, existing);
                    continue;
                }

                try
                {
                    // 按简单名加载：由 exe 目录/deps.json 解析，拿到的一定是随版本发布的那份 DLL。
                    var assembly = Assembly.Load(new AssemblyName(name));
                    MarkLoaded(name, assembly);
                }
                catch (Exception ex)
                {
                    loaded.Remove(name);
                    failed.Add(name);
                    AppStartup.LogDiagnostic(
                        $"built-in widget assembly '{name}' could not be preloaded: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // 冒烟日志只记阶段名（其它执行者脚本按名字读），细节进 GlassDiagnostics。
            // 模块初始化时 SmokeLog 还没配置好：那时不置位，宿主显式调用时再写这一行。
            if (SmokeLog.Enabled && !stageLogged)
            {
                stageLogged = true;
                SmokeLog.Stage("widgets-preloaded");
            }
        }

        // --dump-widgets=<path>：预加载完成后把内置组件清单写成 JSON（诊断开关，不属于正常启动）。
        WidgetDump.TryWriteRequestedFromCommandLine();
    }

    private static void MarkLoaded(string name, Assembly assembly)
    {
        loaded.Add(name);
        failed.Remove(name);

        if (originLogged.Add(name))
            LogOrigin(assembly);
    }

    /// <summary>
    /// 记录每份内置程序集的实际加载位置。全部应当是 exe 同目录；如果出现
    /// <c>DataFolder\Widgets</c> 里的第二份，说明「两份 Notes/Clock 程序集」问题又回来了。
    /// </summary>
    private static void LogOrigin(Assembly assembly)
    {
        var name = assembly.GetName().Name ?? "?";
        var location = GetLocation(assembly);
        var appDirectory = TrimDirectory(AppContext.BaseDirectory);
        var directory = string.IsNullOrEmpty(location) ? string.Empty : TrimDirectory(Path.GetDirectoryName(location) ?? string.Empty);
        var inAppDirectory = string.Equals(directory, appDirectory, StringComparison.OrdinalIgnoreCase);

        AppStartup.LogDiagnostic(
            $"built-in widget assembly {name} v{assembly.GetName().Version} loaded into the default load context " +
            $"from {location}" +
            (inAppDirectory
                ? string.Empty
                : $"!! NOT the application directory {appDirectory}; a second copy would split types, static state and the WinRT marshaller"));
    }

    /// <summary>Default 上下文里是否已经加载了这个名字的程序集（与 AssemblyProvider 的查找规则一致）。</summary>
    private static Assembly? FindLoadedInDefault(string assemblyName)
    {
        var moduleName = assemblyName + ".dll";

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (!string.Equals(assembly.ManifestModule.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                continue;
            return assembly;
        }

        return null;
    }

    private static bool diagnosticsBridgeInstalled;

    /// <summary>
    /// Core 的 <c>AssemblyProvider</c> 用 BCL <see cref="Trace"/> 记录「内置包无法卸载」这类信息。
    /// （DeskCanvas.Core 不认识宿主的 GlassDiagnostics，也不为此新增公开成员）。这里挂一个只转发
    /// <c>[AssemblyProvider]</c> 消息的监听器，把那条通道接到同一个 glass-debug.log。只装一次。
    /// </summary>
    private static void EnsureDiagnosticsBridge()
    {
        if (diagnosticsBridgeInstalled) return;
        diagnosticsBridgeInstalled = true;

        try
        {
            Trace.Listeners.Add(new AssemblyProviderTraceListener());
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"assembly provider trace bridge failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class AssemblyProviderTraceListener : TraceListener
    {
        private const string Prefix = "[AssemblyProvider]";

        public override void Write(string? message) => Forward(message);

        public override void WriteLine(string? message) => Forward(message);

        private static void Forward(string? message)
        {
            if (message is null || !message.StartsWith(Prefix, StringComparison.Ordinal)) return;

            try
            {
                AppStartup.LogDiagnostic(message);
            }
            catch
            {
                // 诊断写入不能反过来拖垮加/卸载路径。
            }
        }
    }

    private static IReadOnlyList<string> Snapshot(HashSet<string> set)
    {
        lock (Gate)
        {
            return builtInNames.Where(set.Contains).ToArray();
        }
    }

    private static string GetLocation(Assembly assembly)
    {
        try
        {
            return assembly.Location;
        }
        catch (NotSupportedException)
        {
            return string.Empty;
        }
    }

    private static string TrimDirectory(string path)
        => path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// 兜底预加载：入口程序集加载时（<c>Program.Main</c> 之前）执行一次。预加载只加载程序集元数据，
    /// 不碰 WinUI/Avalonia，因此可以安全地发生在任何 UI 初始化之前。
    /// </summary>
    [ModuleInitializer]
    internal static void PreloadOnModuleLoad()
    {
        try
        {
            Preload();
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"built-in widget preload (module initializer) failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
