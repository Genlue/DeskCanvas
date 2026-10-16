using System.Diagnostics;
using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

/// <summary>
/// Process-wide resource ledger: an observation point for cached and leased pixel memory.
/// Owners (caches, widgets, the glass pipeline) report their current usage after every counter
/// change; <see cref="Snapshot"/> aggregates it per (owner, kind).
/// <para>
/// This is observation only: <see cref="Report"/> never throws into the caller's path and never
/// changes any release or eviction decision. The table is bounded — a kind with more distinct
/// owners than <see cref="MaxOwnersPerKind"/> has its smallest rows merged into one "…" overflow
/// row, so an unbounded instance-id dimension cannot grow the ledger without limit.
/// </para>
/// </summary>
public sealed class ResourceLedger : IResourceLedger
{
    /// <summary>Maximum distinct owners kept per kind before smallest rows merge into "…".</summary>
    public const int MaxOwnersPerKind = 256;

    /// <summary>Owner name of the overflow row that absorbs the smallest entries beyond the cap.</summary>
    public const string OverflowOwner = "…";

    /// <summary>Checks-only seam: wipes every reported row. Production code must not call this.</summary>
    /// <remarks>Named for the check fixtures (mirroring the *Checks convention). Runtime has no
    /// InternalsVisibleTo, so a public method with an explicit contract beats reflection.</remarks>
    public static void ResetForChecks() => Instance.Clear();

    public static ResourceLedger Instance { get; } = new();

    private ResourceLedger() { }

    private readonly object gate = new();

    // kind → owner → (count, bytes, release thread). Per-kind sub-tables keep the cap check O(1).
    private readonly Dictionary<ResourceKind, Dictionary<string, Row>> kinds = new();

    private sealed record Row(int Count, long Bytes, string ReleaseThread);

    public void Report(string owner, ResourceKind kind, int count, long estimatedBytes, string releaseThread)
    {
        try
        {
            // Defensive on purpose: reporting sits inside caches and widget view models, and a
            // malformed call must degrade to "not recorded", never break the producer.
            if (string.IsNullOrWhiteSpace(owner)) return;
            if (count < 0) count = 0;
            if (estimatedBytes < 0) estimatedBytes = 0;

            lock (gate)
            {
                if (!kinds.TryGetValue(kind, out var owners))
                    kinds[kind] = owners = new Dictionary<string, Row>();

                if (count == 0 && estimatedBytes == 0)
                {
                    owners.Remove(owner);
                    return;
                }

                // Converge below the cap *counting* the overflow row, so the table stays
                // bounded after the new owner is added (the overflow row itself is a row).
                while (!owners.ContainsKey(owner) && owners.Count >= MaxOwnersPerKind)
                    MergeSmallestIntoOverflow(owners);

                owners[owner] = new Row(count, estimatedBytes, releaseThread ?? string.Empty);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ResourceLedger] report dropped: {ex.Message}");
        }
    }

    /// <summary>Drops the smallest-by-bytes row into the "…" overflow row so a new owner fits.</summary>
    private static void MergeSmallestIntoOverflow(Dictionary<string, Row> owners)
    {
        string? smallest = null;
        foreach (var (owner, row) in owners)
        {
            if (owner == OverflowOwner) continue;
            if (smallest == null || row.Bytes < owners[smallest].Bytes) smallest = owner;
        }
        if (smallest == null) return;

        var smallestRow = owners[smallest];
        owners.Remove(smallest);
        var overflow = owners.TryGetValue(OverflowOwner, out var existing)
            ? existing
            : new Row(0, 0, smallestRow.ReleaseThread);
        owners[OverflowOwner] = new Row(overflow.Count + smallestRow.Count, overflow.Bytes + smallestRow.Bytes, smallestRow.ReleaseThread);
    }

    public IReadOnlyList<ResourceUsage> Snapshot()
    {
        lock (gate)
        {
            var rows = new List<ResourceUsage>();
            foreach (var (kind, owners) in kinds)
            {
                foreach (var (owner, row) in owners)
                    rows.Add(new ResourceUsage(owner, kind, row.Count, row.Bytes, row.ReleaseThread));
            }
            // Stable identity of the report: kind, then owner, so callers can diff two snapshots.
            rows.Sort(static (a, b) =>
            {
                var byKind = ((int)a.Kind).CompareTo((int)b.Kind);
                return byKind != 0 ? byKind : string.CompareOrdinal(a.Owner, b.Owner);
            });
            return rows;
        }
    }

    private void Clear()
    {
        lock (gate) kinds.Clear();
    }
}
