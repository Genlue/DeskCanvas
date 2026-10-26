using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// Every Win32 entry point the glass capture layer needs, in one place.
/// <para>
/// The capture layer deliberately does not reuse <c>DesktopCapturer</c>'s private imports: that
/// class is the legacy GDI path and is kept intact as the fallback, so the two must not share
/// mutable state (last-error latches, DPI contexts) through a common helper.
/// </para>
/// </summary>
internal static class NativeMethods
{
    // ---- virtual desktop metrics (physical pixels; the process is per-monitor DPI aware) ----
    internal const int SmXVirtualScreen = 76;
    internal const int SmYVirtualScreen = 77;
    internal const int SmCxVirtualScreen = 78;
    internal const int SmCyVirtualScreen = 79;

    /// <summary><c>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2</c>.</summary>
    internal static readonly IntPtr DpiAwarenessPerMonitorV2 = new(-4);

    internal const uint GwHwndNext = 0x0002;
    internal const uint GwHwndPrev = 0x0003;

    internal const uint SrcCopy = 0x00CC0020;
    internal const uint PwRenderFullContent = 0x00000002;

    internal const uint WdaNone = 0x00000000;
    internal const uint WdaExcludeFromCapture = 0x00000011;

    internal const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    internal delegate bool EnumWindowsProc(IntPtr window, IntPtr param);

    [DllImport("user32.dll")] internal static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")] internal static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    internal const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X, Y;

        public Point(int x, int y) { X = x; Y = y; }
    }

    [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr param);

    [DllImport("user32.dll")] internal static extern IntPtr GetWindow(IntPtr window, uint command);

    [DllImport("user32.dll")] internal static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")] internal static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out WindowRect rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr window, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

    // ---- GDI (region-limited fallback when WGC is unavailable) ----

    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")] internal static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll", SetLastError = true)] internal static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")] internal static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")] internal static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, IntPtr bits, ref BitmapInfo info, uint usage);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public int Colors;
    }

    /// <summary>Top-down 32bpp header: rows read straight into an <c>SKBitmap</c>'s pixel buffer.</summary>
    internal static BitmapInfo TopDownHeader(int width, int height) => new()
    {
        Header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = 0
        }
    };

    internal static string ClassNameOf(IntPtr window)
    {
        if (window == IntPtr.Zero) return string.Empty;
        var buffer = new StringBuilder(64);
        return GetClassName(window, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static bool IsCloaked(IntPtr window) =>
        DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
}
