using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskCanvas.Services;

/// <summary>
/// A parsed, normalized global hotkey: zero or more modifiers plus exactly one non-modifier key.
/// <para>
/// The parser is deliberately strict — a single key without modifiers and <c>F12</c> are rejected,
/// because either would swallow ordinary typing or the debugger's reserved key for the whole
/// desktop. The numeric modifiers / virtual-key codes match <c>RegisterHotKey</c>, but the type is
/// pure so the whole rule set can be verified without a live desktop
/// (see <c>tests/SidebarHotKeyChecks</c>).
/// </para>
/// </summary>
public sealed record SidebarHotKey(bool Control, bool Alt, bool Shift, bool Win, string Key)
{
    /// <summary>Win32 <c>MOD_ALT</c>.</summary>
    public const uint ModAlt = 0x0001;
    /// <summary>Win32 <c>MOD_CONTROL</c>.</summary>
    public const uint ModControl = 0x0002;
    /// <summary>Win32 <c>MOD_SHIFT</c>.</summary>
    public const uint ModShift = 0x0004;
    /// <summary>Win32 <c>MOD_WIN</c>.</summary>
    public const uint ModWin = 0x0008;
    /// <summary>Win32 <c>MOD_NOREPEAT</c> — a held-down hotkey fires once.</summary>
    public const uint ModNoRepeat = 0x4000;

    /// <summary>The default hotkey (Ctrl+Alt+Space).</summary>
    public static SidebarHotKey Default { get; } = new(true, true, false, false, "Space");

    /// <summary>Win32 modifier flags (without <see cref="ModNoRepeat"/>).</summary>
    public uint Modifiers =>
        (Control ? ModControl : 0) | (Alt ? ModAlt : 0) | (Shift ? ModShift : 0) | (Win ? ModWin : 0);

    /// <summary>True when at least one modifier is present.</summary>
    public bool HasModifier => Control || Alt || Shift || Win;

    /// <summary>True when nothing below the key itself is bound (a bare key).</summary>
    public bool IsBareKey => !HasModifier;

    /// <summary>Canonical, order-stable string form (<c>"Ctrl+Alt+Space"</c>) used for persistence and display.</summary>
    public string Normalized
    {
        get
        {
            var parts = new List<string>(4);
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(Key);
            return string.Join("+", parts);
        }
    }

    /// <summary>Parse and normalize a hotkey string.</summary>
    /// <param name="text">User input such as <c>"Ctrl+Alt+Space"</c> (case-insensitive, separators <c>+</c>).</param>
    /// <param name="hotKey">The parsed hotkey when valid.</param>
    /// <returns><c>true</c> when the combination is acceptable.</returns>
    public static bool TryParse(string? text, out SidebarHotKey? hotKey)
    {
        hotKey = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var control = false;
        var alt = false;
        var shift = false;
        var win = false;
        string? key = null;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": control = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win" or "windows" or "meta" or "super": win = true; continue;
            }

            // A second non-modifier key makes the whole combination invalid.
            if (key != null) return false;
            if (!TryResolveKey(token, out key)) return false;
        }

        if (key == null) return false;

        var candidate = new SidebarHotKey(control, alt, shift, win, key);
        if (!IsAcceptable(candidate)) return false;

        hotKey = candidate;
        return true;
    }

    /// <summary>Parse, falling back to <paramref name="fallback"/> when the input is invalid.</summary>
    public static SidebarHotKey ParseOrDefault(string? text, SidebarHotKey? fallback = null)
    {
        if (TryParse(text, out var parsed) && parsed != null) return parsed;
        return fallback ?? Default;
    }

    /// <summary>Round-trip check used when loading persisted settings.</summary>
    public static bool IsAcceptable(SidebarHotKey hotKey) => !hotKey.IsBareKey && hotKey.Key != "F12";

    /// <summary>Win32 virtual-key code for the bound key.</summary>
    public static bool TryVirtualKey(string keyName, out uint virtualKey)
    {
        virtualKey = 0;
        if (KeyCodes.TryGetValue(keyName, out var code))
        {
            virtualKey = code;
            return true;
        }

        if (keyName.Length == 1)
        {
            var c = char.ToUpperInvariant(keyName[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                virtualKey = c;
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveKey(string token, out string canonical)
    {
        foreach (var pair in CanonicalKeys)
        {
            if (string.Equals(pair.Alias, token, StringComparison.OrdinalIgnoreCase))
            {
                canonical = pair.Canonical;
                return true;
            }
        }

        // Single letter / digit.
        if (token.Length == 1 && char.IsLetterOrDigit(token[0]))
        {
            canonical = token.ToUpperInvariant();
            return true;
        }

        canonical = string.Empty;
        return false;
    }

    private static readonly (string Alias, string Canonical)[] CanonicalKeys =
    [
        ("Space", "Space"), ("Spacebar", "Space"),
        ("Tab", "Tab"), ("Enter", "Enter"), ("Return", "Enter"),
        ("Esc", "Esc"), ("Escape", "Esc"),
        ("Backspace", "Backspace"), ("Insert", "Insert"), ("Ins", "Insert"),
        ("Delete", "Delete"), ("Del", "Delete"),
        ("Home", "Home"), ("End", "End"),
        ("PageUp", "PageUp"), ("PgUp", "PageUp"),
        ("PageDown", "PageDown"), ("PgDn", "PageDown"),
        ("Up", "Up"), ("Down", "Down"), ("Left", "Left"), ("Right", "Right"),
        ("F1", "F1"), ("F2", "F2"), ("F3", "F3"), ("F4", "F4"), ("F5", "F5"), ("F6", "F6"),
        ("F7", "F7"), ("F8", "F8"), ("F9", "F9"), ("F10", "F10"), ("F11", "F11"),
        // F12 parses (so the user gets the specific rule below, not "unsupported key") but is
        // always rejected by IsAcceptable — it is reserved for the debugger.
        ("F12", "F12"),
    ];

    private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Esc"] = 0x1B,
        ["Backspace"] = 0x08, ["Insert"] = 0x2D, ["Delete"] = 0x2E,
        ["Home"] = 0x24, ["End"] = 0x23, ["PageUp"] = 0x21, ["PageDown"] = 0x22,
        ["Left"] = 0x25, ["Up"] = 0x26, ["Right"] = 0x27, ["Down"] = 0x28,
        ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74, ["F6"] = 0x75,
        ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79, ["F11"] = 0x7A, ["F12"] = 0x7B
    };
}
