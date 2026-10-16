namespace Shortcuts.Models;

/// <summary>Action grouping; the category drives the block color in the editor.</summary>
public enum ActionCategory
{
    Launch,
    Media,
    System,
    Text,
    Flow,
    DeskCanvas,
}

/// <summary>How a parameter is edited and rendered.</summary>
public enum ParamKind
{
    Text,
    Number,
    Choice,
    FilePath,
    FolderPath,
}

/// <summary>One parameter of an action.</summary>
/// <param name="Key">Key inside <see cref="ShortcutAction.Params"/>.</param>
/// <param name="LabelKey">Locale key of the label.</param>
/// <param name="Kind">Editor kind.</param>
/// <param name="DefaultValue">Default literal for freshly inserted actions.</param>
/// <param name="Options">(value, localeKey) pairs for <see cref="ParamKind.Choice"/>.</param>
/// <param name="Min">Lower bound for <see cref="ParamKind.Number"/>.</param>
/// <param name="Max">Upper bound for <see cref="ParamKind.Number"/>.</param>
/// <param name="UnitKey">Optional locale format (with {0}) appended to the chip text.</param>
/// <param name="Primary">Primary parameters always show an inline chip, even when unset.</param>
public sealed record ParamSpec(
    string Key,
    string LabelKey,
    ParamKind Kind,
    string? DefaultValue = null,
    IReadOnlyList<(string Value, string LabelKey)>? Options = null,
    double Min = 0,
    double Max = 100,
    string? UnitKey = null,
    bool Primary = false);

/// <summary>Static definition of one action type.</summary>
public sealed record ActionDefinition(
    string Type,
    ActionCategory Category,
    string Glyph,
    string TitleKey,
    IReadOnlyList<ParamSpec> Params);

/// <summary>
/// The v1 action catalog — the "basic safe set": no arbitrary command execution.
/// Executors live in <c>Services/Executors</c> keyed by the same type ids.
/// </summary>
public static class ActionCatalog
{
    private static readonly List<ActionDefinition> Definitions =
    [
        // ---- Launch (blue) ---------------------------------------------------------
        new("openApp", ActionCategory.Launch, "app", "Shortcuts_Action_openApp",
        [
            new("target", "Shortcuts_Param_target", ParamKind.FilePath, Primary: true),
            new("args", "Shortcuts_Param_args", ParamKind.Text),
        ]),
        new("openFile", ActionCategory.Launch, "folder", "Shortcuts_Action_openFile",
        [
            new("path", "Shortcuts_Param_path", ParamKind.FilePath, Primary: true),
        ]),
        new("openUrl", ActionCategory.Launch, "globe", "Shortcuts_Action_openUrl",
        [
            new("url", "Shortcuts_Param_url", ParamKind.Text, "https://", Primary: true),
        ]),
        new("webSearch", ActionCategory.Launch, "search", "Shortcuts_Action_webSearch",
        [
            new("query", "Shortcuts_Param_query", ParamKind.Text, Primary: true),
            new("engine", "Shortcuts_Param_engine", ParamKind.Choice, "bing",
            [
                ("bing", "Shortcuts_Engine_bing"),
                ("baidu", "Shortcuts_Engine_baidu"),
                ("google", "Shortcuts_Engine_google"),
            ]),
        ]),

        // ---- Media (pink) ----------------------------------------------------------
        new("mediaPlayPause", ActionCategory.Media, "play", "Shortcuts_Action_mediaPlayPause", []),
        new("mediaNext", ActionCategory.Media, "next", "Shortcuts_Action_mediaNext", []),
        new("mediaPrevious", ActionCategory.Media, "previous", "Shortcuts_Action_mediaPrevious", []),

        // ---- System & tools (grey) -------------------------------------------------
        new("setVolume", ActionCategory.System, "volume", "Shortcuts_Action_setVolume",
        [
            new("level", "Shortcuts_Param_level", ParamKind.Number, "50", Min: 0, Max: 100,
                UnitKey: "Shortcuts_Unit_Percent", Primary: true),
        ]),
        new("muteToggle", ActionCategory.System, "mute", "Shortcuts_Action_muteToggle", []),
        new("lockScreen", ActionCategory.System, "lock", "Shortcuts_Action_lockScreen", []),
        new("sleep", ActionCategory.System, "moon", "Shortcuts_Action_sleep", []),
        new("screenshot", ActionCategory.System, "camera", "Shortcuts_Action_screenshot", []),
        new("emptyRecycleBin", ActionCategory.System, "trash", "Shortcuts_Action_emptyRecycleBin", []),
        new("wait", ActionCategory.System, "timer", "Shortcuts_Action_wait",
        [
            new("seconds", "Shortcuts_Param_seconds", ParamKind.Number, "1", Min: 0, Max: 600,
                UnitKey: "Shortcuts_Param_SecondsUnit", Primary: true),
        ]),
        new("notify", ActionCategory.System, "bell", "Shortcuts_Action_notify",
        [
            new("message", "Shortcuts_Param_message", ParamKind.Text, Primary: true),
        ]),

        // ---- Text & data (amber) ---------------------------------------------------
        new("getClipboard", ActionCategory.Text, "clip", "Shortcuts_Action_getClipboard", []),
        new("setClipboard", ActionCategory.Text, "clip", "Shortcuts_Action_setClipboard",
        [
            new("text", "Shortcuts_Param_text", ParamKind.Text, Primary: true),
        ]),
        new("textTransform", ActionCategory.Text, "text", "Shortcuts_Action_textTransform",
        [
            new("input", "Shortcuts_Param_input", ParamKind.Text, Primary: true),
            new("operation", "Shortcuts_Param_operation", ParamKind.Choice, "upper",
            [
                ("upper", "Shortcuts_Choice_upper"),
                ("lower", "Shortcuts_Choice_lower"),
                ("trim", "Shortcuts_Choice_trim"),
                ("replace", "Shortcuts_Choice_replace"),
            ], Primary: true),
            new("find", "Shortcuts_Param_find", ParamKind.Text),
            new("replace_with", "Shortcuts_Param_replace_with", ParamKind.Text),
        ]),
        new("currentDate", ActionCategory.Text, "clock", "Shortcuts_Action_currentDate",
        [
            new("format", "Shortcuts_Param_format", ParamKind.Text, "yyyy-MM-dd HH:mm", Primary: true),
        ]),
        new("randomNumber", ActionCategory.Text, "sparkle", "Shortcuts_Action_randomNumber",
        [
            new("min", "Shortcuts_Param_min", ParamKind.Number, "1", Min: -1000000, Max: 1000000),
            new("max", "Shortcuts_Param_max", ParamKind.Number, "100", Min: -1000000, Max: 1000000),
        ]),

        // ---- Control flow (purple) -------------------------------------------------
        new("if", ActionCategory.Flow, "branch", "Shortcuts_Action_if",
        [
            new("left", "Shortcuts_Param_left", ParamKind.Text, Primary: true),
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
            ], Primary: true),
            new("right", "Shortcuts_Param_right", ParamKind.Text),
        ]),
        new("repeat", ActionCategory.Flow, "repeat", "Shortcuts_Action_repeat",
        [
            new("count", "Shortcuts_Param_count", ParamKind.Number, "3", Min: 1, Max: 100,
                UnitKey: "Shortcuts_Unit_Times", Primary: true),
        ]),
        new("comment", ActionCategory.Flow, "comment", "Shortcuts_Action_comment",
        [
            new("note", "Shortcuts_Param_note", ParamKind.Text, Primary: true),
        ]),

        // ---- DeskCanvas host integration (teal) ------------------------------------
        new("themeSurface", ActionCategory.DeskCanvas, "sliders", "Shortcuts_Action_themeSurface",
        [
            new("mode", "Shortcuts_Param_surface", ParamKind.Choice, "cycle",
            [
                ("cycle", "Shortcuts_Surface_cycle"),
                ("acrylic", "Shortcuts_Surface_acrylic"),
                ("liquidglass", "Shortcuts_Surface_liquidglass"),
                ("softglow", "Shortcuts_Surface_softglow"),
                ("liquidglassv2", "Shortcuts_Surface_liquidglassv2"),
                ("solid", "Shortcuts_Surface_solid"),
                ("colorful", "Shortcuts_Surface_colorful"),
            ], Primary: true),
        ]),
        new("themeDarkMode", ActionCategory.DeskCanvas, "sun", "Shortcuts_Action_themeDarkMode",
        [
            new("mode", "Shortcuts_Param_mode", ParamKind.Choice, "toggle",
            [
                ("toggle", "Shortcuts_Mode_toggle"),
                ("dark", "Shortcuts_Dark_dark"),
                ("light", "Shortcuts_Dark_light"),
                ("system", "Shortcuts_Dark_system"),
            ], Primary: true),
        ]),
        new("widgetsVisibility", ActionCategory.DeskCanvas, "eye", "Shortcuts_Action_widgetsVisibility",
        [
            new("mode", "Shortcuts_Param_mode", ParamKind.Choice, "toggle",
            [
                ("toggle", "Shortcuts_Mode_toggle"),
                ("hide", "Shortcuts_Mode_hide"),
                ("show", "Shortcuts_Mode_show"),
            ], Primary: true),
        ]),
        new("addReminder", ActionCategory.DeskCanvas, "list", "Shortcuts_Action_addReminder",
        [
            new("title", "Shortcuts_Param_title", ParamKind.Text, Primary: true),
        ]),
        new("openDataFolder", ActionCategory.DeskCanvas, "folder", "Shortcuts_Action_openDataFolder", []),
    ];

    private static readonly Dictionary<string, ActionDefinition> Index =
        Definitions.ToDictionary(d => d.Type, StringComparer.Ordinal);

    /// <summary>All definitions, in catalog order (grouped by category).</summary>
    public static IReadOnlyList<ActionDefinition> All => Definitions;

    public static ActionDefinition? Get(string type) =>
        Index.GetValueOrDefault(type);

    /// <summary>Category order used by the library drawer.</summary>
    public static readonly ActionCategory[] CategoryOrder =
    [
        ActionCategory.Launch,
        ActionCategory.Media,
        ActionCategory.System,
        ActionCategory.Text,
        ActionCategory.Flow,
        ActionCategory.DeskCanvas,
    ];

    public static string CategoryLabelKey(ActionCategory category) => category switch
    {
        ActionCategory.Launch => "Shortcuts_Cat_Launch",
        ActionCategory.Media => "Shortcuts_Cat_Media",
        ActionCategory.System => "Shortcuts_Cat_System",
        ActionCategory.Text => "Shortcuts_Cat_Text",
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
}
