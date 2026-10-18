using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Shortcuts.Services;

/// <summary>One keyboard event for <see cref="KeyboardInput"/> (virtual key or unicode char).</summary>
public readonly record struct KeyStroke(ushort VirtualKey, bool IsKeyUp, char? Unicode = null, bool Extended = false)
{
    public static KeyStroke KeyDown(ushort virtualKey) =>
        new(virtualKey, IsKeyUp: false, Extended: KeyboardInput.IsExtendedKey(virtualKey));

    public static KeyStroke KeyUp(ushort virtualKey) =>
        new(virtualKey, IsKeyUp: true, Extended: KeyboardInput.IsExtendedKey(virtualKey));

    public static KeyStroke UnicodeDown(char value) => new(0, IsKeyUp: false, Unicode: value);

    public static KeyStroke UnicodeUp(char value) => new(0, IsKeyUp: true, Unicode: value);
}

/// <summary>
/// SendInput seam. Actions go through <see cref="KeyboardInput.Send"/> so tests can inject a
/// fake and assert exactly which keys are pressed/released without touching the real keyboard.
/// </summary>
public interface IKeyboardInputSender
{
    void Send(IReadOnlyList<KeyStroke> strokes);
}

/// <summary>Process-wide keyboard sender (one Win32 implementation, replaceable in checks).</summary>
public static class KeyboardInput
{
    private static IKeyboardInputSender sender = new Win32KeyboardInputSender();

    public static IKeyboardInputSender Sender
    {
        get => sender;
        set => sender = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static void Send(IReadOnlyList<KeyStroke> strokes) => sender.Send(strokes);

    /// <summary>Navigation / right-hand modifiers need KEYEVENTF_EXTENDEDKEY.</summary>
    public static bool IsExtendedKey(ushort virtualKey) => virtualKey is
        0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or
        0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90;
}

/// <summary>Real SendInput implementation (arrays are sent in one call, preserving order).</summary>
internal sealed class Win32KeyboardInputSender : IKeyboardInputSender
{
    private const uint InputKeyboard = 1;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public void Send(IReadOnlyList<KeyStroke> strokes)
    {
        if (strokes.Count == 0) return;

        var inputs = new INPUT[strokes.Count];
        for (var i = 0; i < strokes.Count; i++)
        {
            var stroke = strokes[i];
            if (stroke.Unicode is { } unicode)
            {
                inputs[i] = new INPUT
                {
                    Type = InputKeyboard,
                    Union = new InputUnion
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            VirtualKey = 0,
                            ScanCode = unicode,
                            Flags = KeyEventUnicode | (stroke.IsKeyUp ? KeyEventKeyUp : 0),
                        },
                    },
                };
            }
            else
            {
                inputs[i] = new INPUT
                {
                    Type = InputKeyboard,
                    Union = new InputUnion
                    {
                        Keyboard = new KEYBDINPUT
                        {
                            VirtualKey = stroke.VirtualKey,
                            Flags = (stroke.Extended ? KeyEventExtended : 0) |
                                    (stroke.IsKeyUp ? KeyEventKeyUp : 0),
                        },
                    },
                };
            }
        }

        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (sent != (uint)inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion Union;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public HARDWAREINPUT Hardware;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint Msg;
        public ushort ParamL;
        public ushort ParamH;
    }
}
