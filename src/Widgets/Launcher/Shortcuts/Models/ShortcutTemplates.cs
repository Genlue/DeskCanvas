namespace Shortcuts.Models;

/// <summary>
/// Starter shortcuts and panel templates (all action types are in the safe set).
/// <para>
/// Templates are graphs, not v1 trees: a template may need a data link (the screenshot path
/// feeding the notification) or a routed error exit, and those only exist in the graph model.
/// </para>
/// </summary>
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
        Graph = WorkflowMigration.FromActions([]),
    };

    public static ShortcutDefinition LockScreen() => Build(
        "Shortcuts_Template_Lock", "lock", "#185FA5",
        ShortcutAction.Create("lockScreen"));

    /// <summary>Mute explicitly and report the state the action actually produced.</summary>
    public static ShortcutDefinition QuietFocus()
    {
        var mute = ShortcutAction.With("setMute", ("muted", "true"));
        // The reported state is the setMute output, not a fixed sentence. The token resolves
        // through the node's default data port at run time.
        var message = $"{Services.Loc.Get("Shortcuts_Output_Muted")}: {{{{{mute.Id}}}}}";
        var notify = ShortcutAction.With("notify", ("message", message));
        return Build("Shortcuts_Template_Mute", "mute", "#534AB7", mute, notify);
    }

    /// <summary>Save a screenshot and show the real file path the action returned.</summary>
    public static ShortcutDefinition Screenshot()
    {
        var shot = ShortcutAction.Create("screenshot");
        var notify = ShortcutAction.Create("notify");
        var shortcut = Build("Shortcuts_Template_Shot", "camera", "#0F6E56", shot, notify);

        // A data link, not a canned sentence: the message is whatever file was actually written.
        shortcut.Graph!.AddDataLink(shot.Id, notify.Id, "message", "path");
        // Keep the binding projection in sync with the link (the canvas renders the source).
        notify.Params["message"] = ShortcutValue.Variable(shot.Id, "path");
        return shortcut;
    }

    /// <summary>Dim the room, start playback and open a blank note surface.</summary>
    public static ShortcutDefinition DeepFocus()
    {
        var dark = ShortcutAction.With("themeDarkMode", ("mode", "dark"));
        var app = ShortcutAction.With("openApp", ("target", "notepad.exe"));
        var media = ShortcutAction.Create("mediaPlayPause");
        var notify = ShortcutAction.With("notify", ("message", Services.Loc.Get("Shortcuts_Template_FocusNotice")));

        var shortcut = Build("Shortcuts_Template_Focus", "moon", "#534AB7", dark, app, media, notify);
        var graph = shortcut.Graph!;
        var mediaNode = graph.Find(media.Id);

        // "No media session" is a normal state. Route the failure to the notification instead of
        // making the media step the last one and relying on it to finish.
        if (mediaNode != null)
        {
            mediaNode.OnError = ErrorPolicy.Route;
            graph.Links.Add(WorkflowLink.Exec(media.Id, WorkflowNodeTypes.ErrorPort, notify.Id));
        }

        return shortcut;
    }

    /// <summary>Open the usual working folders, a scratchpad and the browser start page.</summary>
    public static ShortcutDefinition StartWorkspace() => Build(
        "Shortcuts_Template_Workspace", "app", "#185FA5",
        ShortcutAction.With("openFolder", ("path", @"%USERPROFILE%\Desktop")),
        ShortcutAction.With("openFolder", ("path", @"%USERPROFILE%\Downloads")),
        ShortcutAction.With("openUrl", ("url", "https://cn.bing.com")),
        ShortcutAction.With("openApp", ("target", "notepad.exe")));

    /// <summary>End-of-day sequence; sleep is destructive enough to require a confirmation.</summary>
    public static ShortcutDefinition ClockOut()
    {
        var shortcut = Build(
            "Shortcuts_Template_ClockOut", "lock", "#5F5E5A",
            ShortcutAction.With("wait", ("seconds", "2")),
            ShortcutAction.Create("lockScreen"),
            ShortcutAction.Create("sleep"));
        shortcut.ConfirmBeforeRun = true;
        return shortcut;
    }

    /// <summary>
    /// A minimal declared-input workflow: the start node exposes the input as a data port, the
    /// value is converted and returned to the caller. Short enough to stay a real example.
    /// </summary>
    public static ShortcutDefinition InputDemo()
    {
        var start = new WorkflowNode { Type = WorkflowNodeTypes.Start, X = 0, Y = 0 };
        var convert = WorkflowNode.Create("toJson", 300, 0);
        var ret = WorkflowNode.Create(WorkflowNodeTypes.Return, 600, 0);

        var graph = new WorkflowGraph { Nodes = [start, convert, ret] };
        graph.Links.Add(WorkflowLink.Exec(start.Id, WorkflowNodeTypes.Out, convert.Id));
        graph.Links.Add(WorkflowLink.Exec(convert.Id, WorkflowNodeTypes.Out, ret.Id));
        graph.Links.Add(WorkflowLink.Data(start.Id, "text", convert.Id, "value"));
        graph.Links.Add(WorkflowLink.Data(convert.Id, "text", ret.Id, "value"));

        return new ShortcutDefinition
        {
            Name = Services.Loc.Get("Shortcuts_Template_Input"),
            Glyph = "text",
            Color = "#3B6D11",
            InputFields =
            [
                new ShortcutInputField
                {
                    Key = "text",
                    Name = Services.Loc.Get("Shortcuts_Param_text"),
                    DataType = DataType.Text,
                    Required = true,
                },
            ],
            Graph = graph,
        };
    }

    private static ShortcutDefinition Build(
        string nameKey, string glyph, string color, params ShortcutAction[] actions) => new()
    {
        Name = Services.Loc.Get(nameKey),
        Glyph = glyph,
        Color = color,
        Graph = WorkflowMigration.FromActions(actions.ToList()),
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
        ("Shortcuts_Template_Input", InputDemo),
    ];
}
