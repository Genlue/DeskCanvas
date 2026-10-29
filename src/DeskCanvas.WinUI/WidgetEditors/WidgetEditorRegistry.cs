using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DeskCanvas.Core.Models.Attributes;
using DeskCanvas.WinUI.Host;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>组件设置页在 WinUI 里的迁移状态。/summary>
internal enum WidgetEditorMigrationState
{
    /// <summary>已在本工程里实现 WinUI 页面，工厂可创建。/summary>
    Migrated = 0,

    /// <summary>内置组件。6 个程序集之一）声明了 <c>EditModelViewType</c>，但页面还没迁移。/summary>
    NotMigrated = 1,

    /// <summary>非内置程序集：只有旧 Avalonia 编辑页，需要插件作者接。WinUI 工厂（已知限。R5）。/summary>
    PluginNeedsAdapter = 2,
}

/// <summary>
/// 编辑页注册表的键。c>(assemblyName, settingsViewFullName)</c>。/// <para>
/// 。b>原设置页类型</b>的完整名而不是组件名：多个组件类型共用一个设置页（Clock 。/// AnalogI/AnalogIII/AnalogIV、Weather 。7 个类型）时天然合并成一条，覆盖率也按页统计。/// </para>
/// </summary>
internal readonly record struct WidgetEditorKey(string AssemblyName, string ViewTypeName)
{
    public override string ToString() => $"{AssemblyName}:{ViewTypeName}";
}

/// <summary>注册表解析结果：页面工厂 + 原设置页类型全名 + 迁移状态。/summary>
internal sealed class WidgetEditorRegistration
{
    public WidgetEditorRegistration(
        WidgetEditorKey key,
        WidgetEditorMigrationState state,
        Func<WidgetEditorContext, IWidgetEditorPage>? factory)
    {
        Key = key;
        State = state;
        Factory = factory;
    }

    public WidgetEditorKey Key { get; }

    public WidgetEditorMigrationState State { get; }

    /// <summary>只在 <see cref="WidgetEditorMigrationState.Migrated"/> 时非空。/summary>
    public Func<WidgetEditorContext, IWidgetEditorPage>? Factory { get; }

    /// <summary>。Avalonia 设置页类型全名（保留身份，便于对。诊断）。/summary>
    public string LegacyViewTypeName => Key.ViewTypeName;
}

/// <summary>
/// 组件编辑页注册表（报。§3 第三。S3.2）：。<c>(assemblyName, viewTypeName)</c> 。WinUI 页面工厂。/// <para>
/// <b>自注。/b>：每个页面在自己的文件里。<c>[ModuleInitializer]</c> 。<see cref="Register"/>。/// 后续 37 页的执行者各改各的文件，不需要碰本文件或窗口。示例见
/// <c>Widgets/Clock/DigitalClockEditorPage.cs</c>。/// </para>
/// <para>
/// <b>覆盖率是诚实。/b>。see cref="DescribeCoverage"/> 。<c>WidgetInfoAttribute.EditModelViewType</c>
/// 枚举内置组件的全部编辑页（当。37），再逐条问注册表。未注册的内置页显示为“未迁移”，
/// 打开时展示明确的未迁移页——不会回退。Avalonia 页面，也不会。37 页都当成已实现。/// </para>
/// </summary>
internal static class WidgetEditorRegistry
{
    private static readonly object Gate = new();

    private static readonly Dictionary<WidgetEditorKey, Func<WidgetEditorContext, IWidgetEditorPage>> Factories = [];

    /// <summary>登记一个已迁移页面。重复登记会覆盖（便于热重载/测试）。/summary>
    /// <param name="assemblyName">组件程序集名。 <c>WidgetInfoAttribute</c> 所在程序集名，忽略大小写比较）。/param>
    /// <param name="viewTypeName"><c>EditModelViewType</c> 的完整类型名（旧设置页身份）。/param>
    /// <param name="factory">创建页面；只允许 <c>new</c> 页面，不读写 Provider（页面在 Attach 里读）。/param>
    public static void Register(string assemblyName, string viewTypeName,
        Func<WidgetEditorContext, IWidgetEditorPage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assemblyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewTypeName);
        ArgumentNullException.ThrowIfNull(factory);

        var key = new WidgetEditorKey(assemblyName, viewTypeName);
        lock (Gate)
        {
            Factories[key] = factory;
        }
    }

    /// <summary>该键是否已有 WinUI 页面（覆盖率检。诊断用）。/summary>
    public static bool IsMigrated(WidgetEditorKey key)
    {
        lock (Gate)
        {
            return Factories.ContainsKey(key);
        }
    }

    /// <summary>
    /// 解析一个键：已注册 。<see cref="WidgetEditorMigrationState.Migrated"/>。
    /// 否则按程序集区分“内置未迁移”与“外部插件需适配”。
    /// </summary>
    public static WidgetEditorRegistration Resolve(WidgetEditorKey key)
    {
        Func<WidgetEditorContext, IWidgetEditorPage>? factory;
        lock (Gate)
        {
            Factories.TryGetValue(key, out factory);
        }

        if (factory is not null)
        {
            return new WidgetEditorRegistration(key, WidgetEditorMigrationState.Migrated, factory);
        }

        var state = IsBuiltInAssembly(key.AssemblyName)
            ? WidgetEditorMigrationState.NotMigrated
            : WidgetEditorMigrationState.PluginNeedsAdapter;
        return new WidgetEditorRegistration(key, state, null);
    }

    /// <summary>程序集是否属于内置组件（26 个程序集名，忽略大小写）。/summary>
    public static bool IsBuiltInAssembly(string assemblyName)
        => WidgetAssemblyRegistry.BuiltInAssemblyNames
            .Any(name => string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 反射内置组件。<c>WidgetInfoAttribute.EditModelViewType</c> 得到权威目录，再与注册表对账。
    /// 已预加载。Default 上下文，这里只做 <see cref="Assembly.Load(AssemblyName)"/>（不会执行组件代码）。
    /// <para>结果含每个编辑页被哪些组件类型共用，便于报告与后续页面执行者核对。/para>
    /// </summary>
    public static WidgetEditorCoverage DescribeCoverage()
    {
        var entries = new List<WidgetEditorCoverageEntry>();
        var errors = new List<string>();
        var seen = new HashSet<WidgetEditorKey>();

        foreach (var assemblyName in WidgetAssemblyRegistry.BuiltInAssemblyNames)
        {
            try
            {
                var assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (var attribute in assembly.GetCustomAttributes<WidgetInfoAttribute>())
                {
                    if (attribute.EditModelViewType is not { } editType) continue;

                    var key = new WidgetEditorKey(assemblyName, editType.FullName ?? editType.Name);
                    var existingIndex = entries.FindIndex(entry => entry.Key == key);
                    if (existingIndex >= 0)
                    {
                        entries[existingIndex].WidgetTypes.Add(attribute.ViewType.Name);
                        continue;
                    }

                    if (!seen.Add(key)) continue;

                    var registration = Resolve(key);
                    var entry = new WidgetEditorCoverageEntry(key, registration.State);
                    entry.WidgetTypes.Add(attribute.ViewType.Name);
                    entries.Add(entry);
                }
            }
            catch (Exception ex)
            {
                errors.Add($"{assemblyName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        var stale = new List<WidgetEditorKey>();
        lock (Gate)
        {
            stale.AddRange(Factories.Keys.Where(key => !seen.Contains(key)));
        }

        return new WidgetEditorCoverage(entries, errors, stale);
    }
}

/// <summary>一个内置编辑页在覆盖率报告里的状态。/summary>
internal sealed class WidgetEditorCoverageEntry(WidgetEditorKey key, WidgetEditorMigrationState state)
{
    public WidgetEditorKey Key { get; } = key;

    public WidgetEditorMigrationState State { get; } = state;

    /// <summary>共用这个设置页的组件类型名（例如 AnalogI/AnalogIII/AnalogIV）。/summary>
    public List<string> WidgetTypes { get; } = [];
}

/// <summary>一次覆盖率快照。7 页里已实现多少、未迁移多少，以及反射失败与过期注册。/summary>
internal sealed class WidgetEditorCoverage(
    IReadOnlyList<WidgetEditorCoverageEntry> entries,
    IReadOnlyList<string> errors,
    IReadOnlyList<WidgetEditorKey> staleRegistrations)
{
    public IReadOnlyList<WidgetEditorCoverageEntry> Entries { get; } = entries;

    public IReadOnlyList<string> Errors { get; } = errors;

    /// <summary>注册了但不在权威目录里的键（拼错程序集名/旧页面名时会被这里抓到）。/summary>
    public IReadOnlyList<WidgetEditorKey> StaleRegistrations { get; } = staleRegistrations;

    public int Total => Entries.Count;

    public int Migrated => Entries.Count(entry => entry.State == WidgetEditorMigrationState.Migrated);

    public int NotMigrated => Entries.Count(entry => entry.State == WidgetEditorMigrationState.NotMigrated);

    public int PluginNeedsAdapter => Entries.Count(entry => entry.State == WidgetEditorMigrationState.PluginNeedsAdapter);

    /// <summary>一行摘要（写诊断日。报告用）。/summary>
    public string Summary =>
        $"{Total} built-in edit pages: {Migrated} migrated to WinUI, {NotMigrated} not migrated" +
        (PluginNeedsAdapter > 0 ? $", {PluginNeedsAdapter} plugin pages need adaptation" : string.Empty) +
        (Errors.Count > 0 ? $", {Errors.Count} enumeration error(s)" : string.Empty) +
        (StaleRegistrations.Count > 0 ? $", {StaleRegistrations.Count} stale registration(s)" : string.Empty);
}
