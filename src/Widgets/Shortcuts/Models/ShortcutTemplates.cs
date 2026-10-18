namespace Shortcuts.Models;

/// <summary>Starter shortcuts and panel templates (all action types are in the safe v1 set).</summary>
public static class ShortcutTemplates
{
    /// <summary>The three shortcuts seeded into a fresh document.</summary>
    public static List<ShortcutDefinition> CreateStarterSet() =>
    [
        LockScreen(),
        QuietFocus(),
        Screenshot(),
    ];

    /// <summary>A blank shortcut for the "new" entry points.</summary>
    public static ShortcutDefinition Blank() => new()
    {
        Name = Services.Loc.Get("Shortcuts_NewName"),
        Glyph = "bolt",
        Color = "#534AB7",
        Actions = [],
    };

    public static ShortcutDefinition LockScreen() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_Lock"),
        Glyph = "lock",
        Color = "#185FA5",
        Actions = [ShortcutAction.Create("lockScreen")],
    };

    public static ShortcutDefinition QuietFocus() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_Mute"),
        Glyph = "mute",
        Color = "#534AB7",
        Actions =
        [
            ShortcutAction.Create("muteToggle"),
            ShortcutAction.With("notify", ("message", Services.Loc.Get("Shortcuts_Output_Muted"))),
        ],
    };

    public static ShortcutDefinition Screenshot() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_Shot"),
        Glyph = "camera",
        Color = "#0F6E56",
        Actions =
        [
            ShortcutAction.Create("screenshot"),
            ShortcutAction.With("notify", ("message", Services.Loc.Get("Shortcuts_Output_Screenshot"))),
        ],
    };

    /// <summary>Dim the room, start playback and open a blank note surface.</summary>
    public static ShortcutDefinition DeepFocus() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_Focus"),
        Glyph = "moon",
        Color = "#534AB7",
        Actions =
        [
            ShortcutAction.With("themeDarkMode", ("mode", "dark")),
            ShortcutAction.With("openApp", ("target", "notepad.exe")),
            ShortcutAction.With("notify", ("message", Services.Loc.Get("Shortcuts_Template_FocusNotice"))),
            // Media last: no active SMTC session is a normal state and must not block the rest.
            ShortcutAction.Create("mediaPlayPause"),
        ],
    };

    /// <summary>Open the usual working folders, a scratchpad and the browser start page.</summary>
    public static ShortcutDefinition StartWorkspace() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_Workspace"),
        Glyph = "app",
        Color = "#185FA5",
        Actions =
        [
            ShortcutAction.With("openFolder", ("path", @"%USERPROFILE%\Desktop")),
            ShortcutAction.With("openFolder", ("path", @"%USERPROFILE%\Downloads")),
            ShortcutAction.With("openUrl", ("url", "https://cn.bing.com")),
            ShortcutAction.With("openApp", ("target", "notepad.exe")),
        ],
    };

    /// <summary>End-of-day sequence; sleep is destructive enough to require a confirmation.</summary>
    public static ShortcutDefinition ClockOut() => new()
    {
        Name = Services.Loc.Get("Shortcuts_Template_ClockOut"),
        Glyph = "lock",
        Color = "#5F5E5A",
        ConfirmBeforeRun = true,
        Actions =
        [
            ShortcutAction.With("wait", ("seconds", "2")),
            ShortcutAction.Create("lockScreen"),
            ShortcutAction.Create("sleep"),
        ],
    };

    /// <summary>Template entries offered by the panel's "new" menu: (locale key, factory).</summary>
    public static IReadOnlyList<(string LabelKey, Func<ShortcutDefinition> Create)> Menu { get; } =
    [
        ("Shortcuts_Template_Blank", Blank),
        ("Shortcuts_Template_Lock", LockScreen),
        ("Shortcuts_Template_Mute", QuietFocus),
        ("Shortcuts_Template_Shot", Screenshot),
        ("Shortcuts_Template_Focus", DeepFocus),
        ("Shortcuts_Template_Workspace", StartWorkspace),
        ("Shortcuts_Template_ClockOut", ClockOut),
    ];
}
