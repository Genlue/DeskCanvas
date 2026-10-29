using System.Diagnostics;
using System.Reflection;
using System.Resources;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;

namespace DeskCanvas.Core.Services;
 
/// <inheritdoc />
public class AssemblyProvider : IAssemblyProvider
{
    private readonly Dictionary<string, AssemblyLoadContext> loadedContexts = new();
    private ILookup<string, AssemblyInfo> assemblyCache;
    private readonly IServiceProvider serviceProvider;

    /// <summary>
    /// Core 的诊断出口：写 <see cref="Trace"/>。默认的 <see cref="DefaultTraceListener"/> 已经覆盖
    /// 附加调试器的场景；宿主还可以再挂一个 <see cref="TraceListener"/> 把这条通道接到自己的日志
    /// （新 WinUI 入口的预加载器就是这么把「内置包无法卸载」写进 GlassDiagnostics 的）。
    /// 不新增公开成员，<c>tests/ApiSnapshotChecks</c> 的基线保持不动。
    /// </summary>
    private static void Log(string message)
    {
        try
        {
            Trace.WriteLine($"[AssemblyProvider] {message}");
        }
        catch
        {
            // 诊断输出失败不能影响加载/卸载路径。
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AssemblyProvider"/> class.
    /// </summary>
    /// <param name="serviceProvider">The service provider.</param>
    public AssemblyProvider(IServiceProvider serviceProvider)
    {
        assemblyCache = GetAssemblyInfos(Const.WidgetsFolder);
        this.serviceProvider = serviceProvider;
    }

    private readonly Dictionary<string, ILookup<string, AssemblyInfo>> assemblyInfoCache = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    /// <remarks>
    /// 返回值 = 目录扫描结果 + <see cref="AssemblyLoadContext.Default"/> 里「已加载且带
    /// <see cref="WidgetInfoAttribute"/>」的程序集（新 GUI 用 ProjectReference 引入的内置组件
    /// 由 WinUI 入口的预加载器加载进来）。
    /// <para>
    /// 同名只保留 Default 的那一份（内置优先）：否则内置包会同时以「GUI 引用的类型」和
    /// 「插件上下文里的类型」出现两次，static 状态、DI 与 WinRT marshaller 都会分裂
    /// （报告 §3 第一步 6、§4.2）。目录扫描仍按目录独立缓存；内置清单是另一份缓存并按
    /// Default 上下文内容变化失效，两者不会互相污染。没有预加载时（旧桌面入口）内置清单为空，
    /// 这里原样返回目录扫描结果对象，行为与迁移前完全一致。
    /// </para>
    /// </remarks>
    public ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath)
    {
        var scanned = GetScannedAssemblyInfos(directoryPath);
        var builtIn = GetBuiltInAssemblyInfos();
        if (builtIn.Count == 0) return scanned;

        var builtInNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in builtIn) builtInNames.Add(info.AssemblyName);

        // Default 优先：内置包的条目先入列，目录扫描里同名组整组丢弃。
        var merged = new List<AssemblyInfo>(builtIn.Count + scanned.Count);
        merged.AddRange(builtIn);
        foreach (var group in scanned)
        {
            if (!builtInNames.Contains(group.Key))
                merged.AddRange(group);
        }

        return merged.ToLookup(assembly => assembly.AssemblyName);
    }

    /// <summary>迁移前的方法体：只扫目录，按目录路径缓存。</summary>
    private ILookup<string, AssemblyInfo> GetScannedAssemblyInfos(string directoryPath)
    {
        lock (assemblyInfoCache)
        {
            if (assemblyInfoCache.TryGetValue(directoryPath, out var cached))
                return cached;

            var assemblies = Directory.Exists(directoryPath)
                ? Directory
                    .GetFiles(directoryPath, "*.dll")
                    .Where(IsPotentialWidgetAssembly)
                    .Select(GetAssemblyInfo)
                    .Where(info => info != null)
                    .Cast<AssemblyInfo>()
                    .ToList()
                : [];

            var lookup = assemblies.ToLookup(assembly => assembly.AssemblyName);
            assemblyInfoCache[directoryPath] = lookup;
            return lookup;
        }
    }

    private static readonly object builtInGate = new();

    /// <summary>上一次计算内置清单时 Default 上下文里的程序集集合（Default 只增不减）。</summary>
    private static HashSet<Assembly> builtInSnapshot = [];

    private static IReadOnlyList<AssemblyInfo> builtInInfos = [];

    /// <summary>
    /// Default 上下文里「已加载 + 带 WidgetInfoAttribute」的程序集清单。
    /// <para>
    /// 缓存按 Default 上下文的内容失效：预加载发生在 <see cref="AssemblyProvider"/> 构造之前或之后
    /// 都能合并进来，不会把构造时那份空清单永远钉死。描述一份程序集需要读 Locale 资源管理器
    /// （会走类型加载），所以缓存是必要的——Default 很少新增程序集，失效次数有限。
    /// </para>
    /// </summary>
    private static IReadOnlyList<AssemblyInfo> GetBuiltInAssemblyInfos()
    {
        var loaded = AssemblyLoadContext.Default.Assemblies.ToArray();

        lock (builtInGate)
        {
            if (builtInSnapshot.Count == loaded.Length && loaded.All(builtInSnapshot.Contains))
                return builtInInfos;

            var infos = new List<AssemblyInfo>();
            foreach (var assembly in loaded)
            {
                if (DescribeLoadedAssembly(assembly) is { } info)
                    infos.Add(info);
            }

            builtInSnapshot = new HashSet<Assembly>(loaded);
            builtInInfos = infos;
            return builtInInfos;
        }
    }

    /// <summary>
    /// 把 Default 上下文里已加载的程序集描述成 <see cref="AssemblyInfo"/>；不是组件包返回 null。
    /// 与目录扫描版的唯一区别是不创建（也不卸载）插件上下文。
    /// </summary>
    private static AssemblyInfo? DescribeLoadedAssembly(Assembly assembly)
    {
        try
        {
            var widgetAttributes = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToList();
            if (widgetAttributes.Count == 0) return null;

            var assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName)) return null;

            var localeAttribute = assembly.GetCustomAttributes<LocaleAttribute>().FirstOrDefault();
            var companyAttribute = assembly.GetCustomAttributes<AssemblyCompanyAttribute>().FirstOrDefault();
            var locale = GetLocaleResourceManagerCore(assembly);
            var displayName = locale?.GetString(localeAttribute?.DisplayName ?? "") ?? assemblyName;
            var categories = widgetAttributes
                .Select(widget => widget.Category)
                .Where(category => !string.IsNullOrEmpty(category))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new AssemblyInfo(
                GetLocation(assembly),
                assemblyName,
                displayName,
                companyAttribute?.Company ?? "",
                assembly.GetName().Version ?? new Version(0, 0),
                localeAttribute?.IconData ?? "",
                categories);
        }
        catch (Exception ex)
        {
            Log($"describing loaded assembly {assembly.FullName} failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>已加载程序集的磁盘路径；动态程序集/单文件形态没有路径时返回空串。</summary>
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

    private static bool IsPotentialWidgetAssembly(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        if (fileName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("av_", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("WinRT.", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("TimeZone", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("Markdig.dll", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("DeskCanvas.Core.dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private AssemblyInfo? GetAssemblyInfo(string filePath)
    {
        try
        {
            var context = new PluginLoadContext(filePath);
            var assembly = context.LoadFromAssemblyPath(filePath);
            var localeAttribute = assembly.GetCustomAttributes<LocaleAttribute>().FirstOrDefault();
            var companyAttribute = assembly.GetCustomAttributes<AssemblyCompanyAttribute>().FirstOrDefault();
            var widgetAttributes = assembly.GetCustomAttributes<WidgetInfoAttribute>();

            if (!widgetAttributes.Any()) return null;

            var assemblyName = assembly.GetName().Name!;
            var version = assembly.GetName().Version!;
            var company = companyAttribute?.Company ?? "";
            var locale = GetLocaleResourceManager(assembly);
            var displayName = locale?.GetString(localeAttribute?.DisplayName ?? "") ?? assemblyName;
            var categories = widgetAttributes
                .Select(widget => widget.Category)
                .Where(category => !string.IsNullOrEmpty(category))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToList();

            context.Unload();

            return new AssemblyInfo(filePath, assemblyName, displayName, company, version, localeAttribute?.IconData ?? "", categories);
        }
        catch (Exception)
        {
            return null;
        }
    }
    
    /// <inheritdoc />
    /// <remarks>
    /// 顺序（报告 §3 第一步 6）：<see cref="AssemblyLoadContext.Default"/> 里已加载的同名程序集 →
    /// 本类此前建立的插件上下文 → 原「扫描 <see cref="Const.WidgetsFolder"/> 并新建
    /// <see cref="PluginLoadContext"/>」路径。
    /// <para>
    /// Default 优先是内置组件「只保留一份实例」的关键：新 GUI 用 ProjectReference 引入的
    /// Notes/Clock 等随版本发布的 DLL 在启动时被预加载进 Default，宿主与组件反射拿到的必须是
    /// 同一个 <see cref="Assembly"/>，否则组件里的 static 状态、DI 注册和 WinRT marshaller 都会
    /// 各有一份。第三方包不在 Default 里，继续走原来的插件上下文，行为不变。
    /// </para>
    /// </remarks>
    public Assembly LoadAssembly(string name)
    {
        if (FindLoadedInDefault(name) is { } builtIn)
            return builtIn;

        if (loadedContexts.TryGetValue(name, out var context))
            return context.Assemblies.Single(assembly => 
                assembly.ManifestModule.Name == $"{name}.dll");
        
        var filePath = GetAssemblyPath(name);
        context = new PluginLoadContext(filePath);
        loadedContexts[name] = context;

        return context.LoadFromAssemblyPath(filePath);
    }

    /// <summary>
    /// <paramref name="assemblyName"/> 是否已经在 <see cref="AssemblyLoadContext.Default"/> 里加载。
    /// 命中说明它是内置组件（或宿主直接引用的程序集），进程里已经存在唯一实例。
    /// </summary>
    /// <param name="assemblyName">程序集简单名（例如 <c>Notes</c>）。</param>
    /// <returns>已加载的程序集；没有时返回 null。</returns>
    private static Assembly? FindLoadedInDefault(string assemblyName)
    {
        if (string.IsNullOrWhiteSpace(assemblyName)) return null;

        var name = assemblyName.Trim();
        var moduleName = name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? name : $"{name}.dll";

        foreach (var assembly in AssemblyLoadContext.Default.Assemblies)
        {
            if (!string.Equals(assembly.ManifestModule.Name, moduleName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            return assembly;
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// 内置组件（<see cref="AssemblyLoadContext.Default"/> 里的程序集）无法卸载：Default 上下文在
    /// 进程结束前一直存活，CLR 也不提供卸载它的 API。所以这里记录日志后直接返回，不抛给调用方
    /// ——组件设置页的「重新加载」之类的入口不应因此把整个进程带崩。
    /// </para>
    /// <para>
    /// 由此得到迁移期的已知限制（报告 §3 第一步 6、R6）：<b>内置组件的热替换改为下次启动生效</b>。
    /// 新 GUI 引用的内置包统一使用随版本发布的 DLL；需要换版本时替换文件并重启。
    /// 通过 <see cref="PluginLoadContext"/> 加载的第三方包仍按原逻辑真正卸载。
    /// </para>
    /// </remarks>
    public void UnloadAssembly(string name)
    {
        if (FindLoadedInDefault(name) is { } builtIn)
        {
            Log($"UnloadAssembly({name}): built-in assembly at {GetLocation(builtIn)} lives in the default " +
                "load context and cannot be unloaded; its replacement takes effect after the next restart.");
            return;
        }

        if (!loadedContexts.TryGetValue(name, out var context))
            throw new InvalidOperationException($"Assembly {name} is not loaded");

        context.Unload();
        loadedContexts.Remove(name);
    }

    /// <inheritdoc />
    public object Activate(Type type, params object[] args)
    {
        Exception? lastEx = null;
        try
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, type, args);
        }
        catch (Exception ex)
        {
            lastEx = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
            try
            {
                if (args.Length == 0)
                    return Activator.CreateInstance(type)!;
                return Activator.CreateInstance(type, args)!;
            }
            catch (Exception ex2)
            {
                lastEx = ex2 is System.Reflection.TargetInvocationException tie2 && tie2.InnerException != null ? tie2.InnerException : ex2;
                var constructors = type.GetConstructors()
                    .OrderByDescending(c => c.GetParameters().Length);
                foreach (var ctor in constructors)
                {
                    var pars = ctor.GetParameters();
                    var values = new object?[pars.Length];
                    var ok = true;
                    for (int i = 0; i < pars.Length; i++)
                    {
                        var pt = pars[i].ParameterType;
                        var fromArgs = args.FirstOrDefault(a => a != null && pt.IsAssignableFrom(a.GetType()));
                        if (fromArgs != null)
                        {
                            values[i] = fromArgs;
                        }
                        else
                        {
                            var fromDi = serviceProvider.GetService(pt);
                            if (fromDi != null)
                            {
                                values[i] = fromDi;
                            }
                            else if (pars[i].HasDefaultValue)
                            {
                                values[i] = pars[i].DefaultValue;
                            }
                            else
                            {
                                ok = false;
                                break;
                            }
                        }
                    }
                    if (ok)
                    {
                        try
                        {
                            return ctor.Invoke(values);
                        }
                        catch (Exception ex3)
                        {
                            lastEx = ex3 is System.Reflection.TargetInvocationException tie3 && tie3.InnerException != null ? tie3.InnerException : ex3;
                        }
                    }
                }
                throw new InvalidOperationException($"Failed to create an instance of {type.Name}: {lastEx?.Message}", lastEx);
            }
        }
    }

    /// <inheritdoc />
    public ResourceManager? GetLocaleResourceManager(Assembly assembly) => GetLocaleResourceManagerCore(assembly);

    /// <summary>
    /// 与 <see cref="GetLocaleResourceManager"/> 相同，但为静态：Default 上下文里的内置组件
    /// 在 <see cref="DescribeLoadedAssembly"/> 中也要解析显示名/图标。
    /// </summary>
    private static ResourceManager? GetLocaleResourceManagerCore(Assembly assembly)
    {
        try
        {
            var assemblyName = assembly.GetName().Name;
            var localeType = assembly.GetType($"{assemblyName}.Locales.Locale")
                          ?? assembly.GetType("Locale");
            if (localeType == null)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).Cast<Type>().ToArray();
                }
                localeType = types.FirstOrDefault(t => t.Name == "Locale");
            }

            return localeType?
                .GetProperty(nameof(ResourceManager), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?
                .GetValue(null) as ResourceManager;
        }
        catch
        {
            return null;
        }
    }
    
    private string GetAssemblyPath(string name, bool updateCache = false)
    {
        if (updateCache) 
            assemblyCache = GetAssemblyInfos(Const.WidgetsFolder);
        
        var assemblyInfo = assemblyCache[name]
            .MaxBy(assembly => assembly.Version);

        if (assemblyInfo != default) 
            return assemblyInfo.FilePath;

        if (!updateCache)
            return GetAssemblyPath(name, true);
        
        throw new FileNotFoundException($"Assembly {name} not found");
    }

    /// <summary>
    /// Assemblies that must exist exactly once per process, even though every widget lives in its
    /// own collectible <see cref="PluginLoadContext"/>.
    /// <para>
    /// <c>WinRT.Runtime</c> registers the process-wide COM marshaller
    /// (<c>ComWrappers.RegisterForMarshalling</c>) the first time a WinRT projection is touched,
    /// and that call throws <c>InvalidOperationException: Attempt to update previously set global
    /// instance.</c> when a <b>second</b> instance of the runtime tries to do the same. Loading a
    /// per-widget copy therefore breaks every widget that touches WinRT after the first one —
    /// the Shortcuts notifications/media actions surfaced it as a red "Attempt to update
    /// previously set global instance." toast, and the silent consumers (Music SMTC,
    /// ControlCenter radios) simply stopped working. One shared copy keeps the marshaller single
    /// and makes the projection types identical across widgets.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ProcessGlobalAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "WinRT.Runtime",
        "Microsoft.Windows.SDK.NET",
    };

    /// <summary>
    /// Loader for <see cref="ProcessGlobalAssemblies"/>: a non-collectible context that resolves
    /// them from the widget folder, so every plugin context gets the same assembly instances.
    /// Never unloaded — the process-wide COM marshaller keeps pointing at these types.
    /// </summary>
    private static class SharedRuntime
    {
        private static readonly object Gate = new();
        private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Folders the shared assemblies were found in, in discovery order.</summary>
        private static readonly List<string> ProbeFolders = [];

        private static readonly AssemblyLoadContext Context = new SharedRuntimeLoadContext();

        public static Assembly Load(string name, string candidatePath)
        {
            lock (Gate)
            {
                if (Loaded.TryGetValue(name, out var cached)) return cached;

                Remember(Path.GetDirectoryName(candidatePath));

                var assembly = Context.Assemblies.FirstOrDefault(a =>
                                    string.Equals(a.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                                ?? Context.LoadFromAssemblyPath(candidatePath);

                Loaded[name] = assembly;
                return assembly;
            }
        }

        private static void Remember(string? folder)
        {
            if (string.IsNullOrEmpty(folder)) return;
            if (ProbeFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) return;
            ProbeFolders.Add(folder);
        }

        /// <summary>
        /// Companion resolution for the shared context (the projection needs
        /// <c>WinRT.Runtime</c>): the folders these assemblies came from, then the widget folder.
        /// </summary>
        private static string? Probe(string name)
        {
            foreach (var folder in ProbeFolders)
            {
                var candidate = Path.Combine(folder, $"{name}.dll");
                if (File.Exists(candidate)) return candidate;
            }

            var widgets = Path.Combine(Const.WidgetsFolder, $"{name}.dll");
            return File.Exists(widgets) ? widgets : null;
        }

        private sealed class SharedRuntimeLoadContext() : AssemblyLoadContext("DeskCanvas.SharedRuntime")
        {
            protected override Assembly? Load(AssemblyName assemblyName)
            {
                if (assemblyName.Name is not { } name || !ProcessGlobalAssemblies.Contains(name))
                    return null;

                // Re-entrant on the owning thread (Load already holds Gate while it loads).
                lock (Gate)
                {
                    var path = Probe(name);
                    return path != null ? LoadFromAssemblyPath(path) : null;
                }
            }
        }
    }

    private class PluginLoadContext(string pluginPath) : AssemblyLoadContext(true)
    {
        private readonly AssemblyDependencyResolver resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // Process-global runtimes first: they must never be duplicated per widget context.
            if (assemblyName.Name is { } sharedName && ProcessGlobalAssemblies.Contains(sharedName))
            {
                var sharedPath = resolver.ResolveAssemblyToPath(assemblyName)
                                 ?? ProbeAdjacent(assemblyName);
                if (sharedPath != null)
                    return SharedRuntime.Load(sharedName, sharedPath);
            }

            var assembly = Default.Assemblies.FirstOrDefault(a => a.GetName().Name == assemblyName.Name);
            if (assembly != null)
            {
                return assembly;
            }

            var assemblyPath = resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            // Probe adjacent directory for widget-local dependencies (e.g. Microsoft.Windows.SDK.NET.dll)
            var candidate = ProbeAdjacent(assemblyName);
            if (candidate != null)
            {
                return LoadFromAssemblyPath(candidate);
            }

            return null;
        }

        /// <summary>Widget-local copy of <paramref name="assemblyName"/>, or null when absent.</summary>
        private string? ProbeAdjacent(AssemblyName assemblyName)
        {
            var pluginDir = Path.GetDirectoryName(pluginPath);
            if (pluginDir == null) return null;

            var candidate = Path.Combine(pluginDir, $"{assemblyName.Name}.dll");
            return File.Exists(candidate) ? candidate : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var libraryPath = resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
    
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}