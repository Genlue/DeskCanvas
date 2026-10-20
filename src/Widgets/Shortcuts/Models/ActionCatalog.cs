namespace Shortcuts.Models;

/// <summary>Action grouping; the category drives the block color in the editor.</summary>
public enum ActionCategory
{
    Launch,
    Media,
    System,
    Text,
    Data,
    Flow,
    DeskCanvas,
}

/// <summary>
/// How a parameter is edited and rendered. This is presentation only — what the runner accepts
/// is <see cref="ParamSpec.DataType"/>.
/// </summary>
public enum ParamKind
{
    Text,
    Number,
    Choice,
    FilePath,
    FolderPath,
    Hotkey,
    MultilineText,
    Json,
    List,
    Shortcut,
}

/// <summary>Shows a parameter only while another parameter holds one of the listed values.</summary>
public sealed record ParamVisibility(string Key, params string[] Values)
{
    public bool IsVisible(Func<string, string?> read)
    {
        var current = read(Key) ?? string.Empty;
        return Values.Length == 0 || Values.Contains(current, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>One parameter of an action.</summary>
/// <param name="Key">Key inside <see cref="ShortcutAction.Params"/>.</param>
/// <param name="LabelKey">Locale key of the label.</param>
/// <param name="Kind">Editor control.</param>
/// <param name="DefaultValue">Default literal; applied only when the parameter was never provided.</param>
/// <param name="Options">(value, localeKey) pairs for <see cref="ParamKind.Choice"/>.</param>
/// <param name="Min">Lower bound for numeric parameters.</param>
/// <param name="Max">Upper bound for numeric parameters.</param>
/// <param name="UnitKey">Optional locale format (with {0}) appended to the chip text.</param>
/// <param name="Primary">Primary parameters always show an inline chip, even when unset.</param>
/// <param name="DataType">Value type the runner accepts and data links must match.</param>
/// <param name="Required">The run fails (with node + parameter) when the value is missing.</param>
/// <param name="AllowEmpty">Whether an empty string is a legal value for a required parameter.</param>
/// <param name="Bindable">Whether a data link may feed this parameter.</param>
/// <param name="HelpKey">Optional one-line help shown in the parameter editor.</param>
/// <param name="VisibleWhen">Conditional visibility, driven by another parameter's literal.</param>
public sealed record ParamSpec(
    string Key,
    string LabelKey,
    ParamKind Kind,
    string? DefaultValue = null,
    IReadOnlyList<(string Value, string LabelKey)>? Options = null,
    double Min = 0,
    double Max = 100,
    string? UnitKey = null,
    bool Primary = false,
    DataType DataType = DataType.Text,
    bool Required = false,
    bool AllowEmpty = true,
    bool Bindable = true,
    string? HelpKey = null,
    ParamVisibility? VisibleWhen = null);

/// <summary>
/// One output port of an action. Exec ports only route control flow; data ports carry a
/// <see cref="RunValue"/> that later steps can read through a data link or a reference.
/// </summary>
/// <param name="Id">Port id written into <see cref="WorkflowLink.FromPort"/>.</param>
/// <param name="LabelKey">Locale key of the port label.</param>
/// <param name="DataType">Type carried by a data port.</param>
/// <param name="IsExec">True for a control-flow port.</param>
/// <param name="IsDefault">The port a plain data link binds to when no port was named.</param>
/// <param name="HelpKey">Optional description.</param>
public sealed record OutputSpec(
    string Id,
    string LabelKey,
    DataType DataType = DataType.Null,
    bool IsExec = false,
    bool IsDefault = false,
    string? HelpKey = null)
{
    public bool IsData => !IsExec;
}

/// <summary>What happens when a step reports an error.</summary>
public enum ErrorPolicy
{
    /// <summary>Stop the run (default).</summary>
    Stop,

    /// <summary>Continue through the node's <c>error</c> exec port.</summary>
    Route,
}

/// <summary>Static definition of one action type.</summary>
/// <param name="Type">Stable id written into the document.</param>
/// <param name="Category">Grouping; drives the block color.</param>
/// <param name="Glyph">Glyph id resolved by <see cref="Services.GlyphLibrary"/>.</param>
/// <param name="TitleKey">Locale key of the display name.</param>
/// <param name="Params">Editable parameters, in display order.</param>
/// <param name="Outputs">Execution and data ports, in card order.</param>
/// <param name="SummaryKey">Locale key of the one-line purpose shown in the library.</param>
/// <param name="Hidden">
/// Legacy or internal types that must stay resolvable (old documents, migration) but are not
/// offered by the library drawer — the v1 <c>if</c>/<c>repeat</c> containers live here.
/// </param>
/// <param name="DefaultErrorPolicy">Initial value of the per-node error strategy.</param>
/// <param name="Retryable">Whether the run policy may retry this action (no side effects assumed).</param>
public sealed record ActionDefinition(
    string Type,
    ActionCategory Category,
    string Glyph,
    string TitleKey,
    IReadOnlyList<ParamSpec> Params,
    IReadOnlyList<OutputSpec> Outputs,
    string? SummaryKey = null,
    bool Hidden = false,
    ErrorPolicy DefaultErrorPolicy = ErrorPolicy.Stop,
    bool Retryable = false)
{
    public ParamSpec? Param(string key) =>
        Params.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.Ordinal));

    /// <summary>The port a bare data link from this action binds to.</summary>
    public OutputSpec? DefaultDataOutput =>
        Outputs.FirstOrDefault(o => o.IsDefault) ?? Outputs.FirstOrDefault(o => o.IsData);

    public OutputSpec? Output(string portId) =>
        Outputs.FirstOrDefault(o => string.Equals(o.Id, portId, StringComparison.Ordinal));
}

/// <summary>
/// The action catalog — the "basic safe set": no arbitrary command execution.
/// Executors live in <c>Services/Executors</c> keyed by the same type ids.
/// </summary>
public static class ActionCatalog
{
    private const string POut = "Shortcuts_Port_Out";
    private const string PErr = "Shortcuts_Port_Error";

    /// <summary>Every action gets an exec exit and an error exit, then its data ports.</summary>
    private static OutputSpec[] Ports(params OutputSpec[] data) =>
    [
        new(WorkflowNodeTypes.Out, POut, IsExec: true),
        new(WorkflowNodeTypes.ErrorPort, PErr, IsExec: true),
        .. data,
    ];

    private static readonly List<ActionDefinition> Definitions =
    [
        // ---- Launch (blue) ---------------------------------------------------------
        new("openApp", ActionCategory.Launch, "app", "Shortcuts_Action_openApp",
        [
            new("target", "Shortcuts_Param_target", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
            new("args", "Shortcuts_Param_args", ParamKind.Text),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_openApp"),

        new("openFile", ActionCategory.Launch, "folder", "Shortcuts_Action_openFile",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_openFile"),

        new("openFolder", ActionCategory.Launch, "folder", "Shortcuts_Action_openFolder",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FolderPath, Primary: true,
                DataType: DataType.Path, Required: true),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_openFolder"),

        new("openUrl", ActionCategory.Launch, "globe", "Shortcuts_Action_openUrl",
        [
            new("url", "Shortcuts_Param_url", ParamKind.Text, "https://", Primary: true, Required: true),
        ],
        Ports(new OutputSpec("url", "Shortcuts_Port_Url", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_openUrl"),

        new("webSearch", ActionCategory.Launch, "search", "Shortcuts_Action_webSearch",
        [
            new("query", "Shortcuts_Param_query", ParamKind.Text, Primary: true, Required: true),
            new("engine", "Shortcuts_Param_engine", ParamKind.Choice, "bing",
            [
                ("bing", "Shortcuts_Engine_bing"),
                ("baidu", "Shortcuts_Engine_baidu"),
                ("google", "Shortcuts_Engine_google"),
            ]),
        ],
        Ports(new OutputSpec("url", "Shortcuts_Port_Url", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_webSearch"),

        // ---- Media (pink) ----------------------------------------------------------
        new("mediaPlayPause", ActionCategory.Media, "play", "Shortcuts_Action_mediaPlayPause",
            [], Ports(), "Shortcuts_Desc_mediaPlayPause"),
        new("mediaNext", ActionCategory.Media, "next", "Shortcuts_Action_mediaNext",
            [], Ports(), "Shortcuts_Desc_mediaNext"),
        new("mediaPrevious", ActionCategory.Media, "previous", "Shortcuts_Action_mediaPrevious",
            [], Ports(), "Shortcuts_Desc_mediaPrevious"),

        // ---- System & tools (grey) -------------------------------------------------
        new("setVolume", ActionCategory.System, "volume", "Shortcuts_Action_setVolume",
        [
            new("level", "Shortcuts_Param_level", ParamKind.Number, "50", Min: 0, Max: 100,
                UnitKey: "Shortcuts_Unit_Percent", Primary: true, DataType: DataType.Number,
                Required: true, HelpKey: "Shortcuts_Help_setVolume"),
        ],
        Ports(new OutputSpec("level", "Shortcuts_Port_Level", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_setVolume"),

        new("muteToggle", ActionCategory.System, "mute", "Shortcuts_Action_muteToggle",
            [], Ports(new OutputSpec("muted", "Shortcuts_Port_Muted", DataType.Boolean, IsDefault: true)),
            "Shortcuts_Desc_muteToggle"),

        new("setMute", ActionCategory.System, "mute", "Shortcuts_Action_setMute",
        [
            new("muted", "Shortcuts_Param_muted", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Mute_on"),
                ("false", "Shortcuts_Mute_off"),
            ], Primary: true, DataType: DataType.Boolean, Required: true),
        ],
        Ports(new OutputSpec("muted", "Shortcuts_Port_Muted", DataType.Boolean, IsDefault: true)),
        "Shortcuts_Desc_setMute"),

        new("lockScreen", ActionCategory.System, "lock", "Shortcuts_Action_lockScreen",
            [], Ports(), "Shortcuts_Desc_lockScreen"),
        new("sleep", ActionCategory.System, "moon", "Shortcuts_Action_sleep",
            [], Ports(), "Shortcuts_Desc_sleep"),

        new("screenshot", ActionCategory.System, "camera", "Shortcuts_Action_screenshot",
        [
            new("scope", "Shortcuts_Param_scope", ParamKind.Choice, "virtual",
            [
                ("virtual", "Shortcuts_Scope_virtual"),
                ("primary", "Shortcuts_Scope_primary"),
            ], Primary: true),
            new("directory", "Shortcuts_Param_directory", ParamKind.FolderPath, DataType: DataType.Path,
                Bindable: true, HelpKey: "Shortcuts_Help_screenshotDir"),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_screenshot"),

        new("emptyRecycleBin", ActionCategory.System, "trash", "Shortcuts_Action_emptyRecycleBin",
            [], Ports(), "Shortcuts_Desc_emptyRecycleBin"),

        new("wait", ActionCategory.System, "timer", "Shortcuts_Action_wait",
        [
            new("seconds", "Shortcuts_Param_seconds", ParamKind.Number, "1", Min: 0, Max: 600,
                UnitKey: "Shortcuts_Param_SecondsUnit", Primary: true, DataType: DataType.Number,
                Required: true, HelpKey: "Shortcuts_Help_wait"),
        ],
        Ports(), "Shortcuts_Desc_wait"),

        new("notify", ActionCategory.System, "bell", "Shortcuts_Action_notify",
        [
            new("message", "Shortcuts_Param_message", ParamKind.Text, Primary: true, Required: true),
            new("title", "Shortcuts_Param_title", ParamKind.Text),
        ],
        Ports(), "Shortcuts_Desc_notify"),

        new("minimizeAll", ActionCategory.System, "desktop", "Shortcuts_Action_minimizeAll",
            [], Ports(), "Shortcuts_Desc_minimizeAll"),

        new("typeText", ActionCategory.System, "keyboard", "Shortcuts_Action_typeText",
        [
            new("text", "Shortcuts_Param_sendText", ParamKind.MultilineText, Primary: true,
                Required: true, AllowEmpty: false, HelpKey: "Shortcuts_Help_typeText"),
        ],
        Ports(), "Shortcuts_Desc_typeText"),

        new("sendKeySequence", ActionCategory.System, "keyboard", "Shortcuts_Action_sendKeySequence",
        [
            new("keys", "Shortcuts_Param_keys", ParamKind.Text, Primary: true, Required: true,
                AllowEmpty: false, HelpKey: "Shortcuts_Help_sendKeySequence"),
        ],
        Ports(), "Shortcuts_Desc_sendKeySequence"),

        new("sendHotkey", ActionCategory.System, "keyboard", "Shortcuts_Action_sendHotkey",
        [
            new("hotkey", "Shortcuts_Param_hotkey", ParamKind.Hotkey, Primary: true,
                Required: true, AllowEmpty: false),
            new("holdMs", "Shortcuts_Param_holdMs", ParamKind.Number, "40", Min: 10, Max: 500,
                UnitKey: "Shortcuts_Unit_Milliseconds", Primary: true, DataType: DataType.Number),
        ],
        Ports(), "Shortcuts_Desc_sendHotkey"),

        new("runCommand", ActionCategory.System, "terminal", "Shortcuts_Action_runCommand",
        [
            new("shell", "Shortcuts_Param_shell", ParamKind.Choice, "powershell",
            [
                ("powershell", "Shortcuts_Shell_powershell"),
                ("pwsh", "Shortcuts_Shell_pwsh"),
                ("cmd", "Shortcuts_Shell_cmd"),
            ], Primary: true),
            new("command", "Shortcuts_Param_command", ParamKind.MultilineText, Primary: true,
                Required: true, AllowEmpty: false, HelpKey: "Shortcuts_Help_runCommand"),
            new("workingDir", "Shortcuts_Param_workingDir", ParamKind.FolderPath,
                DataType: DataType.Path, HelpKey: "Shortcuts_Help_runCommandDir"),
            new("timeout", "Shortcuts_Param_timeout", ParamKind.Number, "30", Min: 1, Max: 600,
                UnitKey: "Shortcuts_Param_SecondsUnit", Primary: true, DataType: DataType.Number),
            new("failOnExit", "Shortcuts_Param_failOnExit", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_FailExit_on"),
                ("false", "Shortcuts_FailExit_off"),
            ], DataType: DataType.Boolean),
        ],
        Ports(
            new("stdout", "Shortcuts_Port_StdOut", DataType.Text, IsDefault: true),
            new("stderr", "Shortcuts_Port_StdErr", DataType.Text),
            new("exitCode", "Shortcuts_Port_ExitCode", DataType.Number)),
        "Shortcuts_Desc_runCommand"),

        // ---- Text & data (amber) ---------------------------------------------------
        new("getClipboard", ActionCategory.Text, "clip", "Shortcuts_Action_getClipboard",
            [],
            Ports(
                new("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true),
                new("hasText", "Shortcuts_Port_HasText", DataType.Boolean)),
            "Shortcuts_Desc_getClipboard"),

        new("setClipboard", ActionCategory.Text, "clip", "Shortcuts_Action_setClipboard",
        [
            new("text", "Shortcuts_Param_text", ParamKind.MultilineText, Primary: true,
                Required: true, AllowEmpty: true, HelpKey: "Shortcuts_Help_setClipboard"),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_setClipboard"),

        new("textTransform", ActionCategory.Text, "text", "Shortcuts_Action_textTransform",
        [
            new("input", "Shortcuts_Param_input", ParamKind.MultilineText, Primary: true,
                DataType: DataType.Text),
            new("operation", "Shortcuts_Param_operation", ParamKind.Choice, "upper",
            [
                ("upper", "Shortcuts_Choice_upper"),
                ("lower", "Shortcuts_Choice_lower"),
                ("trim", "Shortcuts_Choice_trim"),
                ("replace", "Shortcuts_Choice_replace"),
            ], Primary: true, Required: true),
            new("find", "Shortcuts_Param_find", ParamKind.Text, Required: true, AllowEmpty: false,
                VisibleWhen: new ParamVisibility("operation", "replace")),
            new("replace_with", "Shortcuts_Param_replace_with", ParamKind.Text,
                VisibleWhen: new ParamVisibility("operation", "replace")),
            new("ignoreCase", "Shortcuts_Param_ignoreCase", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Case_ignore"),
                ("false", "Shortcuts_Case_exact"),
            ], DataType: DataType.Boolean, VisibleWhen: new ParamVisibility("operation", "replace")),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_textTransform"),

        new("currentDate", ActionCategory.Text, "clock", "Shortcuts_Action_currentDate",
            [], Ports(new OutputSpec("date", "Shortcuts_Port_Date", DataType.DateTime, IsDefault: true)),
            "Shortcuts_Desc_currentDate"),

        new("formatDate", ActionCategory.Text, "clock", "Shortcuts_Action_formatDate",
        [
            new("date", "Shortcuts_Param_date", ParamKind.Text, DataType: DataType.DateTime,
                Primary: true, HelpKey: "Shortcuts_Help_formatDate"),
            new("format", "Shortcuts_Param_format", ParamKind.Text, "yyyy-MM-dd HH:mm",
                Primary: true, Required: true, AllowEmpty: false),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_formatDate"),

        new("dateAdd", ActionCategory.Text, "clock", "Shortcuts_Action_dateAdd",
        [
            new("date", "Shortcuts_Param_date", ParamKind.Text, DataType: DataType.DateTime,
                Primary: true, HelpKey: "Shortcuts_Help_formatDate"),
            new("amount", "Shortcuts_Param_amount", ParamKind.Number, "1", Min: -100000, Max: 100000,
                DataType: DataType.Number, Primary: true, Required: true),
            new("unit", "Shortcuts_Param_dateUnit", ParamKind.Choice, "days",
            [
                ("seconds", "Shortcuts_Unit_Seconds"),
                ("minutes", "Shortcuts_Unit_Minutes"),
                ("hours", "Shortcuts_Unit_Hours"),
                ("days", "Shortcuts_Unit_Days"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("date", "Shortcuts_Port_Date", DataType.DateTime, IsDefault: true)),
        "Shortcuts_Desc_dateAdd"),

        new("dateDiff", ActionCategory.Text, "clock", "Shortcuts_Action_dateDiff",
        [
            new("from", "Shortcuts_Param_from", ParamKind.Text, DataType: DataType.DateTime,
                Primary: true, Required: true, HelpKey: "Shortcuts_Help_formatDate"),
            new("to", "Shortcuts_Param_to", ParamKind.Text, DataType: DataType.DateTime,
                Primary: true, HelpKey: "Shortcuts_Help_formatDate"),
            new("unit", "Shortcuts_Param_dateUnit", ParamKind.Choice, "days",
            [
                ("seconds", "Shortcuts_Unit_Seconds"),
                ("minutes", "Shortcuts_Unit_Minutes"),
                ("hours", "Shortcuts_Unit_Hours"),
                ("days", "Shortcuts_Unit_Days"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("amount", "Shortcuts_Port_Amount", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_dateDiff"),

        new("randomNumber", ActionCategory.Text, "sparkle", "Shortcuts_Action_randomNumber",
        [
            new("min", "Shortcuts_Param_min", ParamKind.Number, "1", Min: -1000000, Max: 1000000,
                DataType: DataType.Number, Required: true),
            new("max", "Shortcuts_Param_max", ParamKind.Number, "100", Min: -1000000, Max: 1000000,
                DataType: DataType.Number, Required: true, HelpKey: "Shortcuts_Help_randomNumber"),
        ],
        Ports(new OutputSpec("number", "Shortcuts_Port_Number", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_randomNumber"),

        new("calculate", ActionCategory.Text, "sliders", "Shortcuts_Action_calculate",
        [
            new("a", "Shortcuts_Param_a", ParamKind.Number, "0", Min: -1e12, Max: 1e12,
                DataType: DataType.Number, Primary: true, Required: true),
            new("op", "Shortcuts_Param_calcOp", ParamKind.Choice, "add",
            [
                ("add", "Shortcuts_Calc_add"),
                ("subtract", "Shortcuts_Calc_subtract"),
                ("multiply", "Shortcuts_Calc_multiply"),
                ("divide", "Shortcuts_Calc_divide"),
                ("modulo", "Shortcuts_Calc_modulo"),
            ], Primary: true, Required: true),
            new("b", "Shortcuts_Param_b", ParamKind.Number, "0", Min: -1e12, Max: 1e12,
                DataType: DataType.Number, Primary: true, Required: true),
        ],
        Ports(new OutputSpec("number", "Shortcuts_Port_Number", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_calculate"),

        new("convertValue", ActionCategory.Text, "sliders", "Shortcuts_Action_convertValue",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, DataType: DataType.Any,
                Primary: true),
            new("target", "Shortcuts_Param_targetType", ParamKind.Choice, "text",
            [
                ("text", "Shortcuts_Type_text"),
                ("number", "Shortcuts_Type_number"),
                ("boolean", "Shortcuts_Type_boolean"),
                ("datetime", "Shortcuts_Type_datetime"),
                ("list", "Shortcuts_Type_list"),
                ("json", "Shortcuts_Type_json"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_convertValue"),

        new("httpRequest", ActionCategory.Text, "globe", "Shortcuts_Action_httpRequest",
        [
            new("url", "Shortcuts_Param_url", ParamKind.Text, "https://", Primary: true,
                Required: true, AllowEmpty: false),
            new("method", "Shortcuts_Param_method", ParamKind.Choice, "GET",
            [
                ("GET", "Shortcuts_Http_GET"),
                ("POST", "Shortcuts_Http_POST"),
                ("PUT", "Shortcuts_Http_PUT"),
                ("PATCH", "Shortcuts_Http_PATCH"),
                ("DELETE", "Shortcuts_Http_DELETE"),
            ], Primary: true, Required: true),
            new("headers", "Shortcuts_Param_headers", ParamKind.MultilineText,
                HelpKey: "Shortcuts_Help_httpHeaders",
                VisibleWhen: new ParamVisibility("method", "POST", "PUT", "PATCH", "DELETE", "GET")),
            new("query", "Shortcuts_Param_query", ParamKind.Text, HelpKey: "Shortcuts_Help_httpQuery"),
            new("contentType", "Shortcuts_Param_contentType", ParamKind.Text, "application/json",
                VisibleWhen: new ParamVisibility("method", "POST", "PUT", "PATCH")),
            new("body", "Shortcuts_Param_body", ParamKind.MultilineText,
                VisibleWhen: new ParamVisibility("method", "POST", "PUT", "PATCH")),
            new("timeout", "Shortcuts_Param_timeout", ParamKind.Number, "15", Min: 1, Max: 60,
                UnitKey: "Shortcuts_Param_SecondsUnit", Primary: true, DataType: DataType.Number),
        ],
        Ports(
            new("body", "Shortcuts_Port_Body", DataType.Text, IsDefault: true),
            new("statusCode", "Shortcuts_Port_StatusCode", DataType.Number),
            new("headers", "Shortcuts_Port_Headers", DataType.Object),
            new("truncated", "Shortcuts_Port_Truncated", DataType.Boolean)),
        "Shortcuts_Desc_httpRequest", Retryable: true),

        // ---- Data nodes (green) ----------------------------------------------------
        new("text", ActionCategory.Data, "text", "Shortcuts_Action_text",
        [
            new("value", "Shortcuts_Param_value", ParamKind.MultilineText, Primary: true),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_text"),

        new("number", ActionCategory.Data, "sliders", "Shortcuts_Action_number",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Number, "0", Min: -1e12, Max: 1e12,
                Primary: true, DataType: DataType.Number),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_number"),

        new("boolean", ActionCategory.Data, "check", "Shortcuts_Action_boolean",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Bool_true"),
                ("false", "Shortcuts_Bool_false"),
            ], Primary: true, DataType: DataType.Boolean),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Boolean, IsDefault: true)),
        "Shortcuts_Desc_boolean"),

        new("getVar", ActionCategory.Data, "list", "Shortcuts_Action_getVar",
        [
            new("name", "Shortcuts_Param_varName", ParamKind.Text, Primary: true,
                Required: true, AllowEmpty: false),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_getVar"),

        new("splitText", ActionCategory.Data, "list", "Shortcuts_Action_splitText",
        [
            new("text", "Shortcuts_Param_input", ParamKind.MultilineText, Primary: true,
                DataType: DataType.Text),
            new("separator", "Shortcuts_Param_separator", ParamKind.Choice, "newline",
            [
                ("newline", "Shortcuts_Sep_newline"),
                ("comma", "Shortcuts_Sep_comma"),
                ("space", "Shortcuts_Sep_space"),
                ("custom", "Shortcuts_Sep_custom"),
            ], Primary: true, Required: true),
            new("custom", "Shortcuts_Param_separatorText", ParamKind.Text,
                VisibleWhen: new ParamVisibility("separator", "custom")),
            new("trim", "Shortcuts_Param_trim", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Trim_on"),
                ("false", "Shortcuts_Trim_off"),
            ], DataType: DataType.Boolean),
            new("keepEmpty", "Shortcuts_Param_keepEmpty", ParamKind.Choice, "false",
            [
                ("true", "Shortcuts_KeepEmpty_on"),
                ("false", "Shortcuts_KeepEmpty_off"),
            ], DataType: DataType.Boolean),
        ],
        Ports(new OutputSpec("list", "Shortcuts_Port_List", DataType.List, IsDefault: true)),
        "Shortcuts_Desc_splitText"),

        new("joinText", ActionCategory.Data, "list", "Shortcuts_Action_joinText",
        [
            new("list", "Shortcuts_Param_list", ParamKind.List, Primary: true, DataType: DataType.List),
            new("separator", "Shortcuts_Param_separator", ParamKind.Choice, "newline",
            [
                ("newline", "Shortcuts_Sep_newline"),
                ("comma", "Shortcuts_Sep_comma"),
                ("space", "Shortcuts_Sep_space"),
                ("custom", "Shortcuts_Sep_custom"),
            ], Primary: true, Required: true),
            new("custom", "Shortcuts_Param_separatorText", ParamKind.Text,
                VisibleWhen: new ParamVisibility("separator", "custom")),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_joinText"),

        new("createList", ActionCategory.Data, "list", "Shortcuts_Action_createList",
        [
            new("a", "Shortcuts_Param_item1", ParamKind.Text, DataType: DataType.Any, Primary: true),
            new("b", "Shortcuts_Param_item2", ParamKind.Text, DataType: DataType.Any),
            new("c", "Shortcuts_Param_item3", ParamKind.Text, DataType: DataType.Any),
            new("d", "Shortcuts_Param_item4", ParamKind.Text, DataType: DataType.Any),
        ],
        Ports(new OutputSpec("list", "Shortcuts_Port_List", DataType.List, IsDefault: true)),
        "Shortcuts_Desc_createList"),

        new("listCount", ActionCategory.Data, "list", "Shortcuts_Action_listCount",
        [
            new("list", "Shortcuts_Param_list", ParamKind.List, Primary: true, DataType: DataType.List,
                Required: true),
        ],
        Ports(new OutputSpec("count", "Shortcuts_Port_Count", DataType.Number, IsDefault: true)),
        "Shortcuts_Desc_listCount"),

        new("listItem", ActionCategory.Data, "list", "Shortcuts_Action_listItem",
        [
            new("list", "Shortcuts_Param_list", ParamKind.List, Primary: true, DataType: DataType.List,
                Required: true),
            new("index", "Shortcuts_Param_index", ParamKind.Number, "0", Min: 0, Max: 1000000,
                Primary: true, DataType: DataType.Number, Required: true,
                HelpKey: "Shortcuts_Help_listItem"),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_listItem"),

        new("parseJson", ActionCategory.Data, "branch", "Shortcuts_Action_parseJson",
        [
            new("text", "Shortcuts_Param_input", ParamKind.Json, Primary: true,
                DataType: DataType.Text, Required: true, AllowEmpty: false),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_parseJson"),

        new("getProperty", ActionCategory.Data, "branch", "Shortcuts_Action_getProperty",
        [
            new("value", "Shortcuts_Param_object", ParamKind.Text, Primary: true,
                DataType: DataType.Object),
            new("path", "Shortcuts_Param_fieldPath", ParamKind.Text, Primary: true,
                Required: true, AllowEmpty: false, HelpKey: "Shortcuts_Help_fieldPath"),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_getProperty"),

        new("toJson", ActionCategory.Data, "branch", "Shortcuts_Action_toJson",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, Primary: true,
                DataType: DataType.Any),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_toJson"),

        new("askInput", ActionCategory.Data, "pencil", "Shortcuts_Action_askInput",
        [
            new("title", "Shortcuts_Param_title", ParamKind.Text, Primary: true, Required: true,
                AllowEmpty: false),
            new("prompt", "Shortcuts_Param_prompt", ParamKind.Text),
            new("default", "Shortcuts_Param_defaultValue", ParamKind.Text),
            new("type", "Shortcuts_Param_inputType", ParamKind.Choice, "text",
            [
                ("text", "Shortcuts_Type_text"),
                ("number", "Shortcuts_Type_number"),
                ("boolean", "Shortcuts_Type_boolean"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_askInput"),

        new("showResult", ActionCategory.Data, "eye", "Shortcuts_Action_showResult",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, Primary: true,
                DataType: DataType.Any),
            new("title", "Shortcuts_Param_title", ParamKind.Text),
        ],
        Ports(), "Shortcuts_Desc_showResult"),

        new("selectFile", ActionCategory.Data, "folder", "Shortcuts_Action_selectFile",
        [
            new("title", "Shortcuts_Param_title", ParamKind.Text),
            new("filter", "Shortcuts_Param_filter", ParamKind.Text),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_selectFile"),

        new("selectFolder", ActionCategory.Data, "folder", "Shortcuts_Action_selectFolder",
        [
            new("title", "Shortcuts_Param_title", ParamKind.Text),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_selectFolder"),

        new("pathExists", ActionCategory.Data, "folder", "Shortcuts_Action_pathExists",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
        ],
        Ports(new OutputSpec("exists", "Shortcuts_Port_Exists", DataType.Boolean, IsDefault: true)),
        "Shortcuts_Desc_pathExists"),

        new("readTextFile", ActionCategory.Data, "text", "Shortcuts_Action_readTextFile",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
            new("encoding", "Shortcuts_Param_encoding", ParamKind.Choice, "auto",
            [
                ("auto", "Shortcuts_Encoding_auto"),
                ("utf8", "Shortcuts_Encoding_utf8"),
                ("gbk", "Shortcuts_Encoding_gbk"),
            ]),
        ],
        Ports(new OutputSpec("text", "Shortcuts_Port_Text", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_readTextFile"),

        new("writeTextFile", ActionCategory.Data, "text", "Shortcuts_Action_writeTextFile",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
            new("text", "Shortcuts_Param_text", ParamKind.MultilineText, Primary: true,
                Required: true, AllowEmpty: true),
            new("mode", "Shortcuts_Param_writeMode", ParamKind.Choice, "create",
            [
                ("create", "Shortcuts_Write_create"),
                ("overwrite", "Shortcuts_Write_overwrite"),
                ("append", "Shortcuts_Write_append"),
            ], Primary: true, Required: true, HelpKey: "Shortcuts_Help_writeMode"),
            new("encoding", "Shortcuts_Param_encoding", ParamKind.Choice, "utf8",
            [
                ("utf8", "Shortcuts_Encoding_utf8"),
                ("gbk", "Shortcuts_Encoding_gbk"),
            ]),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_writeTextFile"),

        new("listFiles", ActionCategory.Data, "list", "Shortcuts_Action_listFiles",
        [
            new("directory", "Shortcuts_Param_directory", ParamKind.FolderPath, Primary: true,
                DataType: DataType.Path, Required: true),
            new("filter", "Shortcuts_Param_filter", ParamKind.Text, "*"),
            new("recursive", "Shortcuts_Param_recursive", ParamKind.Choice, "false",
            [
                ("true", "Shortcuts_Recursive_on"),
                ("false", "Shortcuts_Recursive_off"),
            ], DataType: DataType.Boolean),
        ],
        Ports(new OutputSpec("list", "Shortcuts_Port_List", DataType.List, IsDefault: true)),
        "Shortcuts_Desc_listFiles"),

        new("pathJoin", ActionCategory.Data, "folder", "Shortcuts_Action_pathJoin",
        [
            new("base", "Shortcuts_Param_basePath", ParamKind.FolderPath, Primary: true,
                DataType: DataType.Path, Required: true),
            new("child", "Shortcuts_Param_childPath", ParamKind.Text, Primary: true,
                Required: true, AllowEmpty: false),
        ],
        Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
        "Shortcuts_Desc_pathJoin"),

        new("pathInfo", ActionCategory.Data, "folder", "Shortcuts_Action_pathInfo",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true,
                DataType: DataType.Path, Required: true),
        ],
        Ports(
            new("name", "Shortcuts_Port_Name", DataType.Text, IsDefault: true),
            new("extension", "Shortcuts_Port_Extension", DataType.Text),
            new("parent", "Shortcuts_Port_Parent", DataType.Path)),
        "Shortcuts_Desc_pathInfo"),

        // ---- Windows (grey, second half) -------------------------------------------
        new("findWindow", ActionCategory.System, "desktop", "Shortcuts_Action_findWindow",
        [
            new("title", "Shortcuts_Param_windowTitle", ParamKind.Text, Primary: true,
                Required: true, AllowEmpty: false),
            new("match", "Shortcuts_Param_match", ParamKind.Choice, "contains",
            [
                ("contains", "Shortcuts_Match_contains"),
                ("equals", "Shortcuts_Match_equals"),
                ("startsWith", "Shortcuts_Match_startsWith"),
            ], Primary: true),
        ],
        Ports(new OutputSpec("window", "Shortcuts_Port_Window", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_findWindow"),

        new("activateWindow", ActionCategory.System, "desktop", "Shortcuts_Action_activateWindow",
        [
            new("window", "Shortcuts_Param_window", ParamKind.Text, Primary: true,
                DataType: DataType.Text, Required: true, AllowEmpty: false),
        ],
        Ports(), "Shortcuts_Desc_activateWindow"),

        new("waitForWindow", ActionCategory.System, "desktop", "Shortcuts_Action_waitForWindow",
        [
            new("title", "Shortcuts_Param_windowTitle", ParamKind.Text, Primary: true,
                Required: true, AllowEmpty: false),
            new("match", "Shortcuts_Param_match", ParamKind.Choice, "contains",
            [
                ("contains", "Shortcuts_Match_contains"),
                ("equals", "Shortcuts_Match_equals"),
                ("startsWith", "Shortcuts_Match_startsWith"),
            ], Primary: true),
            new("timeout", "Shortcuts_Param_timeout", ParamKind.Number, "10", Min: 1, Max: 120,
                UnitKey: "Shortcuts_Param_SecondsUnit", DataType: DataType.Number),
        ],
        Ports(new OutputSpec("window", "Shortcuts_Port_Window", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_waitForWindow"),

        // ---- Control flow (purple) -------------------------------------------------
        // Graph nodes: each owns named output ports (see WorkflowNodeTypes.Outputs), which is
        // what makes real branching/looping possible — the old nested containers could only
        // run children in order.
        new("start", ActionCategory.Flow, "play", "Shortcuts_Action_start", [], [], Hidden: true),

        new("branch", ActionCategory.Flow, "branch", "Shortcuts_Action_branch",
        [
            new("left", "Shortcuts_Param_left", ParamKind.Text, Primary: true, DataType: DataType.Any),
            new("op", "Shortcuts_Param_op", ParamKind.Choice, "equals",
            [
                ("equals", "Shortcuts_Op_equals"),
                ("notEquals", "Shortcuts_Op_notEquals"),
                ("contains", "Shortcuts_Op_contains"),
                ("startsWith", "Shortcuts_Op_startsWith"),
                ("endsWith", "Shortcuts_Op_endsWith"),
                ("greater", "Shortcuts_Op_greater"),
                ("less", "Shortcuts_Op_less"),
                ("greaterOrEqual", "Shortcuts_Op_greaterOrEqual"),
                ("lessOrEqual", "Shortcuts_Op_lessOrEqual"),
                ("isEmpty", "Shortcuts_Op_isEmpty"),
                ("isNotEmpty", "Shortcuts_Op_isNotEmpty"),
                ("isTrue", "Shortcuts_Op_isTrue"),
                ("isFalse", "Shortcuts_Op_isFalse"),
            ], Primary: true, Required: true),
            new("right", "Shortcuts_Param_right", ParamKind.Text, DataType: DataType.Any),
            new("ignoreCase", "Shortcuts_Param_ignoreCase", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Case_ignore"),
                ("false", "Shortcuts_Case_exact"),
            ], DataType: DataType.Boolean,
                VisibleWhen: new ParamVisibility("op", "equals", "notEquals", "contains",
                    "startsWith", "endsWith")),
        ], [], "Shortcuts_Desc_branch"),

        new("switch", ActionCategory.Flow, "branch", "Shortcuts_Action_switch",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, Primary: true,
                DataType: DataType.Any),
            new("ignoreCase", "Shortcuts_Param_ignoreCase", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Case_ignore"),
                ("false", "Shortcuts_Case_exact"),
            ], DataType: DataType.Boolean),
        ], [], "Shortcuts_Desc_switch"),

        new("forLoop", ActionCategory.Flow, "repeat", "Shortcuts_Action_forLoop",
        [
            new("count", "Shortcuts_Param_count", ParamKind.Number, "3", Min: 1, Max: 1000,
                UnitKey: "Shortcuts_Unit_Times", Primary: true, DataType: DataType.Number,
                Required: true),
        ], [], "Shortcuts_Desc_forLoop"),

        new("whileLoop", ActionCategory.Flow, "repeat", "Shortcuts_Action_whileLoop",
        [
            new("left", "Shortcuts_Param_left", ParamKind.Text, Primary: true, DataType: DataType.Any),
            new("op", "Shortcuts_Param_op", ParamKind.Choice, "equals",
            [
                ("equals", "Shortcuts_Op_equals"),
                ("notEquals", "Shortcuts_Op_notEquals"),
                ("contains", "Shortcuts_Op_contains"),
                ("startsWith", "Shortcuts_Op_startsWith"),
                ("endsWith", "Shortcuts_Op_endsWith"),
                ("greater", "Shortcuts_Op_greater"),
                ("less", "Shortcuts_Op_less"),
                ("greaterOrEqual", "Shortcuts_Op_greaterOrEqual"),
                ("lessOrEqual", "Shortcuts_Op_lessOrEqual"),
                ("isEmpty", "Shortcuts_Op_isEmpty"),
                ("isNotEmpty", "Shortcuts_Op_isNotEmpty"),
                ("isTrue", "Shortcuts_Op_isTrue"),
                ("isFalse", "Shortcuts_Op_isFalse"),
            ], Primary: true, Required: true),
            new("right", "Shortcuts_Param_right", ParamKind.Text, DataType: DataType.Any),
            new("max", "Shortcuts_Param_maxIterations", ParamKind.Number, "100", Min: 1, Max: 1000,
                UnitKey: "Shortcuts_Unit_Times", Primary: true, DataType: DataType.Number,
                Required: true),
            new("ignoreCase", "Shortcuts_Param_ignoreCase", ParamKind.Choice, "true",
            [
                ("true", "Shortcuts_Case_ignore"),
                ("false", "Shortcuts_Case_exact"),
            ], DataType: DataType.Boolean,
                VisibleWhen: new ParamVisibility("op", "equals", "notEquals", "contains",
                    "startsWith", "endsWith")),
        ], [], "Shortcuts_Desc_whileLoop"),

        new("forEach", ActionCategory.Flow, "list", "Shortcuts_Action_forEach",
        [
            new("list", "Shortcuts_Param_list", ParamKind.List, Primary: true, DataType: DataType.List,
                Required: true),
            new("var", "Shortcuts_Param_varName", ParamKind.Text, "item", Required: true,
                AllowEmpty: false, HelpKey: "Shortcuts_Help_forEachVar"),
            new("separator", "Shortcuts_Param_separator", ParamKind.Choice, "newline",
            [
                ("newline", "Shortcuts_Sep_newline"),
                ("comma", "Shortcuts_Sep_comma"),
                ("space", "Shortcuts_Sep_space"),
            ], HelpKey: "Shortcuts_Help_forEachSeparator"),
        ], [], "Shortcuts_Desc_forEach"),

        new("break", ActionCategory.Flow, "stop", "Shortcuts_Action_break", [], [],
            "Shortcuts_Desc_break"),
        new("continue", ActionCategory.Flow, "next", "Shortcuts_Action_continue", [], [],
            "Shortcuts_Desc_continue"),
        new("end", ActionCategory.Flow, "stop", "Shortcuts_Action_end", [], [],
            "Shortcuts_Desc_end"),

        new("setVar", ActionCategory.Flow, "text", "Shortcuts_Action_setVar",
        [
            new("name", "Shortcuts_Param_varName", ParamKind.Text, "result", Primary: true,
                Required: true, AllowEmpty: false, HelpKey: "Shortcuts_Help_setVarName"),
            new("value", "Shortcuts_Param_value", ParamKind.MultilineText, Primary: true,
                DataType: DataType.Any),
        ],
        [
            new(WorkflowNodeTypes.Out, POut, IsExec: true),
            new(WorkflowNodeTypes.ErrorPort, PErr, IsExec: true),
            new("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true),
        ], "Shortcuts_Desc_setVar"),

        new("collect", ActionCategory.Flow, "list", "Shortcuts_Action_collect",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, Primary: true,
                DataType: DataType.Any),
        ],
        [
            new(WorkflowNodeTypes.Out, POut, IsExec: true),
            new(WorkflowNodeTypes.ErrorPort, PErr, IsExec: true),
            new("list", "Shortcuts_Port_List", DataType.List, IsDefault: true),
        ], "Shortcuts_Desc_collect"),

        new("mergeValue", ActionCategory.Flow, "branch", "Shortcuts_Action_mergeValue",
        [
            new("a", "Shortcuts_Param_item1", ParamKind.Text, DataType: DataType.Any, Primary: true),
            new("b", "Shortcuts_Param_item2", ParamKind.Text, DataType: DataType.Any),
            new("c", "Shortcuts_Param_item3", ParamKind.Text, DataType: DataType.Any),
            new("d", "Shortcuts_Param_item4", ParamKind.Text, DataType: DataType.Any),
        ],
        [
            new(WorkflowNodeTypes.Out, POut, IsExec: true),
            new(WorkflowNodeTypes.ErrorPort, PErr, IsExec: true),
            new("value", "Shortcuts_Port_Value", DataType.Any, IsDefault: true),
        ], "Shortcuts_Desc_mergeValue"),

        new("return", ActionCategory.Flow, "next", "Shortcuts_Action_return",
        [
            new("value", "Shortcuts_Param_value", ParamKind.Text, Primary: true,
                DataType: DataType.Any),
        ], [], "Shortcuts_Desc_return"),

        new("note", ActionCategory.Flow, "comment", "Shortcuts_Action_note",
        [
            new("note", "Shortcuts_Param_note", ParamKind.MultilineText, Primary: true),
        ], [], "Shortcuts_Desc_note"),

        new("runShortcut", ActionCategory.Flow, "bolt", "Shortcuts_Action_runShortcut",
        [
            new("target", "Shortcuts_Param_shortcut", ParamKind.Shortcut, Primary: true,
                Required: true, AllowEmpty: false, Bindable: false),
            new("arguments", "Shortcuts_Param_arguments", ParamKind.Json,
                DataType: DataType.Object, HelpKey: "Shortcuts_Help_arguments"),
        ],
        Ports(new OutputSpec("result", "Shortcuts_Port_Result", DataType.Any, IsDefault: true)),
        "Shortcuts_Desc_runShortcut"),

        // ---- Legacy v1 containers (migrated to branch / forLoop on load) -----------
        new("if", ActionCategory.Flow, "branch", "Shortcuts_Action_if",
        [
            new("left", "Shortcuts_Param_left", ParamKind.Text, Primary: true, DataType: DataType.Any),
            new("op", "Shortcuts_Param_op", ParamKind.Choice, "equals",
            [
                ("equals", "Shortcuts_Op_equals"),
                ("notEquals", "Shortcuts_Op_notEquals"),
                ("contains", "Shortcuts_Op_contains"),
                ("startsWith", "Shortcuts_Op_startsWith"),
                ("endsWith", "Shortcuts_Op_endsWith"),
                ("greater", "Shortcuts_Op_greater"),
                ("less", "Shortcuts_Op_less"),
                ("isEmpty", "Shortcuts_Op_isEmpty"),
                ("isNotEmpty", "Shortcuts_Op_isNotEmpty"),
            ], Primary: true, Required: true),
            new("right", "Shortcuts_Param_right", ParamKind.Text, DataType: DataType.Any),
        ], [], Hidden: true),

        new("repeat", ActionCategory.Flow, "repeat", "Shortcuts_Action_repeat",
        [
            new("count", "Shortcuts_Param_count", ParamKind.Number, "3", Min: 1, Max: 100,
                UnitKey: "Shortcuts_Unit_Times", Primary: true, DataType: DataType.Number),
        ], [], Hidden: true),

        // Legacy pass-through comment: it keeps its exec exits so an old document that used it
        // as a step still runs; new documents use `note`, which never executes.
        new("comment", ActionCategory.Flow, "comment", "Shortcuts_Action_comment",
        [
            new("note", "Shortcuts_Param_note", ParamKind.Text, Primary: true),
        ], Ports(), Hidden: true),

        // ---- DeskCanvas host integration (teal) ------------------------------------
        new("themeSurface", ActionCategory.DeskCanvas, "sliders", "Shortcuts_Action_themeSurface",
        [
            new("mode", "Shortcuts_Param_surface", ParamKind.Choice, "cycle",
            [
                ("cycle", "Shortcuts_Surface_cycle"),
                ("acrylic", "Shortcuts_Surface_acrylic"),
                ("liquidglass", "Shortcuts_Surface_liquidglass"),
                ("colorful", "Shortcuts_Surface_colorful"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("surface", "Shortcuts_Port_Surface", DataType.Text, IsDefault: true)),
        "Shortcuts_Desc_themeSurface"),

        new("themeDarkMode", ActionCategory.DeskCanvas, "sun", "Shortcuts_Action_themeDarkMode",
        [
            new("mode", "Shortcuts_Param_mode", ParamKind.Choice, "toggle",
            [
                ("toggle", "Shortcuts_Dark_toggle"),
                ("cycle", "Shortcuts_Dark_cycle"),
                ("dark", "Shortcuts_Dark_dark"),
                ("light", "Shortcuts_Dark_light"),
                ("system", "Shortcuts_Dark_system"),
            ], Primary: true, Required: true, HelpKey: "Shortcuts_Help_themeDarkMode"),
        ],
        Ports(new OutputSpec("dark", "Shortcuts_Port_Dark", DataType.Boolean, IsDefault: true)),
        "Shortcuts_Desc_themeDarkMode"),

        new("widgetsVisibility", ActionCategory.DeskCanvas, "eye", "Shortcuts_Action_widgetsVisibility",
        [
            new("mode", "Shortcuts_Param_mode", ParamKind.Choice, "toggle",
            [
                ("toggle", "Shortcuts_Mode_toggle"),
                ("hide", "Shortcuts_Mode_hide"),
                ("show", "Shortcuts_Mode_show"),
            ], Primary: true, Required: true),
        ],
        Ports(new OutputSpec("visible", "Shortcuts_Port_Visible", DataType.Boolean, IsDefault: true)),
        "Shortcuts_Desc_widgetsVisibility"),

        new("addReminder", ActionCategory.DeskCanvas, "list", "Shortcuts_Action_addReminder",
        [
            new("title", "Shortcuts_Param_title", ParamKind.Text, Primary: true, Required: true,
                AllowEmpty: false),
            new("due", "Shortcuts_Param_due", ParamKind.Text, DataType: DataType.DateTime,
                HelpKey: "Shortcuts_Help_due"),
            new("notes", "Shortcuts_Param_notes", ParamKind.MultilineText),
        ],
        Ports(new OutputSpec("due", "Shortcuts_Port_Due", DataType.DateTime, IsDefault: true)),
        "Shortcuts_Desc_addReminder"),

        new("openDataFolder", ActionCategory.DeskCanvas, "folder", "Shortcuts_Action_openDataFolder",
            [], Ports(new OutputSpec("path", "Shortcuts_Port_Path", DataType.Path, IsDefault: true)),
            "Shortcuts_Desc_openDataFolder"),
    ];

    private static readonly Dictionary<string, ActionDefinition> Index =
        Definitions.ToDictionary(d => d.Type, StringComparer.Ordinal);

    /// <summary>All definitions, in catalog order (grouped by category).</summary>
    public static IReadOnlyList<ActionDefinition> All => Definitions;

    /// <summary>Definitions the library drawer offers (legacy containers are hidden).</summary>
    public static IReadOnlyList<ActionDefinition> Visible { get; } =
        Definitions.Where(d => !d.Hidden).ToList();

    public static ActionDefinition? Get(string type) =>
        Index.GetValueOrDefault(type);

    /// <summary>Category order used by the library drawer.</summary>
    public static readonly ActionCategory[] CategoryOrder =
    [
        ActionCategory.Launch,
        ActionCategory.Media,
        ActionCategory.System,
        ActionCategory.Text,
        ActionCategory.Data,
        ActionCategory.Flow,
        ActionCategory.DeskCanvas,
    ];

    public static string CategoryLabelKey(ActionCategory category) => category switch
    {
        ActionCategory.Launch => "Shortcuts_Cat_Launch",
        ActionCategory.Media => "Shortcuts_Cat_Media",
        ActionCategory.System => "Shortcuts_Cat_System",
        ActionCategory.Text => "Shortcuts_Cat_Text",
        ActionCategory.Data => "Shortcuts_Cat_Data",
        ActionCategory.Flow => "Shortcuts_Cat_Flow",
        _ => "Shortcuts_Cat_DeskCanvas",
    };

    /// <summary>Block color per category — the iOS "color carries meaning" convention.</summary>
    public static string CategoryColor(ActionCategory category) => category switch
    {
        ActionCategory.Launch => "#185FA5",
        ActionCategory.Media => "#993556",
        ActionCategory.System => "#5F5E5A",
        ActionCategory.Text => "#854F0B",
        ActionCategory.Data => "#3B6D11",
        ActionCategory.Flow => "#534AB7",
        _ => "#0F6E56",
    };

    /// <summary>Tile palette offered by the icon picker.</summary>
    public static readonly string[] Palette =
    [
        "#534AB7", "#185FA5", "#0F6E56", "#854F0B", "#993556", "#5F5E5A",
        "#3B6D11", "#BA7517", "#A32D2D", "#378ADD", "#888780", "#2C2C2A",
    ];

    public static string OptionLabelKey(ParamSpec spec, string value) =>
        spec.Options?.FirstOrDefault(o => o.Value == value).LabelKey ?? string.Empty;

    /// <summary>Locale label for a data type, used by port chips and the reference menu.</summary>
    public static string TypeLabelKey(DataType type) => type switch
    {
        DataType.Number => "Shortcuts_Type_number",
        DataType.Boolean => "Shortcuts_Type_boolean",
        DataType.DateTime => "Shortcuts_Type_datetime",
        DataType.Path => "Shortcuts_Type_path",
        DataType.List => "Shortcuts_Type_list",
        DataType.Object => "Shortcuts_Type_json",
        DataType.Null => "Shortcuts_Type_null",
        DataType.Any => "Shortcuts_Type_any",
        _ => "Shortcuts_Type_text",
    };
}
