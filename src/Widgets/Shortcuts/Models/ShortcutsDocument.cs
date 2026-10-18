using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shortcuts.Models;

/// <summary>
/// The unified shortcuts document persisted to <c>shortcuts.json</c> in the data folder.
/// One document is shared by every Shortcuts widget across all screens.
/// </summary>
public sealed class ShortcutsDocument
{
    /// <summary>Schema version; bumped when a migration is needed.</summary>
    public int Version { get; set; } = 1;

    /// <summary>All shortcuts, in the order shown on the card and in the panel.</summary>
    public List<ShortcutDefinition> Shortcuts { get; set; } = [];

    /// <summary>Last shortcut opened in the editor (restores the editor's subject across restarts).</summary>
    public string? LastEditedId { get; set; }
}

/// <summary>A single shortcut: identity, appearance and its action tree.</summary>
public sealed class ShortcutDefinition
{
    public string Id { get; set; } = NewId();
    public string Name { get; set; } = string.Empty;

    /// <summary>Glyph id resolved by <see cref="Services.GlyphLibrary"/>.</summary>
    public string Glyph { get; set; } = "bolt";

    /// <summary>Tile color in HEX (#RRGGBB).</summary>
    public string Color { get; set; } = "#534AB7";

    /// <summary>Ask for confirmation before running (destructive shortcuts).</summary>
    public bool ConfirmBeforeRun { get; set; }

    /// <summary>
    /// Global hotkey in canonical text form ("Ctrl+Alt+1"), or <c>null</c> when unbound.
    /// Missing in old files deserializes to <c>null</c>; null is omitted when writing so
    /// documents that never used hotkeys stay byte-for-byte compatible.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hotkey { get; set; }

    public List<ShortcutAction> Actions { get; set; } = [];

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Deep copy through JSON — the model is a pure data tree, so this is exact.</summary>
    public ShortcutDefinition Clone()
    {
        var json = JsonSerializer.Serialize(this, DocumentJson.Options);
        return JsonSerializer.Deserialize<ShortcutDefinition>(json, DocumentJson.Options)!;
    }

    /// <summary>Total action nodes, containers included (progress denominator).</summary>
    [JsonIgnore]
    public int ActionCount
    {
        get
        {
            var count = 0;
            void Walk(List<ShortcutAction> list)
            {
                foreach (var action in list)
                {
                    count++;
                    Walk(action.Children);
                    Walk(action.ElseChildren);
                }
            }
            Walk(Actions);
            return count;
        }
    }
}

/// <summary>One action node; control-flow actions own <see cref="Children"/> / <see cref="ElseChildren"/>.</summary>
public sealed class ShortcutAction
{
    public string Id { get; set; } = ShortcutDefinition.NewId();

    /// <summary>Action type id resolved through <see cref="ActionCatalog"/>.</summary>
    public string Type { get; set; } = string.Empty;

    public Dictionary<string, ShortcutValue> Params { get; set; } = [];

    /// <summary>Body of a repeat / if action (then-branch).</summary>
    public List<ShortcutAction> Children { get; set; } = [];

    /// <summary>Else-branch of an <c>if</c> action.</summary>
    public List<ShortcutAction> ElseChildren { get; set; } = [];

    /// <summary>Editor state: container collapsed.</summary>
    public bool Collapsed { get; set; }

    public static ShortcutAction Create(ActionDefinition definition)
    {
        var action = new ShortcutAction { Type = definition.Type };
        foreach (var spec in definition.Params)
        {
            if (spec.DefaultValue != null)
                action.Params[spec.Key] = ShortcutValue.Literal(spec.DefaultValue);
        }
        return action;
    }

    /// <summary>Convenience for building default documents / templates.</summary>
    public static ShortcutAction Create(string type) =>
        ActionCatalog.Get(type) is { } definition
            ? Create(definition)
            : new ShortcutAction { Type = type };

    public static ShortcutAction With(string type, params (string Key, string Value)[] values)
    {
        var action = Create(type);
        foreach (var (key, value) in values)
            action.Params[key] = ShortcutValue.Literal(value);
        return action;
    }
}

/// <summary>
/// A parameter value: either a literal string, or a reference to the output of an earlier
/// action (the "magic variable" of the design). <c>SourceActionId == "@last"</c> means
/// "result of the previous step".
/// </summary>
public sealed class ShortcutValue
{
    /// <summary>"literal" or "variable".</summary>
    public string Kind { get; set; } = ValueKinds.Literal;

    /// <summary>Literal text (numbers and booleans are stored as text, parsed per parameter spec).</summary>
    public string? Text { get; set; }

    /// <summary>Referenced action id, or "@last" for the previous step's result.</summary>
    public string? SourceActionId { get; set; }

    [JsonIgnore]
    public bool IsVariable => Kind == ValueKinds.Variable && !string.IsNullOrEmpty(SourceActionId);

    public static ShortcutValue Literal(string? text) => new() { Kind = ValueKinds.Literal, Text = text };

    public static ShortcutValue Variable(string sourceActionId) =>
        new() { Kind = ValueKinds.Variable, SourceActionId = sourceActionId };

    public ShortcutValue Clone() => new() { Kind = Kind, Text = Text, SourceActionId = SourceActionId };
}

public static class ValueKinds
{
    public const string Literal = "literal";
    public const string Variable = "variable";

    /// <summary>Special variable source: the output of the previous step.</summary>
    public const string LastResult = "@last";
}

/// <summary>Shared serializer options for the document tree.</summary>
public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    public static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Tree helpers used by the runner and the editor.</summary>
public static class ShortcutTree
{
    /// <summary>Walk every node with the list that contains it and its index in that list.</summary>
    public static IEnumerable<(ShortcutAction Action, List<ShortcutAction> Siblings, int Index)> Walk(
        List<ShortcutAction> roots)
    {
        for (var i = 0; i < roots.Count; i++)
        {
            var action = roots[i];
            yield return (action, roots, i);
            foreach (var item in Walk(action.Children)) yield return item;
            foreach (var item in Walk(action.ElseChildren)) yield return item;
        }
    }

    public static IEnumerable<ShortcutAction> Flatten(List<ShortcutAction> roots)
    {
        foreach (var (action, _, _) in Walk(roots)) yield return action;
    }

    public static ShortcutAction? Find(List<ShortcutAction> roots, string id) =>
        Flatten(roots).FirstOrDefault(a => a.Id == id);

    /// <summary>True when <paramref name="candidate"/> lives inside <paramref name="root"/> (self included).</summary>
    public static bool Contains(ShortcutAction root, string candidateId)
    {
        if (root.Id == candidateId) return true;
        return Flatten(root.Children).Any(a => a.Id == candidateId)
               || Flatten(root.ElseChildren).Any(a => a.Id == candidateId);
    }

    /// <summary>Remove a node from wherever it currently lives; returns true when found.</summary>
    public static bool Remove(List<ShortcutAction> roots, string id)
    {
        var found = Walk(roots).FirstOrDefault(t => t.Action.Id == id);
        if (found.Action == null) return false;
        found.Siblings.RemoveAt(found.Index);
        return true;
    }
}
