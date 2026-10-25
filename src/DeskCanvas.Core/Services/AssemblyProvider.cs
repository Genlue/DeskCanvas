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
    public ILookup<string, AssemblyInfo> GetAssemblyInfos(string directoryPath)
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
    public Assembly LoadAssembly(string name)
    {
        if (loadedContexts.TryGetValue(name, out var context))
            return context.Assemblies.Single(assembly => 
                assembly.ManifestModule.Name == $"{name}.dll");
        
        var filePath = GetAssemblyPath(name);
        context = new PluginLoadContext(filePath);
        loadedContexts[name] = context;

        return context.LoadFromAssemblyPath(filePath);
    }
    
    /// <inheritdoc />
    public void UnloadAssembly(string name)
    {
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
    public ResourceManager? GetLocaleResourceManager(Assembly assembly)
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