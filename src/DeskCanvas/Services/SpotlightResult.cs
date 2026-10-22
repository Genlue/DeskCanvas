using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeskCanvas.Services;

/// <summary>
/// 统一结果行的类别。分组标签由 ViewModel 层按 Kind 映射（Locale.Spotlight_Category_*），
/// 管线本身只负责产出 Kind，不产生任何 UI 文案。
/// </summary>
public enum SpotlightResultKind
{
    /// <summary>launcher 目录里的应用快捷方式（LauncherCatalog）。</summary>
    App,

    /// <summary>FileSearchService 的文件 / 文件夹命中。</summary>
    File,

    /// <summary>剪贴板历史条目（SpotlightClipboardService）。</summary>
    Clipboard,

    /// <summary>外部插件（如 Shortcuts 组件）注册的工作流；在类别上归入「组件」。</summary>
    Workflow,

    /// <summary>
    /// 组件数据行（清单事项、课程、便签……）。外部提供者声明自己的分组标题，
    /// 因此每个组件的数据在结果里是一组独立的分组，而不是混进「快捷指令」。
    /// </summary>
    Widget,

    /// <summary>
    /// 在浏览器中搜索当前查询（SpotlightWebProvider）。整组只有一行：它是"动作"，不是"匹配"，
    /// 所以恒排在所有真实结果之后。
    /// <para>
    /// 组内还可能有引擎真正返回的网页命中（<see cref="SpotlightResult.WebUrl"/> 非 null）：
    /// 它们与动作行同组，动作行排在最上（分 300），命中在其下递减——"打开搜索"和"这几条结果"
    /// 是同一件事的两种粒度，拆成两个分组的标题只会重复。
    /// </para>
    /// </summary>
    Web,
}

/// <summary>
/// 结果行的 Id 规则。VM 与管线必须逐字一致（usage store 去重、图标缓存、撤销都用它），
/// 因此规则写在唯一一处，而不是在两个文件里各拼一遍字符串。
/// </summary>
public static class SpotlightResultIds
{
    /// <summary>一条引擎返回的网页命中的 Id。</summary>
    public static string WebHit(string url) => "webhit:" + url;
}

/// <summary>
/// 聚焦搜索统一结果行。四个内置 Provider 与外部插件都产出这一种行，ViewModel 拿到即用、
/// 不再二次加工：分数已赋好（组内降序即显示顺序），Id 与 ViewModel.SpotlightItem 的 Id 同规则，
/// 因此 VM 可以直接用 Id 去 usage store 记录启动 / 查图标。
/// <para>
/// 不抛异常契约同样适用于本行的构造方：Provider 对单条脏数据（空名、空路径）降级跳过，
/// 而不是让一次击键的整批结果失败。
/// </para>
/// </summary>
/// <param name="Kind">结果类别，决定分组与图标 / 操作的渲染分支。</param>
/// <param name="Id">
/// 稳定标识，规则与 ViewModel.SpotlightItem 一致：
/// app→<see cref="LauncherEntry.Key"/>（小写路径）；file→"file:"+小写完整路径；
/// clipboard→"clip:"+条目 Id；workflow→"wf:"+工作流 Id；widget→"wd:"+数据行 Id。
/// </param>
/// <param name="Title">主标题（应用名 / 文件名 / 剪贴板首行 / 组件数据行标题）。</param>
/// <param name="Subtitle">
/// 副标题；数据风格的后缀（如剪贴板的"来自 xxx · 3m"）在此层拼好，VM 不加工。
/// </param>
/// <param name="Score">
/// 组内排序分，已由 Provider 赋好：VM 对组内做降序即可，无需再打分。
/// 分数带与 <c>SpotlightRanking</c> 的带契约对齐（文件 500 起）。
/// </param>
/// <param name="TargetPath">
/// Enter 打开 / 执行的目标：应用的 lnk 路径或文件完整路径；组件行为 null。
/// </param>
/// <param name="IconPath">从该路径抽图标；null → VM 用 Title 首字母占位。</param>
/// <param name="Badge">行尾徽标文本（目前只有快捷指令的热键），null 表示不显示。</param>
/// <param name="IsPinned">是否被用户置顶。</param>
/// <param name="LaunchCount">历史启动次数（usage store 中的计数）。</param>
/// <param name="LastLaunchedUtc">最近一次启动时间；null 表示从未启动过。</param>
/// <param name="IsBroken">快捷方式目标已失效（失效项保留展示，由 VM 打上标记）。</param>
/// <param name="ClipboardText">Kind==Clipboard 时的完整剪贴板文本（预览 / 二次复制用）。</param>
/// <param name="IsLink">剪贴板文本是否被判定为链接。</param>
/// <param name="SourceProcess">剪贴板捕获时的来源进程名，无法解析时为 null。</param>
/// <param name="CapturedUtc">剪贴板捕获时间（UTC）。</param>
/// <param name="RunAsync">
/// Enter 执行体；返回 false 表示什么都没发生（VM 据此决定是否关闭窗口）。
/// null 时 VM 回落到按 <see cref="TargetPath"/> Shell 打开。
/// </param>
/// <param name="GroupTitle">
/// Kind == Widget 时的分组标题（清单 / 课程……）。null 时按 Kind 的固定文案分组。
/// </param>
/// <param name="WebQuery">
/// Kind == Web 时的已解析目标（引擎 / 浏览器 / 最终地址）。行标题与预览栏都从它派生，
/// 因此按下回车时不需要再读一次设置——两次读之间用户可能刚好改过引擎。
/// </param>
/// <param name="WebUrl">
/// Kind == Web 且这是<b>引擎返回的一条命中</b>时的地址。null 表示这一行是"在浏览器中搜索"的动作行——
/// 两者同组不同行，用这一个字段区分，而不是再加一个 Kind（分组、类别过滤都会因此多一个分支）。
/// </param>
/// <param name="WebSnippet">命中的摘要正文（已去标签、截断），null 表示没有摘要。</param>
public sealed record SpotlightResult(
    SpotlightResultKind Kind,
    string Id,
    string Title,
    string? Subtitle,
    double Score,
    string? TargetPath = null,
    string? IconPath = null,
    string? Badge = null,
    bool IsPinned = false,
    int LaunchCount = 0,
    DateTime? LastLaunchedUtc = null,
    bool IsBroken = false,
    string? ClipboardText = null,
    bool IsLink = false,
    string? SourceProcess = null,
    DateTime? CapturedUtc = null,
    Func<Task<bool>>? RunAsync = null,
    string? GroupTitle = null,
    SpotlightWebQuery? WebQuery = null,
    string? WebUrl = null,
    string? WebSnippet = null);

/// <summary>
/// 插件（如 Shortcuts 组件）向宿主注册的结果提供者。
/// <para>
/// 宿主不引用插件工程——依赖方向是插件→宿主，插件在自己的初始化里调
/// <see cref="SpotlightExternalResults.Register"/> 把自己挂进来。这样"聚焦搜索"保持一个
/// 可独立构建的核心，插件的增删不需要改宿主一行代码。
/// </para>
/// </summary>
public interface ISpotlightExternalProvider
{
    /// <summary>稳定标识（如 "shortcuts"）；重复 Register 同 Id = 替换。</summary>
    string Id { get; }

    /// <summary>
    /// 用查询取一批命中。实现应当不抛异常：内部失败返回空列表即可——
    /// 一个坏插件不能拖垮路由里的其他结果源。
    /// </summary>
    Task<IReadOnlyList<SpotlightExternalHit>> QueryAsync(string query, int limit, CancellationToken ct);
}

/// <summary>外部提供者返回的单条工作流命中；由路由层映射成 <see cref="SpotlightResult"/>（Kind=Workflow）。</summary>
/// <param name="Id">工作流的稳定 Id（不含 "wf:" 前缀，前缀由路由层统一加）。</param>
/// <param name="Title">主标题。</param>
/// <param name="Subtitle">副标题；null 表示无。</param>
/// <param name="Badge">行尾徽标（快捷指令的热键）；透传给结果行。</param>
/// <param name="RunAsync">Enter 执行体；返回 false 表示什么都没发生。</param>
/// <param name="Kind">
/// 结果类别。默认 <see cref="SpotlightResultKind.Workflow"/> 保持旧插件的语义；
/// 组件数据（清单 / 课程）用 <see cref="SpotlightResultKind.Widget"/> 并给出
/// <paramref name="GroupTitle"/>，结果会以独立分组出现。
/// </param>
/// <param name="GroupTitle">分组标题（Kind == Widget 时使用），如「清单」「课程」。</param>
public sealed record SpotlightExternalHit(
    string Id,
    string Title,
    string? Subtitle,
    string? Badge,
    Func<Task<bool>> RunAsync,
    SpotlightResultKind Kind = SpotlightResultKind.Workflow,
    string? GroupTitle = null);

/// <summary>
/// 外部结果提供者的线程安全注册表。宿主持有一个实例并把它交给
/// <see cref="SpotlightQueryRouter"/>；插件在任意线程 Register / Unregister。
/// <para>
/// 未注册任何提供者时查询为空——路由层把"外部"当成可缺席的第五个结果源，
/// 而不是一个必须存在的服务。
/// </para>
/// </summary>
public sealed class SpotlightExternalResults
{
    // 提供者数量是个位数（当前只有 Shortcuts 组件一个量级），线性查找比维护
    // 第二个字典索引更简单，且 List 保留了注册顺序——查询结果的顺序因此是确定的。
    private readonly object gate = new();
    private readonly List<ISpotlightExternalProvider> providers = [];

    /// <summary>注册一个提供者；同 Id 的旧提供者被替换（插件热重载的场景）。</summary>
    public void Register(ISpotlightExternalProvider provider)
    {
        if (provider is null || string.IsNullOrEmpty(provider.Id)) return;

        lock (gate)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, provider.Id, StringComparison.Ordinal))
                {
                    providers[i] = provider;
                    return;
                }
            }

            providers.Add(provider);
        }
    }

    /// <summary>按 Id 注销一个提供者；未注册过的 Id 是静默 no-op。</summary>
    public void Unregister(string providerId)
    {
        if (string.IsNullOrEmpty(providerId)) return;

        lock (gate)
        {
            for (var i = 0; i < providers.Count; i++)
            {
                if (string.Equals(providers[i].Id, providerId, StringComparison.Ordinal))
                {
                    providers.RemoveAt(i);
                    return;
                }
            }
        }
    }

    /// <summary>当前已注册的提供者快照（注册顺序）。返回副本，遍历期间增删安全。</summary>
    public IReadOnlyList<ISpotlightExternalProvider> Providers
    {
        get
        {
            lock (gate) return providers.ToArray();
        }
    }
}
