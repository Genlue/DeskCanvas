using Shortcuts.Models;
using Windows.Media.Control;

namespace Shortcuts.Services.Executors;

internal enum MediaAction
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>Media transport via the Windows SMTC session (same stack the Music widget uses).</summary>
internal sealed class MediaExecutor(MediaAction mediaAction) : IActionExecutor
{
    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService") + " - " + ex.Message);
        }

        var session = manager?.GetCurrentSession();
        if (session == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Output_NoMedia"));

        bool ok = mediaAction switch
        {
            MediaAction.PlayPause => await session.TryTogglePlayPauseAsync(),
            MediaAction.Next => await session.TrySkipNextAsync(),
            MediaAction.Previous => await session.TrySkipPreviousAsync(),
            _ => false,
        };

        if (!ok)
            throw new ActionFailedException(Loc.Get("Shortcuts_Output_NoMedia"));

        return null;
    }
}
