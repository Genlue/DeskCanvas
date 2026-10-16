namespace DeskCanvas.Runtime;

/// <summary>
/// Shared timezone resolution for the widget family (the clock views, calendar, …). Each
/// consumer previously carried a private copy of the same boilerplate; this is the single
/// Runtime version. Consumer #1 is the digital clock pilot (P8 batch A); the calendar (A2)
/// is the designed second consumer — the remaining copies migrate in their own slices.
/// <para>
/// Semantics: <c>null</c> resolves to <see cref="TimeZoneInfo.Local"/> (the widgets' unset
/// timezone setting), a resolvable Windows or IANA id (both accepted by
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c> on .NET 6+, which converts between the two
/// databases on demand) resolves to it, and an unknown id falls back to
/// <see cref="TimeZoneInfo.Utc"/> instead of throwing. Successful resolutions are cached in
/// a small bounded map — lookups happen per settings load, never per tick.
/// </para>
/// </summary>
public static class SharedTimeZone
{
    /// <summary>Cache bound with a simple clear-on-full policy (lookups are rare, hit rate
    /// is not critical; a real widget never exceeds a handful of zones).</summary>
    private const int CacheLimit = 32;

    private static readonly object gate = new();
    private static readonly Dictionary<string, TimeZoneInfo> cache = new(StringComparer.OrdinalIgnoreCase);

    public static TimeZoneInfo Resolve(string? windowsId)
    {
        if (windowsId == null) return TimeZoneInfo.Local;

        lock (gate)
        {
            if (cache.TryGetValue(windowsId, out var cached)) return cached;
        }

        TimeZoneInfo resolved;
        try { resolved = TimeZoneInfo.FindSystemTimeZoneById(windowsId); }
        catch { return TimeZoneInfo.Utc; }

        lock (gate)
        {
            if (cache.Count >= CacheLimit) cache.Clear();
            cache[windowsId] = resolved;
        }

        return resolved;
    }

    /// <summary>Test seam (public reset convention, see <see cref="ResourceLedger.ResetForChecks"/>).</summary>
    public static void ResetForChecks()
    {
        lock (gate) cache.Clear();
    }
}
