using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shortcuts.Models;

/// <summary>
/// The unified shortcuts document persisted to <c>shortcuts.json</c> in the data folder.
/// One document is shared by every Shortcuts widget across all screens.
/// </summary>
public sealed class ShortcutsDocument
{
    /// <summary>
    /// Schema version. 1 = nested action tree, 2 = execution graph, 3 = typed ports, error
    /// exits, shortcut inputs and per-node error policy. Every step is idempotent and the
    /// previous file is backed up before it is rewritten.
    /// </summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>All shortcuts, in the order shown on the card and in the panel.</summary>
    public List<ShortcutDefinition> Shortcuts { get; set; } = [];

    /// <summary>Last shortcut opened in the editor (restores the editor's subject across restarts).</summary>
    public string? LastEditedId { get; set; }
}

/// <summary>One declared input of a shortcut, exposed as a data port on its <c>start</c> node.</summary>
public sealed class ShortcutInputField
{
    public string Key { get; set; } = "input1";

    public string Name { get; set; } = string.Empty;

    public DataType DataType { get; set; } = DataType.Text;

    public bool Required { get; set; }

    public string? DefaultValue { get; set; }
}

/// <summary>A single shortcut: identity, appearance and its execution graph.</summary>
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

    /// <summary>Declared input fields; the start node exposes one data port each.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ShortcutInputField>? InputFields { get; set; }

    /// <summary>
    /// Execution graph — the single source of truth. Null only for documents written by
    /// v1 (or freshly built templates); <see cref="EffectiveGraph"/> migrates those in place.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowGraph? Graph { get; set; }

    /// <summary>
    /// Legacy v1 action tree. Read once during migration, then emptied so the document has
    /// exactly one source of truth (a stale tree silently shadowing the graph would be the
    /// worst possible failure mode).
    /// </summary>
    public List<ShortcutAction> Actions { get; set; } = [];

    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public static string NewId() => Guid.NewGuid().ToString("N");

    /// <summary>Inputs, never null (a fresh shortcut declares none).</summary>
    [JsonIgnore]
    public List<ShortcutInputField> Inputs => InputFields ??= [];

    /// <summary>The graph to run/edit, migrating a v1 action tree on first access.</summary>
    [JsonIgnore]
    public WorkflowGraph EffectiveGraph
    {
        get
        {
            if (Graph != null) return Graph;

            Graph = WorkflowMigration.FromActions(Actions);
            Actions = [];
            return Graph;
        }
    }

    /// <summary>Deep copy through JSON — the model is a pure data tree, so this is exact.</summary>
    public ShortcutDefinition Clone()
    {
        var json = JsonSerializer.Serialize(this, DocumentJson.Options);
        return JsonSerializer.Deserialize<ShortcutDefinition>(json, DocumentJson.Options)!;
    }

    /// <summary>Executable node count, notes included (progress denominator / tile caption).</summary>
    [JsonIgnore]
    public int ActionCount
    {
        get
        {
            var graph = EffectiveGraph;
            return graph.Nodes.Count(n => n.Type != WorkflowNodeTypes.Start);
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
/// A parameter value: either a literal string, or a reference to the typed output of an earlier
/// node (the "magic variable" of the design). <c>SourceActionId == "@last"</c> means
/// "the most recent data result".
/// <para>
/// Serialized compactly: a plain literal is written as a JSON string, a reference as an object.
/// That keeps AI-authored documents readable (<c>"target": "notepad.exe"</c>) while still
/// round-tripping the richer form.
/// </para>
/// </summary>
[JsonConverter(typeof(ShortcutValueJsonConverter))]
public sealed class ShortcutValue
{
    /// <summary>"literal" or "variable".</summary>
    public string Kind { get; set; } = ValueKinds.Literal;

    /// <summary>Literal text (numbers and booleans are stored as text, parsed per parameter spec).</summary>
    public string? Text { get; set; }

    /// <summary>Referenced node id (or a named variable), or "@last" for the latest data result.</summary>
    public string? SourceActionId { get; set; }

    /// <summary>
    /// Output port of the referenced node. Null means "that node's default data output", which
    /// keeps old documents working while still allowing multi-output actions.
    /// </summary>
    public string? SourcePort { get; set; }

    [JsonIgnore]
    public bool IsVariable => Kind == ValueKinds.Variable && !string.IsNullOrEmpty(SourceActionId);

    public static ShortcutValue Literal(string? text) => new() { Kind = ValueKinds.Literal, Text = text };

    public static ShortcutValue Variable(string sourceActionId, string? sourcePort = null) =>
        new() { Kind = ValueKinds.Variable, SourceActionId = sourceActionId, SourcePort = sourcePort };

    public ShortcutValue Clone() =>
        new() { Kind = Kind, Text = Text, SourceActionId = SourceActionId, SourcePort = SourcePort };
}

public static class ValueKinds
{
    public const string Literal = "literal";
    public const string Variable = "variable";

    /// <summary>Special variable source: the most recent data result.</summary>
    public const string LastResult = "@last";
}

/// <summary>
/// Reads a parameter value from either a JSON string (literal) or an object
/// (<c>{"kind":"variable","sourceActionId":"n3","sourcePort":"text"}</c>); writes the compact form.
/// </summary>
public sealed class ShortcutValueJsonConverter : JsonConverter<ShortcutValue>
{
    public override ShortcutValue? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return ShortcutValue.Literal(reader.GetString());

            case JsonTokenType.Number:
                return ShortcutValue.Literal(reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture));

            case JsonTokenType.True:
                return ShortcutValue.Literal("true");

            case JsonTokenType.False:
                return ShortcutValue.Literal("false");

            case JsonTokenType.Null:
                return null;

            case JsonTokenType.StartObject:
            {
                string kind = ValueKinds.Literal;
                string? text = null;
                string? source = null;
                string? port = null;

                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    if (reader.TokenType != JsonTokenType.PropertyName) continue;
                    var name = reader.GetString();
                    reader.Read();

                    if (string.Equals(name, "kind", StringComparison.OrdinalIgnoreCase))
                        kind = reader.GetString() ?? ValueKinds.Literal;
                    else if (string.Equals(name, "text", StringComparison.OrdinalIgnoreCase))
                        text = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    else if (string.Equals(name, "sourceActionId", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(name, "source", StringComparison.OrdinalIgnoreCase))
                        source = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    else if (string.Equals(name, "sourcePort", StringComparison.OrdinalIgnoreCase))
                        port = reader.TokenType == JsonTokenType.Null ? null : reader.GetString();
                    else
                        reader.Skip();
                }

                return new ShortcutValue { Kind = kind, Text = text, SourceActionId = source, SourcePort = port };
            }

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, ShortcutValue value, JsonSerializerOptions options)
    {
        if (value.IsVariable)
        {
            writer.WriteStartObject();
            writer.WriteString("kind", ValueKinds.Variable);
            writer.WriteString("sourceActionId", value.SourceActionId);
            if (!string.IsNullOrEmpty(value.SourcePort)) writer.WriteString("sourcePort", value.SourcePort);
            writer.WriteEndObject();
            return;
        }

        writer.WriteStringValue(value.Text ?? string.Empty);
    }
}

/// <summary>Shared serializer options for the document tree.</summary>
public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
        Converters = { new ShortcutValueJsonConverter() },
    };

    public static readonly JsonSerializerOptions FileOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new ShortcutValueJsonConverter() },
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
