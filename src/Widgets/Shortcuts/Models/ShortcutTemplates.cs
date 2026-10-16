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

    /// <summary>Template entries offered by the panel's "new" menu: (locale key, factory).</summary>
    public static IReadOnlyList<(string LabelKey, Func<ShortcutDefinition> Create)> Menu { get; } =
    [
        ("Shortcuts_Template_Blank", Blank),
        ("Shortcuts_Template_Lock", LockScreen),
        ("Shortcuts_Template_Mute", QuietFocus),
        ("Shortcuts_Template_Shot", Screenshot),
    ];
}
