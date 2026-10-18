using Shortcuts.Models;

namespace Shortcuts.Services.Executors;

/// <summary>
/// Send a key combination and/or unicode text to the foreground window via SendInput.
/// Combinations are comma/semicolon separated ("Win+R", "Ctrl+V"). Because a Win-key
/// combination can reach the shell, those require an explicit confirmation first.
/// </summary>
internal sealed class SendKeysExecutor : IActionExecutor
{
    private const ushort VkControl = 0x11;
    private const ushort VkMenu = 0x12;
    private const ushort VkShift = 0x10;
    private const ushort VkLWin = 0x5B;

    private const int MaxTextLength = 2000;

    public async Task<string?> ExecuteAsync(ShortcutAction action, RunContext context)
    {
        var keys = context.Resolve(action, "keys").Trim();
        var text = context.Resolve(action, "text");

        if (keys.Length == 0 && text.Length == 0)
        {
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_EmptyParam",
                Loc.Get("Shortcuts_Param_keys") + " / " + Loc.Get("Shortcuts_Param_sendText")));
        }

        if (text.Length > MaxTextLength)
            throw new ActionFailedException(Loc.Format("Shortcuts_Error_TooLong", MaxTextLength));

        var combos = new List<HotkeyGesture>();
        if (keys.Length > 0)
        {
            foreach (var part in keys.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!HotkeyGesture.TryParse(part, requireModifier: false, out var gesture, out _))
                    throw new ActionFailedException(Loc.Format("Shortcuts_Error_InvalidKeys", part));
                combos.Add(gesture);
            }
        }

        // Win+... can invoke shell verbs (Win+R, Win+X); never do that silently.
        if (combos.Any(c => c.Modifiers.HasFlag(HotkeyModifiers.Win)))
        {
            if (!await context.ConfirmAsync(Loc.Get("Shortcuts_Confirm_SendKeys")).ConfigureAwait(true))
                throw new ActionFailedException(Loc.Get("Shortcuts_Error_ConfirmReject"));
        }

        try
        {
            foreach (var combo in combos)
            {
                SendCombo(combo);
                await Task.Delay(20, context.Token).ConfigureAwait(true);
            }

            if (text.Length > 0)
            {
                SendText(text);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ActionFailedException(Loc.Get("Shortcuts_Error_SendKeys") + " - " + ex.Message);
        }

        return Loc.Get("Shortcuts_Output_KeysSent");
    }

    private static void SendCombo(HotkeyGesture gesture)
    {
        var strokes = new List<KeyStroke>(8);

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) strokes.Add(KeyStroke.KeyDown(VkControl));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) strokes.Add(KeyStroke.KeyDown(VkMenu));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) strokes.Add(KeyStroke.KeyDown(VkShift));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) strokes.Add(KeyStroke.KeyDown(VkLWin));

        var vk = (ushort)gesture.VirtualKey;
        strokes.Add(KeyStroke.KeyDown(vk));
        strokes.Add(KeyStroke.KeyUp(vk));

        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Win)) strokes.Add(KeyStroke.KeyUp(VkLWin));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) strokes.Add(KeyStroke.KeyUp(VkShift));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) strokes.Add(KeyStroke.KeyUp(VkMenu));
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) strokes.Add(KeyStroke.KeyUp(VkControl));

        KeyboardInput.Send(strokes);
    }

    private static void SendText(string text)
    {
        var strokes = new List<KeyStroke>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;

            if (ch is '\n' or '\t')
            {
                var vk = (ushort)(ch == '\n' ? 0x0D : 0x09);
                strokes.Add(KeyStroke.KeyDown(vk));
                strokes.Add(KeyStroke.KeyUp(vk));
                continue;
            }

            // Surrogate pairs: each UTF-16 code unit is sent as its own unicode key event.
            strokes.Add(KeyStroke.UnicodeDown(ch));
            strokes.Add(KeyStroke.UnicodeUp(ch));
        }

        KeyboardInput.Send(strokes);
    }
}
