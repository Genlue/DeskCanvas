namespace DeskCanvas.Runtime;

public sealed record PersistentSettingsResult<T>(bool Succeeded, long Revision, T Snapshot, string? Error = null);

/// <summary>A draft belongs to one editor. Publication follows a successful durable save.</summary>
public sealed class PersistentSettingsSession<T> where T : notnull
{
    private readonly Func<T> read;
    private readonly Action<T> save;
    private T baseline;
    public PersistentSettingsSession(Func<T> read, Action<T> save)
    {
        this.read = read;
        this.save = save;
        baseline = Draft = read();
    }
    public T Draft { get; private set; }
    public long Revision { get; private set; }
    public event EventHandler<PersistentSettingsResult<T>>? Committed;
    public void Stage(T value) => Draft = value;
    public void Reload() => baseline = Draft = read();
    public PersistentSettingsResult<T> Commit(long expectedRevision)
    {
        if (expectedRevision != Revision || !EqualityComparer<T>.Default.Equals(read(), baseline))
            return new(false, Revision, read(), "revision-conflict");
        if (EqualityComparer<T>.Default.Equals(Draft, baseline)) return new(true, Revision, baseline);
        try { save(Draft); }
        catch (Exception) { return new(false, Revision, baseline, "save-failed"); }
        baseline = Draft = read();
        var result = new PersistentSettingsResult<T>(true, ++Revision, baseline);
        Committed?.Invoke(this, result);
        return result;
    }
}
