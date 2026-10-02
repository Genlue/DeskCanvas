using System;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// System-wide hotkey for toggling the sidebar.
/// <para>
/// Primary path: Win32 <c>RegisterHotKey</c> with <c>MOD_NOREPEAT</c> on a <b>message-only window
/// of this service's own window class</b>. The window procedure is never swapped into an existing
/// window: replacing <c>GWLP_WNDPROC</c> on an Avalonia (managed) window class is exactly the
/// regression <c>tests/SubclassProbe</c> guards against — the address in that slot points into the
/// CLR, so forwarding through <c>CallWindowProc</c> can drop every message and freeze the window.
/// </para>
/// <para>
/// Safety net: registration can be refused (another app already owns the combination) and message
/// delivery can be blocked by a third-party hook. A light <c>GetAsyncKeyState</c> poll therefore
/// watches the same combination on a rising edge and fires if nothing fired recently. The hotkey
/// therefore keeps working even when <c>WM_HOTKEY</c> never arrives, while a genuine conflict is
/// still reported to the settings page.
/// </para>
/// </summary>
public sealed class GlobalHotKeyService : IGlobalHotKeyService, IDisposable
{
    private const uint WM_HOTKEY = 0x0312;
    private const int IdA = 0x4B11;
    private const int IdB = 0x4B12;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    /// <summary>Two triggers for one press (message + poll) must not toggle twice.</summary>
    private const long DeDupMs = 300;

    private readonly IAppSettingsProvider appSettingsProvider;

    private IntPtr hwnd;
    private WndProc? wndProc;                 // kept alive for the lifetime of our window class
    private string className = string.Empty;

    private int activeId = IdA;
    private bool registered;
    private SidebarHotKey current;

    private DispatcherTimer? poller;
    private bool comboWasDown;
    private long lastFireTicks;
    private bool everFired;

    public GlobalHotKeyService(IAppSettingsProvider appSettingsProvider)
    {
        this.appSettingsProvider = appSettingsProvider;
        current = ReadFromSettings();
    }

    /// <inheritdoc />
    public string HotKey => current.Normalized;

    /// <inheritdoc />
    public bool IsRegistered => registered;

    /// <summary>Why the last registration attempt failed, or <c>null</c>.</summary>
    public string? LastError { get; private set; }

    /// <inheritdoc />
    public event EventHandler? HotKeyPressed;

    /// <inheritdoc />
    public void Start()
    {
        if (!OperatingSystem.IsWindows()) return;

        // The safety net runs whether or not Win32 accepted the registration.
        if (poller == null)
        {
            poller = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => Poll());
            poller.Start();
        }

        if (hwnd != IntPtr.Zero) return;

        try
        {
            className = "DeskCanvasSidebarHotKey" + Guid.NewGuid().ToString("N");
            wndProc = WndProcCallback;

            var module = GetModuleHandle(null);
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
                hInstance = module,
                lpszClassName = className
            };
            if (RegisterClassEx(ref wc) == 0) { wndProc = null; return; }

            hwnd = CreateWindowEx(0, className, "DeskCanvas.HotKey", 0, 0, 0, 0, 0,
                HWND_MESSAGE, IntPtr.Zero, module, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) { wndProc = null; return; }

            // A failed registration must never stop the app from starting.
            TryRegister(current, out var error);
            LastError = error;
        }
        catch
        {
            // Hotkey support is a convenience — never a startup blocker.
        }
    }

    /// <inheritdoc />
    public bool TrySetHotKey(string? hotKey, out string? error)
    {
        error = null;
        if (!SidebarHotKey.TryParse(hotKey, out var parsed) || parsed == null)
        {
            error = "无效的快捷键：需要至少一个修饰键（Ctrl / Alt / Shift / Win），且不能使用 F12。";
            LastError = error;
            return false;
        }

        if (hwnd == IntPtr.Zero)
        {
            // The message window is not up yet (or a non-Windows host): accept and remember; the
            // poll uses it immediately and Start() registers it.
            current = parsed;
            LastError = null;
            return true;
        }

        if (TryRegister(parsed, out error))
        {
            LastError = null;
            return true;
        }

        // Keep the old combination; report the conflict.
        LastError = error;
        return false;
    }

    private bool TryRegister(SidebarHotKey hotKey, out string? error)
    {
        error = null;
        if (hwnd == IntPtr.Zero) { current = hotKey; return true; }

        if (!SidebarHotKey.TryVirtualKey(hotKey.Key, out var vk))
        {
            error = "不支持的按键。";
            return false;
        }

        // Register the candidate under the *other* id first, so a failure leaves the current one intact.
        var candidateId = activeId == IdA ? IdB : IdA;
        var mods = hotKey.Modifiers | SidebarHotKey.ModNoRepeat;

        if (!RegisterHotKey(hwnd, candidateId, mods, vk))
        {
            error = "该快捷键已被其他程序占用或系统不允许，已保留原快捷键。";
            return false;
        }

        if (registered)
            UnregisterHotKey(hwnd, activeId);

        activeId = candidateId;
        registered = true;
        current = hotKey;
        return true;
    }

    // ---------- Triggers ----------

    private void Fire()
    {
        var now = Environment.TickCount64;

        // The "never fired yet" case must not be expressed as a sentinel tick value: `now` minus
        // `long.MinValue` overflows a signed long and wraps negative, which compares as "too soon"
        // — and because the field was only written on a successful fire, that comparison stayed
        // true forever and swallowed every hotkey press of the session, from both triggers.
        if (everFired && now - lastFireTicks < DeDupMs) return;
        everFired = true;
        lastFireTicks = now;

        try
        {
            HotKeyPressed?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            // A failing handler must not break the message loop or the poll timer.
        }
    }

    /// <summary>Rising-edge poll of the configured combination (works even without WM_HOTKEY).</summary>
    private void Poll()
    {
        if (!OperatingSystem.IsWindows()) return;

        var down = IsCombinationDown(current);
        if (down && !comboWasDown) Fire();
        comboWasDown = down;
    }

    private static bool IsCombinationDown(SidebarHotKey hotKey)
    {
        if (hotKey.Control && !IsKeyDown(VK_CONTROL)) return false;
        if (hotKey.Alt && !IsKeyDown(VK_MENU)) return false;
        if (hotKey.Shift && !IsKeyDown(VK_SHIFT)) return false;
        if (hotKey.Win && !IsKeyDown(VK_LWIN) && !IsKeyDown(VK_RWIN)) return false;
        return SidebarHotKey.TryVirtualKey(hotKey.Key, out var vk) && IsKeyDown((int)vk);
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    /// <summary>
    /// Window procedure of <b>our own</b> message-only window — never installed on an Avalonia
    /// window (see the type remarks).
    /// </summary>
    private IntPtr WndProcCallback(IntPtr window, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WM_HOTKEY)
        {
            Fire();
            return IntPtr.Zero;
        }

        return DefWindowProc(window, message, wParam, lParam);
    }

    private SidebarHotKey ReadFromSettings()
    {
        try
        {
            return SidebarHotKey.ParseOrDefault(appSettingsProvider.Get().EffectiveSidebar.HotKey, SidebarHotKey.Default);
        }
        catch
        {
            return SidebarHotKey.Default;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        poller?.Stop();
        poller = null;

        if (!OperatingSystem.IsWindows()) return;

        if (registered && hwnd != IntPtr.Zero)
        {
            try { UnregisterHotKey(hwnd, activeId); } catch { }
            registered = false;
        }

        if (hwnd != IntPtr.Zero)
        {
            try { DestroyWindow(hwnd); } catch { }
            hwnd = IntPtr.Zero;
        }

        if (!string.IsNullOrEmpty(className))
        {
            try { UnregisterClass(className, GetModuleHandle(null)); } catch { }
            className = string.Empty;
        }

        wndProc = null;
    }

    public void Dispose() => Stop();

    // ---------- Win32 ----------

    private delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
