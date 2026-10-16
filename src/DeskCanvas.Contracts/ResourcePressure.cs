namespace DeskCanvas.Contracts;

/// <summary>
/// Graded rebuildable-memory pressure of the process, derived from the resource ledger
/// snapshot against an experimental soft cap. The level drives only the strength of
/// existing maintenance actions (trim priority, idle-capture release timing) — never
/// anything the user can see.
/// </summary>
public enum ResourcePressureLevel
{
    /// <summary>Comfortable: rebuildable pixel memory is well below the observation cap.</summary>
    None,

    /// <summary>Elevated: approaching the cap; no consumer action at this level by default
    /// (it exists so a later graded tier has a rung to land on).</summary>
    Watch,

    /// <summary>High: at or near the cap; existing maintenance actions escalate one step.</summary>
    High,
}

/// <summary>
/// One evaluation input: the ledger-derived total of rebuildable pixel bytes, the soft cap
/// it is judged against, and whether that total is rising. <see cref="Rising"/> is carried
/// for the later graded tiers of the long-term plan (§14.3); the current evaluator is a pure
/// ratio check and ignores it — callers derive it by comparing two snapshots.
/// </summary>
public sealed record ResourcePressureState(long TotalReclaimableBytes, long CapBytes, bool Rising);

/// <summary>
/// Pure evaluation of resource pressure from a ledger snapshot. Thresholds are
/// <b>experiment parameters, not release values</b>: like <see cref="ResourceBudget.Tiers"/>
/// they are to be calibrated on real machines and frozen afterwards.
/// </summary>
public static class ResourcePressureEvaluator
{
    /// <summary>Total/cap ratio at or above which the level becomes <see cref="ResourcePressureLevel.Watch"/>.</summary>
    public const double WatchRatio = 0.5;

    /// <summary>Total/cap ratio at or above which the level becomes <see cref="ResourcePressureLevel.High"/>.</summary>
    public const double HighRatio = 0.8;

    /// <summary>
    /// Evaluates one pressure state. ratio = Total/Cap: &lt; 0.5 → None, &lt; 0.8 → Watch,
    /// ≥ 0.8 → High. A cap of zero or less means "no cap agreed yet" and always evaluates to
    /// None — observation must never invent pressure on its own.
    /// </summary>
    public static ResourcePressureLevel Evaluate(ResourcePressureState state)
    {
        if (state is null || state.CapBytes <= 0) return ResourcePressureLevel.None;
        var total = Math.Max(0, state.TotalReclaimableBytes);
        var ratio = (double)total / state.CapBytes;
        if (ratio < WatchRatio) return ResourcePressureLevel.None;
        return ratio < HighRatio ? ResourcePressureLevel.Watch : ResourcePressureLevel.High;
    }

    // Pending-streak state of the hysteresis accumulator (the level in effect itself is the
    // caller's `previous` argument, so it is not duplicated here).
    private static ResourcePressureLevel pendingLevel = ResourcePressureLevel.None;
    private static int pendingRounds;

    /// <summary>Checks-only seam: clears the hysteresis accumulator. Production code must not call this.</summary>
    /// <remarks>Named for the check fixtures (mirroring the *Checks convention; Contracts has no
    /// InternalsVisibleTo, so a public method with an explicit contract beats reflection).</remarks>
    public static void ResetForChecks()
    {
        pendingLevel = ResourcePressureLevel.None;
        pendingRounds = 0;
    }

    /// <summary>
    /// Anti-flap filter for the level stream: a level that differs from the one in effect is
    /// adopted only after it has been observed on <paramref name="stableRounds"/> consecutive
    /// rounds, so a metric hovering on a threshold cannot make maintenance strength (and the
    /// timing it controls) oscillate.
    /// <para>
    /// Semantics per call: a candidate equal to <paramref name="previous"/> is maintained and
    /// clears any pending streak; a candidate that diverges from both starts (or restarts) the
    /// streak — that observation itself counts as its first round; a candidate matching the
    /// pending streak counts one more round toward adoption. The streak is adopted as soon as
    /// it reaches <paramref name="stableRounds"/> rounds, so 1 switches on the first differing
    /// round and ≤ 0 switches immediately. Back-and-forth jitter between two candidates never
    /// switches.
    /// </para>
    /// </summary>
    /// <param name="previous">The level currently in effect for the caller's stream.</param>
    /// <param name="candidate">The freshly evaluated level.</param>
    /// <param name="stableRounds">Consecutive rounds required before a switch; ≤ 1 switches on the first differing round.</param>
    public static ResourcePressureLevel ApplyHysteresis(
        ResourcePressureLevel previous, ResourcePressureLevel candidate, int stableRounds)
    {
        if (candidate == previous)
        {
            pendingLevel = candidate;
            pendingRounds = 0;
            return previous;
        }
        if (stableRounds <= 0)
        {
            pendingLevel = candidate;
            pendingRounds = 0;
            return candidate;
        }
        if (candidate != pendingLevel)
        {
            pendingLevel = candidate;
            pendingRounds = 1;
            return pendingRounds >= stableRounds ? Adopt(candidate) : previous;
        }
        pendingRounds++;
        return pendingRounds >= stableRounds ? Adopt(candidate) : previous;
    }

    /// <summary>Adopts the pending candidate and clears the streak.</summary>
    private static ResourcePressureLevel Adopt(ResourcePressureLevel candidate)
    {
        pendingRounds = 0;
        return candidate;
    }

    /// <summary>
    /// Sums the estimated bytes of a ledger snapshot. Every row the ledger holds is by contract
    /// rebuildable cached or leased pixel memory (all kinds count, including
    /// <see cref="ResourceKind.PicturePendingDecode"/>), so this is a plain sum — no kind
    /// filtering, which is also what makes unknown kinds (future enum values) tolerated.
    /// </summary>
    public static long TotalReclaimableBytes(IReadOnlyList<ResourceUsage> snapshot)
    {
        if (snapshot is null || snapshot.Count == 0) return 0;
        long total = 0;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var bytes = snapshot[i].EstimatedBytes;
            if (bytes > 0) total += bytes; // the ledger clamps negatives; guard anyway
        }
        return total;
    }
}
