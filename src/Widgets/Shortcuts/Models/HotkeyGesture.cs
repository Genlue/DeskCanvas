using System.Globalization;
using Avalonia.Input;

namespace Shortcuts.Models;

/// <summary>Modifier flags; the values intentionally match Win32 MOD_* so they can be passed through.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>Why a hotkey text could not be turned into a gesture.</summary>
public enum HotkeyParseError
{
    None,
    Empty,
    MissingKey,
    UnknownKey,
    ModifierRequired,
    TooLong,
}

/// <summary>
/// One global hotkey, stored as canonical text ("Ctrl+Alt+1") plus the pieces Win32
/// RegisterHotKey needs. Parsing is culture-invariant and never throws.
/// </summary>
public readonly record struct HotkeyGesture(
    HotkeyModifiers Modifiers,
    uint VirtualKey,
    string KeyToken)
{
    private const int MaxTextLength = 64;

    /// <summary>Canonical modifier order: Ctrl, Alt, Shift, Win (the order shown in the UI).</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyToken);
            return string.Join("+", parts);
        }
    }

    /// <summary>True for F1–F24: safe to use without a modifier.</summary>
    public bool IsFunctionKey =>
        VirtualKey is >= 0x70 and <= 0x87;

    public override string ToString() => Text;

    // ---- key table ---------------------------------------------------------------

    /// <summary>Token → virtual-key code. Tokens are matched case-insensitively.</summary>
    private static readonly Dictionary<string, (uint Vk, string Canonical)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = (0x20, "Space"),
            ["Tab"] = (0x09, "Tab"),
            ["Enter"] = (0x0D, "Enter"),
            ["Return"] = (0x0D, "Enter"),
            ["Backspace"] = (0x08, "Backspace"),
            ["Back"] = (0x08, "Backspace"),
            ["Delete"] = (0x2E, "Delete"),
            ["Del"] = (0x2E, "Delete"),
            ["Insert"] = (0x2D, "Insert"),
            ["Ins"] = (0x2D, "Insert"),
            ["Home"] = (0x24, "Home"),
            ["End"] = (0x23, "End"),
            ["PageUp"] = (0x21, "PageUp"),
            ["PgUp"] = (0x21, "PageUp"),
            ["PageDown"] = (0x22, "PageDown"),
            ["PgDn"] = (0x22, "PageDown"),
            ["Left"] = (0x25, "Left"),
            ["Up"] = (0x26, "Up"),
            ["Right"] = (0x27, "Right"),
            ["Down"] = (0x28, "Down"),
            ["Escape"] = (0x1B, "Escape"),
            ["Esc"] = (0x1B, "Escape"),
            ["CapsLock"] = (0x14, "CapsLock"),
            ["PrintScreen"] = (0x2C, "PrintScreen"),
            ["Pause"] = (0x13, "Pause"),
            ["NumLock"] = (0x90, "NumLock"),
            ["ScrollLock"] = (0x91, "ScrollLock"),
            ["Multiply"] = (0x6A, "Multiply"),
            ["Add"] = (0x6B, "Add"),
            ["Subtract"] = (0x6D, "Subtract"),
            ["Decimal"] = (0x6E, "Decimal"),
            ["Divide"] = (0x6F, "Divide"),
            // Common punctuation. The plus key is spelled "Plus" because "+" is the combo
            // separator; all other symbols are accepted both by name and literally.
            ["Minus"] = (0xBD, "-"),
            ["-"] = (0xBD, "-"),
            ["Plus"] = (0xBB, "Plus"),
            ["="] = (0xBB, "Plus"),
            ["Comma"] = (0xBC, ","),
            [","] = (0xBC, ","),
            ["Period"] = (0xBE, "."),
            ["."] = (0xBE, "."),
            ["Slash"] = (0xBF, "/"),
            ["/"] = (0xBF, "/"),
            ["Semicolon"] = (0xBA, ";"),
            [";"] = (0xBA, ";"),
            ["Quote"] = (0xDE, "'"),
            ["'"] = (0xDE, "'"),
            ["LeftBracket"] = (0xDB, "["),
            ["["] = (0xDB, "["),
            ["RightBracket"] = (0xDD, "]"),
            ["]"] = (0xDD, "]"),
            ["Backslash"] = (0xDC, "\\"),
            ["\\"] = (0xDC, "\\"),
            ["Backtick"] = (0xC0, "`"),
            ["`"] = (0xC0, "`"),
        };

    static HotkeyGesture()
    {
        for (var c = 'A'; c <= 'Z'; c++)
            Keys[c.ToString()] = ((uint)c, c.ToString());

        for (var digit = 0; digit <= 9; digit++)
        {
            var token = digit.ToString(CultureInfo.InvariantCulture);
            Keys[token] = ((uint)('0' + digit), token);
            Keys["NumPad" + token] = ((uint)(0x60 + digit), "NumPad" + token);
        }

        for (var i = 1; i <= 24; i++)
            Keys["F" + i.ToString(CultureInfo.InvariantCulture)] = ((uint)(0x6F + i), "F" + i);
    }

    // ---- parsing -----------------------------------------------------------------

    /// <summary>
    /// Parse canonical/loose text. When <paramref name="requireModifier"/> is true a bare
    /// letter/digit is rejected (F1–F24 may still stand alone).
    /// </summary>
    public static bool TryParse(
        string? text,
        bool requireModifier,
        out HotkeyGesture gesture,
        out HotkeyParseError error)
    {
        gesture = default;
        error = HotkeyParseError.None;

        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            error = HotkeyParseError.Empty;
            return false;
        }

        if (trimmed.Length > MaxTextLength)
        {
            error = HotkeyParseError.TooLong;
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? keyToken = null;

        foreach (var rawPart in trimmed.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.Trim();
            if (part.Length == 0) continue;

            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= HotkeyModifiers.Control;
                    continue;
                case "ALT":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;
                case "SHIFT":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;
                case "WIN":
                case "WINDOWS":
                case "META":
                case "SUPER":
                    modifiers |= HotkeyModifiers.Win;
                    continue;
            }

            // A second non-modifier token means the text is not a single hotkey.
            if (keyToken != null)
            {
                error = HotkeyParseError.UnknownKey;
                return false;
            }

            keyToken = part;
        }

        if (keyToken == null)
        {
            error = HotkeyParseError.MissingKey;
            return false;
        }

        if (!Keys.TryGetValue(keyToken, out var entry))
        {
            error = HotkeyParseError.UnknownKey;
            return false;
        }

        var isFunctionKey = entry.Vk is >= 0x70 and <= 0x87;
        if (requireModifier && modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            error = HotkeyParseError.ModifierRequired;
            return false;
        }

        gesture = new HotkeyGesture(modifiers, entry.Vk, entry.Canonical);
        return true;
    }

    /// <summary>
    /// Build a gesture from a recorded Avalonia key event. Pure modifier keys return
    /// <see cref="HotkeyParseError.MissingKey"/> so the recorder can keep waiting.
    /// </summary>
    public static bool TryFromKey(
        Key key,
        KeyModifiers keyModifiers,
        bool requireModifier,
        out HotkeyGesture gesture,
        out HotkeyParseError error)
    {
        gesture = default;
        error = HotkeyParseError.None;

        if (!TryMapKey(key, out var token))
        {
            error = IsModifierKey(key) ? HotkeyParseError.MissingKey : HotkeyParseError.UnknownKey;
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        if (keyModifiers.HasFlag(KeyModifiers.Control)) modifiers |= HotkeyModifiers.Control;
        if (keyModifiers.HasFlag(KeyModifiers.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (keyModifiers.HasFlag(KeyModifiers.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (keyModifiers.HasFlag(KeyModifiers.Meta)) modifiers |= HotkeyModifiers.Win;

        if (!Keys.TryGetValue(token, out var entry))
        {
            error = HotkeyParseError.UnknownKey;
            return false;
        }

        var isFunctionKey = entry.Vk is >= 0x70 and <= 0x87;
        if (requireModifier && modifiers == HotkeyModifiers.None && !isFunctionKey)
        {
            error = HotkeyParseError.ModifierRequired;
            return false;
        }

        gesture = new HotkeyGesture(modifiers, entry.Vk, entry.Canonical);
        return true;
    }

    /// <summary>Map an Avalonia key to a token our table understands (recording only).</summary>
    public static bool TryMapKey(Key key, out string token)
    {
        token = string.Empty;

        if (key is >= Key.A and <= Key.Z)
        {
            token = ((char)('A' + (key - Key.A))).ToString();
            return true;
        }

        if (key is >= Key.D0 and <= Key.D9)
        {
            token = ((char)('0' + (key - Key.D0))).ToString();
            return true;
        }

        if (key is >= Key.F1 and <= Key.F24)
        {
            token = "F" + (key - Key.F1 + 1).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            token = "NumPad" + (key - Key.NumPad0).ToString(CultureInfo.InvariantCulture);
            return true;
        }

        if (key == Key.Enter || key == Key.Return)
        {
            token = "Enter";
            return true;
        }

        token = key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Up => "Up",
            Key.Right => "Right",
            Key.Down => "Down",
            Key.Escape => "Escape",
            Key.CapsLock => "CapsLock",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.NumLock => "NumLock",
            Key.Scroll => "ScrollLock",
            Key.OemMinus => "-",
            Key.OemPlus => "Plus",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            _ => string.Empty,
        };

        return token.Length > 0 && Keys.ContainsKey(token);
    }

    /// <summary>Pure modifier keys must not terminate recording.</summary>
    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or
        Key.LWin or Key.RWin;
}
