using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Services;
using DeskCanvas.Locales;

namespace DeskCanvas.Services;

/// <summary>
/// 应用结果源：对 <see cref="LauncherCatalog.Entries"/> 用 <see cref="SpotlightRanking.Score"/>
/// 打分（文本相关度 + 置顶 / 近期 / 次数 + 目标路径别名通道），分大于 0 才入选，降序取 limit。
/// <para>
/// 分数直接采用 <see cref="SpotlightRanking"/> 的带契约输出（exact 2000 / quick key 1900 /
/// prefix ~900 …），与应用网格过滤共用同一套带距，用户在网格和结果列表里感知到同一套排序。
/// </para>
/// </summary>
public sealed class SpotlightAppProvider
{
    private readonly LauncherCatalog catalog;
    private readonly SpotlightUsageStore usage;

    /// <summary>Builds the provider over the launcher catalogue and the usage store.</summary>
    public SpotlightAppProvider(LauncherCatalog catalog, SpotlightUsageStore usage)
    {
        // 两个依赖都是宿主装配的核心服务，null 是装配 bug 而非环境故障——按
        // SpotlightClipboardService 的先例在此 fail fast，之后所有成员兑现"不抛异常"契约。
        this.catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        this.usage = usage ?? throw new ArgumentNullException(nameof(usage));
    }

    /// <summary>
    /// 按查询对目录打分。空查询返回空（Recents 由 <see cref="Recents"/> 负责）；任何条目级
    /// 异常降级为空组——打分发生在击键热路径上，一条脏数据不能拖垮整批结果。
    /// </summary>
    public async Task<IReadOnlyList<SpotlightResult>> QueryAsync(string query, int limit, CancellationToken ct)
    {
        if (limit <= 0) return [];
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return [];

        try
        {
            var scored = new List<SpotlightResult>();
            foreach (var entry in catalog.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var entryUsage = usage.Get(entry.Key);
                var score = SpotlightRanking.Score(entry.Name, trimmed, entryUsage, entry.Target);
                if (score <= 0) continue;
                scored.Add(CreateResult(entry, entryUsage, score));
            }

            return scored.OrderByDescending(r => r.Score).Take(limit).ToList();
        }
        catch (OperationCanceledException)
        {
            throw; // 取消属于调用方的节奏控制，交由路由层统一降级。
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 最近使用的应用（空查询态的首行）：按 <see cref="LauncherUsage.LastLaunchedUtc"/> 新在前，
    /// 取 ≤limit 条，候选条件为 LaunchCount&gt;0 或置顶。从未启动的置顶项排在末尾。
    /// <para>
    /// 列表顺序即显示顺序（新在前）；Score 字段赋的是 usage-only 分（SpotlightRanking 对空
    /// 查询的输出），保持与查询态同一把尺子，VM 若要按分重排也是自洽的。
    /// </para>
    /// </summary>
    public IReadOnlyList<SpotlightResult> Recents(int limit)
    {
        if (limit <= 0) return [];

        try
        {
            var candidates = new List<SpotlightResult>();
            foreach (var entry in catalog.Entries)
            {
                var entryUsage = usage.Get(entry.Key);
                if (entryUsage.LaunchCount <= 0 && !entryUsage.Pinned) continue;
                candidates.Add(CreateResult(entry, entryUsage, SpotlightRanking.Score(entry.Name, string.Empty, entryUsage, entry.Target)));
            }

            // null（从未启动）降级为 MinValue，自然沉底；OrderByDescending 稳定，
            // 同刻度项保持目录的名字序。
            return candidates
                .OrderByDescending(r => r.LastLaunchedUtc ?? DateTime.MinValue)
                .Take(limit)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 浏览（应用圆钮 Alt+1）：列出目录里的每一个应用，不做任何文本匹配。
    /// <para>
    /// 排序仍走 <see cref="SpotlightRanking"/>：空查询时它输出 usage-only 分（置顶 / 近期 /
    /// 次数），所以浏览列表和用户平时看到的顺序是同一把尺子，未用过的应用同分、按目录名字序
    /// 稳定排列。这是与 <see cref="QueryAsync"/> 分开的一个方法而不是"空查询特例"：查询路径的
    /// 契约（空查询 = 没有结果）被 <see cref="SpotlightQueryRouter"/> 的空查询短路依赖着，
    /// 把它改宽会让每一次清空输入框都变成一次全目录扫描。
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<SpotlightResult>> BrowseAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);

        try
        {
            var all = new List<SpotlightResult>(catalog.Entries.Count);
            foreach (var entry in catalog.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var entryUsage = usage.Get(entry.Key);
                all.Add(CreateResult(entry, entryUsage, SpotlightRanking.Score(entry.Name, string.Empty, entryUsage, entry.Target)));
            }

            return Task.FromResult<IReadOnlyList<SpotlightResult>>(
                all.OrderByDescending(r => r.Score).Take(limit).ToList());
        }
        catch (OperationCanceledException)
        {
            throw; // 取消属于调用方的节奏控制，交由路由层统一降级。
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);
        }
    }

    /// <summary>目录条目 → 统一结果行。TargetPath/IconPath 都用 lnk 路径：Enter 经 Shell 打开快捷方式（其参数 / 工作目录生效），图标也从 lnk 抽取。</summary>
    private static SpotlightResult CreateResult(LauncherEntry entry, LauncherUsage entryUsage, double score) =>
        new(
            SpotlightResultKind.App,
            entry.Key, // LauncherEntry.Key 已是小写路径，Id 规则直接满足
            entry.Name,
            Subtitle: null,
            Score: score,
            TargetPath: entry.ShortcutPath,
            IconPath: entry.ShortcutPath,
            IsPinned: entryUsage.Pinned,
            LaunchCount: entryUsage.LaunchCount,
            LastLaunchedUtc: entryUsage.LastLaunchedUtc,
            IsBroken: entry.IsBroken);
}

/// <summary>
/// 文件结果源：包装 <see cref="FileSearchService.SearchAsync"/>（去抖由 VM 负责，这里直查）。
/// <para>
/// 命中按返回顺序赋递减分（首个 500，步长 5）——FileSearchService 内部已按自己的相关度排过序，
/// 这里只需把"顺序"翻译成"分数"，让统一结果行在没有 FileSearchQuery 依赖的情况下保序。
/// </para>
/// </summary>
public sealed class SpotlightFileProvider
{
    /// <summary>
    /// 首个命中的分数；步长 5。分数只用于组内保序（文件组与其它组天然隔离），所以列表再长
    /// 也只是把顺序翻译成分数，末位为负不影响显示。
    /// </summary>
    private const double FirstHitScore = 500;

    /// <summary>相邻命中之间的分差。</summary>
    private const double ScoreStep = 5;

    private readonly FileSearchService files;

    /// <summary>Builds the provider over the file search service.</summary>
    public SpotlightFileProvider(FileSearchService files)
    {
        this.files = files ?? throw new ArgumentNullException(nameof(files));
    }

    /// <summary>按查询搜文件；空查询返回空。SearchAsync 自身从不抛异常，这里只保留取消的传播。</summary>
    public async Task<IReadOnlyList<SpotlightResult>> QueryAsync(string query, int limit, CancellationToken ct)
    {
        if (limit <= 0) return [];
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return [];

        var hits = await files.SearchAsync(trimmed, ct).ConfigureAwait(false);

        var results = new List<SpotlightResult>(hits.Count);
        for (var i = 0; i < hits.Count && i < limit; i++)
        {
            var hit = hits[i];
            results.Add(new SpotlightResult(
                SpotlightResultKind.File,
                "file:" + hit.FullPath.ToLowerInvariant(), // Id 规则：小写完整路径，VM 侧去重 / 记 usage 依赖这个稳定性
                hit.Name,
                Subtitle: Path.GetDirectoryName(hit.FullPath), // 同名文件靠所在文件夹区分；数据风格后缀，VM 直接展示
                Score: FirstHitScore - ScoreStep * i,
                TargetPath: hit.FullPath,
                IconPath: hit.FullPath)); // 文件路径直接抽图标；抽不到时 VM 回落首字母占位
        }

        return results;
    }

    /// <summary>
    /// 浏览（文件圆钮 Alt+2）：刻意返回空。
    /// <para>
    /// 文件源没有"列出全部"的廉价答案：Everything 的 IPC 与本地索引都只回答<b>查询</b>，让它们
    /// 无查询地吐出内容等于把整块磁盘的索引搬到列表里——既慢，也不是"文件"这个类别想表达的
    /// 东西。空贡献是诚实的答复，界面用现有文案请用户输入关键词（见 VM 的 NoResultsText），
    /// 而不是编造几行最近文件。
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<SpotlightResult>> BrowseAsync(int limit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);
    }
}

/// <summary>
/// 剪贴板历史结果源：对每个条目的<b>可搜索文本</b>用 <see cref="LauncherScoring.Score"/> 匹配，
/// 分大于 0 才出现；空查询不返回行。
/// <para>
/// 三种格式各有一段可搜索文本：文本条目是正文首行，文件条目是每个文件名（取最高分），
/// 图片条目是「图片 + 尺寸」——这样"复制一张截图后搜 图片 / 1080"仍然找得到它，而不是
/// 因为图片没有文字就永远搜不到。
/// </para>
/// <para>
/// 副标题在此层拼成数据风格的后缀（"来自 chrome · 3m"；文件多一条"等共 N 个文件"），
/// 相对时间用数字+字母简写而不走 Locale——它不是句子而是数据后缀，VM 层不加工，
/// 这样分组标签之外的文案面保持为零。
/// </para>
/// </summary>
public sealed class SpotlightClipboardProvider
{
    /// <summary>浏览列表的首条分数；每条递减 1，只用于保序（组内互相比较，跨组无意义）。</summary>
    private const double BrowseScore = 600;

    private readonly SpotlightClipboardService clipboard;

    /// <summary>Builds the provider over the clipboard history service.</summary>
    public SpotlightClipboardProvider(SpotlightClipboardService clipboard)
    {
        this.clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
    }

    /// <summary>按查询匹配剪贴板历史，降序取 limit。同分项保持 Entries 的新在前顺序（稳定排序）。</summary>
    public Task<IReadOnlyList<SpotlightResult>> QueryAsync(string query, int limit, CancellationToken ct)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);
        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);

        ct.ThrowIfCancellationRequested();

        try
        {
            var results = new List<SpotlightResult>();
            foreach (var entry in clipboard.Entries) // 快照，newest first
            {
                var score = ScoreOf(entry, trimmed);
                if (score <= 0) continue;

                results.Add(CreateResult(entry, score));
            }

            return Task.FromResult<IReadOnlyList<SpotlightResult>>(
                results.OrderByDescending(r => r.Score).Take(limit).ToList());
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);
        }
    }

    /// <summary>
    /// 浏览（剪贴板圆钮 Alt+4）：整段历史，新在前，不做文本匹配。
    /// <para>
    /// 分数在这里只用来<b>保序</b>：<see cref="SpotlightQueryRouter"/> 会按分降序重排，所以给
    /// 每条赋一个递减分即可把"历史顺序"翻译成"显示顺序"，与查询态同一个机制。历史本来就只有
    /// 几十到几百条，全部列出不需要额外扫描。
    /// </para>
    /// </summary>
    public Task<IReadOnlyList<SpotlightResult>> BrowseAsync(int limit, CancellationToken ct)
    {
        if (limit <= 0) return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);

        ct.ThrowIfCancellationRequested();

        try
        {
            var entries = clipboard.Entries; // 快照，newest first
            var results = new List<SpotlightResult>(Math.Min(entries.Count, limit));
            for (var i = 0; i < entries.Count && results.Count < limit; i++)
            {
                results.Add(CreateResult(entries[i], BrowseScore - i));
            }

            return Task.FromResult<IReadOnlyList<SpotlightResult>>(results);
        }
        catch
        {
            return Task.FromResult<IReadOnlyList<SpotlightResult>>([]);
        }
    }

    /// <summary>
    /// 一条历史 → 一行结果。三种格式只在"标题 / 可搜索文本 / 载荷"上不同，其余（Id 规则、
    /// 副标题、复制回去的执行体）完全共用，所以只有一个出口。
    /// </summary>
    private SpotlightResult CreateResult(ClipboardEntry entry, double score)
    {
        var captured = entry;
        return new SpotlightResult(
            SpotlightResultKind.Clipboard,
            "clip:" + entry.Id,
            TitleOf(entry),
            Subtitle: SubtitleOf(entry),
            Score: score,
            ClipboardText: entry.Kind == ClipboardContentKind.Text ? entry.Text : null,
            IsLink: entry.IsLink,
            SourceProcess: entry.SourceProcess,
            CapturedUtc: entry.CapturedUtc,
            RunAsync: () => Task.FromResult(clipboard.CopyBack(captured.Id) is not null),
            ClipboardKind: entry.Kind,
            ClipboardImagePath: entry.ImagePath,
            ClipboardFilePaths: entry.FilePaths,
            ClipboardImageWidth: entry.ImageWidth,
            ClipboardImageHeight: entry.ImageHeight);
    }

    /// <summary>
    /// 行标题。文本：第一行（多行文本的"标题"永远是用户复制时看到的第一行）；图片：格式 + 尺寸；
    /// 文件：第一个文件名（"等共 N 个"进副标题）。
    /// </summary>
    public static string TitleOf(ClipboardEntry entry) => entry.Kind switch
    {
        ClipboardContentKind.Image => string.Format(
            Locale.Spotlight_Clipboard_Image_Title, entry.ImageWidth, entry.ImageHeight),
        ClipboardContentKind.Files => entry.FilePaths is { Count: > 0 } files
            ? Path.GetFileName(files[0].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            : Locale.Spotlight_Clipboard_Files_Empty,
        _ => FirstLine(entry.Text),
    };

    /// <summary>
    /// 一条条目的匹配分。文本打分正文首行（历史行为的延续）；文件对<b>每个</b>文件名和完整路径
    /// 各打一次取最高（文件名短，分数天然高于长字符串，正好符合"按文件名找人"）；
    /// 图片用「图片 1920×1080」这类合成串，让"图片"和像素数都可搜。
    /// </summary>
    private static double ScoreOf(ClipboardEntry entry, string query)
    {
        switch (entry.Kind)
        {
            case ClipboardContentKind.Files when entry.FilePaths is { Count: > 0 } files:
            {
                var best = 0;
                foreach (var file in files)
                {
                    best = Math.Max(best, LauncherScoring.Score(Path.GetFileName(file), query));
                    if (best >= 700) break; // a real name hit: no longer string can beat it meaningfully
                }
                return best;
            }
            case ClipboardContentKind.Image:
                return LauncherScoring.Score(
                    TitleOf(entry) + " image 图片 png screenshot 截图", query);
            default:
                return LauncherScoring.Score(FirstLine(entry.Text), query);
        }
    }

    /// <summary>
    /// 相对时间的固定格式：<c>&lt;60s→"now"、&lt;60m→"Nm"、&lt;24h→"Nh"、否则 "Nd"</c>，
    /// UtcNow 基准。未来的时间戳（时钟偏差）落在第一档，显示 "now" 而不是负数。
    /// </summary>
    public static string RelativeTime(DateTime capturedUtc)
    {
        var seconds = (DateTime.UtcNow - capturedUtc).TotalSeconds;
        if (seconds < 60) return "now";
        if (seconds < 3600) return $"{(int)(seconds / 60)}m";
        if (seconds < 86400) return $"{(int)(seconds / 3600)}h";
        return $"{(int)(seconds / 86400)}d";
    }

    /// <summary>首行：到第一个换行为止并去首尾空白——多行文本的"标题"永远是用户复制时看到的第一行。</summary>
    private static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var newline = text.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? text : text[..newline];
        return line.Trim();
    }

    /// <summary>
    /// 副标题：数量（仅多文件时）→ 来源 → 相对时间，用 " · " 连接。
    /// 有来源进程时为"来自 {source} · {相对时间}"，否则只有相对时间。
    /// </summary>
    public static string SubtitleOf(ClipboardEntry entry)
    {
        var relative = RelativeTime(entry.CapturedUtc);
        var parts = new List<string>(3);

        if (entry.Kind == ClipboardContentKind.Files && entry.FilePaths is { Count: > 1 } files)
            parts.Add(string.Format(Locale.Spotlight_Clipboard_Files_Count, files.Count));

        if (!string.IsNullOrEmpty(entry.SourceProcess))
            parts.Add(string.Format(Locale.Spotlight_Clipboard_Source, entry.SourceProcess));

        parts.Add(relative);
        return string.Join(" · ", parts);
    }
}
