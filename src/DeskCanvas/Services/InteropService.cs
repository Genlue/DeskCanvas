using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Controls;

namespace DeskCanvas.Services;

public class InteropService
{
    private const int SPI_GETDESKWALLPAPER = 0x0073;

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(int uAction, int uParam, StringBuilder lpvParam, int fuWinIni);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minSize, IntPtr maxSize);

    /// <summary>
    /// Give memory back to the OS: a full, compacting collection (so free managed segments are
    /// decommitted) followed by a working-set trim.
    /// <para>
    /// The collection is <b>blocking</b> on purpose. A non-blocking one returns immediately, so the
    /// working-set trim used to run before the collection had even finished — which is why pausing
    /// the widgets behind a fullscreen game visibly freed nothing.
    /// </para>
    /// </summary>
    public static void TrimProcessMemory()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    public static string GetWallpaperPath()
    {
        StringBuilder wallpaperPath = new StringBuilder(260);
        SystemParametersInfo(SPI_GETDESKWALLPAPER, wallpaperPath.Capacity, wallpaperPath, 0);
        return wallpaperPath.ToString();
    }
    public static void RemoveWindowFromAltTab(Window window)
    {
        const int WS_EX_TOOLWINDOW = 0x00000080;
        const int GWL_EXSTYLE = -20;

        var handle = window.TryGetPlatformHandle()?.Handle;
        
        if (handle == null) return;

        var exStyle = (int)GetWindowLong(handle.Value, GWL_EXSTYLE);

        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLong(handle.Value, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// Clip the native window to the widget card's rounded rectangle.
    /// <para>
    /// The widget window spans the whole grid cell, but the frosted-glass card
    /// only occupies the cell minus the margin, with rounded corners. Clipping
    /// the native window region makes the OS-side blur follow the card exactly,
    /// and makes the transparent margin area click-through (clicks outside the
    /// card fall through to the desktop instead of being captured by the widget).
    /// </para>
    /// </summary>
    /// <param name="window">The target window.</param>
    /// <param name="x">Region left edge (physical pixels, relative to the window).</param>
    /// <param name="y">Region top edge (physical pixels, relative to the window).</param>
    /// <param name="width">Region width (physical pixels).</param>
    /// <param name="height">Region height (physical pixels).</param>
    /// <param name="radius">Corner radius (physical pixels, typically <c>Dimensions.Radius</c>).</param>
    public static void SetWidgetRegion(Window window, int x, int y, int width, int height, int radius)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null) return;
        SetWindowRegion(handle.Value, x, y, width, height, radius);
    }

    /// <summary>
    /// Clip any native Win32 window (such as a PopupRoot) to a rounded rectangle region.
    /// This forces the OS-level DWM AcrylicBlur to strictly follow the exact same rounded
    /// rectangle boundary without bleeding blur into the corner areas.
    /// </summary>
    public static void SetWindowRegion(IntPtr handle, int x, int y, int width, int height, int radius)
    {
        if (handle == IntPtr.Zero) return;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);

        // Win32 GDI CreateRoundRectRgn treats right/bottom as exclusive.
        // x + width + 1 ensures the right and bottom border pixel columns
        // are included rather than truncated.
        var region = radius > 0
            ? CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, radius * 2, radius * 2)
            : CreateRectRgn(x, y, x + width + 1, y + height + 1);
        if (region == IntPtr.Zero) return;

        if (SetWindowRgn(handle, region, true) == 0)
            DeleteObject(region);
    }

    /// <summary>
    /// Set a custom non-rectangular window region (e.g. text glyphs) from horizontal scanline spans.
    /// Used by frameless widgets in OS-level AcrylicBlur mode so DWM applies real-time blur strictly
    /// inside the glyph shapes, while keeping the surrounding desktop visible and click-through.
    /// </summary>
    public static void SetWindowRegionFromSpans(Window window, IReadOnlyList<(int Left, int Top, int Right, int Bottom)> rects)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null) return;
        if (rects == null || rects.Count == 0)
        {
            IntPtr emptyRgn = CreateRectRgn(0, 0, 0, 0);
            if (emptyRgn != IntPtr.Zero)
            {
                if (SetWindowRgn(handle.Value, emptyRgn, true) == 0)
                    DeleteObject(emptyRgn);
            }
            return;
        }

        const int rectSize = 16;
        const int headerSize = 32;
        int count = rects.Count;
        int totalSize = headerSize + count * rectSize;
        byte[] buffer = new byte[totalSize];

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        int offset = headerSize;
        for (int i = 0; i < count; i++)
        {
            var (l, t, r, b) = rects[i];
            if (l < minX) minX = l;
            if (t < minY) minY = t;
            if (r > maxX) maxX = r;
            if (b > maxY) maxY = b;

            BitConverter.GetBytes(l).CopyTo(buffer, offset + 0);
            BitConverter.GetBytes(t).CopyTo(buffer, offset + 4);
            BitConverter.GetBytes(r).CopyTo(buffer, offset + 8);
            BitConverter.GetBytes(b).CopyTo(buffer, offset + 12);
            offset += rectSize;
        }

        BitConverter.GetBytes(32).CopyTo(buffer, 0);       // dwSize
        BitConverter.GetBytes(1).CopyTo(buffer, 4);        // iType = RDH_RECTANGLES
        BitConverter.GetBytes((uint)count).CopyTo(buffer, 8); // nCount
        BitConverter.GetBytes((uint)(count * rectSize)).CopyTo(buffer, 12); // nRgnSize
        BitConverter.GetBytes(minX).CopyTo(buffer, 16);
        BitConverter.GetBytes(minY).CopyTo(buffer, 20);
        BitConverter.GetBytes(maxX).CopyTo(buffer, 24);
        BitConverter.GetBytes(maxY).CopyTo(buffer, 28);

        IntPtr rgn = ExtCreateRegion(IntPtr.Zero, (uint)totalSize, buffer);
        if (rgn != IntPtr.Zero)
        {
            if (SetWindowRgn(handle.Value, rgn, true) == 0)
                DeleteObject(rgn);
        }
    }

    /// <summary>
    /// Clip a native window to the <b>union</b> of several rounded rectangles.
    /// <para>
    /// The sidebar is one window holding a grid of separate cards, so unlike the desktop widget it
    /// has no single card rectangle to clip to. The OS-level acrylic backdrop covers the whole HWND,
    /// so without this the gaps between the cards — and the strip beside them — would frost over as
    /// well. OR-ing one region per card makes the DWM blur follow the cards exactly, exactly like
    /// <see cref="SetWidgetRegion"/> does for a single widget.
    /// </para>
    /// <para>
    /// A GDI region is 1-bit, so the anti-aliased curve of a card corner is truncated here. That
    /// trade-off is inherent to the acrylic path (see <c>Widget.ApplyWidgetRegion</c>), which is why
    /// it is applied <b>only</b> for <see cref="DeskCanvas.Core.Models.Settings.SurfaceStyle.Acrylic"/>
    /// — the rendered glass materials keep their per-pixel alpha.
    /// </para>
    /// </summary>
    /// <param name="window">The target window.</param>
    /// <param name="rects">One region per card: physical pixels relative to the window, plus radius.</param>
    public static void SetWindowRegionFromRoundedRects(
        Window window, IReadOnlyList<(int X, int Y, int Width, int Height, int Radius)> rects)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        if (rects.Count == 0)
        {
            // An empty sidebar has no card to show: clipping everything away keeps the acrylic
            // backdrop from covering the empty strip.
            SetWindowRegion(handle, 0, 0, 1, 1, 0);
            return;
        }

        var combined = CreateRectRgn(0, 0, 0, 0);
        if (combined == IntPtr.Zero) return;

        var built = false;
        try
        {
            foreach (var (x, y, width, height, radius) in rects)
            {
                var w = Math.Max(1, width);
                var h = Math.Max(1, height);
                var r = Math.Clamp(radius, 0, Math.Min(w, h) / 2);
                // Win32 GDI treats right/bottom as exclusive (see SetWindowRegion).
                var piece = r > 0
                    ? CreateRoundRectRgn(x, y, x + w + 1, y + h + 1, r * 2, r * 2)
                    : CreateRectRgn(x, y, x + w + 1, y + h + 1);
                if (piece == IntPtr.Zero) continue;

                CombineRgn(combined, combined, piece, RGN_OR);
                DeleteObject(piece);
            }
            built = true;
        }
        finally
        {
            // Nothing was handed over yet, so the accumulation region is still ours to free.
            if (!built) DeleteObject(combined);
        }

        // SetWindowRgn takes ownership of the region on success; on failure it stays ours.
        if (SetWindowRgn(handle, combined, true) == 0)
            DeleteObject(combined);
    }

    /// <summary>
    /// Remove a previously applied window region (full-window rectangle again).
    /// </summary>
    public static void ClearWidgetRegion(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null) return;
        SetWindowRgn(handle.Value, IntPtr.Zero, true);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr ExtCreateRegion(IntPtr lpXform, uint nCount, byte[] lpRgnData);

    /// <summary><c>RGN_OR</c> — combine two regions into their union.</summary>
    private const int RGN_OR = 2;

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int fnCombineMode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

    private static void SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        int error;
        IntPtr result;

        SetLastError(0);

        if (IntPtr.Size == 4)
        {
            var tempResult = IntSetWindowLong(hWnd, nIndex, IntPtrToInt32(dwNewLong));
            error = Marshal.GetLastWin32Error();
            result = new IntPtr(tempResult);
        }
        else
        {
            result = IntSetWindowLongPtr(hWnd, nIndex, dwNewLong);
            error = Marshal.GetLastWin32Error();
        }

        if (result == IntPtr.Zero && error != 0) throw new Win32Exception(error);
    }

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr IntSetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int IntSetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private static int IntPtrToInt32(IntPtr intPtr)
    {
        return unchecked((int)intPtr.ToInt64());
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>
    /// Position and size a native window in physical screen coordinates using Win32 SetWindowPos.
    /// Used by full-screen overlays (such as GridEditor) to guarantee placement on the target screen
    /// across mixed DPI monitors and negative desktop coordinates.
    /// </summary>
    public static void SetWindowPosition(Window window, int x, int y, int width, int height, bool topmost = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null || handle.Value == IntPtr.Zero) return;

        var insertAfter = topmost ? HWND_TOPMOST : IntPtr.Zero;
        var flags = SWP_SHOWWINDOW | SWP_FRAMECHANGED | (topmost ? 0u : SWP_NOZORDER);
        SetWindowPos(handle.Value, insertAfter, x, y, width, height, flags);
    }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError")]
    private static extern void SetLastError(int dwErrorCode);

    /// <summary>Value for <see cref="AllowSetForegroundWindow"/> meaning "any process".</summary>
    private const int ASFW_ANY = -1;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    /// <summary>
    /// Let other processes pull a window to the foreground.
    /// <para>
    /// Windows only lets the current foreground process reassign the foreground. A launch started
    /// by the shell (a second launch of the app) is still foreground, so it grants this before
    /// signalling the already running instance, which could otherwise only flash its taskbar
    /// button instead of coming to the front.
    /// </para>
    /// </summary>
    public static void AllowOtherProcessToTakeForeground()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { AllowSetForegroundWindow(ASFW_ANY); } catch { }
    }

    /// <summary>
    /// Raise a window to the foreground.
    /// <para>
    /// Deliberately does <b>not</b> call <c>ShowWindow</c>: restoring a window is the caller's job
    /// (it knows whether it was minimized), and <c>SW_RESTORE</c> here would give the window a
    /// second, competing placement path.
    /// </para>
    /// </summary>
    public static void BringToFront(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null || handle.Value == IntPtr.Zero) return;

        try { SetForegroundWindow(handle.Value); } catch { }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref uint value, int size);

    private const uint DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND = 1;

    /// <summary>
    /// Disables the default Windows 11 DWM outer window frame/border (e.g. for context menu popups)
    /// so the menu only renders its own single unified border and corner radius without a mismatched
    /// DWM outer stroke.
    /// </summary>
    public static void DisableWindowBorder(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            uint colorNone = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorNone, sizeof(uint));

            uint doNotRound = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref doNotRound, sizeof(uint));
        }
        catch { }
    }

    // ---------- Show-desktop (Win+D) observation ----------
    //
    // The desktop furniture has to be restored after Win+D. Intercepting SC_MINIMIZEALL used to be
    // done by replacing a widget window's GWLP_WNDPROC — which freezes an Avalonia (managed) window:
    // the slot then holds a CLR address, so forwarding through CallWindowProc drops every message
    // and Windows paints the busy ring. tests/SubclassProbe guards against that regression; the
    // replacement is this low-level keyboard hook, which never touches a window procedure.
    //
    // The hook is installed lazily on the first subscriber and is never allowed to swallow an event:
    // a low-level hook that returns non-zero would break Win+D (and typing) for the whole machine.

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_D = 0x44;

    private static readonly object KeyboardHookGate = new();
    private static IntPtr keyboardHookHandle = IntPtr.Zero;
    private static LowLevelKeyboardProc? keyboardHookProc;
    private static Action? showDesktopRequested;

    /// <summary>
    /// Register a callback raised when the user presses Win+D (the chord is only observed, never
    /// swallowed). Safe to call repeatedly; the hook itself is installed once.
    /// </summary>
    public static void OnShowDesktopRequested(Action callback)
    {
        lock (KeyboardHookGate)
        {
            showDesktopRequested += callback;
        }
        EnsureKeyboardHook();
    }

    private static void EnsureKeyboardHook()
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (KeyboardHookGate)
        {
            if (keyboardHookHandle != IntPtr.Zero) return;

            keyboardHookProc = KeyboardHookCallback;
            keyboardHookHandle = SetWindowsHookExW(WH_KEYBOARD_LL, keyboardHookProc, IntPtr.Zero, 0);

            // A refused install (UAC, a locked-down session, another hook owner) must not take the
            // app down: the feature is simply unavailable.
            if (keyboardHookHandle == IntPtr.Zero)
            {
                keyboardHookProc = null;
            }
        }
    }

    /// <summary>Test hook: the installed hook handle (installs it first when possible).</summary>
    public static IntPtr KeyboardHookInstalledForTest()
    {
        EnsureKeyboardHook();
        lock (KeyboardHookGate) return keyboardHookHandle;
    }

    /// <summary>
    /// Test hook: feed a synthetic key event through the chord logic and report whether it fired.
    /// </summary>
    /// <param name="message">One of WM_KEYDOWN/WM_KEYUP/WM_SYSKEYDOWN/WM_SYSKEYUP.</param>
    /// <param name="virtualKey">Virtual key code of the event.</param>
    /// <param name="winHeld">Whether the Win key is held.</param>
    public static bool RunKeyboardHookForTest(int message, int virtualKey, bool winHeld) =>
        HandleKeyboardEvent(message, virtualKey, winHeld);

    private static IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                var winHeld = IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN);
                HandleKeyboardEvent((int)wParam, (int)info.vkCode, winHeld);
            }
            catch
            {
                // Observing is best-effort; the chord must never be disturbed by our failure.
            }
        }

        // Never swallow: the OS must still perform the actual show-desktop.
        return CallNextHookEx(keyboardHookHandle, code, wParam, lParam);
    }

    /// <summary>
    /// The chord rule: Win+D fires the callbacks on the D key-up while Win is held. A bare D, or the
    /// Win key alone, must never fire (a false positive would pop every window back up while typing).
    /// </summary>
    private static bool HandleKeyboardEvent(int message, int virtualKey, bool winHeld)
    {
        if (virtualKey is VK_LWIN or VK_RWIN) return false;
        if (virtualKey != VK_D) return false;
        if (message is not (WM_KEYUP or WM_SYSKEYUP)) return false;
        if (!winHeld) return false;

        Action? callbacks;
        lock (KeyboardHookGate) callbacks = showDesktopRequested;

        try
        {
            callbacks?.Invoke();
        }
        catch
        {
            // A failing subscriber must not break the hook.
        }

        return true;
    }

    private static bool IsKeyDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
