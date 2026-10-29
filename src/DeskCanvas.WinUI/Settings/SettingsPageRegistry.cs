using System;
using System.Collections.Generic;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 页面键 → 页面工厂的自注册表。**并行迁移页面时只改自己页面的文件**：每个页面在自己的
/// <c>.xaml.cs</c> 里用 <c>[ModuleInitializer]</c> 调用 <see cref="Register"/>，不需要改
/// <see cref="SettingsPageFactory"/> 或设置窗口的任何文件，避免多人同时改同一个 switch。
/// <example>
/// <code>
/// internal static class GeneralPageRegistration
/// {
///     [ModuleInitializer]
///     internal static void Register()
///         => SettingsPageRegistry.Register(
///                SettingsPageKeys.General,
///                services => new GeneralPage(services));
/// }
/// </code>
/// </example>
/// <para>
/// 键空间（与 <c>DeskCanvas.Services.SettingsPageKeys</c> 对齐）：
/// <list type="bullet">
/// <item><description><c>General</c>/<c>Appearance</c>/<c>Spotlight</c>/<c>MultiScreen</c>/<c>Advanced</c>/
/// <c>Profiles</c>/<c>About</c> —— 7 个系统设置页，精确键。</description></item>
/// <item><description><c>Gallery</c> —— 组件库「全部组件」页。</description></item>
/// <item><description><c>Gallery:&lt;后缀&gt;</c> —— 组件库子页（分类页、其他扩展、单包页）。用
/// <see cref="RegisterPrefix"/> 一次覆盖全部，页面在 <c>Attach</c> 里从上下文读本页的组件包。</description></item>
/// </list>
/// 精确键优先于前缀；前缀按登记顺序匹配，具体前缀要先登记。
/// </para>
/// </summary>
public static class SettingsPageRegistry
{
    private static readonly object Gate = new();

    private static readonly Dictionary<string, Func<SettingsPageContext, ISettingsPage>> Exact =
        new(StringComparer.Ordinal);

    private static readonly List<(string Prefix, Func<SettingsPageContext, ISettingsPage> Factory)> Prefixes = [];

    /// <summary>
    /// 登记一个精确页面键的页面工厂。重复登记会覆盖（便于热重载/测试）。

    /// <para>工厂只负责 <c>new</c> 出页面实例；依赖请从 <paramref name="factory"/> 的
    /// <see cref="IServiceProvider"/> 解析（IAppSettingsProvider/IAssemblyProvider/ProfileService/
    /// DisplayMonitorService/UpdateService/ILocaleService 等），不要把服务塞进构造签名的第二套参数表。</para>
    /// </summary>
    public static void Register(string pageKey, Func<IServiceProvider, ISettingsPage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pageKey);
        ArgumentNullException.ThrowIfNull(factory);

        lock (Gate)
        {
            Exact[pageKey] = context => factory(context.Services);
        }
    }

    /// <summary>
    /// 登记一个前缀页面工厂（例如 <c>SettingsPageKeys.GalleryPrefix</c>）。页面在 Attach 时从
    /// <see cref="SettingsPageContext.PageKey"/>/<see cref="SettingsPageContext.Assemblies"/> 区分自己是哪一页。

    /// </summary>
    public static void RegisterPrefix(string prefix, Func<IServiceProvider, ISettingsPage> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(factory);

        lock (Gate)
        {
            Prefixes.Add((prefix, context => factory(context.Services)));
        }
    }

    /// <summary>是否有页面（精确或前缀）认领这个键；覆盖率检查/诊断可以直接用。</summary>
    public static bool IsRegistered(string pageKey)
    {
        if (string.IsNullOrEmpty(pageKey)) return false;

        lock (Gate)
        {
            if (Exact.ContainsKey(pageKey)) return true;

            foreach (var (prefix, _) in Prefixes)
            {
                if (pageKey.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }

            return false;
        }
    }

    /// <summary>尝试创建页面；没有登记时返回 false，由工厂回退到占位页。</summary>
    public static bool TryCreate(SettingsPageContext context, out ISettingsPage page)
    {
        ArgumentNullException.ThrowIfNull(context);

        Func<SettingsPageContext, ISettingsPage>? factory = null;
        lock (Gate)
        {
            if (!Exact.TryGetValue(context.PageKey, out factory))
            {
                foreach (var (prefix, candidate) in Prefixes)
                {
                    if (!context.PageKey.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    factory = candidate;
                    break;
                }
            }
        }

        if (factory is null)
        {
            page = null!;
            return false;
        }

        page = factory(context);
        return true;
    }
}
