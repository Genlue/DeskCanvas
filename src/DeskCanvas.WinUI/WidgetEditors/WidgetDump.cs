using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskCanvas.Core;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// <c>--dump-widgets=&lt;path&gt;</c> 诊断开关（。<c>--smoke</c> 同族，报。§3 第一。5/6 的验证工具）。/// 。26 个内置组件包里的每一。<see cref="WidgetInfoAttribute"/> 写成 JSON，附上程序集的实际加载路径。/// <para>
/// 用途有两个：一是验证内置组件确实从 exe 同目录的 DLL 加载（而不。<c>DataFolder\Widgets</c> 。/// 由插件上下文加载的第二份），二是作为后续 37 个组件设置页迁移的任务底稿——每一行都。/// <c>editType</c>，为空表示该组件没有设置页。/// </para>
/// <para>
/// 开关在 <see cref="WidgetAssemblyRegistry.Preload"/> 末尾处理：预加载之后才有完整的清单。/// 写文件失败只写日志，绝不影响启动。/// </para>
/// </summary>
internal static class WidgetDump
{
    private const string Prefix = "--dump-widgets=";
    private const string Flag = "--dump-widgets";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>命令行里没有 <c>--dump-widgets</c> 时什么都不做。/summary>
    public static void TryWriteRequestedFromCommandLine()
    {
        var path = GetRequestedPath();
        if (path is null) return;

        try
        {
            Write(path);
        }
        catch (Exception ex)
        {
            AppStartup.LogDiagnostic($"--dump-widgets: writing '{path}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// 读取 <c>--dump-widgets=&lt;path&gt;</c>（也接受 <c>--dump-widgets &lt;path&gt;</c>）。
    /// 参数来自 <see cref="Program.StartupArgs"/>（入口已经解析过）；模块初始化阶段该数组还是空的。
    /// 那时退回进程命令行。
    /// </summary>
    private static string? GetRequestedPath()
    {
        var args = Program.StartupArgs is { Length: > 0 } startup
            ? startup
            : Environment.GetCommandLineArgs().Skip(1).ToArray();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                var value = arg[Prefix.Length..].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }

            if (string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                var value = args[i + 1].Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private static void Write(string path)
    {
        // 用真实的 AssemblyProvider（与运行时同一个类、同一套合并逻辑）读目录 + Default 上下文；
        // 这里不需。DI，Activate 不会被调用，所以给一个空容器即可。
        var provider = new AssemblyProvider(new EmptyServiceProvider());
        var catalog = provider.GetAssemblyInfos(Const.WidgetsFolder);

        var assemblies = new List<AssemblyEntry>();
        var widgets = new List<WidgetEntry>();
        var errors = new List<string>();

        foreach (var name in WidgetAssemblyRegistry.BuiltInAssemblyNames)
        {
            Assembly assembly;
            try
            {
                // Default 优先：命中的就是预加载那一份，LoadedFrom 必然。exe 同目录。
                assembly = provider.LoadAssembly(name);
            }
            catch (Exception ex)
            {
                errors.Add($"{name}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            var location = Location(assembly);
            var info = catalog[name].MaxBy(entry => entry.Version);
            var attributes = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToList();

            assemblies.Add(new AssemblyEntry(
                name,
                info?.DisplayName ?? name,
                assembly.GetName().Version?.ToString() ?? string.Empty,
                location,
                InApplicationDirectory(location),
                AssemblyLoadContext.GetLoadContext(assembly) == AssemblyLoadContext.Default,
                attributes.Count));

            foreach (var attribute in attributes)
            {
                widgets.Add(new WidgetEntry(
                    name,
                    attribute.ViewType.FullName ?? attribute.ViewType.Name,
                    attribute.ViewType.Name,
                    attribute.ModelType?.FullName,
                    attribute.EditModelViewType?.FullName,
                    attribute.Title,
                    attribute.Category,
                    attribute.PresetSpans.Select(span => $"{span.Columns}x{span.Rows}").ToList(),
                    location));
            }
        }

        var editable = widgets.Count(widget => widget.EditType is not null);
        var dump = new
        {
            Schema = 1,
            GeneratedAt = DateTimeOffset.Now.ToString("O"),
            ExePath = Environment.ProcessPath,
            ExeDirectory = AppContext.BaseDirectory,
            WidgetsFolder = Const.WidgetsFolder,
            WidgetsFolderExists = Directory.Exists(Const.WidgetsFolder),
            BuiltInAssemblyNames = WidgetAssemblyRegistry.BuiltInAssemblyNames,
            BuiltInAssemblyCount = WidgetAssemblyRegistry.BuiltInAssemblyNames.Count,
            LoadedAssemblyCount = assemblies.Count,
            WidgetCount = widgets.Count,
            EditableWidgetCount = editable,
            Assemblies = assemblies,
            Widgets = widgets,
            Errors = errors,
        };

        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        File.WriteAllText(fullPath, JsonSerializer.Serialize(dump, Options) + Environment.NewLine);

        AppStartup.LogDiagnostic(
            $"--dump-widgets: wrote {widgets.Count} widgets ({editable} with editType) from " +
            $"{assemblies.Count}/{WidgetAssemblyRegistry.BuiltInAssemblyNames.Count} built-in assemblies to {fullPath}");
    }

    private static string Location(Assembly assembly)
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

    private static bool InApplicationDirectory(string location)
    {
        if (string.IsNullOrEmpty(location)) return false;

        static string Trim(string value) => value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(
            Trim(Path.GetDirectoryName(location) ?? string.Empty),
            Trim(AppContext.BaseDirectory),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>JSON 里的一条内置组件（一。<see cref="WidgetInfoAttribute"/>）。/summary>
    private sealed record WidgetEntry(
        string Assembly,
        string ViewType,
        string ViewTypeName,
        string? ModelType,
        string? EditType,
        string? TitleKey,
        string? Category,
        IReadOnlyList<string> PresetSpans,
        string LoadedFrom);

    /// <summary>JSON 里的一条内置程序集（加载位置与是否来自 Default 上下文）。/summary>
    private sealed record AssemblyEntry(
        string Assembly,
        string DisplayName,
        string Version,
        string LoadedFrom,
        bool LocatedInExeDirectory,
        bool LoadedIntoDefault,
        int WidgetCount);

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
