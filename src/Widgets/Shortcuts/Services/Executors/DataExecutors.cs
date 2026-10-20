using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Text constant node.</summary>
internal sealed class TextConstantExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var text = context.Text(action, "value");
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = RunValue.FromText(text) }, text));
    }
}

/// <summary>Number constant node.</summary>
internal sealed class NumberConstantExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var number = context.Number(action, "value");
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = RunValue.FromNumber(number) },
            number.ToString("0.############", CultureInfo.InvariantCulture)));
    }
}

/// <summary>Boolean constant node.</summary>
internal sealed class BooleanConstantExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var flag = context.Bool(action, "value");
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = RunValue.FromBoolean(flag) },
            flag ? "true" : "false"));
    }
}

/// <summary>Read a named variable; an unset name is an error, not an empty string.</summary>
internal sealed class GetVarExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var name = context.Text(action, "name").Trim();
        if (context.GetVariable(name) is not { } value)
        {
            return Task.FromResult(ActionResult.Fail("VariableMissing",
                Loc.Format("Shortcuts_Error_VariableMissing", name), "name"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = value }, value.DisplayText));
    }
}

internal static class Separators
{
    public static string Resolve(RunContext context, ShortcutAction action) =>
        context.Text(action, "separator") switch
        {
            "comma" => ",",
            "space" => " ",
            "custom" => context.Text(action, "custom"),
            _ => Environment.NewLine,
        };
}

/// <summary>Split text into a list with explicit trim / keep-empty options.</summary>
internal sealed class SplitTextExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var text = context.Text(action, "text");
        var separator = Separators.Resolve(context, action);
        var trim = context.Bool(action, "trim");
        var keepEmpty = context.Bool(action, "keepEmpty");

        var parts = separator.Length == 0
            ? [text]
            : text.Split(separator, StringSplitOptions.None);

        var items = new List<RunValue>(parts.Length);
        foreach (var part in parts)
        {
            var value = trim ? part.Trim() : part;
            if (!keepEmpty && value.Length == 0) continue;
            items.Add(RunValue.FromText(value));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["list"] = RunValue.FromList(items) },
            Loc.Format("Shortcuts_Output_ListCount", items.Count)));
    }
}

/// <summary>Join a list into text.</summary>
internal sealed class JoinTextExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var items = context.List(action, "list");
        var separator = Separators.Resolve(context, action);
        var text = string.Join(separator, items.Select(i => i.ToText()));

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(text) }, text));
    }
}

/// <summary>Build a list from up to four values; unset slots are skipped.</summary>
internal sealed class CreateListExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var items = new List<RunValue>();
        foreach (var key in new[] { "a", "b", "c", "d" })
        {
            var node = context.Graph.Find(action.Id);
            if (node == null) continue;
            if (!node.Params.ContainsKey(key) && context.Graph.IncomingData(node.Id, key) == null) continue;

            var value = context.Optional(node, key);
            if (value.IsNull) continue;
            items.Add(value);
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["list"] = RunValue.FromList(items) },
            Loc.Format("Shortcuts_Output_ListCount", items.Count)));
    }
}

internal sealed class ListCountExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var count = context.List(action, "list").Count;
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["count"] = RunValue.FromNumber(count) },
            count.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>Element at a zero-based index; out of range is an error.</summary>
internal sealed class ListItemExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var items = context.List(action, "list");
        var index = (int)context.Number(action, "index");

        if (index < 0 || index >= items.Count)
        {
            return Task.FromResult(ActionResult.Fail("IndexOutOfRange",
                Loc.Format("Shortcuts_Error_IndexRange", index, items.Count), "index"));
        }

        var value = items[index];
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = value }, value.DisplayText));
    }
}

internal static class JsonValues
{
    public static RunValue FromElement(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => RunValue.FromText(element.GetString()),
        JsonValueKind.Number => element.TryGetInt64(out var integer)
            ? RunValue.FromNumber(integer)
            : RunValue.FromNumber(element.GetDouble()),
        JsonValueKind.True => RunValue.FromBoolean(true),
        JsonValueKind.False => RunValue.FromBoolean(false),
        JsonValueKind.Null => RunValue.Null,
        JsonValueKind.Array => RunValue.FromList(element.EnumerateArray().Select(FromElement)),
        _ => RunValue.FromJson(element.GetRawText()),
    };
}

/// <summary>Parse JSON text into an object or list value.</summary>
internal sealed class ParseJsonExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var text = context.Text(action, "text");
        try
        {
            using var document = JsonDocument.Parse(text);
            var value = JsonValues.FromElement(document.RootElement);
            if (value.Type is not (DataType.Object or DataType.List))
                value = RunValue.FromJson(document.RootElement.GetRawText());

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["value"] = value }, value.DisplayText));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ActionResult.Fail("InvalidJson",
                Loc.Format("Shortcuts_Error_InvalidJson", ex.Message), "text"));
        }
    }
}

/// <summary>Read a field from a JSON object with a dotted / indexed path (<c>a.b[0].c</c>).</summary>
internal sealed class GetPropertyExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var source = context.Value(action, "value");
        var path = context.Text(action, "path").Trim();

        JsonNode? node;
        try
        {
            var json = source.Type == DataType.Object
                ? source.Json
                : JsonSerializer.Serialize(source.ToText());
            node = JsonNode.Parse(json ?? "null");
        }
        catch (JsonException ex)
        {
            return Task.FromResult(ActionResult.Fail("InvalidJson",
                Loc.Format("Shortcuts_Error_InvalidJson", ex.Message), "value"));
        }

        foreach (var segment in PathSegments(path))
        {
            if (node == null)
                return Task.FromResult(ActionResult.Fail("FieldMissing",
                    Loc.Format("Shortcuts_Error_FieldMissing", path), "path"));

            if (segment.Index is { } index)
            {
                if (node is not JsonArray array || index >= array.Count)
                {
                    return Task.FromResult(ActionResult.Fail("FieldMissing",
                        Loc.Format("Shortcuts_Error_FieldMissing", path), "path"));
                }

                node = array[index];
            }
            else
            {
                if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment.Name!, out var child))
                {
                    return Task.FromResult(ActionResult.Fail("FieldMissing",
                        Loc.Format("Shortcuts_Error_FieldMissing", path), "path"));
                }

                node = child;
            }
        }

        if (node == null)
        {
            // JSON null is a value, not a missing field: report it as such.
            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["value"] = RunValue.Null }, "(null)"));
        }

        var result = node.GetValueKind() switch
        {
            JsonValueKind.String => RunValue.FromText(node.GetValue<string>()),
            JsonValueKind.Number => node.AsValue().TryGetValue<long>(out var integer)
                ? RunValue.FromNumber(integer)
                : RunValue.FromNumber(node.GetValue<double>()),
            JsonValueKind.True => RunValue.FromBoolean(true),
            JsonValueKind.False => RunValue.FromBoolean(false),
            JsonValueKind.Array => RunValue.FromList(node.AsArray()
                .Select(item => RunValue.FromText(item?.ToJsonString() ?? string.Empty))),
            _ => RunValue.FromJson(node.ToJsonString()),
        };

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["value"] = result }, result.DisplayText));
    }

    private static IEnumerable<(string? Name, int? Index)> PathSegments(string path)
    {
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var name = raw;
            while (true)
            {
                var open = name.IndexOf('[');
                if (open < 0)
                {
                    if (name.Length > 0) yield return (name, null);
                    break;
                }

                if (open > 0) yield return (name[..open], null);

                var close = name.IndexOf(']', open);
                if (close < 0) break;

                if (int.TryParse(name[(open + 1)..close], out var index)) yield return (null, index);
                name = name[(close + 1)..];
            }
        }
    }
}

/// <summary>Serialize any value to JSON text.</summary>
internal sealed class ToJsonExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var value = context.Value(action, "value");

        var text = value.Type switch
        {
            DataType.Object => value.Json ?? "null",
            DataType.List => JsonSerializer.Serialize(value.AsList().Select(ToNode)),
            DataType.Number => value.Number.ToString("0.############", CultureInfo.InvariantCulture),
            DataType.Boolean => value.Boolean ? "true" : "false",
            DataType.Null => "null",
            _ => JsonSerializer.Serialize(value.ToText()),
        };

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(text) }, text));
    }

    private static object? ToNode(RunValue value) => value.Type switch
    {
        DataType.Number => value.Number,
        DataType.Boolean => value.Boolean,
        DataType.Null => null,
        DataType.Object => JsonNode.Parse(value.Json ?? "null"),
        DataType.List => value.AsList().Select(ToNode).ToList(),
        _ => value.ToText(),
    };
}

/// <summary>Ask the user for one value; cancelling stops the run.</summary>
internal sealed class AskInputExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title");
        var prompt = context.Text(action, "prompt");
        var defaultText = context.Text(action, "default");
        var type = context.Text(action, "type") switch
        {
            "number" => DataType.Number,
            "boolean" => DataType.Boolean,
            _ => DataType.Text,
        };

        RunValue? defaultValue = defaultText.Length == 0
            ? null
            : RunValue.FromText(defaultText);

        try
        {
            var answer = await context.Services.Prompts
                .AskAsync(title, prompt.Length == 0 ? null : prompt, defaultValue, type, context.Token)
                .ConfigureAwait(true);

            if (answer == null)
                return ActionResult.Fail("InputCancelled", Loc.Get("Shortcuts_Error_InputCancelled"));

            return ActionResult.With(
                new Dictionary<string, RunValue> { ["value"] = answer }, answer.DisplayText);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("InputFailed", ex.Message);
        }
    }
}

/// <summary>Show a value in a copyable view.</summary>
internal sealed class ShowResultExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var value = context.Value(action, "value");
        var title = context.Text(action, "title");
        if (title.Length == 0) title = Loc.Get("Shortcuts_Action_showResult");

        try
        {
            await context.Services.Prompts.ShowResultAsync(title, value).ConfigureAwait(true);
            return ActionResult.Ok(value.DisplayText);
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("ShowFailed", ex.Message);
        }
    }
}
