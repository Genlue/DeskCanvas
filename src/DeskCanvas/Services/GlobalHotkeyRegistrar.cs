using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;

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
/// The single process-wide owner of <c>RegisterHotKey</c>, plus the one low-level keyboard hook
/// used to take over combinations the OS refuses to register (例如 Win+S).
/// <para>
/// One message-only window (<c>HWND_MESSAGE</c>) receives every <c>WM_HOTKEY</c>; callers map
/// their own registration ids. This used to live inside the Shortcuts plugin as a private class;
/// it moved to the host because a second consumer (聚焦搜索) needs the same platform seam and
/// duplicating it is exactly what 开发规范 §1.2 forbids. Shortcuts now delegates here.
/// </para>
/// <para>
/// <b>Hook fallback:</b> <c>RegisterHotKey</c> cannot own a combination Windows reserves
/// (<c>ERROR_HOTKEY_ALREADY_REGISTERED</c>), so callers that are allowed to take over a system
/// shortcut can call <see cref="RegisterWithHook"/>. A single <c>WH_KEYBOARD_LL</c> hook runs on
/// a dedicated message-loop thread, matches only the registered gestures, and swallows their
/// key-down/up. It must never do more than compare virtual-key codes there: Windows silently
/// removes a low-level hook whose callback exceeds <c>LowLevelHooksTimeout</c>.
/// </para>
/// <para>
/// <b>Thread affinity:</b> the window is created on the calling thread and Win32 posts
/// <c>WM_HOTKEY</c> to that thread's queue. Register from the UI thread (the Avalonia Win32 loop
/// dispatches those messages), and raise <see cref="Pressed"/> on that same thread. Hook matches
/// arrive on the hook thread and are marshalled back to the UI dispatcher.
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

    /// <summary>Hook registrations by caller id; replaced wholesale into <see cref="hookSnapshot"/>.</summary>
    private readonly Dictionary<int, HookBinding> hookBindings = new();

    /// <summary>Lock-free view the hook callback reads; never mutate the array in place.</summary>
    private volatile HookBinding[] hookSnapshot = [];

    private LowLevelKeyboardHook? hookHost;

    /// <summary>Recorder callbacks; set while a settings-page recorder is capturing.</summary>
    private Action<string>? gestureCaptureCallback;
    private Action? gestureCancelCallback;
    private volatile bool gestureCaptureActive;

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
        get
        {
            lock (gate) return hwnd != IntPtr.Zero || hookHost is { IsReady: true };
        }
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
            // old gesture alive — including a hook binding the previous configuration used.
            try { UnregisterHotKey(hwnd, registrationId); } catch { }
            RemoveHookBindingLocked(registrationId);

            var ok = RegisterHotKey(hwnd, registrationId,
                (uint)modifiers | ModNoRepeat, virtualKey);
            if (!ok) LastError = Marshal.GetLastWin32Error();
            return ok;
        }
    }

    /// <summary>
    /// Take over a gesture with a low-level keyboard hook. Used when <see cref="Register"/> failed
    /// because Windows or another program owns the combination (Win+S 是典型例子).
    /// <para>
    /// The hook is process-wide and shared by every hook registration; it is installed lazily on
    /// the first successful call and removed when the last binding is unregistered. The caller
    /// must opt in explicitly: a hook swallows the combination from every application, including
    /// the system UI the gesture used to open.
    /// </para>
    /// </summary>
    /// <returns>
    /// False when the hook thread could not be created or the gesture duplicates another hook
    /// binding; <see cref="LastError"/> then carries the Win32 error (or 1409 for a duplicate).
    /// </returns>
    public bool RegisterWithHook(int registrationId, GlobalHotkeyModifiers modifiers, uint virtualKey)
    {
        lock (gate)
        {
            if (disposed) return false;

            // The same rule the text parser enforces: a bare printable key would swallow typing.
            if (modifiers == GlobalHotkeyModifiers.None && (virtualKey is < 0x70 or > 0x87))
            {
                LastError = 87; // ERROR_INVALID_PARAMETER
                return false;
            }

            foreach (var pair in hookBindings)
            {
                if (pair.Key != registrationId
                    && pair.Value.Modifiers == modifiers
                    && pair.Value.VirtualKey == virtualKey)
                {
                    LastError = 1409; // ERROR_HOTKEY_ALREADY_REGISTERED
                    return false;
                }
            }

            // A previous registration for this id may still be a real RegisterHotKey binding.
            if (hwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(hwnd, registrationId); } catch { }
            }

            hookBindings[registrationId] = new HookBinding(registrationId, modifiers, virtualKey);
            hookSnapshot = hookBindings.Values.ToArray();

            if (hookHost is not { IsReady: true })
            {
                hookHost?.Dispose();
                hookHost = new LowLevelKeyboardHook(this);
                if (!hookHost.Start(out var error))
                {
                    hookHost.Dispose();
                    hookHost = null;
                    RemoveHookBindingLocked(registrationId);
                    LastError = error;
                    return false;
                }
            }

            LastError = 0;
            return true;
        }
    }

    /// <summary>
    /// Capture the next key combination with the same low-level hook, so the recorder can see
    /// combinations the shell would otherwise consume (Win+S never reaches a normal window).
    /// <para>
    /// The captured combination is reported on the UI thread; <paramref name="cancelled"/> runs
    /// instead when Escape is pressed. Only one capture is active at a time; starting another
    /// replaces it.
    /// </para>
    /// </summary>
    public bool BeginGestureCapture(Action<string> captured, Action? cancelled = null)
    {
        lock (gate)
        {
            if (disposed) return false;

            gestureCaptureCallback = captured;
            gestureCancelCallback = cancelled;
            gestureCaptureActive = true;

            if (hookHost is not { IsReady: true })
            {
                hookHost?.Dispose();
                hookHost = new LowLevelKeyboardHook(this);
                if (!hookHost.Start(out var error))
                {
                    hookHost.Dispose();
                    hookHost = null;
                    gestureCaptureActive = false;
                    gestureCaptureCallback = null;
                    gestureCancelCallback = null;
                    LastError = error;
                    return false;
                }
            }

            hookHost.BeginCapture();
            LastError = 0;
            return true;
        }
    }

    /// <summary>Stop capturing; the hook stays installed only while real hook bindings remain.</summary>
    public void EndGestureCapture()
    {
        lock (gate)
        {
            gestureCaptureActive = false;
            gestureCaptureCallback = null;
            gestureCancelCallback = null;

            if (hookHost == null) return;
            hookHost.CancelCapture();
            if (hookBindings.Count > 0) return;

            hookHost.Dispose();
            hookHost = null;
        }
    }

    public void Unregister(int registrationId)
    {
        lock (gate)
        {
            if (hwnd != IntPtr.Zero)
            {
                try { UnregisterHotKey(hwnd, registrationId); } catch { }
            }

            RemoveHookBindingLocked(registrationId);
        }
    }

    /// <summary>The Win32 error of the last failed registration (0 when it succeeded).</summary>
    public int LastError { get; private set; }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;

            gestureCaptureActive = false;
            gestureCaptureCallback = null;
            gestureCancelCallback = null;

            hookHost?.Dispose();
            hookHost = null;
            hookBindings.Clear();
            hookSnapshot = [];

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

    /// <summary>Remove one hook binding; the hook itself stops with the last one.</summary>
    private void RemoveHookBindingLocked(int registrationId)
    {
        if (!hookBindings.Remove(registrationId)) return;

        hookSnapshot = hookBindings.Values.ToArray();
        if (hookBindings.Count > 0) return;

        hookHost?.Dispose();
        hookHost = null;
    }

    /// <summary>
    /// A hook match, from the hook thread. The gesture is matched against the modifiers that were
    /// physically down at the moment the action key went down; extra modifiers do not match, which
    /// is the same exact-set rule <c>RegisterHotKey</c> applies.
    /// </summary>
    private int? MatchHookGesture(uint virtualKey, GlobalHotkeyModifiers modifiers)
    {
        foreach (var binding in hookSnapshot)
        {
            if (binding.VirtualKey == virtualKey && binding.Modifiers == modifiers)
                return binding.Id;
        }

        return null;
    }

    /// <summary>
    /// Raise <see cref="Pressed"/> on the UI thread. The hook callback cannot create/show windows
    /// itself (it runs on its own message-loop thread), and a slow callback is removed by Windows.
    /// </summary>
    private void NotifyHookPressed(int registrationId)
        => PostToUi(() =>
        {
            try { Pressed?.Invoke(registrationId); } catch { }
        });

    /// <summary>A recorder capture completed (already on the hook thread; post to the UI).</summary>
    private void NotifyGestureCaptured(string text)
    {
        if (!gestureCaptureActive) return;

        gestureCaptureActive = false;
        var callback = gestureCaptureCallback;
        gestureCaptureCallback = null;
        gestureCancelCallback = null;
        if (callback == null) return;

        PostToUi(() =>
        {
            try { callback(text); } catch { }
        });
    }

    /// <summary>The recorder was cancelled with Escape.</summary>
    private void NotifyGestureCaptureCancelled()
    {
        if (!gestureCaptureActive) return;

        gestureCaptureActive = false;
        var callback = gestureCancelCallback;
        gestureCaptureCallback = null;
        gestureCancelCallback = null;
        if (callback == null) return;

        PostToUi(() =>
        {
            try { callback(); } catch { }
        });
    }

    /// <summary>
    /// Raise a notification on the UI thread. Both the hook thread and the WM_HOTKEY path use
    /// this so the callers never have to know which thread they are on.
    /// </summary>
    private static void PostToUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess()) action();
            else Dispatcher.UIThread.Post(action);
        }
        catch
        {
            // The dispatcher is shutting down; dropping one notification is better than throwing
            // into native code from the hook thread.
        }
    }

    private readonly record struct HookBinding(int Id, GlobalHotkeyModifiers Modifiers, uint VirtualKey);

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

    // ---- low-level keyboard hook -------------------------------------------------

    /// <summary>
    /// The single <c>WH_KEYBOARD_LL</c> host. It lives on its own thread with a message loop
    /// because Windows delivers low-level hook callbacks through that thread's queue; running it
    /// on the Avalonia UI thread would let a busy frame starve the callback and get the hook
    /// silently removed (<c>LowLevelHooksTimeout</c>, at most 1000 ms on Windows 10 1709+).
    /// </summary>
    private sealed class LowLevelKeyboardHook : IDisposable
    {
        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_QUIT = 0x0012;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const int VK_LWIN = 0x5B;
        private const int VK_RWIN = 0x5C;
        private const int VK_LSHIFT = 0xA0;
        private const int VK_RSHIFT = 0xA1;
        private const int VK_LCONTROL = 0xA2;
        private const int VK_RCONTROL = 0xA3;
        private const int VK_LMENU = 0xA4;
        private const int VK_RMENU = 0xA5;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_DUMMY = 0xFF;
        private const uint DummyMarker = 0x44435348; // 'DCSH'

        private readonly GlobalHotkeyRegistrar owner;
        private readonly HookProc proc;
        private readonly bool[] downKeys = new bool[256];
        private Thread? thread;
        private ManualResetEventSlim? ready;
        private IntPtr hook;
        private uint threadId;
        private int startError;
        private int captureVk;
        private volatile bool captureMode;
        private bool winSuppressed;
        private bool disposed;

        public LowLevelKeyboardHook(GlobalHotkeyRegistrar owner)
        {
            this.owner = owner;
            proc = Callback;
        }

        public bool IsReady => hook != IntPtr.Zero;

        public bool Start(out int error)
        {
            ready = new ManualResetEventSlim();
            thread = new Thread(Run) { IsBackground = true, Name = "DeskCanvas low-level hotkey hook" };
            thread.Start();

            if (!ready.Wait(TimeSpan.FromSeconds(3)))
            {
                error = 1460; // ERROR_TIMEOUT
                Dispose();
                return false;
            }

            error = startError;
            if (hook != IntPtr.Zero) return true;

            Dispose();
            return false;
        }

        /// <summary>Start swallowing the next combination and reporting it to the recorder.</summary>
        public void BeginCapture()
        {
            Array.Clear(downKeys);
            captureVk = 0;
            winSuppressed = false;
            captureMode = true;
        }

        /// <summary>Stop capturing; the hook stays installed if real bindings still need it.</summary>
        public void CancelCapture()
        {
            captureMode = false;
            captureVk = 0;
            winSuppressed = false;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;

            var worker = thread;
            if (worker != null && worker.IsAlive)
            {
                PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                worker.Join(TimeSpan.FromSeconds(2));
            }

            thread = null;
            ready?.Dispose();
            ready = null;
            hook = IntPtr.Zero;
        }

        private void Run()
        {
            threadId = GetCurrentThreadId();
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);
            if (hook == IntPtr.Zero) startError = Marshal.GetLastWin32Error();

            ready?.Set();
            if (hook == IntPtr.Zero) return;

            try
            {
                while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }
            finally
            {
                if (hook != IntPtr.Zero)
                {
                    try { UnhookWindowsHookEx(hook); } catch { }
                    hook = IntPtr.Zero;
                }
            }
        }

        private IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);

                // Our own dummy key (injected to keep the Start menu closed) must not match.
                if (data.dwExtraInfo != (IntPtr)DummyMarker)
                {
                    var message = wParam.ToInt32();
                    var down = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
                    var up = message == WM_KEYUP || message == WM_SYSKEYUP;

                    if ((down || up) && data.vkCode < (uint)downKeys.Length)
                    {
                        var vk = (int)data.vkCode;

                        if (captureMode)
                        {
                            if (HandleCapture(vk, down, up)) return (IntPtr)1;
                            return CallNextHookEx(hook, nCode, wParam, lParam);
                        }

                        if (down)
                        {
                            if (captureVk == vk)
                            {
                                // Auto-repeat of the action key that just fired or was captured:
                                // swallow it so the system never sees the key down twice.
                                downKeys[vk] = true;
                                return (IntPtr)1;
                            }

                            if (captureVk == 0 && !downKeys[vk])
                            {
                                var modifiers = CurrentModifiers();
                                if (owner.MatchHookGesture((uint)vk, modifiers) is int registrationId)
                                {
                                    captureVk = vk;

                                    // Win is special: the shell opens Start on a lone Win tap. The
                                    // dummy key makes the release look like part of a combination.
                                    if ((modifiers & GlobalHotkeyModifiers.Win) != 0) InjectDummy();

                                    downKeys[vk] = true;
                                    owner.NotifyHookPressed(registrationId);
                                    return (IntPtr)1;
                                }
                            }

                            downKeys[vk] = true;
                        }
                        else
                        {
                            downKeys[vk] = false;

                            // A Win key-down swallowed while recording must have its up swallowed
                            // too, even after capture mode ended; the shell never saw the down.
                            if (winSuppressed && IsWin(vk))
                            {
                                winSuppressed = false;
                                return (IntPtr)1;
                            }

                            if (captureVk == vk)
                            {
                                captureVk = 0;
                                return (IntPtr)1;
                            }
                        }
                    }
                }
            }

            return CallNextHookEx(hook, nCode, wParam, lParam);
        }

        /// <summary>
        /// Recorder mode. Modifiers are tracked locally; Win is swallowed so a lone tap cannot open
        /// Start, and the first non-modifier key becomes the gesture (Escape cancels).
        /// </summary>
        private bool HandleCapture(int vk, bool down, bool up)
        {
            if (down)
            {
                if (IsModifier(vk))
                {
                    downKeys[vk] = true;

                    // The shell must not see the Win key at all while recording, otherwise a
                    // modifier-only tap would still open Start when released.
                    if (IsWin(vk))
                    {
                        winSuppressed = true;
                        return true;
                    }

                    return false;
                }

                if (vk == VK_ESCAPE)
                {
                    downKeys[vk] = true;
                    captureVk = vk;
                    captureMode = false;
                    owner.NotifyGestureCaptureCancelled();
                    return true;
                }

                var modifiers = CurrentModifiers();
                var text = GlobalHotkeyGesture.Describe(modifiers, (uint)vk);
                if (text == null)
                {
                    // A key the launcher cannot bind (e.g. a media key): let it through and keep
                    // waiting instead of turning the recorder into a black hole.
                    downKeys[vk] = true;
                    return false;
                }

                downKeys[vk] = true;
                captureVk = vk;
                captureMode = false;

                // The real binding will need the same Start-menu guard.
                if ((modifiers & GlobalHotkeyModifiers.Win) != 0) InjectDummy();

                owner.NotifyGestureCaptured(text);
                return true;
            }

            if (up)
            {
                downKeys[vk] = false;

                if (captureVk == vk)
                {
                    captureVk = 0;
                    return true;
                }

                if (winSuppressed && IsWin(vk))
                {
                    winSuppressed = false;
                    return true;
                }
            }

            return false;
        }

        private static bool IsModifier(int vk)
            => vk is VK_LWIN or VK_RWIN
                or VK_LSHIFT or VK_RSHIFT
                or VK_LCONTROL or VK_RCONTROL
                or VK_LMENU or VK_RMENU;

        private static bool IsWin(int vk) => vk is VK_LWIN or VK_RWIN;

        private GlobalHotkeyModifiers CurrentModifiers()
        {
            var modifiers = GlobalHotkeyModifiers.None;
            if (downKeys[VK_LCONTROL] || downKeys[VK_RCONTROL]) modifiers |= GlobalHotkeyModifiers.Control;
            if (downKeys[VK_LMENU] || downKeys[VK_RMENU]) modifiers |= GlobalHotkeyModifiers.Alt;
            if (downKeys[VK_LSHIFT] || downKeys[VK_RSHIFT]) modifiers |= GlobalHotkeyModifiers.Shift;
            if (downKeys[VK_LWIN] || downKeys[VK_RWIN]) modifiers |= GlobalHotkeyModifiers.Win;
            return modifiers;
        }

        /// <summary>
        /// Inject an unassigned key (VK 0xFF) down/up. Applications ignore it, but it makes the
        /// shell treat the Win press as a combination instead of a lone tap, so releasing Win does
        /// not open Start. The same trick PowerToys' Keyboard Manager uses.
        /// </summary>
        private static void InjectDummy()
        {
            var inputs = new INPUT[2];
            inputs[0].type = 1; // INPUT_KEYBOARD
            inputs[0].U.ki = new KEYBDINPUT
            {
                wVk = VK_DUMMY,
                dwExtraInfo = (IntPtr)DummyMarker,
            };
            inputs[1].type = 1;
            inputs[1].U.ki = new KEYBDINPUT
            {
                wVk = VK_DUMMY,
                dwFlags = KEYEVENTF_KEYUP,
                dwExtraInfo = (IntPtr)DummyMarker,
            };

            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        // ---- interop -------------------------------------------------------------

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    }
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

        // The secure attention sequence and the lock shortcut are handled below any hook; a
        // "taken over" status for either would be a promise we cannot keep.
        if (IsReserved(modifiers, vk))
        {
            error = "reserved";
            return false;
        }

        return true;
    }

    /// <summary>Parse, falling back to <paramref name="fallback"/> when the text is unusable.</summary>
    public static GlobalHotkeyGesture ParseOrFallback(string? text, GlobalHotkeyGesture fallback)
        => TryParse(text, out var gesture, out _) ? gesture : fallback;

    /// <summary>
    /// Canonical gesture text for a Win32 virtual-key event, or <c>null</c> when the key has no
    /// token the parser understands. The hook recorder and the settings text box therefore produce
    /// the same spelling ("Win+S", not "Win+s" or "Win+0x53").
    /// </summary>
    public static string? Describe(GlobalHotkeyModifiers modifiers, uint virtualKey)
    {
        if (!TryDescribeKey(virtualKey, out var token)) return null;

        var parts = new List<string>(5);
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(GlobalHotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(token);
        return string.Join("+", parts);
    }

    /// <summary>Key token alone (no modifiers), or false for a key the launcher cannot bind.</summary>
    public static bool TryDescribeKey(uint virtualKey, out string token)
    {
        if (virtualKey is >= (uint)'A' and <= (uint)'Z')
        {
            token = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= (uint)'0' and <= (uint)'9')
        {
            token = ((char)virtualKey).ToString();
            return true;
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            token = "F" + (virtualKey - 0x70 + 1);
            return true;
        }

        // The dictionary carries aliases (Enter/Return, Escape/Esc); source order makes the
        // first spelling the canonical one, exactly what the parser writes back.
        foreach (var pair in Keys)
        {
            if (pair.Value.Vk != virtualKey) continue;
            token = pair.Value.Canonical;
            return true;
        }

        token = string.Empty;
        return false;
    }

    /// <summary>Win+L and Ctrl+Alt+Del cannot be taken over by any user-mode hook.</summary>
    private static bool IsReserved(GlobalHotkeyModifiers modifiers, uint virtualKey)
        => (modifiers == GlobalHotkeyModifiers.Win && virtualKey == 0x4C)
           || (modifiers == (GlobalHotkeyModifiers.Control | GlobalHotkeyModifiers.Alt)
               && virtualKey == 0x2E);

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
