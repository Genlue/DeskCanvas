using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>一个显示器（物理像素）：整屏矩形、工作区、是否主屏、有效 DPI。</summary>
internal readonly record struct EditorMonitor(
    int X, int Y, int Width, int Height,
    int WorkX, int WorkY, int WorkWidth, int WorkHeight,
    bool IsPrimary, uint Dpi)
{
    /// <summary>
    /// 旧的窗口几何记忆槽位里的显示器签名，与 Avalonia 的 <c>EditWidget.ScreenId</c> 同一格式。
    /// 主屏写 <c>primary</c>，其余写 <c>"WxH@X,Y"</c>（整屏边界，不是工作区）。
    /// 这样旧的 ui-state.json 条目仍能命中。
    /// </summary>
    public string Id => IsPrimary ? "primary" : $"{Width}x{Height}@{X},{Y}";
}

/// <summary>
/// 编辑窗口需要的一点点 Win32 适配：窗口客户区尺寸（DIP↔物理像素）、工作区定位、显示器签名。
/// <para>
/// 与 <c>Settings/SettingsNativeWindow</c> 同一套做法（P/Invoke + 显式 DPI 换算），但这里不复用
/// 那个类型：设置窗口执行者也在改自己的文件，编辑窗口独立一份原生适配可以并行推进、互不阻塞。
/// 只用 Win32 API，不引入 <c>Windows.Graphics.*</c> 投影（本工程关闭了 WinSDK winmd 引用）。
/// </para>
/// </summary>
internal static class EditorWindowNative
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int SwRestore = 9;
    private const int MdtEffectiveDpi = 0;

    /// <summary>窗口句柄；取不到返回 <see cref="IntPtr.Zero"/>（不抛）。</summary>
    public static IntPtr GetHandle(Window window)
    {
        try
        {
            return WindowNative.GetWindowHandle(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Editor] 取窗口句柄失败：{ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>客户区大小（物理像素）。</summary>
    public static (int Width, int Height) GetClientSize(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return (0, 0);
        return GetClientRect(hwnd, out var client) ? (client.Right - client.Left, client.Bottom - client.Top) : (0, 0);
    }

    /// <summary>外框矩形（物理像素，屏幕坐标）。</summary>
    public static (int X, int Y, int Width, int Height) GetOuterRect(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return (0, 0, 0, 0);
        if (!GetWindowRect(hwnd, out var rect)) return (0, 0, 0, 0);
        return (rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>窗口当前 DPI；取不到返回 0（调用方回退到显示器 DPI/96）。</summary>
    public static uint GetWindowDpi(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        try
        {
            return GetDpiForWindow(hwnd);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// 把客户区设为 <paramref name="widthDip"/>×<paramref name="heightDip"/>，外框中心放在
    /// (<paramref name="centerX"/>,<paramref name="centerY"/>)（物理像素），并夹在显示器工作区内。
    /// 负坐标显示器保持合法负坐标（与旧 EditWidget 的救援规则一致）。
    /// </summary>
    public static void ApplyClientGeometry(IntPtr hwnd, double widthDip, double heightDip,
        int centerX, int centerY, double scale, EditorMonitor monitor)
    {
        if (hwnd == IntPtr.Zero) return;
        if (!double.IsFinite(scale) || scale <= 0) scale = 1.0;

        var clientWidth = Math.Max(1, (int)Math.Round(widthDip * scale));
        var clientHeight = Math.Max(1, (int)Math.Round(heightDip * scale));

        // 用窗口自己的「外框 - 客户区」差值反推边框 + 标题栏，避免 AdjustWindowRectEx 的 DPI 感知方式偏差。
        var outerNow = GetOuterRect(hwnd);
        var clientNow = GetClientSize(hwnd);
        var borderWidth = Math.Max(0, outerNow.Width - clientNow.Width);
        var borderHeight = Math.Max(0, outerNow.Height - clientNow.Height);

        var outerWidth = clientWidth + borderWidth;
        var outerHeight = clientHeight + borderHeight;

        var x = centerX - outerWidth / 2;
        var y = centerY - outerHeight / 2;
        x = Math.Clamp(x, monitor.WorkX, Math.Max(monitor.WorkX, monitor.WorkX + monitor.WorkWidth - outerWidth));
        y = Math.Clamp(y, monitor.WorkY, Math.Max(monitor.WorkY, monitor.WorkY + monitor.WorkHeight - outerHeight));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, outerWidth, outerHeight, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>只移动外框（尺寸不变），用于按真实 DPI 校正尺寸后保持原位置。</summary>
    public static void MoveTo(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>把窗口恢复（最小化时）并拉到前台。</summary>
    public static void BringToFront(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
    }

    /// <summary>窗口是否最小化（最小化时保存几何会写入 (-32000,-32000)，旧实现直接跳过）。</summary>
    public static bool IsMinimized(IntPtr hwnd) => hwnd != IntPtr.Zero && IsIconic(hwnd);

    // ---- 显示 ------------------------------------------------------------------------------

    /// <summary>枚举全部显示器。失败时返回空列表（调用方回退 96 DPI/单屏假设）。</summary>
    public static IReadOnlyList<EditorMonitor> EnumerateMonitors()
    {
        var list = new List<EditorMonitor>();
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                if (TryDescribe(monitor, out var described)) list.Add(described);
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Editor] 枚举显示器失败：{ex.Message}");
        }

        return list;
    }

    /// <summary>主显示器；枚举失败时返回一个以 0,0 为原点、96 DPI 的保守值。</summary>
    public static EditorMonitor PrimaryMonitor()
    {
        foreach (var monitor in EnumerateMonitors())
        {
            if (monitor.IsPrimary) return monitor;
        }

        var all = EnumerateMonitors();
        if (all.Count > 0) return all[0];
        return new EditorMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1040, true, 96);
    }

    /// <summary>按旧几何条目的显示器签名找显示器；找不到返回 null（调用方回退主屏）。</summary>
    public static EditorMonitor? FindMonitor(string? monitorId)
    {
        if (string.IsNullOrWhiteSpace(monitorId)) return null;

        var monitors = EnumerateMonitors();
        if (string.Equals(monitorId, "primary", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var monitor in monitors)
            {
                if (monitor.IsPrimary) return monitor;
            }

            return monitors.Count > 0 ? monitors[0] : null;
        }

        // "WxH@X,Y"（旧 Avalonia 的 ScreenId 的格式，使用整屏边界）。
        var at = monitorId.IndexOf('@');
        if (at <= 0) return null;
        var size = monitorId[..at];
        var position = monitorId[(at + 1)..];
        var xSplit = size.IndexOf('x');
        var comma = position.IndexOf(',');
        if (xSplit <= 0 || comma <= 0) return null;

        if (!int.TryParse(size[..xSplit], out var width)
            || !int.TryParse(size[(xSplit + 1)..], out var height)
            || !int.TryParse(position[..comma], out var x)
            || !int.TryParse(position[(comma + 1)..], out var y))
        {
            return null;
        }

        foreach (var monitor in monitors)
        {
            if (monitor.Width == width && monitor.Height == height && monitor.X == x && monitor.Y == y)
                return monitor;
        }

        return null;
    }

    /// <summary>窗口当前所在显示器（取不到时回退主屏）。</summary>
    public static EditorMonitor MonitorOf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return PrimaryMonitor();

        var handle = MonitorFromWindow(hwnd, 0 /* MONITOR_DEFAULTTONEAREST */);
        if (handle != IntPtr.Zero && TryDescribe(handle, out var monitor)) return monitor;

        return PrimaryMonitor();
    }

    private static bool TryDescribe(IntPtr monitor, out EditorMonitor described)
    {
        described = default;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        var dpi = 96u;
        try
        {
            if (GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
                dpi = dpiX;
        }
        catch (Exception)
        {
            // Shcore 不可用时按 96 处理（与设置窗口的兜底一致）。
        }

        var bounds = info.Monitor;
        var work = info.WorkArea;
        described = new EditorMonitor(
            bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top,
            (info.Flags & 1) != 0, dpi);
        return true;
    }

    // ---- native -----------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect WorkArea;
        public uint Flags;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
