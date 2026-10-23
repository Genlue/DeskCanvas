using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Services;

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
}

/// <summary>
/// 剪贴板历史结果源：对 <see cref="ClipboardEntry.Text"/> 的首行用
/// <see cref="LauncherScoring.Score"/> 匹配，分大于 0 才出现；空查询不返回行。
/// <para>
/// 副标题在此层拼成数据风格的后缀（"来自 chrome · 3m"），相对时间用数字+字母简写而
/// 不走 Locale——它不是句子而是数据后缀，VM 层不加工，这样分组标签之外的文案面保持为零。
/// </para>
/// </summary>
public sealed class SpotlightClipboardProvider
{
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
                var firstLine = FirstLine(entry.Text);
                var score = (double)LauncherScoring.Score(firstLine, trimmed);
                if (score <= 0) continue;

                var captured = entry;
                results.Add(new SpotlightResult(
                    SpotlightResultKind.Clipboard,
                    "clip:" + entry.Id,
                    firstLine,
                    Subtitle: DescribeSource(entry),
                    Score: score,
                    ClipboardText: entry.Text,
                    IsLink: entry.IsLink,
                    SourceProcess: entry.SourceProcess,
                    CapturedUtc: entry.CapturedUtc,
                    RunAsync: () => Task.FromResult(clipboard.CopyBack(captured.Id) is not null)));
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

    /// <summary>副标题：有来源进程 → "来自 {source} · {相对时间}"，否则只有相对时间。</summary>
    private static string DescribeSource(ClipboardEntry entry)
    {
        var relative = RelativeTime(entry.CapturedUtc);
        return string.IsNullOrEmpty(entry.SourceProcess)
            ? relative
            : $"来自 {entry.SourceProcess} · {relative}";
    }
}
