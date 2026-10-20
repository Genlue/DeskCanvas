using System.Globalization;
using System.Text.Json;

namespace Shortcuts.Models;

/// <summary>
/// The value types a port or parameter can carry. <see cref="ParamKind"/> decides which editor
/// control is shown; <see cref="DataType"/> decides what the runner accepts and how a value is
/// passed along a data link. The two are deliberately separate: a <c>Number</c> parameter can be
/// edited with a slider or a text box, and a <c>Text</c> parameter may still hold a path.
/// </summary>
public enum DataType
{
    /// <summary>Accepts anything (only used by generic pass-through ports).</summary>
    Any,
    Text,
    Number,
    Boolean,
    DateTime,
    Path,
    List,
    Object,
    Null,
}

/// <summary>Where a resolved parameter value came from (shown in the run details panel).</summary>
public enum ParamOrigin
{
    /// <summary>Nothing was configured; the catalog default applies.</summary>
    Default,

    /// <summary>A literal typed into the editor.</summary>
    Literal,

    /// <summary>A data link from another node's output port.</summary>
    Link,

    /// <summary>An explicit <c>variable</c> reference (node output or named variable).</summary>
    Variable,

    /// <summary>A loop-scoped reserved name (<c>index</c> / <c>item</c>).</summary>
    LoopVariable,

    /// <summary>The shortcut's own input field.</summary>
    Input,
}

/// <summary>
/// One runtime value. Immutable, self-describing and never <c>null</c> — the runner needs to tell
/// "this node produced an empty string" apart from "this node never ran", and a nullable string
/// cannot express that difference. Everything a step returns travels as one of these.
/// </summary>
public sealed class RunValue
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private RunValue(DataType type, string? text = null, double number = 0, bool boolean = false,
        DateTime date = default, IReadOnlyList<RunValue>? items = null, string? json = null)
    {
        Type = type;
        Text = text;
        Number = number;
        Boolean = boolean;
        Date = date;
        Items = items;
        Json = json;
    }

    public DataType Type { get; }

    /// <summary>Payload for <see cref="DataType.Text"/> / <see cref="DataType.Path"/>.</summary>
    public string? Text { get; }

    public double Number { get; }

    public bool Boolean { get; }

    public DateTime Date { get; }

    /// <summary>Payload for <see cref="DataType.List"/>.</summary>
    public IReadOnlyList<RunValue>? Items { get; }

    /// <summary>Raw JSON payload for <see cref="DataType.Object"/>.</summary>
    public string? Json { get; }

    public static RunValue Null { get; } = new(DataType.Null);

    public static RunValue FromText(string? text) => text == null ? Null : new(DataType.Text, text: text);

    public static RunValue FromPath(string? path) =>
        string.IsNullOrEmpty(path) ? FromText(path) : new(DataType.Path, text: path);

    public static RunValue FromNumber(double number) => new(DataType.Number, number: number);

    public static RunValue FromBoolean(bool value) => new(DataType.Boolean, boolean: value);

    public static RunValue FromDate(DateTime value) => new(DataType.DateTime, date: value);

    public static RunValue FromJson(string? json) =>
        string.IsNullOrWhiteSpace(json) ? Null : new(DataType.Object, json: json);

    public static RunValue FromList(IEnumerable<RunValue> items) =>
        new(DataType.List, items: items.ToList());

    public static RunValue FromList(IEnumerable<string> items) =>
        FromList(items.Select(FromText));

    public bool IsNull => Type == DataType.Null;

    /// <summary>True when the value carries no visible content (null or empty text/list).</summary>
    public bool IsEmpty => Type switch
    {
        DataType.Null => true,
        DataType.Text or DataType.Path => string.IsNullOrEmpty(Text),
        DataType.List => Items is not { Count: > 0 },
        _ => false,
    };

    /// <summary>Text form used for templates, clipboard writes and summaries.</summary>
    public string ToText() => Type switch
    {
        DataType.Null => string.Empty,
        DataType.Text or DataType.Path => Text ?? string.Empty,
        DataType.Number => FormatNumber(Number),
        DataType.Boolean => Boolean ? "true" : "false",
        DataType.DateTime => Date.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        DataType.List => string.Join("\n", (Items ?? []).Select(i => i.ToText())),
        DataType.Object => Json ?? string.Empty,
        _ => Text ?? string.Empty,
    };

    /// <summary>Human-readable label used by the run details panel; null is spelled out.</summary>
    public string DisplayText => Type switch
    {
        DataType.Null => "(null)",
        DataType.Text or DataType.Path when string.IsNullOrEmpty(Text) => "(empty)",
        DataType.List => $"[{Items?.Count ?? 0}] {Truncate(ToText(), 60)}",
        DataType.Object => Truncate(Json ?? string.Empty, 80),
        _ => Truncate(ToText(), 80),
    };

    public bool TryGetNumber(out double value)
    {
        switch (Type)
        {
            case DataType.Number:
                value = Number;
                return true;
            case DataType.Boolean:
                value = Boolean ? 1 : 0;
                return true;
            case DataType.Text or DataType.Path:
                return double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
            default:
                value = 0;
                return false;
        }
    }

    public bool TryGetBoolean(out bool value)
    {
        switch (Type)
        {
            case DataType.Boolean:
                value = Boolean;
                return true;
            case DataType.Number:
                value = Math.Abs(Number) > double.Epsilon;
                return true;
            case DataType.Text or DataType.Path:
            {
                var text = (Text ?? string.Empty).Trim();
                if (bool.TryParse(text, out value)) return true;
                if (text is "1" or "yes" or "on") { value = true; return true; }
                if (text is "0" or "no" or "off" or "") { value = false; return true; }
                value = false;
                return false;
            }
            default:
                value = false;
                return false;
        }
    }

    public bool TryGetDate(out DateTime value)
    {
        switch (Type)
        {
            case DataType.DateTime:
                value = Date;
                return true;
            case DataType.Text or DataType.Path:
                return DateTime.TryParse(Text, CultureInfo.CurrentCulture, DateTimeStyles.None, out value)
                       || DateTime.TryParse(Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
            default:
                value = default;
                return false;
        }
    }

    /// <summary>List view: a list returns its items, a single value a one-element list.</summary>
    public IReadOnlyList<RunValue> AsList() => Type switch
    {
        DataType.List => Items ?? [],
        DataType.Null => [],
        _ => [this],
    };

    /// <summary>True when the value can be consumed as <paramref name="target"/> without loss.</summary>
    public bool CanCoerceTo(DataType target) => target switch
    {
        DataType.Any => true,
        DataType.Null => IsNull,
        _ => Type == target || (Type is DataType.Text or DataType.Path && target is DataType.Text or DataType.Path)
             || (target == DataType.Number && TryGetNumber(out _))
             || (target == DataType.Boolean && TryGetBoolean(out _))
             || (target == DataType.DateTime && TryGetDate(out _)),
    };

    /// <summary>Convert to <paramref name="target"/>; throws <see cref="FormatException"/> when impossible.</summary>
    public RunValue CoerceTo(DataType target)
    {
        if (target is DataType.Any) return this;
        if (Type == target) return this;

        switch (target)
        {
            case DataType.Text:
                return FromText(ToText());
            case DataType.Path:
                return FromPath(ToText());
            case DataType.Number when TryGetNumber(out var number):
                return FromNumber(number);
            case DataType.Boolean when TryGetBoolean(out var flag):
                return FromBoolean(flag);
            case DataType.DateTime when TryGetDate(out var date):
                return FromDate(date);
            case DataType.List:
                return FromList(AsList());
            case DataType.Null when IsNull:
                return Null;
        }

        throw new FormatException($"Cannot convert {Type} to {target}");
    }

    public override string ToString() => ToText();

    private static string FormatNumber(double number) =>
        number.ToString("0.############", CultureInfo.InvariantCulture);

    private static string Truncate(string text, int max)
    {
        var single = text.Replace("\r", string.Empty).Replace('\n', ' ');
        return single.Length <= max ? single : single[..(max - 1)] + "…";
    }

    internal static string SerializeJson(object payload) => JsonSerializer.Serialize(payload, JsonOptions);
}
