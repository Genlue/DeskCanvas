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
    /// <para>
    /// This is the mechanism, not the policy: it is expensive (a compacting gen-2 collection) and
    /// a working-set trim only evicts resident pages that get touched again, so it must not run on
    /// a timer. Call it through <see cref="MemoryTrimmerService.RequestTrim"/>, which applies the
    /// single-flight / cooldown / hysteresis guards and keeps it off the UI thread.
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

    /// <summary>
    /// Working-set trim only — the cheap half of <see cref="TrimProcessMemory"/>.
    /// <para>
    /// <b>What it is for.</b> The periodic release path (see
    /// <c>MemoryTrimmerService</c>) wants to hand resident pages back on a short cadence, and a
    /// compacting gen-2 collection is far too expensive to run at that cadence: it walks the whole
    /// live heap and moves objects, which is tens of milliseconds of stalls on a desktop-widget
    /// host. This variant costs a single kernel call.
    /// </para>
    /// <para>
    /// <b>What it cannot do.</b> It cannot reduce <c>PrivateMemorySize64</c> — the trim only
    /// evicts resident pages to the page file, so committed memory is unchanged and the pages are
    /// faulted back in as soon as the widgets render again. Measured on this app: a trim took the
    /// working set from 1260 MiB to 27 MiB while private bytes stayed at exactly 611 MiB. That is
    /// why it is a presentation knob, not a fix, and why the caller decides whether to pay for the
    /// full collection based on whether the heap actually grew.
    /// </para>
    /// </summary>
    public static void TrimWorkingSet()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
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

        if (handle == null || !IsWindow(handle.Value)) return;

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
    /// <returns>True when the region was set on the window; false when there is no handle
    /// yet (the window has not been created) or the OS rejected it — callers must retry
    /// instead of assuming the clip is in place.</returns>
    public static bool SetWidgetRegion(Window window, int x, int y, int width, int height, int radius)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null) return false;
        return SetWindowRegion(handle.Value, x, y, width, height, radius);
    }

    /// <summary>
    /// Clip any native Win32 window (such as a PopupRoot) to a rounded rectangle region.
    /// This forces the OS-level DWM AcrylicBlur to strictly follow the exact same rounded
    /// rectangle boundary without bleeding blur into the corner areas.
    /// </summary>
    /// <returns>True when the region was applied; false for an invalid handle or when
    /// <c>SetWindowRgn</c> rejected the region.</returns>
    public static bool SetWindowRegion(IntPtr handle, int x, int y, int width, int height, int radius)
    {
        if (handle == IntPtr.Zero) return false;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);

        // Win32 GDI CreateRoundRectRgn treats right/bottom as exclusive.
        // [x, x + width) covers exactly 'width' pixels without 1-pixel bleed.
        //
        // GDI quirk (measured on Windows 11 22631 at 125 % DPI, uniform for every radius
        // 1…30 and every size 10…15 px): CreateRoundRectRgn *drops the last row and column*
        // of the requested rect — asking for (15,15)-(390,390) yields a region whose
        // GetRgnBox is (15,15)-(389,389) (374×374) and PtInRegion(300,389) is false.
        // CreateRectRgn does NOT do this. The consequence was the missing bottom rim:
        // ApplyWidgetRegion asks for exactly the card rect, the card's last device row holds
        // the 1 px specular hairline, and the OS clipped that row away — only the corner arcs
        // (which curve away from the last row) survived. Compensate by asking for one extra
        // row/column; the reported box then equals the card exactly (375×375).
        var region = radius > 0
            ? CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, radius * 2, radius * 2)
            : CreateRectRgn(x, y, x + width, y + height);
        if (region == IntPtr.Zero) return false;

        if (SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Clip a native window to a rounded rectangle <b>unioned with a set of circles</b> — the
    /// 分类圆钮 that stand beside the 聚焦搜索 bar while Alt is held.
    /// <para>
    /// 毛玻璃 paints its OS backdrop strictly inside the window region, so a region that only
    /// described the card would delete the circles from the screen, and one that described the whole
    /// window would leave a blurred slab behind them. The union is the only shape that matches what
    /// the layout actually draws.
    /// </para>
    /// </summary>
    /// <returns>True when the region was applied; false for an invalid handle or a rejected region.</returns>
    public static bool SetWindowRegionUnion(
        IntPtr handle,
        int x,
        int y,
        int width,
        int height,
        int radius,
        IReadOnlyList<(int X, int Y, int Size)> circles)
    {
        if (handle == IntPtr.Zero) return false;

        width = Math.Max(1, width);
        height = Math.Max(1, height);
        radius = Math.Clamp(radius, 0, Math.Min(width, height) / 2);

        // Same +1 compensation as SetWindowRegion: CreateRoundRectRgn drops the last row/column.
        var region = radius > 0
            ? CreateRoundRectRgn(x, y, x + width + 1, y + height + 1, radius * 2, radius * 2)
            : CreateRectRgn(x, y, x + width, y + height);
        if (region == IntPtr.Zero) return false;

        foreach (var circle in circles)
        {
            var size = Math.Max(1, circle.Size);
            var disc = CreateEllipticRgn(circle.X, circle.Y, circle.X + size + 1, circle.Y + size + 1);
            if (disc == IntPtr.Zero) continue;

            CombineRgn(region, region, disc, RGN_OR);
            DeleteObject(disc);
        }

        if (SetWindowRgn(handle, region, true) == 0)
        {
            DeleteObject(region);
            return false;
        }

        return true;
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
    /// Remove a previously applied window region (full-window rectangle again).
    /// </summary>
    public static void ClearWindowRegion(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        SetWindowRgn(handle, IntPtr.Zero, true);
    }

    /// <summary>
    /// Remove a previously applied window region (full-window rectangle again).
    /// </summary>
    public static void ClearWidgetRegion(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null) return;
        ClearWindowRegion(handle.Value);
    }

    [DllImport("gdi32.dll")]
    private static extern IntPtr ExtCreateRegion(IntPtr lpXform, uint nCount, byte[] lpRgnData);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int x1, int y1, int x2, int y2, int cx, int cy);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int x1, int y1, int x2, int y2);

    /// <summary>CombineRgn's OR mode: dest := dest ∪ src.</summary>
    private const int RGN_OR = 2;

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr hrgnDst, IntPtr hrgnSrc1, IntPtr hrgnSrc2, int iMode);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

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

    private const uint DWMWA_TRANSITIONS_FORCEDISABLED = 3;
    private const uint DWMWA_BORDER_COLOR = 34;
    private const uint DWMWA_CAPTION_COLOR = 35;
    private const uint DWMWA_TEXT_COLOR = 36;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWCP_DONOTROUND = 1;

    /// <summary>
    /// Disables the default Windows 11 DWM outer window frame/border (e.g. for context menu popups)
    /// so the menu only renders its own single unified border and corner radius without a mismatched
    /// DWM outer stroke.
    /// </summary>
    /// <summary>
    /// Disables Windows DWM native window open/close transition animations for the given HWND
    /// so the application's own compositor-driven transitions can run smoothly without running
    /// consecutively or conflicting with the OS window animation.
    /// </summary>
    public static void DisableWindowTransitions(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        try
        {
            uint disable = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_TRANSITIONS_FORCEDISABLED, ref disable, sizeof(uint));
        }
        catch { }
    }

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

    /// <summary>
    /// Hides the DWM-drawn native caption (title text + opaque background band) while keeping the
    /// system caption buttons. Used when a window extends its client area but stays in the native
    /// title-bar mode: DWM would otherwise fill the top strip with an opaque caption background
    /// and the window title text, drawing a hard opaque band over the acrylic window.
    /// <para>
    /// DWMWA_COLOR_NONE requires Windows 11 (build 22000+); on older systems the calls fail with
    /// a non-zero HRESULT and are silently ignored — the caption keeps its default look.
    /// </para>
    /// </summary>
    public static void HideNativeCaption(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle;
        if (handle == null || handle.Value == IntPtr.Zero) return;

        try
        {
            uint none = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(handle.Value, DWMWA_CAPTION_COLOR, ref none, sizeof(uint));
            DwmSetWindowAttribute(handle.Value, DWMWA_TEXT_COLOR, ref none, sizeof(uint));
        }
        catch { }
    }
}