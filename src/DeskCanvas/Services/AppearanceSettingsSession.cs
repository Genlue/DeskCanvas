using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Runtime;

namespace DeskCanvas.Services;

/// <summary>Real settings editor adapter; the existing provider supplies atomic file replacement.</summary>
public sealed class AppearanceSettingsSession
{
    private readonly PersistentSettingsSession<Theme> session;
    public AppearanceSettingsSession(IAppSettingsProvider provider)
    {
        session = new(() => provider.Get().Theme, theme =>
        {
            var settings = provider.Get();
            provider.Save(settings.WithThemeForSurface(theme.EffectiveSurface, theme) with { Theme = theme });
        });
    }
    public Theme Draft => session.Draft;
    public long Revision => session.Revision;
    public void Stage(Theme theme) => session.Stage(theme);
    public void Reload() => session.Reload();
    public PersistentSettingsResult<Theme> Commit() => session.Commit(session.Revision);
}
