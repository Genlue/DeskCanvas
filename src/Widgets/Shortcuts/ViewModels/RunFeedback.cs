using Avalonia.Threading;
using Shortcuts.Services;

namespace Shortcuts.ViewModels;

/// <summary>Shared translation of runner events into per-tile visual state.</summary>
public static class RunFeedback
{
    public static void Apply(ShortcutTileViewModel tile, RunEvent runEvent)
    {
        switch (runEvent.Kind)
        {
            case RunEventKind.Started:
            case RunEventKind.StepStarted:
                tile.SetRunning(Math.Max(1, runEvent.StepIndex), runEvent.TotalSteps);
                break;

            case RunEventKind.StepFinished:
                if (tile.IsRunning)
                    tile.SetRunning(Math.Max(1, runEvent.StepIndex), runEvent.TotalSteps);
                break;

            case RunEventKind.Finished:
                tile.SetFinished(runEvent.Success, runEvent.Success ? null : runEvent.Detail);
                ScheduleReset(tile);
                break;

            case RunEventKind.Cancelled:
                tile.SetCancelled();
                break;
        }
    }

    private static async void ScheduleReset(ShortcutTileViewModel tile)
    {
        try
        {
            var version = tile.StateVersion;
            await Task.Delay(2500);
            Dispatcher.UIThread.Post(() => tile.ResetIfVersion(version));
        }
        catch
        {
            // A UI reset must never crash the app.
        }
    }
}
