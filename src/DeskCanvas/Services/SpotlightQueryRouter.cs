using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// A category the launcher can list without a query (the 聚焦搜索 category orbs).
/// <para>
/// It lives here rather than reusing <c>ViewModels.SpotlightCategoryKind</c> because the router
/// is the layer the view model depends on, not the other way round — and because the four browse
/// targets are a <b>provider</b> question (who can enumerate what), while the chip kind is a
/// <b>filter</b> question (which rows a chip keeps).
/// </para>
/// </summary>
public enum SpotlightBrowseKind
{
    /// <summary>应用: the whole launcher catalogue.</summary>
    Apps,

    /// <summary>文件: whatever the file backend can list with no query (may be nothing).</summary>
    Files,

    /// <summary>组件: every external entry (workflows + widget data).</summary>
    Widgets,

    /// <summary>剪贴板: the full history.</summary>
    Clipboard,
}

/// <summary>一次聚焦搜索查询。Limit 是每个结果源各自的上限（不是总量）。</summary>
/// <param name="Query">原始查询文本；空白查询 → 只出 Recents。</param>
/// <param name="Limit">应用 / 剪贴板 / 外部提供者各自的上限，默认 40。</param>
/// <param name="FileLimit">
/// 文件源的上限，默认 <see cref="FileSearchService.MaxResults"/>。文件与其它源分开定容：
/// 其它源天然只有几十条候选，而一次文件名查询在整盘索引里能命中成千上万条——文件列表
/// 必须给足，否则用户要找的那个文件根本进不了结果（见 FileSearchService 的候选池说明）。
/// </param>
/// <param name="Browse">
/// 浏览请求：<b>不</b>按查询过滤，而是列出该类别能列出的全部内容（聚焦搜索的分类圆钮）。
/// 设置后 <see cref="Query"/> 必须为空——浏览是"这个类别里有什么"，不是"哪条匹配"；
/// 空查询的短路因此只在没有浏览类别时生效。null（默认）= 普通查询，行为与之前完全一致。
/// </param>
public sealed record SpotlightQueryRequest(
    string Query,
    int Limit = 40,
    int FileLimit = FileSearchService.MaxResults,
    SpotlightBrowseKind? Browse = null);

/// <summary>
/// 一个结果分组。Kind 的顺序即展示顺序（由 <see cref="SpotlightQueryRouter"/> 固定），
/// 组内条目已降序排好——VM 拿到即渲染，不再排序。
/// </summary>
/// <param name="Kind">组类别，VM 用它映射分组标签文案（Locale.Spotlight_Category_*）。</param>
/// <param name="Items">组内结果，已按 <see cref="SpotlightResult.Score"/> 降序。</param>
/// <param name="Title">
/// 可选的分组标题。组件提供者（清单 / 课程……）用它给出比 Kind 更具体的组名；
/// null 时 VM 回落到按 Kind 映射的固定文案。
/// </param>
public sealed record SpotlightGroup(
    SpotlightResultKind Kind,
    IReadOnlyList<SpotlightResult> Items,
    string? Title = null);

/// <summary>路由的完整产出。</summary>
/// <param name="Groups">
/// 非空分组，顺序固定：App → 外部结果（Workflow / Widget，按提供者注册顺序）→ File → Clipboard → Web。
/// Web 恒在最后：它是"在浏览器里搜这句话"的兜底动作，不该插在用户真正要找的东西前面。
/// </param>
/// <param name="Recents">
/// 最近使用的应用（数量跟随请求的 Limit，最多 8），永远返回——VM 在空查询态用它渲染应用网格上方
/// 的那一行，避免再单独调 Provider。
/// </param>
/// <param name="SearchingFiles">
/// 文件查询是否因取消 / 超时未完成（诊断用）。true 时文件组缺席不代表"没有文件"，
/// 只是这一次没等完。
/// </param>
/// <param name="Hint">预留字段；当前恒为 null。</param>
public sealed record SpotlightQueryOutcome(
    IReadOnlyList<SpotlightGroup> Groups,
    IReadOnlyList<SpotlightResult> Recents,
    bool SearchingFiles,
    string? Hint);

/// <summary>
/// 统一结果管线：把应用 / 文件 / 剪贴板 / 外部结果（快捷指令、组件数据）四个结果源并发跑完，
/// 按固定 Kind 顺序分成非空组，组内降序，VM 拿到即用。
/// <para>
/// <b>并发模型：</b>四个源互不依赖，<see cref="Task.WhenAll"/> 并行；每个源独立
/// try-catch 降级为空组——一个源失败（含取消）不拖垮其他三个，这与"击键不能丢结果"的
/// 体验契约一致。文件源可在设置里关闭；空白查询直接短路成"只填 Recents"。
/// </para>
/// <para>本类不抛异常：任何装配外的故障都表现为空组 / 空 Recents。</para>
/// </summary>
public sealed class SpotlightQueryRouter
{
    /// <summary>工作流带的首个分数：位于 usage-only 带（≤600）之内、文件带（500 起）附近，由分组隔离保证不打架。</summary>
    private const double WorkflowBaseScore = 480;

    /// <summary>组件数据（清单 / 课程）的首个分数；与工作流同带，分组隔离。</summary>
    private const double WidgetBaseScore = 460;

    /// <summary>相邻工作流之间的分差。</summary>
    private const double WorkflowScoreStep = 5;

    /// <summary>查询 Limit 的兜底值（请求里的 Limit 非正时使用）。</summary>
    private const int DefaultLimit = 40;

    private static readonly IReadOnlyList<SpotlightResult> EmptyResults = [];

    private readonly SpotlightAppProvider apps;
    private readonly SpotlightFileProvider files;
    private readonly SpotlightClipboardProvider clipboard;
    private readonly SpotlightExternalResults external;
    private readonly SpotlightWebProvider web;
    private readonly IAppSettingsProvider settings;

    /// <summary>Builds the router over the result sources and the settings provider.</summary>
    public SpotlightQueryRouter(
        SpotlightAppProvider apps,
        SpotlightFileProvider files,
        SpotlightClipboardProvider clipboard,
        SpotlightExternalResults external,
        IAppSettingsProvider settings,
        SpotlightWebProvider web)
    {
        // 依赖全是宿主装配的核心单例，null 是装配 bug——按本目录先例 fail fast，
        // 之后 RouteAsync 兑现"不抛异常"契约。
        this.apps = apps ?? throw new ArgumentNullException(nameof(apps));
        this.files = files ?? throw new ArgumentNullException(nameof(files));
        this.clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        this.external = external ?? throw new ArgumentNullException(nameof(external));
        this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
        this.web = web ?? throw new ArgumentNullException(nameof(web));
    }

    /// <summary>
    /// 跑一次完整查询。取消时同样返回（降级后的）outcome 而不是抛出——调用方在击键循环里
    /// 丢弃过期结果即可，无需为取消单独写 try-catch。
    /// </summary>
    public async Task<SpotlightQueryOutcome> RouteAsync(SpotlightQueryRequest request, CancellationToken ct)
    {
        var limit = request is { Limit: > 0 } ? request.Limit : DefaultLimit;
        var fileLimit = request is { FileLimit: > 0 } ? request.FileLimit : FileSearchService.MaxResults;
        var query = request?.Query?.Trim() ?? string.Empty;

        var recents = SafeRecents(limit);

        // A browse is the one request whose empty query means something: "list the category", not
        // "there is nothing to search". It is answered before the short-circuit below, which keeps
        // the query contract ("empty → recents only") intact for every ordinary keystroke.
        if (request?.Browse is { } browse)
            return await RouteBrowseAsync(browse, limit, fileLimit, recents, ct).ConfigureAwait(false);

        // 空白查询短路：apps 返回空、只填 Recents，其余全空（组契约：空组不出现）。
        if (query.Length == 0)
            return new SpotlightQueryOutcome([], recents, false, null);

        var appsTask = GuardAsync(() => apps.QueryAsync(query, limit, ct));
        var workflowTask = GuardGroupsAsync(() => QueryExternalAsync(query, limit, ct));
        var clipboardTask = GuardAsync(() => clipboard.QueryAsync(query, limit, ct));
        var filesTask = FilesEnabled()
            ? GuardAsync(() => files.QueryAsync(query, fileLimit, ct))
            : Task.FromResult((Items: EmptyResults, Faulted: false));

        await Task.WhenAll(appsTask, filesTask, clipboardTask, workflowTask).ConfigureAwait(false);

        var groups = new List<SpotlightGroup>(8);
        AddGroup(groups, SpotlightResultKind.App, appsTask.Result.Items);
        groups.AddRange(workflowTask.Result);
        AddGroup(groups, SpotlightResultKind.File, filesTask.Result.Items);
        AddGroup(groups, SpotlightResultKind.Clipboard, clipboardTask.Result.Items);

        // The web row is not a search source: it is prepared, not queried, and it is the last
        // group by contract (see SpotlightQueryOutcome). It is also the reason a query that
        // matched nothing still shows a row — exactly like "Search Google for …" in Spotlight.
        if (web.Prepare(query) is { } target)
            AddGroup(groups, SpotlightResultKind.Web, [SpotlightWebProvider.CreateResult(target)]);

        return new SpotlightQueryOutcome(groups, recents, filesTask.Result.Faulted, null);
    }

    /// <summary>
    /// Answer a browse request: one category, no query, no ranking. Only that category's source
    /// runs — a browse must cost the same as one normal query source, never four — and it is
    /// guarded and cancellable exactly like the query path, so a slow source degrades to an empty
    /// list instead of a stuck launcher.
    /// <para>
    /// The 网页 action row is deliberately absent: browsing is "show me what is in this category",
    /// and a row that opens a browser is not in any category (see
    /// <see cref="SpotlightQueryOutcome"/>'s Web-last rule for why it exists at all).
    /// </para>
    /// </summary>
    private async Task<SpotlightQueryOutcome> RouteBrowseAsync(
        SpotlightBrowseKind browse, int limit, int fileLimit,
        IReadOnlyList<SpotlightResult> recents, CancellationToken ct)
    {
        var groups = new List<SpotlightGroup>(4);
        var searchingFiles = false;

        switch (browse)
        {
            case SpotlightBrowseKind.Apps:
            {
                var appsResult = await GuardAsync(() => apps.BrowseAsync(limit, ct)).ConfigureAwait(false);
                AddGroup(groups, SpotlightResultKind.App, appsResult.Items);
                break;
            }

            case SpotlightBrowseKind.Files:
            {
                if (FilesEnabled())
                {
                    var fileResult = await GuardAsync(() => files.BrowseAsync(fileLimit, ct)).ConfigureAwait(false);
                    AddGroup(groups, SpotlightResultKind.File, fileResult.Items);
                    searchingFiles = fileResult.Faulted;
                }

                break;
            }

            case SpotlightBrowseKind.Clipboard:
            {
                var clipboardResult = await GuardAsync(() => clipboard.BrowseAsync(limit, ct)).ConfigureAwait(false);
                AddGroup(groups, SpotlightResultKind.Clipboard, clipboardResult.Items);
                break;
            }

            case SpotlightBrowseKind.Widgets:
            {
                // The external providers are plugins: they get an empty query and answer with
                // whatever they can list (see ISpotlightExternalProvider). A plugin that only
                // knows how to score a query simply contributes nothing here.
                var externalGroups = await GuardGroupsAsync(
                    () => QueryExternalAsync(string.Empty, limit, ct)).ConfigureAwait(false);
                groups.AddRange(externalGroups);
                break;
            }
        }

        return new SpotlightQueryOutcome(groups, recents, searchingFiles, null);
    }

    /// <summary>Whether 文件搜索 is on; a settings read that fails counts as on (same rule as the query path).</summary>
    private bool FilesEnabled()
    {
        try
        {
            return settings.Get().EffectiveLauncher.FileSearch;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 把异步取回的网页命中并进已有分组：动作行（分 300）保持在最上，命中按其分数排在其下。
    /// 第二个阶段之所以存在，是因为网页是网络请求——本地四个源必须先渲染，网页回来再补，
    /// 不能让一次 DNS 超时把整次搜索拖慢。
    /// </summary>
    /// <param name="groups">第一阶段的路由结果（可能不含 Web 组）。</param>
    /// <param name="hits">SpotlightWebResults 产出的命中行（Kind 均为 Web、WebUrl 非 null）。</param>
    public static IReadOnlyList<SpotlightGroup> WithWebResults(
        IReadOnlyList<SpotlightGroup> groups, IReadOnlyList<SpotlightResult> hits)
    {
        if (hits == null || hits.Count == 0) return groups ?? [];

        var merged = new List<SpotlightGroup>((groups?.Count ?? 0) + 1);
        var attached = false;

        foreach (var group in groups ?? [])
        {
            if (!attached && group.Kind == SpotlightResultKind.Web)
            {
                attached = true;
                var items = new List<SpotlightResult>(group.Items.Count + hits.Count);
                items.AddRange(group.Items);
                foreach (var hit in hits)
                {
                    // Idempotent: a retry (or a cache hit merged twice) must not double a row.
                    if (items.Any(existing => string.Equals(existing.Id, hit.Id, StringComparison.Ordinal)))
                        continue;
                    items.Add(hit);
                }

                merged.Add(new SpotlightGroup(group.Kind, items.OrderByDescending(r => r.Score).ToList(), group.Title));
                continue;
            }

            merged.Add(group);
        }

        if (!attached)
            merged.Add(new SpotlightGroup(
                SpotlightResultKind.Web, hits.OrderByDescending(r => r.Score).ToList()));

        return merged;
    }

    /// <summary>
    /// 快路径：仅查询内存中的已存入应用（即应用网格目录），0 延迟立即返回。
    /// 用于击键时即刻在搜索框下方渲染已安装/快捷方式应用，不必等待慢速文件与外部提供者。
    /// </summary>
    public async Task<IReadOnlyList<SpotlightGroup>> RouteFastAppsAsync(string query, int limit, CancellationToken ct)
    {
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return [];

        try
        {
            var appResults = await apps.QueryAsync(trimmed, limit, ct).ConfigureAwait(false);
            if (appResults.Count == 0) return [];

            return [new SpotlightGroup(SpotlightResultKind.App, appResults)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// Recents 永远尽力返回；目录 / usage store 的任何异常降级为空列表。
    /// <para>
    /// 数量跟随请求的 <paramref name="limit"/>：最近使用在界面上是"应用网格的一行"，能放几个由
    /// 列数决定，固定取 8 会让七列网格的最近使用行折成两行。上限仍由
    /// <see cref="LauncherSettings.MaxRecentApps"/> 兜底。
    /// </para>
    /// </summary>
    private IReadOnlyList<SpotlightResult> SafeRecents(int limit)
    {
        try
        {
            return apps.Recents(Math.Clamp(limit, 1, LauncherSettings.MaxRecentApps));
        }
        catch
        {
            return EmptyResults;
        }
    }

    /// <summary>
    /// 逐个查询外部提供者并映射为结果行，每个提供者产出<b>自己的一组</b>：快捷指令是
    /// Workflow 组，组件数据（清单 / 课程）是带自定义标题的 Widget 组。分数按 external
    /// 返回顺序全局递减（工作流 480 / 组件 460 起步长 5），跨提供者连续——这样两个插件
    /// 的命中之间也有确定的全序。单个插件的失败被隔离：坏插件只贡献空结果，不拖垮其他插件。
    /// </summary>
    private async Task<IReadOnlyList<SpotlightGroup>> QueryExternalAsync(
        string query, int limit, CancellationToken ct)
    {
        var providers = external.Providers;
        if (providers.Count == 0) return [];

        var groups = new List<SpotlightGroup>();
        var scoreIndex = 0;
        foreach (var provider in providers)
        {
            IReadOnlyList<SpotlightExternalHit> hits;
            try
            {
                hits = await provider.QueryAsync(query, limit, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // 取消是调用方的节奏，交由路由层统一降级。
            }
            catch
            {
                continue; // 插件自报的异常：跳过它，其余插件照常。
            }

            var items = new List<SpotlightResult>(Math.Min(hits.Count, limit));
            var kind = SpotlightResultKind.Workflow;
            string? groupTitle = null;
            for (var i = 0; i < hits.Count && items.Count < limit; i++)
            {
                var hit = hits[i];
                kind = hit.Kind;
                groupTitle = hit.GroupTitle;
                var baseScore = kind == SpotlightResultKind.Widget ? WidgetBaseScore : WorkflowBaseScore;
                var prefix = kind == SpotlightResultKind.Widget ? "wd:" : "wf:";
                items.Add(new SpotlightResult(
                    kind,
                    prefix + hit.Id,
                    hit.Title,
                    Subtitle: hit.Subtitle,
                    Score: baseScore - WorkflowScoreStep * scoreIndex++,
                    TargetPath: null, // 外部结果的"打开"就是 RunAsync，没有可 Shell 的路径
                    IconPath: null,   // 无图标来源 → VM 首字母占位
                    Badge: hit.Badge,
                    RunAsync: hit.RunAsync,
                    GroupTitle: groupTitle));
            }

            if (items.Count > 0)
                groups.Add(new SpotlightGroup(kind, items, groupTitle));
        }

        return groups;
    }

    /// <summary>
    /// 包一层 try-catch：任何源的失败（含取消）都降级为空组，faulted 只用于
    /// SearchingFiles 的诊断标记。Providers 自身吞掉非取消异常，这里的 catch 是
    /// 最后一道防线。
    /// </summary>
    private static async Task<(IReadOnlyList<SpotlightResult> Items, bool Faulted)> GuardAsync(
        Func<Task<IReadOnlyList<SpotlightResult>>> run)
    {
        try
        {
            return (await run().ConfigureAwait(false), false);
        }
        catch
        {
            return (EmptyResults, true);
        }
    }

    /// <summary>Same degradation contract for a source that produces several groups.</summary>
    private static async Task<IReadOnlyList<SpotlightGroup>> GuardGroupsAsync(
        Func<Task<IReadOnlyList<SpotlightGroup>>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch
        {
            return [];
        }
    }

    /// <summary>非空才入组，组内降序（OrderByDescending 稳定，同分保持源内原有顺序）。</summary>
    private static void AddGroup(List<SpotlightGroup> groups, SpotlightResultKind kind, IReadOnlyList<SpotlightResult> items)
    {
        if (items.Count == 0) return;
        groups.Add(new SpotlightGroup(kind, items.OrderByDescending(r => r.Score).ToList()));
    }
}
