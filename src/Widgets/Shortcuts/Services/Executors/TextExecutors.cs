using System.Globalization;
using System.Text.Json;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Read clipboard text; also reports whether there was any.</summary>
internal sealed class GetClipboardExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        try
        {
            var text = await context.Services.System.GetClipboardTextAsync().ConfigureAwait(true) ?? string.Empty;
            return ActionResult.With(new Dictionary<string, RunValue>
            {
                ["text"] = RunValue.FromText(text),
                ["hasText"] = RunValue.FromBoolean(text.Length > 0),
            }, text.Length == 0 ? Loc.Get("Shortcuts_Output_ClipEmpty") : text);
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("ClipboardReadFailed", ex.Message);
        }
    }
}

/// <summary>Write clipboard text; an empty string clears the clipboard.</summary>
internal sealed class SetClipboardExecutor : IActionExecutor
{
    public async Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var text = context.Text(action, "text");

        try
        {
            await context.Services.System.SetClipboardTextAsync(text).ConfigureAwait(true);
            return ActionResult.With(new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(text) },
                Loc.Format("Shortcuts_Output_Copied", text.Length));
        }
        catch (Exception ex)
        {
            return ActionResult.Fail("ClipboardWriteFailed", ex.Message, "text");
        }
    }
}

/// <summary>Upper / lower / trim / replace; find &amp; replace are required only for replace.</summary>
internal sealed class TextTransformExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var input = context.Text(action, "input");
        var operation = context.Text(action, "operation").Trim();

        string output;
        switch (operation)
        {
            case "upper":
                output = input.ToUpperInvariant();
                break;

            case "lower":
                output = input.ToLowerInvariant();
                break;

            case "trim":
                output = input.Trim();
                break;

            case "replace":
            {
                var find = context.Text(action, "find");
                if (find.Length == 0)
                {
                    return Task.FromResult(ActionResult.Fail("EmptyParam",
                        Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_find")), "find"));
                }

                var replacement = context.Text(action, "replace_with");
                var comparison = context.Bool(action, "ignoreCase")
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;

                output = input.Replace(find, replacement, comparison);
                break;
            }

            default:
                return Task.FromResult(ActionResult.Fail("UnknownOperator",
                    Loc.Format("Shortcuts_Error_UnknownOperator", operation), "operation"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(output) },
            output));
    }
}

/// <summary>Current date/time as a DateTime value — formatting belongs to <c>formatDate</c>.</summary>
internal sealed class CurrentDateExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var now = DateTime.Now;
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["date"] = RunValue.FromDate(now) },
            now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
    }
}

/// <summary>Format a date with an explicit pattern.</summary>
internal sealed class FormatDateExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var date = context.Date(action, "date");
        var format = context.Text(action, "format");

        string text;
        try
        {
            text = date.ToString(format, CultureInfo.CurrentCulture);
        }
        catch (FormatException ex)
        {
            return Task.FromResult(ActionResult.Fail("InvalidFormat", ex.Message, "format"));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["text"] = RunValue.FromText(text) }, text));
    }
}

internal static class DateUnits
{
    public static DateTime Add(DateTime value, double amount, string unit) => unit switch
    {
        "seconds" => value.AddSeconds(amount),
        "minutes" => value.AddMinutes(amount),
        "hours" => value.AddHours(amount),
        _ => value.AddDays(amount),
    };

    public static double Diff(DateTime from, DateTime to, string unit) => unit switch
    {
        "seconds" => (to - from).TotalSeconds,
        "minutes" => (to - from).TotalMinutes,
        "hours" => (to - from).TotalHours,
        _ => (to - from).TotalDays,
    };
}

/// <summary>Shift a date by an amount of a named unit.</summary>
internal sealed class DateAddExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var date = context.Date(action, "date");
        var amount = context.Number(action, "amount");
        var unit = context.Text(action, "unit");

        var shifted = DateUnits.Add(date, amount, unit);
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["date"] = RunValue.FromDate(shifted) },
            shifted.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture)));
    }
}

/// <summary>Difference between two dates in a named unit.</summary>
internal sealed class DateDiffExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var from = context.Date(action, "from");
        var to = context.Date(action, "to");
        var unit = context.Text(action, "unit");

        var amount = DateUnits.Diff(from, to, unit);
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["amount"] = RunValue.FromNumber(amount) },
            amount.ToString("0.###", CultureInfo.InvariantCulture)));
    }
}

/// <summary>Random integer in an inclusive range; an inverted range is an error.</summary>
internal sealed class RandomNumberExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var min = (long)Math.Round(context.Number(action, "min"));
        var max = (long)Math.Round(context.Number(action, "max"));

        if (min > max)
        {
            return Task.FromResult(ActionResult.Fail("RangeInvalid",
                Loc.Format("Shortcuts_Error_RangeInvalid", min, max), "min"));
        }

        var value = min == max ? min : Random.Shared.NextInt64(min, max + 1);
        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["number"] = RunValue.FromNumber(value) },
            value.ToString(CultureInfo.InvariantCulture)));
    }
}

/// <summary>Four-function arithmetic on two numbers.</summary>
internal sealed class CalculateExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var a = context.Number(action, "a");
        var b = context.Number(action, "b");
        var op = context.Text(action, "op");

        double result;
        switch (op)
        {
            case "add":
                result = a + b;
                break;
            case "subtract":
                result = a - b;
                break;
            case "multiply":
                result = a * b;
                break;
            case "divide":
                if (Math.Abs(b) < double.Epsilon)
                {
                    return Task.FromResult(ActionResult.Fail("DivideByZero",
                        Loc.Get("Shortcuts_Error_DivideByZero"), "b"));
                }

                result = a / b;
                break;
            case "modulo":
                if (Math.Abs(b) < double.Epsilon)
                {
                    return Task.FromResult(ActionResult.Fail("DivideByZero",
                        Loc.Get("Shortcuts_Error_DivideByZero"), "b"));
                }

                result = a % b;
                break;
            default:
                return Task.FromResult(ActionResult.Fail("UnknownOperator",
                    Loc.Format("Shortcuts_Error_UnknownOperator", op), "op"));
        }

        if (!double.IsFinite(result))
        {
            return Task.FromResult(ActionResult.Fail("NumberInvalid", Loc.Get("Shortcuts_Error_NumberOverflow")));
        }

        return Task.FromResult(ActionResult.With(
            new Dictionary<string, RunValue> { ["number"] = RunValue.FromNumber(result) },
            result.ToString("0.############", CultureInfo.InvariantCulture)));
    }
}

/// <summary>Explicit type conversion; a failed conversion is an error, not a silent string.</summary>
internal sealed class ConvertValueExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var value = context.Value(action, "value");
        var target = context.Text(action, "target");

        var type = target switch
        {
            "number" => DataType.Number,
            "boolean" => DataType.Boolean,
            "datetime" => DataType.DateTime,
            "list" => DataType.List,
            "json" => DataType.Object,
            _ => DataType.Text,
        };

        try
        {
            var converted = type == DataType.Object
                ? RunValue.FromJson(value.Type == DataType.Object ? value.Json : JsonSerializer.Serialize(value.ToText()))
                : value.CoerceTo(type);

            return Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["value"] = converted },
                converted.DisplayText));
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            return Task.FromResult(ActionResult.Fail("ConvertFailed",
                Loc.Format("Shortcuts_Error_Convert", value.DisplayText,
                    Loc.Get(ActionCatalog.TypeLabelKey(type))), "value"));
        }
    }
}
