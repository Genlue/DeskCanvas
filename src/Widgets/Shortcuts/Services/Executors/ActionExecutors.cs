using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// A step failure reported by an executor. The code is stable (tests and the UI branch on it),
/// the message is localized, and <see cref="ParamKey"/> points at the parameter that caused it
/// when there is one.
/// </summary>
public sealed record ActionError(string Code, string Message, string? ParamKey = null)
{
    public static ActionError Of(string code, string message, string? paramKey = null) =>
        new(code, message, paramKey);
}

/// <summary>
/// What one step produced: machine data on named ports, a localized one-line summary for the
/// run log, and an error instead of an exception when the failure is routable.
/// <para>
/// Actions with no data output return an empty <see cref="Outputs"/> map — "the step finished"
/// is not data, and pretending it is was what made "上一步结果" show localization text.
/// </para>
/// </summary>
public sealed record ActionResult
{
    public static readonly ActionResult Done = new(null, null, null);

    public ActionResult(IReadOnlyDictionary<string, RunValue>? outputs, string? summary, ActionError? error)
    {
        Outputs = outputs ?? new Dictionary<string, RunValue>();
        Summary = summary;
        Error = error;
    }

    /// <summary>Typed values keyed by output port id.</summary>
    public IReadOnlyDictionary<string, RunValue> Outputs { get; }

    /// <summary>Localized one-line feedback shown in the run log.</summary>
    public string? Summary { get; }

    public ActionError? Error { get; }

    public bool Succeeded => Error == null;

    public static ActionResult Ok(string? summary = null) => new(null, summary, null);

    public static ActionResult With(string portId, RunValue value, string? summary = null) =>
        new(new Dictionary<string, RunValue> { [portId] = value }, summary, null);

    public static ActionResult With(IReadOnlyDictionary<string, RunValue> outputs, string? summary = null) =>
        new(outputs, summary, null);

    public static ActionResult Fail(string code, string message, string? paramKey = null) =>
        new(null, null, ActionError.Of(code, message, paramKey));

    public static ActionResult Fail(ActionError error) => new(null, null, error);

    /// <summary>Attach a summary to an existing result without touching its data.</summary>
    public ActionResult Describe(string? summary) => new(Outputs, summary, Error);
}

/// <summary>Executes one leaf action type and reports typed outputs.</summary>
public interface IActionExecutor
{
    Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context);
}

/// <summary>Type-id keyed registry for the leaf executors (control flow lives in the runner).</summary>
public static class ActionExecutors
{
    private static readonly Dictionary<string, IActionExecutor> Map = new(StringComparer.Ordinal)
    {
        // Launch
        ["openApp"] = new OpenAppExecutor(),
        ["openFile"] = new OpenPathExecutor(),
        ["openFolder"] = new OpenFolderExecutor(),
        ["openUrl"] = new OpenUrlExecutor(),
        ["webSearch"] = new WebSearchExecutor(),

        // Media
        ["mediaPlayPause"] = new MediaExecutor(MediaAction.PlayPause),
        ["mediaNext"] = new MediaExecutor(MediaAction.Next),
        ["mediaPrevious"] = new MediaExecutor(MediaAction.Previous),

        // System & tools
        ["setVolume"] = new SetVolumeExecutor(),
        ["muteToggle"] = new MuteToggleExecutor(),
        ["setMute"] = new SetMuteExecutor(),
        ["lockScreen"] = new LockScreenExecutor(),
        ["sleep"] = new SleepExecutor(),
        ["screenshot"] = new ScreenshotExecutor(),
        ["emptyRecycleBin"] = new EmptyRecycleBinExecutor(),
        ["wait"] = new WaitExecutor(),
        ["notify"] = new NotifyExecutor(),
        ["minimizeAll"] = new MinimizeAllExecutor(),
        ["typeText"] = new TypeTextExecutor(),
        ["sendKeySequence"] = new SendKeySequenceExecutor(),
        ["sendHotkey"] = new SendHotkeyExecutor(),
        ["runCommand"] = new RunCommandExecutor(),
        ["findWindow"] = new FindWindowExecutor(),
        ["activateWindow"] = new ActivateWindowExecutor(),
        ["waitForWindow"] = new WaitForWindowExecutor(),

        // Text & data
        ["getClipboard"] = new GetClipboardExecutor(),
        ["setClipboard"] = new SetClipboardExecutor(),
        ["textTransform"] = new TextTransformExecutor(),
        ["currentDate"] = new CurrentDateExecutor(),
        ["formatDate"] = new FormatDateExecutor(),
        ["dateAdd"] = new DateAddExecutor(),
        ["dateDiff"] = new DateDiffExecutor(),
        ["randomNumber"] = new RandomNumberExecutor(),
        ["calculate"] = new CalculateExecutor(),
        ["convertValue"] = new ConvertValueExecutor(),
        ["httpRequest"] = new HttpRequestExecutor(),

        // Data nodes
        ["text"] = new TextConstantExecutor(),
        ["number"] = new NumberConstantExecutor(),
        ["boolean"] = new BooleanConstantExecutor(),
        ["getVar"] = new GetVarExecutor(),
        ["splitText"] = new SplitTextExecutor(),
        ["joinText"] = new JoinTextExecutor(),
        ["createList"] = new CreateListExecutor(),
        ["listCount"] = new ListCountExecutor(),
        ["listItem"] = new ListItemExecutor(),
        ["parseJson"] = new ParseJsonExecutor(),
        ["getProperty"] = new GetPropertyExecutor(),
        ["toJson"] = new ToJsonExecutor(),
        ["askInput"] = new AskInputExecutor(),
        ["showResult"] = new ShowResultExecutor(),
        ["selectFile"] = new SelectFileExecutor(),
        ["selectFolder"] = new SelectFolderExecutor(),
        ["pathExists"] = new PathExistsExecutor(),
        ["readTextFile"] = new ReadTextFileExecutor(),
        ["writeTextFile"] = new WriteTextFileExecutor(),
        ["listFiles"] = new ListFilesExecutor(),
        ["pathJoin"] = new PathJoinExecutor(),
        ["pathInfo"] = new PathInfoExecutor(),

        // DeskCanvas host integration
        ["themeSurface"] = new ThemeSurfaceExecutor(),
        ["themeDarkMode"] = new ThemeDarkModeExecutor(),
        ["widgetsVisibility"] = new WidgetsVisibilityExecutor(),
        ["addReminder"] = new AddReminderExecutor(),
        ["openDataFolder"] = new OpenDataFolderExecutor(),
        ["runShortcut"] = new RunShortcutExecutor(),
    };

    public static IActionExecutor? Find(string type) => Map.GetValueOrDefault(type);

    /// <summary>Registered action types (tests assert the catalog and the registry agree).</summary>
    public static IReadOnlyCollection<string> RegisteredTypes => Map.Keys;
}
