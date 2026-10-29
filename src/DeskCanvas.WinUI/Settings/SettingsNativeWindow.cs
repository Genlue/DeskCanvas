using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskCanvas.WinUI.Settings;

/// <summary>
/// 设置窗口需要的少量 Win32 适配：按 DPI 设置初始客户区尺寸、最小化还原、把窗口拉到前台。
/// <para>
/// 只用 P/Invoke 而不用 <c>AppWindow.ResizeClient</c>：后者要吃 <c>Windows.Graphics.SizeInt32</c>，
/// 而本工程关闭了 Windows SDK winmd 引用（见 csproj 的 WinUISDKReferences=false），不引入
/// 额外的投影身份。物理像素 ↔ DIP 的换算在这里显式完成，WinUI 布局本身仍是 DIP（报告 §5 转换表）。
/// </para>
/// </summary>
internal static class SettingsNativeWindow
{
    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>窗口的 HWND；取不到时返回 <see cref="IntPtr.Zero"/>（不抛）。</summary>
    public static IntPtr GetHandle(Window window)
    {
        try
        {
            return WindowNative.GetWindowHandle(window);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 取窗口句柄失败：{ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// 把窗口的**客户区**调整到给定的 DIP 尺寸。

    /// <para>
    /// 用「外层矩形 - 客户区矩形」的差值反推边框 + 标题栏，而不是 <c>AdjustWindowRectEx</c>：
    /// 差值来自窗口自己的真实样式，不用再取 GWL_STYLE，也不会因 DPI 感知方式不同算错。

    /// </para>
    /// </summary>
    /// <summary>
    /// 把窗口客户区调整到给定 DIP 尺寸。<paramref name="scaleOverride"/> 大于 0 时直接用它
    /// （调用方拿得到 <c>XamlRoot.RasterizationScale</c>，那才是 WinUI 真正排版用的缩放）。

    /// </summary>
    public static uint ResizeClient(Window window, double widthDip, double heightDip, double scaleOverride = 0)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return GetDpi(window);

        var dpi = GetDpi(window);
        // 窗口刚创建、还没落到任何显示器上时 GetDpiForWindow 返回 96（Win32 默认值）。

        // 直接按 96 算会把 1000×650 DIP 当成 1000×650 物理像素：125% 缩放下窗口就小了一圈，
        // 而 WinUI 随后按真实缩放（1.25）排列内容，右边和底部会被裁掉。这里用系统 DPI 兜底。

        if (dpi == 96)
        {
            dpi = GetPrimaryMonitorDpi(hwnd);
        }

        var scale = scaleOverride > 0 ? scaleOverride : dpi / 96.0;
        var clientWidth = (int)Math.Round(widthDip * scale);
        var clientHeight = (int)Math.Round(heightDip * scale);
        if (clientWidth <= 0 || clientHeight <= 0) return dpi;

        var outer = default(Rect);
        var client = default(Rect);
        if (!GetWindowRect(hwnd, ref outer) || !GetClientRect(hwnd, ref client))
        {
            // 窗口还没建好真实矩形时退化为直接给外层尺寸：宁可差一圈边框，也不要异常。

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, clientWidth, clientHeight,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
            return dpi;
        }

        var borderWidth = (outer.Right - outer.Left) - (client.Right - client.Left);
        var borderHeight = (outer.Bottom - outer.Top) - (client.Bottom - client.Top);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, clientWidth + borderWidth, clientHeight + borderHeight,
            SwpNoMove | SwpNoZOrder | SwpNoActivate);
        return dpi;
    }

    /// <summary>
    /// 窗口还没落到显示器上时（<see cref="GetDpiForWindow"/> 返回 96 默认值）按主显示器 DPI 估算：
    /// 设置窗口首次出现时会被 <see cref="CenterOnWorkArea"/> 放到主显示器工作区，用它的缩放最接近真实值。

    /// 系统 DPI 可能被"按显示器缩放"覆盖成 96，不能直接当主显示器 DPI 用。

    /// </summary>
    private static uint GetPrimaryMonitorDpi(IntPtr hwnd)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToPrimary);
            if (monitor != IntPtr.Zero
                && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0
                && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Settings] 读取主显示器 DPI 失败：{ex.Message}");
        }

        return GetSystemDpi();
    }

    /// <summary>系统 DPI（PerMonitorV2 进程里等于当前系统缩放对应的 DPI）。</summary>
    private static uint GetSystemDpi()
    {
        try
        {
            var dpi = GetDpiForSystem();
            return dpi == 0 ? 96u : dpi;
        }
        catch (Exception)
        {
            return 96;
        }
    }

    /// <summary>
    /// 首次显示时把窗口居中到它所在监视器的工作区。

    /// <para>
    /// 旧 <c>Settings.axaml</c> 用 <c>WindowStartupLocation="CenterScreen"</c> 只定位一次，之后改 Manual
    /// 由用户摆放的位置说了算（否则每次唤出都跳回屏幕中央）。这里只在创建时调用一次，
    /// 用 Win32 的工作区而不是整屏：任务栏/停靠区不会被盖住。

    /// </para>
    /// </summary>
    public static void CenterOnWorkArea(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        var rect = default(Rect);
        if (!GetWindowRect(hwnd, ref rect)) return;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        var x = info.WorkArea.Left + ((info.WorkArea.Right - info.WorkArea.Left) - width) / 2;
        var y = info.WorkArea.Top + ((info.WorkArea.Bottom - info.WorkArea.Top) - height) / 2;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>当前客户区大小（物理像素）；取不到时返回 (0,0)。</summary>
    public static (int Width, int Height) GetClientSize(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return (0, 0);

        var client = default(Rect);
        if (!GetClientRect(hwnd, ref client)) return (0, 0);
        return (client.Right - client.Left, client.Bottom - client.Top);
    }

    /// <summary>
    /// 最小化还原。最小化的 Win32 窗口停在 (-32000,-32000)，不先还原就"显示"等于把窗口显示到屏幕外
    /// （旧 <c>Settings.ShowAndActivate</c> 同样先还原再 Show）。

    /// </summary>
    public static void RestoreIfMinimized(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }
    }

    /// <summary>
    /// 把窗口真正拉到前台。最小化的窗口停在 (-32000,-32000)，必须先还原，否则「显示」等于显示到屏幕外。

    /// </summary>
    public static void BringToFront(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return;

        if (IsIconic(hwnd))
        {
            ShowWindow(hwnd, SwRestore);
        }

        // SetForegroundWindow 会受前台锁限制；失败不是错误，Window.Activate/AppWindow.Show 已经
        // 把窗口显示出来了，这里只是尽力而为（与旧 InteropService.BringToFront 同一层级）。

        SetForegroundWindow(hwnd);
    }

    /// <summary>窗口当前所在监视器的 DPI；取不到按 96 处理。</summary>
    public static uint GetDpi(Window window)
    {
        var hwnd = GetHandle(window);
        if (hwnd == IntPtr.Zero) return 96;

        try
        {
            var dpi = GetDpiForWindow(hwnd);
            return dpi == 0 ? 96u : dpi;
        }
        catch (Exception)
        {
            return 96;
        }
    }

    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint MonitorDefaultToPrimary = 0x00000001;
    private const int MdtEffectiveDpi = 0;

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, ref Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, ref Rect rect);

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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("Shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
