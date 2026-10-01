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

            context.Unload();

            return new AssemblyInfo(filePath, assemblyName, displayName, company, version, localeAttribute?.IconData ?? "");
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
        try
        {
            return ActivatorUtilities.CreateInstance(serviceProvider, type, args);
        }
        catch
        {
            try
            {
                if (args.Length == 0)
                    return Activator.CreateInstance(type)!;
                return Activator.CreateInstance(type, args)!;
            }
            catch
            {
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
                        catch { }
                    }
                }
                throw new InvalidOperationException($"Failed to create an instance of {type.Name}");
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

    private class PluginLoadContext(string pluginPath) : AssemblyLoadContext(true)
    {
        private readonly AssemblyDependencyResolver resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
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
            var pluginDir = Path.GetDirectoryName(pluginPath);
            if (pluginDir != null)
            {
                var candidate = Path.Combine(pluginDir, $"{assemblyName.Name}.dll");
                if (File.Exists(candidate))
                {
                    return LoadFromAssemblyPath(candidate);
                }
            }

            return null;
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