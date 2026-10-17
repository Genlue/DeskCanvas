using Avalonia.Input.Platform;
using System.Globalization;
using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

internal sealed class GetClipboardExecutor : IActionExecutor
{
    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var topLevel = context.Services.TopLevel;
        if (topLevel?.Clipboard == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        var text = await topLevel.Clipboard.TryGetTextAsync().ConfigureAwait(true);
        return text ?? string.Empty;
    }
}

internal sealed class SetClipboardExecutor : IActionExecutor
{
    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var text = context.Resolve(action, "text");
        if (text.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_text")));

        var topLevel = context.Services.TopLevel;
        if (topLevel?.Clipboard == null)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_NoService"));

        await topLevel.Clipboard.SetTextAsync(text).ConfigureAwait(true);
        return text;
    }
}

internal sealed class TextTransformExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var input = context.Resolve(action, "input");
        var operation = context.ResolveOrDefault(action, "operation");

        var result = operation switch
        {
            "upper" => input.ToUpperInvariant(),
            "lower" => input.ToLowerInvariant(),
            "trim" => input.Trim(),
            "replace" => input.Replace(
                context.Resolve(action, "find"),
                context.Resolve(action, "replace_with"),
                StringComparison.OrdinalIgnoreCase),
            _ => input,
        };

        return Task.FromResult<string?>(result);
    }
}

internal sealed class CurrentDateExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var format = context.ResolveOrDefault(action, "format");
        try
        {
            return Task.FromResult<string?>(DateTime.Now.ToString(format, CultureInfo.CurrentCulture));
        }
        catch
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_InvalidValue"));
        }
    }
}

internal sealed class RandomNumberExecutor : IActionExecutor
{
    public Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var min = (long)Math.Round(context.ResolveNumberClamped(action, "min", 1));
        var max = (long)Math.Round(context.ResolveNumberClamped(action, "max", 100));
        if (min > max) (min, max) = (max, min);

        var value = min == max ? min : Random.Shared.NextInt64(min, max + 1);
        return Task.FromResult<string?>(value.ToString(CultureInfo.InvariantCulture));
    }
}
