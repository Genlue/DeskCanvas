namespace DeskCanvas.Contracts;

[Flags]
public enum ThemeImpact { None = 0, Palette = 1, Geometry = 2, Material = 4, Typography = 8, Motion = 16 }

public sealed record ThemeSnapshot(
    long Revision,
    string Palette,
    string Material,
    double Density = 1,
    ThemeImpact Impact = ThemeImpact.None);

public sealed record SettingsCommitResult<T>(bool Succeeded, long Revision, T Snapshot, string? Conflict = null)
{
    public static SettingsCommitResult<T> ConflictAt(long revision, T snapshot) => new(false, revision, snapshot, "revision-conflict");
}

public sealed class DraftSettingsStore<T> where T : notnull
{
    private readonly object gate = new();
    private readonly Func<T, long, T>? normalizeRevision;
    private T snapshot;
    private long revision;
    public DraftSettingsStore(T initial, long initialRevision = 0, Func<T, long, T>? normalizeRevision = null)
    {
        this.normalizeRevision = normalizeRevision;
        revision = initialRevision;
        snapshot = normalizeRevision is null ? initial : normalizeRevision(initial, initialRevision);
    }
    public T Snapshot { get { lock (gate) return snapshot; } }
    public long Revision { get { lock (gate) return revision; } }
    public event EventHandler<SettingsCommitResult<T>>? Committed;
    public T BeginDraft() => Snapshot;
    public SettingsCommitResult<T> Commit(T draft, long expectedRevision)
    {
        lock (gate)
        {
            if (expectedRevision != revision) return SettingsCommitResult<T>.ConflictAt(revision, snapshot);
            if (EqualityComparer<T>.Default.Equals(draft, snapshot)) return new(true, revision, snapshot);
            revision++;
            snapshot = normalizeRevision is null ? draft : normalizeRevision(draft, revision);
            var result = new SettingsCommitResult<T>(true, revision, snapshot);
            Committed?.Invoke(this, result);
            return result;
        }
    }
}
