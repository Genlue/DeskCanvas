using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Executes one leaf action type. Returns the action's output value (null = none).</summary>
public interface IActionExecutor
{
    Task<string?> ExecuteAsync(ShortcutAction action, RunContext context);
}

/// <summary>Type-id keyed registry for the leaf executors (control flow lives in the runner).</summary>
public static class ActionExecutors
{
    private static readonly Dictionary<string, IActionExecutor> Map = new(StringComparer.Ordinal)
    {
        // Launch
        ["openApp"] = new OpenAppExecutor(),
        ["openFile"] = new OpenPathExecutor(),
        ["openUrl"] = new OpenUrlExecutor(),
        ["webSearch"] = new WebSearchExecutor(),

        // Media
        ["mediaPlayPause"] = new MediaExecutor(MediaAction.PlayPause),
        ["mediaNext"] = new MediaExecutor(MediaAction.Next),
        ["mediaPrevious"] = new MediaExecutor(MediaAction.Previous),

        // System & tools
        ["setVolume"] = new SetVolumeExecutor(),
        ["muteToggle"] = new MuteToggleExecutor(),
        ["lockScreen"] = new LockScreenExecutor(),
        ["sleep"] = new SleepExecutor(),
        ["screenshot"] = new ScreenshotExecutor(),
        ["emptyRecycleBin"] = new EmptyRecycleBinExecutor(),
        ["wait"] = new WaitExecutor(),
        ["notify"] = new NotifyExecutor(),

        // Text & data
        ["getClipboard"] = new GetClipboardExecutor(),
        ["setClipboard"] = new SetClipboardExecutor(),
        ["textTransform"] = new TextTransformExecutor(),
        ["currentDate"] = new CurrentDateExecutor(),
        ["randomNumber"] = new RandomNumberExecutor(),

        // DeskCanvas host integration
        ["themeSurface"] = new ThemeSurfaceExecutor(),
        ["themeDarkMode"] = new ThemeDarkModeExecutor(),
        ["widgetsVisibility"] = new WidgetsVisibilityExecutor(),
        ["addReminder"] = new AddReminderExecutor(),
        ["openDataFolder"] = new OpenDataFolderExecutor(),
    };

    public static IActionExecutor? Find(string type) => Map.GetValueOrDefault(type);
}
