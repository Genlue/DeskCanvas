using System;
using System.Collections.Generic;
using System.Linq;
using DeskCanvas.Core;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Locales;
using DeskCanvas.Services;

namespace DeskCanvas.WinUI.Settings;

/// <summary>设置窗口左栏的一个可导航项（纯数据，不含任何框架控件/几何对象）。</summary>
public sealed class SettingsNavItem
{
    public SettingsNavItem(
        string pageKey,
        string title,
        string? iconPath,
        IReadOnlyList<AssemblyInfo> assemblies)
    {
        PageKey = pageKey ?? throw new ArgumentNullException(nameof(pageKey));
        Title = title ?? string.Empty;
        IconPath = iconPath;
        Assemblies = assemblies ?? Array.Empty<AssemblyInfo>();
    }

    /// <summary>稳定页面键（<see cref="SettingsPageKeys"/>）。</summary>
    public string PageKey { get; }

    /// <summary>已本地化的标题（Locale 资源解析结果）。</summary>
    public string Title { get; }

    /// <summary>矢量路径字符串；null 表示该项没有图标。转 <c>PathIcon</c> 由 WinUI 侧完成。</summary>
    public string? IconPath { get; }

    /// <summary>该页涉及的组件包；系统设置页为空。</summary>
    public IReadOnlyList<AssemblyInfo> Assemblies { get; }
}

/// <summary>左栏的一个分组（系统设置 / 组件库）。</summary>
public sealed class SettingsNavGroup
{
    public SettingsNavGroup(string groupKey, string title, IReadOnlyList<SettingsNavItem> items)
    {
        GroupKey = groupKey;
        Title = title ?? string.Empty;
        Items = items ?? Array.Empty<SettingsNavItem>();
    }

    /// <summary>技术分组键（"System"/"Gallery"），用于日志与测试，不显示给用户。</summary>
    public string GroupKey { get; }

    public string Title { get; }

    public IReadOnlyList<SettingsNavItem> Items { get; }
}

/// <summary>
/// 设置窗口的导航目录：系统设置页固定，组件库的分组由真实组件包元数据驱动。
/// <para>
/// 数据来源只有 <see cref="IAssemblyProvider.GetAssemblyInfos"/>（读 <c>Widgets</c> 目录）与
/// <see cref="DeskCanvas.Core.Models.Attributes.WidgetInfoAttribute.Category"/>，不维护第二份
/// 「哪个包属于哪个分类」的清单——旧实现的硬编码名册正是分类页漏项/重项的来源
/// （报告 §3 第二步；旧 <c>SettingsViewModel</c> 已改成同样的数据驱动方式）。
/// </para>
/// <para>
/// 页面键约定（与 <see cref="SettingsPageKeys"/> 对齐）：
/// <c>General</c> … <c>About</c> 为系统设置页；<c>Gallery</c> 是「全部组件」；
/// <c>Gallery:&lt;分类键&gt;</c> 是分类页（后缀是 <c>WidgetCategory_*</c> 资源键，不是程序集名，
/// 这样多包同分类也能一个键说清）；<c>Gallery:&lt;程序集名&gt;</c> 保留给将来的单包页。
/// 分类键统一带 <c>WidgetCategory_</c> 前缀，因此两种后缀不会冲突。
/// </para>
/// </summary>
public sealed class SettingsPageCatalog
{
    /// <summary>未声明分类的组件包落在「其他扩展」，键后缀用同一个分类资源键，避免再造一个魔法字符串。</summary>
    private const string UncategorizedCategoryKey = "WidgetCategory_Uncategorized";

    /// <summary>
    /// 分类页的固定顺序。键是资源键，标题一律走 Locale；这里的图标只是路径字符串。

    /// 顺序沿用旧实现的批准方案（报告 §3 第二步的 8 个分类）。

    /// </summary>
    private static readonly (string Key, string IconPath)[] CategoryOrder =
    [
        ("WidgetCategory_ClockCalendar", SettingsPageIcons.ClockCalendar),
        ("WidgetCategory_Weather", SettingsPageIcons.Weather),
        ("WidgetCategory_SystemStatus", SettingsPageIcons.SystemStatus),
        ("WidgetCategory_Productivity", SettingsPageIcons.Productivity),
        ("WidgetCategory_Launcher", SettingsPageIcons.Launcher),
        ("WidgetCategory_Lifestyle", SettingsPageIcons.Lifestyle),
        ("WidgetCategory_Utilities", SettingsPageIcons.Utilities),
        ("WidgetCategory_Container", SettingsPageIcons.Container),
    ];

    private readonly Dictionary<string, SettingsNavItem> byKey;

    private SettingsPageCatalog(IReadOnlyList<SettingsNavGroup> groups, string defaultPageKey)
    {
        Groups = groups;
        DefaultPageKey = defaultPageKey;

        byKey = new Dictionary<string, SettingsNavItem>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            foreach (var item in group.Items)
            {
                // 同一个键只保留首个（系统设置页优先于组件库子页）。

                byKey.TryAdd(item.PageKey, item);
            }
        }

        DefaultItem = byKey.TryGetValue(DefaultPageKey, out var fallback)
            ? fallback
            : groups.SelectMany(group => group.Items).First();
    }

    /// <summary>左栏分组，顺序即成显示顺序。</summary>
    public IReadOnlyList<SettingsNavGroup> Groups { get; }

    /// <summary>设置窗口的默认落点：旧 <c>SettingsViewModel.DefaultItem</c> 同样是外观页。</summary>
    public string DefaultPageKey { get; }

    public SettingsNavItem DefaultItem { get; }

    /// <summary>按页面键取导航项；不存在返回 null（调用方回退到 <see cref="DefaultItem"/>）。</summary>
    public SettingsNavItem? Find(string? pageKey)
        => !string.IsNullOrEmpty(pageKey) && byKey.TryGetValue(pageKey, out var item) ? item : null;

    /// <summary>
    /// 从 DI 解析 <see cref="IAssemblyProvider"/> 并枚举组件包目录。

    /// 解析不到（引导尚未就绪）或目录不可读时只返回系统设置分组，不抛异常：
    /// 设置窗口必须始终能打开，组件库分组空了就是空了，不能用异常把整个外壳打掉。

    /// </summary>
    public static SettingsPageCatalog Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var assemblyProvider = services.GetService(typeof(IAssemblyProvider)) as IAssemblyProvider;
        var packs = CollectPacks(assemblyProvider);

        var groups = new List<SettingsNavGroup>
        {
            BuildSystemGroup(),
            BuildGalleryGroup(packs),
        };

        return new SettingsPageCatalog(groups, SettingsPageKeys.Appearance);
    }

    private static SettingsNavGroup BuildSystemGroup()
    {
        // 标题全部来自 Locale 资源；顺序对照报告 §3 第二步的系统页表格。

        var items = new List<SettingsNavItem>
        {
            new(SettingsPageKeys.General, Locale.Settings_General, SettingsPageIcons.General, []),
            new(SettingsPageKeys.Appearance, Locale.Settings_Appearance, SettingsPageIcons.Appearance, []),
            new(SettingsPageKeys.Spotlight, Locale.Settings_Spotlight, SettingsPageIcons.Spotlight, []),
            new(SettingsPageKeys.MultiScreen, Locale.Settings_MultiScreen, SettingsPageIcons.MultiScreen, []),
            new(SettingsPageKeys.Advanced, Locale.Settings_Advanced, SettingsPageIcons.Advanced, []),
            new(SettingsPageKeys.Profiles, Locale.Settings_Profiles, SettingsPageIcons.Profiles, []),
            new(SettingsPageKeys.About, Locale.Settings_About, SettingsPageIcons.About, []),
        };

        // 分组标题复用已有资源键（Settings = 设置/Settings）。

        // TODO(Locale)：等资源批次补 <c>Settings_Section_System</c> 之类的专用键后替换，
        // 直接写中英文会破坏「用户可见文案必须走资源」的约束，所以这里宁可复用。

        return new SettingsNavGroup("System", Locale.Settings, items);
    }

    private static SettingsNavGroup BuildGalleryGroup(IReadOnlyList<AssemblyInfo> packs)
    {
        var items = new List<SettingsNavItem>
        {
            // 「全部组件」：等价于旧 Gallery 的无筛选页。标题复用 WidgetCategory_All。

            new(SettingsPageKeys.Gallery, CategoryTitle("WidgetCategory_All"), SettingsPageIcons.Gallery, packs),
        };

        foreach (var (categoryKey, iconPath) in CategoryOrder)
        {
            var members = packs
                .Where(pack => pack.Categories is { Count: > 0 } categories
                               && categories.Contains(categoryKey, StringComparer.Ordinal))
                .ToList();
            if (members.Count == 0) continue; // 空分类不占位置（与旧实现一致）

            items.Add(new SettingsNavItem(
                SettingsPageKeys.GalleryPrefix + categoryKey,
                CategoryTitle(categoryKey),
                iconPath,
                members));
        }

        // 编译在分类方案之前、或第三方直接丢进来的包：给它们一个落点，而不是从导航里消失。

        var others = packs
            .Where(pack => pack.Categories is not { Count: > 0 })
            .ToList();
        if (others.Count > 0)
        {
            items.Add(new SettingsNavItem(
                SettingsPageKeys.GalleryPrefix + UncategorizedCategoryKey,
                CategoryTitle(UncategorizedCategoryKey),
                SettingsPageIcons.OtherPacks,
                others));
        }

        // 分组标题复用 Widgets = 组件/Widgets。

        // TODO(Locale)：同上，专用键（例如 Settings_Section_Gallery）补齐后替换。

        return new SettingsNavGroup("Gallery", Locale.Widgets, items);
    }

    /// <summary>
    /// 分类资源键 → 本地化标题。资源缺失时回退到资源键本身：不写死中英文，也不让导航项变空。

    /// </summary>
    private static string CategoryTitle(string resourceKey)
        => Locale.ResourceManager.GetString(resourceKey, Locale.Culture) ?? resourceKey;

    /// <summary>
    /// 枚举组件包：同名多版本只保留最高版本（与旧 <c>SettingsViewModel</c> 的去重规则一致），
    /// 再按显示名排序，保证导航顺序稳定（目录枚举顺序不保证）。

    /// </summary>
    private static IReadOnlyList<AssemblyInfo> CollectPacks(IAssemblyProvider? assemblyProvider)
    {
        if (assemblyProvider is null) return [];

        try
        {
            return assemblyProvider
                .GetAssemblyInfos(Const.WidgetsFolder)
                .Select(group => group.MaxBy(pack => pack.Version))
                .Where(pack => pack is not null)
                .Select(pack => pack!)
                .OrderBy(pack => pack.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // 组件目录缺失/被占用不该让设置窗口打不开；导航退化成只有系统设置。

            return [];
        }
    }
}
