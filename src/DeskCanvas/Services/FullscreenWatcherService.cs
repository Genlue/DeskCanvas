using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace DeskCanvas.Services;

/// <summary>A screen or window rectangle in physical pixels.</summary>
public readonly record struct ScreenRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

/// <summary>One attached screen: its full rectangle and its work area (taskbar excluded).</summary>
public readonly record struct ScreenCoverage(ScreenRect Bounds, ScreenRect WorkArea);

/// <summary>One top-level window, reduced to what the coverage rule needs.</summary>
public readonly record struct WindowCoverage(
    bool IsOwnProcess,
    bool IsShellWindow,
    bool IsMinimized,
    bool IsVisible,
    bool IsMaximized,
    ScreenRect Bounds,
    bool IsForeground = false);

/// <summary>
/// Watches for the desktop being fully covered — a fullscreen game, a video player or simply a
/// <b>maximized</b> window on every attached screen — so the host can pause the shared widget
/// timers and release the material caches while the user cannot see the widgets anyway.
/// <para>
/// The widgets themselves are <b>not</b> hidden: they live at the bottom of the z-order, so a
/// covered screen already hides them, and hiding/reshowing every window on each transition was
/// what made them flicker back in late after leaving a game. Only the memory that nothing on
/// screen needs is released.
/// </para>
/// <para>
/// Multi-screen: the state is per screen and the desktop counts as covered only when
/// <b>every</b> attached screen is covered. A maximized game on the primary monitor must not
/// stop the widgets on the secondary one from ticking.
/// </para>
/// <para>
/// Detection combines signals, because none of them is sufficient alone:
/// <list type="bullet">
/// <item><see cref="SHQueryUserNotificationState"/> reports <c>QUNS_RUNNING_D3D_FULL_SCREEN</c>
/// / <c>QUNS_PRESENTATION_MODE</c> for exclusive-fullscreen and presentation modes (which by
/// definition cover every screen), but many borderless-fullscreen games report the ordinary
/// "accepts notifications" state.</item>
/// <item><see cref="IsZoomed"/> marks a maximized window, which covers its own monitor. Desktop
/// widgets live at the bottom of the z-order, so a maximized window hides every one of them just
/// as effectively as a fullscreen one. The window's rectangle (its work area) identifies which
/// monitor it belongs to, so no DPI math is involved.</item>
/// <item>Geometry, for borderless windows that fill a screen without the <c>WS_MAXIMIZE</c>
/// style: the window rectangle must cover at least 98% of a screen's full rectangle (true
/// fullscreen) or of its work area (custom chrome maximized).</item>
/// </list>
/// </para>
/// <para>
/// Both sides of every geometric comparison must live in the SAME coordinate space: DWM reports
/// physical pixels, while <c>GetWindowRect</c> / <c>GetMonitorInfo</c> report DPI-virtualized
/// coordinates for a DPI-unaware caller. Mixing them made a normal window look like it extended
/// past its own monitor ((480,142)-(2538,1306) on a 2048×1152 screen). The candidate rectangles
/// here are always DWM extended frame bounds (physical), compared against Avalonia's physical
/// screen rectangles.
/// </para>
/// <para>
/// Our own windows, the shell (desktop / taskbar), minimized, invisible and DWM-cloaked windows
/// are excluded, and entering the covered state requires two consecutive positive polls so a
/// transient window cannot pause the widgets by accident. Leaving takes effect immediately.
/// </para>
/// </summary>
public class FullscreenWatcherService : IDisposable
{
    /// <summary>Poll interval; the check is a few Win32 calls, far cheaper than a frame.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1200);

    /// <summary>Consecutive positive polls required before reporting the covered state.</summary>
    private const int EnterThreshold = 2;

    /// <summary>
    /// Startup grace period. The widgets are created a moment before the first poll, and their
    /// first background passes (icon extraction, the first monitor sample, the first liquid glass
    /// frame) take a few seconds. Suspending inside that window froze widgets on their empty
    /// initial state — dials with no reading, numerals on the flat fallback wash — which reads as
    /// "the widget stopped working". Nothing is gained by suspending that early.
    /// </summary>
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(15);

    /// <summary>Fraction of a screen a borderless window must cover to count as maximized.</summary>
    private const double CoverageThreshold = 0.98;

    private DispatcherTimer? poller;
    private int consecutiveHits;
    private Window? anchor;
    private DateTime attachedAt;

    /// <summary>
    /// Raised with <c>true</c> when every attached screen is covered by a fullscreen or maximized
    /// application, <c>false</c> as soon as one screen is visible again.
    /// </summary>
    public event EventHandler<bool>? FullscreenChanged;

    /// <summary>Whether every attached screen is currently covered.</summary>
    public bool IsFullscreen { get; private set; }

    /// <summary>Start watching. Any window can serve as the dispatcher anchor.</summary>
    public void Attach(Window anchor)
    {
        this.anchor ??= anchor;
        if (attachedAt == default) attachedAt = DateTime.UtcNow;
        if (poller != null) return;
        poller = new DispatcherTimer(PollInterval, DispatcherPriority.Background, (_, _) => Evaluate());
        poller.Start();
    }

    /// <summary>Run one detection pass and raise <see cref="FullscreenChanged"/> on a change.</summary>
    public void Evaluate()
    {
        if (!OperatingSystem.IsWindows()) return;

        // Give the widgets time to draw and sample once before anything may suspend them.
        if (attachedAt != default && DateTime.UtcNow - attachedAt < StartupGrace)
        {
            Report(false);
            return;
        }

        Report(ObserveDesktop());
    }

    /// <summary>Feed a detection result through the debounce and raise the change event.</summary>
    private void Report(bool covered)
    {
        if (covered)
        {
            // Debounce: a window that only appears for a moment (splash screens,
            // alt-tab previews) must not pause the widgets.
            if (!IsFullscreen && ++consecutiveHits < EnterThreshold) return;
        }
        else
        {
            consecutiveHits = 0;
            if (!IsFullscreen) return;
        }

        if (covered == IsFullscreen) return;

        IsFullscreen = covered;
        try
        {
            FullscreenChanged?.Invoke(this, covered);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FullscreenWatcher] handler failed: {ex.Message}");
        }
    }

    // ---------- Observation (Win32) ----------

    private bool ObserveDesktop()
    {
        try
        {
            var screens = AttachedScreens();
            if (screens.Count == 0) return false;

            // Exclusive fullscreen / presentation mode are unambiguous, and they cover
            // every attached screen.
            if (SHQueryUserNotificationState(out var state) == 0 &&
                (state == QUNS_RUNNING_D3D_FULL_SCREEN || state == QUNS_PRESENTATION_MODE))
            {
                return true;
            }

            return AllScreensCovered(screens, EnumerateCandidates());
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FullscreenWatcher] detection failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>The attached screens, in physical pixels (Avalonia reports physical rects).</summary>
    private IReadOnlyList<ScreenCoverage> AttachedScreens()
    {
        if (anchor?.Screens.All is not { Count: > 0 } all) return [];
        var screens = new List<ScreenCoverage>(all.Count);
        foreach (var screen in all)
            screens.Add(new ScreenCoverage(ToRect(screen.Bounds), ToRect(screen.WorkingArea)));
        return screens;
    }

    /// <summary>
    /// Every top-level window that could cover a screen, already reduced to the flags the
    /// decision rule needs (own windows, the shell, minimized, invisible and cloaked windows
    /// are dropped here, so the pure rule can be exercised in tests with any combination).
    /// </summary>
    private static List<WindowCoverage> EnumerateCandidates()
    {
        var candidates = new List<WindowCoverage>();
        var foreground = GetForegroundWindow();
        EnumWindows((hwnd, _) =>
        {
            var className = GetClassNameString(hwnd);
            var isShell = IsShellClassName(className);
            var isVisible = IsWindowVisible(hwnd);
            var isMinimized = IsIconic(hwnd);
            var isMaximized = IsZoomed(hwnd);
            var isForeground = hwnd == foreground;

            GetWindowThreadProcessId(hwnd, out var pid);
            var isOwn = pid == Environment.ProcessId;

            if (isShell || isOwn || isMinimized || !isVisible || IsCloaked(hwnd)) return true;
            if (IsDesktopOverlay(hwnd)) return true;
            if (!TryGetExtendedFrameBounds(hwnd, out var bounds)) return true;

            var rect = ToRect(bounds);
            if (rect.Width <= 0 || rect.Height <= 0) return true;

            candidates.Add(new WindowCoverage(isOwn, isShell, isMinimized, isVisible, isMaximized, rect, isForeground));
            return true;
        }, IntPtr.Zero);

        return candidates;
    }

    private static bool IsShellWindow(IntPtr hwnd) => IsShellClassName(GetClassNameString(hwnd));

    private static bool IsShellClassName(string className) => className is
        "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Windows.UI.Core.CoreWindow";

    /// <summary>
    /// A desktop overlay — a dock, a live-wallpaper host, a game/GPU overlay, a HUD — rather than
    /// an application that actually covers the desktop.
    /// <para>
    /// These span the screen and stay visible, so the geometry rule would call the desktop
    /// "covered" for as long as the shell tooling runs, which froze every widget while the desktop
    /// was in plain sight. The signature is deliberately narrow — layered <i>and</i> either
    /// click-through or a tool window — so a borderless fullscreen game, which is neither, is
    /// still detected.
    /// </para>
    /// </summary>
    private static bool IsDesktopOverlay(IntPtr hwnd)
    {
        if (IsZoomed(hwnd)) return false;

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        var layered = (exStyle & WS_EX_LAYERED) != 0 || (exStyle & WS_EX_TRANSPARENT) != 0;
        if (!layered) return false;

        return (exStyle & WS_EX_NOACTIVATE) != 0 || (exStyle & WS_EX_TOOLWINDOW) != 0;
    }

    /// <summary>
    /// DWM-cloaked windows (suspended UWP apps, windows on another virtual desktop) keep their
    /// geometry but are not on screen; they must not count as covering a monitor.
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttributeInt(hwnd, DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    // ---------- Decision (pure, testable) ----------

    /// <summary>
    /// Pure decision function: is <b>every</b> attached screen covered by some foreground-style
    /// window? Kept free of Win32 so the whole rule set can be verified without a live desktop
    /// (see <c>tests/FullscreenChecks</c>).
    /// </summary>
    /// <param name="screens">Attached screens (full bounds + work area), physical pixels.</param>
    /// <param name="windows">Candidate top-level windows in the same coordinate space.</param>
    public static bool AllScreensCovered(
        IReadOnlyList<ScreenCoverage> screens,
        IReadOnlyList<WindowCoverage> windows)
    {
        if (screens.Count == 0) return false;

        foreach (var screen in screens)
        {
            if (!IsScreenCovered(screen, windows)) return false;
        }

        return true;
    }

    /// <summary>Is this one screen covered by any of the candidate windows?</summary>
    private static bool IsScreenCovered(ScreenCoverage screen, IReadOnlyList<WindowCoverage> windows)
    {
        foreach (var window in windows)
        {
            if (window.IsOwnProcess || window.IsShellWindow || window.IsMinimized || !window.IsVisible)
                continue;

            // A maximized window covers exactly one monitor: its rectangle is that monitor's
            // work area (or its full rectangle for borderless chrome), and its centre falls
            // inside it. Both tests are geometry-only, so no DPI mixing is possible.
            if (window.IsMaximized)
            {
                if (Covers(window.Bounds, screen.WorkArea) || Covers(window.Bounds, screen.Bounds))
                    return true;
                if (Contains(screen.Bounds, Center(window.Bounds)))
                    return true;
                continue;
            }

            // Geometry alone is not proof: screen-sized windows that nobody is looking at —
            // the Windows input-experience host, an NVIDIA/Steam overlay, a wallpaper engine
            // host — otherwise pin the whole desktop as "covered" forever and every widget
            // freezes while sitting in plain sight. A window that fills the screen without being
            // maximized only counts while it is the one the user is actually in (a borderless
            // fullscreen game or a custom-chrome video player is always the foreground window).
            if (!window.IsForeground) continue;

            if (Covers(window.Bounds, screen.Bounds) || Covers(window.Bounds, screen.WorkArea))
                return true;
        }

        return false;
    }

    /// <summary>Does the window rectangle cover at least 98% of the screen rectangle (both axes)?</summary>
    private static bool Covers(ScreenRect window, ScreenRect screen)
    {
        if (screen.Width <= 0 || screen.Height <= 0) return false;

        var overlapWidth = Math.Min(window.Right, screen.Right) - Math.Max(window.Left, screen.Left);
        var overlapHeight = Math.Min(window.Bottom, screen.Bottom) - Math.Max(window.Top, screen.Top);
        if (overlapWidth <= 0 || overlapHeight <= 0) return false;

        return overlapWidth >= screen.Width * CoverageThreshold &&
               overlapHeight >= screen.Height * CoverageThreshold;
    }

    private static bool Contains(ScreenRect outer, (double X, double Y) point) =>
        point.X >= outer.Left && point.X < outer.Right &&
        point.Y >= outer.Top && point.Y < outer.Bottom;

    private static (double X, double Y) Center(ScreenRect rect) =>
        (rect.Left + rect.Width / 2.0, rect.Top + rect.Height / 2.0);

    // ---------- Win32 ----------

    private const uint QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const uint QUNS_PRESENTATION_MODE = 4;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const uint DWMWA_CLOAKED = 14;

    /// <summary>Extended window styles used to recognise desktop overlays (see IsDesktopOverlay).</summary>
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct NATIVE_RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public NATIVE_RECT rcMonitor;
        public NATIVE_RECT rcWork;
        public uint dwFlags;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extraData);

    private static ScreenRect ToRect(NATIVE_RECT rect) => new(rect.Left, rect.Top, rect.Right, rect.Bottom);

    private static ScreenRect ToRect(PixelRect rect) => new(rect.X, rect.Y, rect.Right, rect.Bottom);

    /// <summary>
    /// Real visual bounds of a window in physical pixels: the DWM extended frame excludes
    /// the invisible resize border that <see cref="GetWindowRect"/> includes.
    /// </summary>
    private static bool TryGetExtendedFrameBounds(IntPtr hwnd, out NATIVE_RECT bounds) =>
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out bounds, Marshal.SizeOf<NATIVE_RECT>()) == 0;

    private static string GetClassNameString(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    public void Dispose()
    {
        poller?.Stop();
        poller = null;
        GC.SuppressFinalize(this);
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out uint state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr extraData);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out NATIVE_RECT rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out NATIVE_RECT value, int size);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, uint attribute, out int value, int size);

    /// <summary>Exposes the raw foreground-window signals for the detection probe.</summary>
    public static ForegroundObservation Observe()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return new ForegroundObservation();

        GetWindowThreadProcessId(hwnd, out var pid);
        return new ForegroundObservation
        {
            HasWindow = true,
            ProcessId = pid,
            ClassName = GetClassNameString(hwnd),
            IsOwnProcess = pid == Environment.ProcessId,
            IsShellWindow = IsShellWindow(hwnd),
            IsMaximized = IsZoomed(hwnd),
            IsMinimized = IsIconic(hwnd),
            IsVisible = IsWindowVisible(hwnd)
        };
    }

    /// <summary>Raw foreground-window signals, for diagnostics.</summary>
    public readonly record struct ForegroundObservation
    {
        public bool HasWindow { get; init; }
        public int ProcessId { get; init; }
        public string ClassName { get; init; }
        public bool IsOwnProcess { get; init; }
        public bool IsShellWindow { get; init; }
        public bool IsMaximized { get; init; }
        public bool IsMinimized { get; init; }
        public bool IsVisible { get; init; }
    }
}
