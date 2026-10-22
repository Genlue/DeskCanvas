using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Input;

namespace DeskCanvas.Services;

/// <summary>
/// Global hotkey modifier flags. The values are the Win32 <c>MOD_*</c> constants so they pass
/// straight through to <c>RegisterHotKey</c>.
/// </summary>
[Flags]
public enum GlobalHotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Win = 0x0008,
}

/// <summary>
/// The single process-wide owner of <c>RegisterHotKey</c>.
/// <para>
/// One message-only window (<c>HWND_MESSAGE</c>) receives every <c>WM_HOTKEY</c>; callers map
/// their own registration ids. This used to live inside the Shortcuts plugin as a private class;
/// it moved to the host because a second consumer (聚焦搜索) needs the same platform seam and
/// duplicating it is exactly what 开发规范 §1.2 forbids. Shortcuts now delegates here.
/// </para>
/// <para>
/// <b>Thread affinity:</b> the window is created on the calling thread and Win32 posts
/// <c>WM_HOTKEY</c> to that thread's queue. Register from the UI thread (the Avalonia Win32 loop
/// dispatches those messages), and raise <see cref="Pressed"/> on that same thread.
/// </para>
/// <para>
/// <b>Idempotence:</b> registering an id that is already registered replaces it; unregistering an
/// unknown id is a no-op. Both are safe to call from teardown paths.
/// </para>
/// </summary>
public sealed class GlobalHotkeyRegistrar : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HwndMessage = -3;

    /// <summary>Suppress auto-repeat: holding the gesture fires once, like macOS Spotlight.</summary>
    private const uint ModNoRepeat = 0x4000;

    private const int ErrorClassAlreadyExists = 1410;

    private readonly object gate = new();
    private readonly string className = "DeskCanvasGlobalHotkey_" + Guid.NewGuid().ToString("N");
    private readonly WndProcDelegate wndProc;

    private IntPtr hwnd;
    private ushort classAtom;
    private bool disposed;

    public GlobalHotkeyRegistrar()
    {
        // Kept in a field: the native window must never call into collected managed memory.
        wndProc = WndProc;
    }

    /// <summary>Raised with the caller's registration id when the OS fires a registered gesture.</summary>
    public event Action<int>? Pressed;

    /// <summary>True once the message-only window exists (i.e. a registration was attempted).</summary>
    public bool IsReady
    {
        get { lock (gate) return hwnd != IntPtr.Zero; }
    }

    /// <summary>
    /// Register <paramref name="registrationId"/>. Returns false when the OS rejects the gesture —
    /// another process owns it, or Windows reserves it (see <see cref="LastError"/>).
    /// </summary>
    public bool Register(int registrationId, GlobalHotkeyModifiers modifiers, uint virtualKey)
    {
        lock (gate)
        {
            if (disposed || !EnsureWindowLocked()) return false;

            // Replace an existing binding for the same id so a settings change never leaves the
            // old gesture alive.
            try { UnregisterHotKey(hwnd, registrationId); } catch { }

            var ok = RegisterHotKey(hwnd, registrationId,
                (uint)modifiers | ModNoRepeat, virtualKey);
            if (!ok) LastError = Marshal.GetLastWin32Error();
            return ok;
        }
    }

    public void Unregister(int registrationId)
    {
        lock (gate)
        {
            if (hwnd == IntPtr.Zero) return;
            try { UnregisterHotKey(hwnd, registrationId); } catch { }
        }
    }

    /// <summary>The Win32 error of the last failed <see cref="Register"/> (0 when it succeeded).</summary>
    public int LastError { get; private set; }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            if (hwnd != IntPtr.Zero)
            {
                try { DestroyWindow(hwnd); } catch { }
                hwnd = IntPtr.Zero;
            }

            if (classAtom != 0)
            {
                try { UnregisterClass(className, GetModuleHandle(null)); } catch { }
                classAtom = 0;
            }
        }
    }

    private bool EnsureWindowLocked()
    {
        if (hwnd != IntPtr.Zero) return true;

        var instance = GetModuleHandle(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = instance,
            lpszClassName = className,
        };

        classAtom = RegisterClassEx(ref windowClass);
        if (classAtom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            return false;

        hwnd = CreateWindowEx(0, className, className, 0, 0, 0, 0, 0,
            new IntPtr(HwndMessage), IntPtr.Zero, instance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            if (classAtom != 0)
            {
                try { UnregisterClass(className, instance); } catch { }
                classAtom = 0;
            }
            return false;
        }

        return true;
    }

    private IntPtr WndProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmHotkey)
        {
            try { Pressed?.Invoke(wParam.ToInt32()); } catch { }
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    // ---- interop -----------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string? windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

/// <summary>
/// A parsed global-hotkey gesture: modifier flags plus a virtual-key code, stored as canonical
/// text ("Ctrl+Alt+1") so it survives a hand-edited settings file.
/// <para>
/// Deliberately a <b>small</b> subset of the Shortcuts plugin's richer gesture model: it covers
/// the launcher's needs (letters, digits, F1–F24, Space/Tab/Enter/Escape). The two should be
/// folded into one shared type once that model moves out of the plugin — tracked as structural
/// debt in the feasibility report, not silently duplicated forever.
/// </para>
/// </summary>
public readonly record struct GlobalHotkeyGesture(
    GlobalHotkeyModifiers Modifiers,
    uint VirtualKey,
    string KeyToken)
{
    private const int MaxTextLength = 64;

    /// <summary>Canonical modifier order: Ctrl, Alt, Shift, Win.</summary>
    public string Text
    {
        get
        {
            var parts = new List<string>(5);
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(GlobalHotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyToken);
            return string.Join("+", parts);
        }
    }

    /// <summary>F1–F24 are safe to bind without a modifier; everything else needs one.</summary>
    public bool IsFunctionKey => VirtualKey is >= 0x70 and <= 0x87;

    public override string ToString() => Text;

    private static readonly Dictionary<string, (uint Vk, string Canonical)> Keys =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = (0x20, "Space"),
            ["Tab"] = (0x09, "Tab"),
            ["Enter"] = (0x0D, "Enter"),
            ["Return"] = (0x0D, "Enter"),
            ["Escape"] = (0x1B, "Escape"),
            ["Esc"] = (0x1B, "Escape"),
            ["Backspace"] = (0x08, "Backspace"),
            ["Delete"] = (0x2E, "Delete"),
            ["Insert"] = (0x2D, "Insert"),
            ["Home"] = (0x24, "Home"),
            ["End"] = (0x23, "End"),
            ["PageUp"] = (0x21, "PageUp"),
            ["PageDown"] = (0x22, "PageDown"),
            ["Up"] = (0x26, "Up"),
            ["Down"] = (0x28, "Down"),
            ["Left"] = (0x25, "Left"),
            ["Right"] = (0x27, "Right"),
            ["`"] = (0xC0, "`"),
            ["-"] = (0xBD, "-"),
            ["="] = (0xBB, "="),
            ["["] = (0xDB, "["),
            ["]"] = (0xDD, "]"),
            [";"] = (0xBA, ";"),
            ["'"] = (0xDE, "'"),
            [","] = (0xBC, ","),
            ["."] = (0xBE, "."),
            ["/"] = (0xBF, "/"),
            ["\\"] = (0xDC, "\\"),
        };

    /// <summary>
    /// Parse "Ctrl+Alt+1" / "Alt+Space" / "F5". Never throws; returns false with a short reason
    /// so a settings page can explain why the gesture was rejected.
    /// </summary>
    public static bool TryParse(string? text, out GlobalHotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;

        var raw = text?.Trim();
        if (string.IsNullOrEmpty(raw)) { error = "empty"; return false; }
        if (raw.Length > MaxTextLength) { error = "too-long"; return false; }

        var modifiers = GlobalHotkeyModifiers.None;
        string? keyToken = null;

        foreach (var part in raw.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            if (token.Length == 0) continue;

            if (TryParseModifier(token, out var modifier))
            {
                modifiers |= modifier;
                continue;
            }

            // The key must be the last meaningful token.
            if (keyToken != null) { error = "multiple-keys"; return false; }
            keyToken = token;
        }

        if (keyToken == null) { error = "missing-key"; return false; }
        if (!TryResolveKey(keyToken, out var vk, out var canonical)) { error = "unknown-key"; return false; }

        gesture = new GlobalHotkeyGesture(modifiers, vk, canonical);

        // A bare printable key would swallow normal typing system-wide.
        if (modifiers == GlobalHotkeyModifiers.None && !gesture.IsFunctionKey)
        {
            error = "modifier-required";
            return false;
        }

        return true;
    }

    /// <summary>Parse, falling back to <paramref name="fallback"/> when the text is unusable.</summary>
    public static GlobalHotkeyGesture ParseOrFallback(string? text, GlobalHotkeyGesture fallback)
        => TryParse(text, out var gesture, out _) ? gesture : fallback;

    /// <summary>
    /// Whether an Avalonia key press is exactly this gesture (same key, same modifier set). Used
    /// by the launcher's category-lock keys, which are local to the overlay and never registered
    /// with the OS.
    /// </summary>
    public bool Matches(Key key, KeyModifiers modifiers)
    {
        var token = LauncherKeyText.DescribeKey(key);
        return token != null
               && string.Equals(token, KeyToken, StringComparison.OrdinalIgnoreCase)
               && LauncherKeyText.ToGlobal(modifiers) == Modifiers;
    }

    private static bool TryParseModifier(string token, out GlobalHotkeyModifiers modifier)
    {
        switch (token.ToLowerInvariant())
        {
            case "ctrl" or "control": modifier = GlobalHotkeyModifiers.Control; return true;
            case "alt": modifier = GlobalHotkeyModifiers.Alt; return true;
            case "shift": modifier = GlobalHotkeyModifiers.Shift; return true;
            case "win" or "meta" or "super": modifier = GlobalHotkeyModifiers.Win; return true;
            default: modifier = GlobalHotkeyModifiers.None; return false;
        }
    }

    private static bool TryResolveKey(string token, out uint vk, out string canonical)
    {
        if (Keys.TryGetValue(token, out var named))
        {
            vk = named.Vk;
            canonical = named.Canonical;
            return true;
        }

        // Single letter / digit.
        if (token.Length == 1)
        {
            var c = char.ToUpperInvariant(token[0]);
            if (c is >= 'A' and <= 'Z') { vk = c; canonical = c.ToString(); return true; }
            if (c is >= '0' and <= '9') { vk = c; canonical = c.ToString(); return true; }
        }

        // F1–F24.
        if (token.Length is 2 or 3 && (token[0] is 'f' or 'F')
            && int.TryParse(token.AsSpan(1), out var index) && index is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + index - 1);
            canonical = "F" + index;
            return true;
        }

        vk = 0;
        canonical = string.Empty;
        return false;
    }
}

/// <summary>
/// Avalonia key + modifiers → the canonical gesture text <see cref="GlobalHotkeyGesture"/>
/// understands. One implementation for the settings recorders and the overlay's matcher: a
/// recorder that wrote "F3" while the matcher compared something else would bind a key that
/// silently never fires.
/// </summary>
public static class LauncherKeyText
{
    /// <summary>Canonical text ("Ctrl+Alt+1"), or null for a key the launcher does not accept.</summary>
    public static string? Describe(KeyModifiers modifiers, Key key)
    {
        var token = DescribeKey(key);
        if (token == null) return null;

        var text = string.Empty;
        if (modifiers.HasFlag(KeyModifiers.Control)) text += "Ctrl+";
        if (modifiers.HasFlag(KeyModifiers.Alt)) text += "Alt+";
        if (modifiers.HasFlag(KeyModifiers.Shift)) text += "Shift+";
        if (modifiers.HasFlag(KeyModifiers.Meta)) text += "Win+";

        return text + token;
    }

    /// <summary>The key alone, without modifiers; null when the launcher cannot bind it.</summary>
    public static string? DescribeKey(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "Num" + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);

        return key switch
        {
            Key.Space => "Space",
            Key.Tab => "Tab",
            Key.Enter => "Enter",
            Key.Back => "Backspace",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.OemTilde => "`",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemPipe => "\\",
            _ => null,
        };
    }

    /// <summary>Avalonia modifier flags → the Win32-shaped flags the gesture stores.</summary>
    public static GlobalHotkeyModifiers ToGlobal(KeyModifiers modifiers)
    {
        var result = GlobalHotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= GlobalHotkeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= GlobalHotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= GlobalHotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= GlobalHotkeyModifiers.Win;
        return result;
    }
}
