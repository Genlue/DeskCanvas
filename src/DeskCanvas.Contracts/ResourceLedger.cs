namespace DeskCanvas.Contracts;

/// <summary>
/// Category of a reported resource. Each value names one family of cached or leased
/// pixel memory so the ledger can aggregate them per family.
/// </summary>
public enum ResourceKind
{
    /// <summary>A captured desktop wallpaper frame held by the live sampler (the largest
    /// recurring allocation in the process; a full virtual desktop at physical resolution).</summary>
    GlassCapture,

    /// <summary>A blurred, downscaled backdrop built from a capture and shared by every glass
    /// widget with the same blur; held by the bounded, evictable source cache.</summary>
    GlassBackdrop,

    /// <summary>An active reference-counted lease around a capture — the "someone is still
    /// drawing from these pixels" accounting that keeps eviction from freeing live frames.</summary>
    GlassLease,

    /// <summary>A cached wallpaper snapshot outside the liquid glass path. Placeholder for a
    /// later wiring batch; nothing reports it yet.</summary>
    WallpaperSnapshot,

    /// <summary>Decoded picture frames held by a picture widget's frame cache.</summary>
    PictureFrame,

    /// <summary>A picture decode that has started but not finished. Placeholder: the current
    /// picture decode path is synchronous, so nothing reports it yet.</summary>
    PicturePendingDecode,

    /// <summary>A pre-rendered glyph / glass frame cached by a clock widget.</summary>
    GlyphMask,

    /// <summary>A map tile bitmap in the in-memory LRU cache. Placeholder only — wiring lands
    /// with the P8 widget batch and must not happen in this slice.</summary>
    MapTile,

    /// <summary>A cached file/folder icon bitmap. Placeholder only — wiring lands with the P8
    /// widget batch and must not happen in this slice.</summary>
    FileIcon,

    /// <summary>Any resource that does not fit a named family.</summary>
    Other,
}

/// <summary>One aggregated ledger row: everything one owner holds of one kind.</summary>
public readonly record struct ResourceUsage(
    string Owner,
    ResourceKind Kind,
    int Count,
    long EstimatedBytes,
    string ReleaseThread);

/// <summary>
/// A process-wide observation point for cached and leased pixel memory. Implementations must
/// never throw into the reporting path: observation may not break production behaviour.
/// </summary>
public interface IResourceLedger
{
    /// <summary>
    /// Reports the <b>absolute</b> current usage of <paramref name="owner"/> for
    /// <paramref name="kind"/>: call after a counter changes and pass the new totals, not a
    /// delta. Reporting zero clears the row.
    /// </summary>
    void Report(string owner, ResourceKind kind, int count, long estimatedBytes, string releaseThread);

    /// <summary>The current usage aggregated per (owner, kind), sorted by kind then owner.</summary>
    IReadOnlyList<ResourceUsage> Snapshot();
}

/// <summary>
/// Pixel byte estimates for BGRA bitmaps. The budget math across the codebase is stated in
/// BGRA bytes; this is the shared compact form of Σ RowBytes × Height. Actual bitmaps may
/// carry a few padding bytes per row (RowBytes ≥ Width × 4); the estimator deliberately
/// ignores that — estimates only feed soft budgets and diagnostics, never correctness.
/// </summary>
public static class GlassMemoryEstimator
{
    /// <summary>Estimated bytes of a Width × Height BGRA bitmap.</summary>
    public static long EstimateBgraBytes(int widthPx, int heightPx)
    {
        if (widthPx <= 0 || heightPx <= 0) return 0;
        return (long)widthPx * heightPx * 4;
    }
}

/// <summary>A named soft memory ceiling. Experimental parameters, not release values.</summary>
public sealed record ResourceBudget(long SoftCapBytes, string TierName)
{
    /// <summary>
    /// Candidate global rebuildable-resource soft caps for the memory pressure experiments.
    /// These are <b>experiment parameters, not release values</b>: stress testing picks one and
    /// it is frozen afterwards. This slice only reports against them; nothing is enforced.
    /// </summary>
    public static IReadOnlyList<ResourceBudget> Tiers { get; } =
    [
        new ResourceBudget(192L * 1024 * 1024, "192 MiB"),
        new ResourceBudget(384L * 1024 * 1024, "384 MiB"),
        new ResourceBudget(768L * 1024 * 1024, "768 MiB"),
    ];
}
