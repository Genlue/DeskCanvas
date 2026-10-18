using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// Send one key combination to the foreground window. Modifiers are pressed in order, the
/// main key next, then everything is released in reverse; a finally block always releases the
/// keys even when SendInput or the hold delay fails, so the user's keyboard cannot stick.
/// </summary>
internal sealed class SendHotkeyExecutor : IActionExecutor
{
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkLWin = 0x5B;

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var raw = context.ResolveOrDefault(action, "hotkey").Trim();
        if (raw.Length == 0)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam",
                Loc.Get("Shortcuts_Param_hotkey")));

        if (!HotkeyGesture.TryParse(raw, requireModifier: false, out var gesture, out _))
            throw new ActionFailedException(Loc.Get("Shortcuts_Hotkey_Invalid"));

        // Never inject keys into the app while the user is recording a binding; the capture
        // control is listening for that same input.
        if (ShortcutHotkeyService.IsCapturing)
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_CaptureActive"));

        var holdMs = Math.Clamp(context.ResolveNumber(action, "holdMs", 40), 10, 500);

        var downs = BuildDownStrokes(gesture);
        var ups = BuildUpStrokes(gesture);

        try
        {
            KeyboardInput.Send(downs);
            if (holdMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(holdMs), context.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_SendKeys") + " - " + ex.Message);
        }
        finally
        {
            // Release even when a press/delay failed halfway. Sending a key-up for a key that
            // was never pressed is harmless; leaving a key down is not. If the batch itself
            // fails, fall back to one key-up at a time.
            try
            {
                KeyboardInput.Send(ups);
            }
            catch
            {
                foreach (var up in ups)
                {
                    try { KeyboardInput.Send([up]); } catch { }
                }
            }
        }

        return Loc.Get("Shortcuts_Output_HotkeySent");
    }

    private static List<KeyStroke> BuildDownStrokes(HotkeyGesture gesture)
    {
        var strokes = new List<KeyStroke>(5);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) strokes.Add(KeyStroke.KeyDown(VkControl));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) strokes.Add(KeyStroke.KeyDown(VkMenu));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) strokes.Add(KeyStroke.KeyDown(VkShift));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) strokes.Add(KeyStroke.KeyDown(VkLWin));
        strokes.Add(KeyStroke.KeyDown((ushort)gesture.VirtualKey));
        return strokes;
    }

    private static List<KeyStroke> BuildUpStrokes(HotkeyGesture gesture)
    {
        var strokes = new List<KeyStroke>(5);
        strokes.Add(KeyStroke.KeyUp((ushort)gesture.VirtualKey));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) strokes.Add(KeyStroke.KeyUp(VkLWin));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) strokes.Add(KeyStroke.KeyUp(VkShift));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) strokes.Add(KeyStroke.KeyUp(VkMenu));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) strokes.Add(KeyStroke.KeyUp(VkControl));
        return strokes;
    }
}
