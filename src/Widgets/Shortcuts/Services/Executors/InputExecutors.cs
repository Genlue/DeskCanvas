using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>Shared guards for the actions that synthesize keyboard input.</summary>
internal static class InputGuard
{
    /// <summary>
    /// Refuse to type while the editor is recording a hotkey: the capture box would swallow the
    /// strokes and the shortcut would silently do nothing.
    /// </summary>
    public static ActionResult? BlockedByRecording() =>
        KeyboardInput.IsRecording
            ? ActionResult.Fail("RecordingHotkey", Loc.Get("Shortcuts_Error_Recording"))
            : null;
}

/// <summary>Type literal text into whatever window currently has focus.</summary>
internal sealed class TypeTextExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (InputGuard.BlockedByRecording() is { } blocked) return Task.FromResult(blocked);

        var text = context.Text(action, "text");
        if (text.Length == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_sendText")), "text"));
        }

        try
        {
            context.Services.System.TypeText(text);
            return Task.FromResult(ActionResult.Ok(Loc.Format("Shortcuts_Output_Typed", text.Length)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("TypeFailed", ex.Message, "text"));
        }
    }
}

/// <summary>Send a comma-separated sequence of key combinations in order.</summary>
internal sealed class SendKeySequenceExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (InputGuard.BlockedByRecording() is { } blocked) return Task.FromResult(blocked);

        var raw = context.Text(action, "keys");
        var combos = raw
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (combos.Count == 0)
        {
            return Task.FromResult(ActionResult.Fail("EmptyParam",
                Loc.Format("Shortcuts_Error_EmptyParam", Loc.Get("Shortcuts_Param_keys")), "keys"));
        }

        var invalid = combos.FirstOrDefault(c => !HotkeyGesture.TryParse(c, requireModifier: false, out _, out _));
        if (invalid != null)
        {
            return Task.FromResult(ActionResult.Fail("InvalidHotkey",
                Loc.Format("Shortcuts_Error_InvalidHotkey", invalid), "keys"));
        }

        try
        {
            context.Services.System.SendKeySequence(combos);
            return Task.FromResult(ActionResult.Ok(Loc.Format("Shortcuts_Output_KeySequence", combos.Count)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("SendKeysFailed", ex.Message, "keys"));
        }
    }
}

/// <summary>Send one key combination (modifiers held for <c>holdMs</c>).</summary>
internal sealed class SendHotkeyExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        if (InputGuard.BlockedByRecording() is { } blocked) return Task.FromResult(blocked);

        var gesture = context.Text(action, "hotkey").Trim();
        if (!HotkeyGesture.TryParse(gesture, requireModifier: false, out var parsed, out _))
        {
            return Task.FromResult(ActionResult.Fail("InvalidHotkey",
                Loc.Format("Shortcuts_Error_InvalidHotkey", gesture), "hotkey"));
        }

        var hold = (int)context.Number(action, "holdMs");

        try
        {
            // The adapter always releases the keys in a finally block.
            context.Services.System.SendHotkey(gesture, hold);
            return Task.FromResult(ActionResult.Ok(Loc.Format("Shortcuts_Output_Hotkey", parsed.Text)));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("SendHotkeyFailed", ex.Message, "hotkey"));
        }
    }
}

/// <summary>Find a window by title; returns an opaque handle as text.</summary>
internal sealed class FindWindowExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title");
        var match = WindowMatches.Parse(context.Text(action, "match"));
        var handle = context.Services.System.FindWindow(title, match);

        return handle == 0
            ? Task.FromResult(ActionResult.Fail("WindowNotFound",
                Loc.Format("Shortcuts_Error_WindowNotFound", title), "title"))
            : Task.FromResult(ActionResult.With(
                new Dictionary<string, RunValue> { ["window"] = RunValue.FromText(handle.ToString()) },
                Loc.Format("Shortcuts_Output_Window", title)));
    }
}

/// <summary>Bring a window found earlier to the foreground.</summary>
internal sealed class ActivateWindowExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var raw = context.Text(action, "window");
        if (!long.TryParse(raw.Trim(), out var handle) || handle == 0)
        {
            return Task.FromResult(ActionResult.Fail("InvalidWindow",
                Loc.Format("Shortcuts_Error_InvalidWindow", raw), "window"));
        }

        try
        {
            context.Services.System.ActivateWindow(handle);
            return Task.FromResult(ActionResult.Ok(Loc.Get("Shortcuts_Output_Activated")));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ActionResult.Fail("ActivateFailed", ex.Message, "window"));
        }
    }
}

/// <summary>Poll until a window appears, or fail once the timeout expires.</summary>
internal sealed class WaitForWindowExecutor : IActionExecutor
{
    public Task<ActionResult> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var title = context.Text(action, "title");
        var match = WindowMatches.Parse(context.Text(action, "match"));
        var timeout = context.Number(action, "timeout");
        var window = TimeSpan.FromSeconds(Math.Clamp(timeout, 1, 120));

        // Polling sleeps on a worker thread: blocking the UI thread here would freeze the
        // widget that started the run.
        return Task.Run(() =>
        {
            if (!context.Services.System.WaitForWindow(title, match, window, context.Token, out var handle))
            {
                return ActionResult.Fail("WindowTimeout",
                    Loc.Format("Shortcuts_Error_WindowTimeout", title, window.TotalSeconds), "title");
            }

            return ActionResult.With(
                new Dictionary<string, RunValue> { ["window"] = RunValue.FromText(handle.ToString()) },
                Loc.Format("Shortcuts_Output_Window", title));
        }, context.Token);
    }
}

internal static class WindowMatches
{
    public static WindowMatch Parse(string value) => value switch
    {
        "equals" => WindowMatch.Equals,
        "startsWith" => WindowMatch.StartsWith,
        _ => WindowMatch.Contains,
    };
}
