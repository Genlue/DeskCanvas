using System;
using System.IO;

namespace DeskCanvas.Core.Services;

/// <summary>
/// The usage record behind one launcher entry. Persisted by the host app
/// (<c>SpotlightUsageStore</c>) and consumed read-only here — the ranking must never depend on
/// where the numbers came from, only on what they mean.
/// </summary>
/// <param name="LaunchCount">Total times the entry was launched. Monotonic; never reset by the ranker.</param>
/// <param name="LastLaunchedUtc">
/// UTC timestamp of the most recent launch; <c>null</c> means "never launched" and earns no
/// recency bonus. Recency decays by the hour so a stale favourite loses to a fresh hit.
/// </param>
/// <param name="Pinned">
/// User-pinned (置顶) flag. Worth more than any other usage signal because it is the only one
/// the user set deliberately.
/// </param>
public sealed record LauncherUsage(
    int LaunchCount = 0,
    DateTime? LastLaunchedUtc = null,
    bool Pinned = false);

/// <summary>
/// The spotlight's combined ranker: text relevance from <see cref="LauncherScoring"/> plus the
/// usage dimensions (pinned, recency, launch count) and the alias channel. Pure and static so a
/// ranking change can be asserted without a window, a store or a folder — same rationale as
/// <see cref="LauncherScoring"/>.
/// <para>
/// Band contract (方案文档 P1.2): exact name &gt; name prefix &gt; alias / target-path hit &gt;
/// recency &gt; launch count &gt; alphabetical. The bands are kept ≥ 100 apart so a usage bonus can
/// never promote an entry across a text tier — a prefix hit must never lose to a frequently
/// launched non-match.
/// </para>
/// </summary>
public static class SpotlightRanking
{
    /// <summary>Band of a full name match, above every text tier.</summary>
    private const double ExactMatchBand = 2000;

    /// <summary>
    /// Floor of the alias (target-path) band. Sits above the usage-only ceiling (600) so an
    /// alias hit always outranks pure recency, per the band contract.
    /// </summary>
    private const double AliasBandFloor = 650;

    /// <summary>Weight of the pinned (置顶) flag; the only signal the user chose deliberately.</summary>
    private const double PinnedBonus = 300;

    /// <summary>Upper bound of the recency bonus, decayed hourly (half-life: one day).</summary>
    private const double RecencyBonusMax = 200;

    /// <summary>Upper bound of the launch-count bonus.</summary>
    private const double CountBonusMax = 100;

    /// <summary>
    /// How many launches saturate the count bonus. 20 launches feels like "habitual" without
    /// letting a one-day burst dominate a year of steady use.
    /// </summary>
    private const double CountBonusSaturation = 20;

    /// <summary>
    /// Hours after which half the recency bonus remains. A day matches the launcher's rhythm:
    /// yesterday's launch still counts, last month's barely does.
    /// </summary>
    private const double RecencyHalfLifeHours = 24;

    /// <summary>
    /// Fraction of the usage score mixed in as a within-band tie-breaker. Capped well below the
    /// 100-point band gap so usage refines a tier without ever crossing into the next one.
    /// </summary>
    private const double UsageTieBreakFraction = 0.05;

    /// <summary>
    /// The combined ranking score; higher ranks first. Equal scores fall back to the caller's
    /// name comparison (name first, then path — the caller owns the tie-break).
    /// <para>
    /// Bands: exact name 2000 &gt; text tiers from <see cref="LauncherScoring.Score"/>
    /// (prefix ~900, substring ~700–800, subsequence ~400) &gt; alias floor 650 &gt; usage-only
    /// ≤ 600. A small usage-derived epsilon (≤ 30) is added on top so equally-scored entries
    /// break by habit instead of alphabet.
    /// </para>
    /// </summary>
    /// <param name="name">Display name of the entry; treated as empty when null.</param>
    /// <param name="query">
    /// The user's query. Blank means "no text intent": rank purely by usage
    /// (pinned &gt; recency &gt; count) — exactly the macOS-spotlight behaviour of an
    /// opened-but-untyped overlay.
    /// </param>
    /// <param name="usage">The usage record; a null degrades to the default (never launched).</param>
    /// <param name="targetPath">
    /// Alias channel: when the target file's name (without extension) matches the query the
    /// entry earns the alias band even if the display name itself doesn't match — e.g. a
    /// shortcut named "代码编辑器" pointing at Code.exe still surfaces for "code".
    /// </param>
    /// <returns>The score; never NaN or Infinity.</returns>
    public static double Score(string name, string query, LauncherUsage usage, string? targetPath = null)
    {
        // Defensive defaults instead of throwing: the ranker runs inside the hot keystroke path,
        // and one malformed entry must not take down the whole result list.
        name ??= string.Empty;
        usage ??= new LauncherUsage();
        var trimmedQuery = query?.Trim() ?? string.Empty;

        var usageScore = UsageScore(usage);
        var usageEpsilon = usageScore * UsageTieBreakFraction;

        if (trimmedQuery.Length == 0)
        {
            // Blank query: no text intent, rank purely by usage. The internal weights already
            // order pinned (300) > recency (≤200) > count (≤100), so no extra layering here.
            return usageScore;
        }

        // Exact name match — detected with the same comparer LauncherScoring uses so the two
        // rankers can never disagree about what "exact" means.
        if (name.Equals(trimmedQuery, StringComparison.CurrentCultureIgnoreCase))
            return ExactMatchBand + usageEpsilon;

        var textScore = (double)LauncherScoring.Score(name, trimmedQuery);

        // Alias channel: score the target file name (extension stripped) with the same text
        // ranker. A weak subsequence hit on the target is still lifted to the alias floor so it
        // outranks pure recency, per the band contract.
        var aliasScore = TargetPathScore(targetPath, trimmedQuery);
        var best = Math.Max(textScore, aliasScore);

        if (best <= 0)
        {
            // No text channel hit at all: keep the entry rankable but strictly below every
            // matched tier by scaling usage down into a sub-band under the alias floor.
            return usageEpsilon;
        }

        return best + usageEpsilon;
    }

    /// <summary>
    /// The usage-only score: pinned (300) + recency (≤200, hourly decay) + count (≤100). The
    /// ceiling of 600 deliberately sits one band below the alias floor, keeping the contract
    /// "alias &gt; recency &gt; count" true even when every usage signal is saturated.
    /// </summary>
    private static double UsageScore(LauncherUsage usage)
    {
        var score = 0.0;

        if (usage.Pinned)
            score += PinnedBonus;

        if (usage.LastLaunchedUtc is { } last)
        {
            // Clamp negative ages (clock skew, restored backups) to "just launched" instead of
            // handing the entry a negative bonus.
            var ageHours = Math.Max(0, (DateTime.UtcNow - last).TotalHours);
            score += RecencyBonusMax * Math.Pow(0.5, ageHours / RecencyHalfLifeHours);
        }

        // Degraded inputs (negative/garbage counts from a corrupt store) yield ≤ 0 here, which
        // is exactly the "no count bonus" default — no need to reject the record.
        score += Math.Min(CountBonusMax, Math.Max(0, usage.LaunchCount) * CountBonusMax / CountBonusSaturation);

        return score;
    }

    /// <summary>
    /// Text score of the alias channel, or 0 when there is no target path or no match. The
    /// result is lifted to <see cref="AliasBandFloor"/> (shorter target names win within the
    /// band, mirroring LauncherScoring's length penalty) so the band stays above usage-only.
    /// </summary>
    private static double TargetPathScore(string? targetPath, string trimmedQuery)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return 0;

        var fileName = Path.GetFileNameWithoutExtension(targetPath.Trim());
        if (string.IsNullOrEmpty(fileName)) return 0;

        var targetScore = LauncherScoring.Score(fileName, trimmedQuery);
        if (targetScore <= 0) return 0;

        return Math.Max(targetScore, AliasBandFloor - Math.Min(50, fileName.Length));
    }
}
