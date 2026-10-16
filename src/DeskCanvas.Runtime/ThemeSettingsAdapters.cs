using DeskCanvas.Contracts;

namespace DeskCanvas.Runtime;

public sealed class ClockThemeSettingsAdapter
{
    public DraftSettingsStore<ThemeSnapshot> Store { get; } = new(
        new ThemeSnapshot(0, "default", "follow-global"),
        normalizeRevision: static (snapshot, revision) => snapshot with { Revision = revision });
    public SettingsCommitResult<ThemeSnapshot> CommitPalette(string palette, long expected) =>
        Store.Commit(Store.Snapshot with { Palette = palette, Impact = ThemeImpact.Palette }, expected);
}

public sealed class CountdownThemeSettingsAdapter
{
    public DraftSettingsStore<ThemeSnapshot> Store { get; } = new(
        new ThemeSnapshot(0, "default", "follow-global"),
        normalizeRevision: static (snapshot, revision) => snapshot with { Revision = revision });
    public SettingsCommitResult<ThemeSnapshot> CommitMaterial(string material, long expected) =>
        Store.Commit(Store.Snapshot with { Material = material, Impact = ThemeImpact.Material }, expected);
}
