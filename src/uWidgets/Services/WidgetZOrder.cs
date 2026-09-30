using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;
using static Avalonia.Controls.Win32Properties;

namespace uWidgets.Services;

/// <summary>
/// Desktop z-order bands, enforced through an Avalonia WndProc hook so that any
/// z-order change (click activation, Alt-Tab, ShowWindow, end of a drag…) ends up
/// in the right band:
///
///     normal application windows   (top)
///     secondary panels             (weather forecast, reminders, folder pop-over…)
///     desktop widget windows       (bottom of the band, above only the desktop)
///
/// Widgets are desktop furniture and must never float above other applications;
/// secondary panels need to stay visible next to their widget, but are "only
/// placed above" the widget band — still below ordinary application windows.
/// Panels anchor above the TOPMOST widget of the band (not just their owner):
/// they are typically larger than their widget and would otherwise slide
/// underneath the neighbouring widgets' cells.
/// </summary>
/// <remarks>
/// The z insertion of a SetWindowPos operation cannot be redirected in flight:
/// the window manager resolves HWND_TOP/HWND_BOTTOM to a concrete window and
/// decides the placement BEFORE sending WM_WINDOWPOSCHANGING, and it ignores
/// rewrites of <c>hwndInsertAfter</c> (only x/y/cx/cy are honoured). Cancelling
/// the operation's z part via SWP_NOZORDER also cannot be used — placement
/// actors verify their outcome and retry, which turns correction into an
/// infinite fight. So the hook lets every placement happen and, on
/// WM_WINDOWPOSCHANGED, re-asserts the band position with one coalesced
/// SetWindowPos whenever the window left its band. The in-band test is
/// "no visible non-widget window below me" — an idempotent predicate, so
/// corrections converge and never fight each other.
///
/// Native OWNERSHIP must be severed for both bands: Avalonia parents every
/// <c>ShowInTaskbar=false</c> window to one shared hidden offscreen window, and
/// popups to their widget (<c>Show(owner)</c>). An owned window can never rest
/// below its owner, so a bottom-pinned widget makes the window manager re-anchor
/// the whole owned group on every correction — and the group's other members
/// land on their own hooks, whose corrections re-anchor the group again: the two
/// bands fight each other forever and the UI thread spins. Widgets and panels
/// therefore drop their owner link and keep their taskbar/Alt-Tab-free behaviour
/// through WS_EX_TOOLWINDOW (which the app also applies on every activation).
/// </remarks>
public static class WidgetZOrder
{
    private const int WM_WINDOWPOSCHANGED = 0x0047;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GW_HWNDNEXT = 2;
    private const uint GW_HWNDPREV = 3;
    private const int GWL_EXSTYLE = -20;
    private const int GWL_HWNDPARENT = -8;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);

    /// <summary>Native handles of the live desktop-widget windows (the bottom band).</summary>
    private static readonly List<IntPtr> widgetHandles = new();
    private static readonly object gate = new();

    /// <summary>
    /// Pin a desktop-widget window to the bottom band for its whole lifetime:
    /// clicking, activating, dragging or re-showing it keeps it below every other
    /// visible application window (and above only the desktop itself).
    /// Call once the native window exists (Window.Opened).
    /// </summary>
    public static void PinWidgetToBottom(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = HwndOf(window);
        if (hwnd == IntPtr.Zero) return;

        window.Closed += (_, _) =>
        {
            lock (gate) widgetHandles.Remove(hwnd);
        };

        SeverNativeOwner(window, hwnd);

        lock (gate)
        {
            PruneDeadHandles();
            if (!widgetHandles.Contains(hwnd)) widgetHandles.Add(hwnd);
        }

        AttachZOrderHook(window, () => HwndBottom);
    }

    /// <summary>
    /// Pin a secondary panel just above the widget band: above every widget window,
    /// but still below ordinary application windows. Call right after Show(),
    /// before Activate() — the hook then keeps the placement for the panel's
    /// whole lifetime.
    /// </summary>
    /// <remarks>
    /// The panel must NOT be WS_EX_TOPMOST: several popups historically set
    /// Topmost="True" to float over the old unpinned widgets. With the band
    /// enforcement active that turns into a tug-of-war — the hook pulls the
    /// panel into the normal band, Avalonia's property sync pushes it back into
    /// the topmost band, and the UI thread spins forever. Clearing Topmost hands
    /// the panel over to the band mechanism, which keeps it above the widgets.
    /// </remarks>
    public static void PinPanelAboveWidgets(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var hwnd = HwndOf(window);
        if (hwnd == IntPtr.Zero) return;

        window.Topmost = false;
        SeverNativeOwner(window, hwnd);

        AttachZOrderHook(window, () => ResolvePanelInsertAfter(hwnd));
    }

    /// <summary>
    /// Drop the native owner link (Avalonia's shared offscreen parent for
    /// taskbar-less windows, or the owning widget for popups) and pin
    /// WS_EX_TOOLWINDOW so the window stays out of the taskbar and Alt-Tab —
    /// the reason the link existed in the first place. See the type remarks for
    /// why an owned window can never rest in the bottom band.
    /// </summary>
    /// <remarks>
    /// Two ordering rules keep the taskbar button from ever flashing:
    /// <list type="number">
    /// <item><b>WS_EX_TOOLWINDOW goes on BEFORE the owner link is severed.</b> While the
    /// window is visible, the gap between "unowned" and "tool window" is a taskbar-visible
    /// window — Explorer creates the uWidgetsPlus taskbar button for it and tears it down
    /// a moment later (the brief taskbar flash when a secondary panel opens).</item>
    /// <item><b>The tool-window bit is re-asserted through
    /// <see cref="Win32Properties.AddWindowStylesCallback"/></b> — Avalonia recomputes the
    /// whole exStyle from scratch on every style update (ShowWindow, state changes,
    /// transparency switches…) and would silently drop a bit we inject once; with the owner
    /// link severed there is nothing left to keep the button away, so it would come back
    /// for good. The callback runs inside every recomputation, so the bit always survives.</item>
    /// </list>
    /// </remarks>
    private static void SeverNativeOwner(Window window, IntPtr hwnd)
    {
        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle | WS_EX_TOOLWINDOW));
        SetWindowLongPtr(hwnd, GWL_HWNDPARENT, IntPtr.Zero);

        AddWindowStylesCallback(window, (style, exStyle) =>
            (style, exStyle | WS_EX_TOOLWINDOW));
    }

    /// <summary>
    /// Insert-after handle that puts a panel directly above the widget band: the
    /// first VISIBLE non-topmost window above the topmost widget. Invisible windows
    /// are skipped as anchors (they can sit anywhere in the chain, so the panel
    /// would be parked at a nonsensical spot), topmost-band windows — taskbar,
    /// always-on-top tools — must not become the anchor either (the panel would be
    /// dragged into the topmost band), and the panel's own handle is skipped so the
    /// resolve can never yield <c>SetWindowPos(panel, panel)</c>. When the band
    /// touches the top of the desktop, HWND_TOP puts the panel at the top of the
    /// normal band; when no widget is alive, the panel falls back to the very
    /// bottom.
    /// </summary>
    private static IntPtr ResolvePanelInsertAfter(IntPtr self)
    {
        var topmost = TopmostWidgetHandle();
        if (topmost == null) return HwndBottom;

        for (var prev = GetWindow(topmost.Value, GW_HWNDPREV);
             prev != IntPtr.Zero;
             prev = GetWindow(prev, GW_HWNDPREV))
        {
            if (prev == self) continue;
            if (!IsWindowVisible(prev)) continue;
            if (!IsTopmostWindow(prev)) return prev;
        }

        return HwndTop;
    }

    /// <summary>
    /// The topmost window of the widget band: walk the desktop z-order from the
    /// top and return the first registered widget handle.
    /// </summary>
    private static IntPtr? TopmostWidgetHandle()
    {
        lock (gate)
        {
            PruneDeadHandles();
            for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
            {
                if (widgetHandles.Contains(hwnd)) return hwnd;
            }
            return null;
        }
    }

    private static void PruneDeadHandles()
    {
        widgetHandles.RemoveAll(h => h == IntPtr.Zero || !IsWindow(h));
    }

    private static bool IsTopmostWindow(IntPtr hwnd) =>
        (GetWindowLong(hwnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;

    /// <summary>
    /// Install the z-order enforcement hook on the window and apply the initial
    /// band placement. The hook is a local function (not a lambda): C# 12 still
    /// rejects ref modifiers on lambda parameters, which the Avalonia hook
    /// delegate requires.
    /// </summary>
    private static void AttachZOrderHook(Window window, Func<IntPtr> resolveInsertAfter)
    {
        var hwnd = HwndOf(window);
        var selfPlacement = false;
        var correctionPending = false;

        void PlaceAtTarget()
        {
            correctionPending = false;
            selfPlacement = true;
            try
            {
                SetWindowPos(hwnd, resolveInsertAfter(), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            finally
            {
                selfPlacement = false;
            }
        }

        /// <summary>True while the window sits in its band: only widget-band windows may be visible below it.</summary>
        bool InBand()
        {
            lock (gate) PruneDeadHandles();

            for (var below = GetWindow(hwnd, GW_HWNDNEXT);
                 below != IntPtr.Zero;
                 below = GetWindow(below, GW_HWNDNEXT))
            {
                if (!IsWindowVisible(below)) continue;
                bool isBandMember;
                lock (gate) isBandMember = widgetHandles.Contains(below);
                if (!isBandMember) return false;
            }
            return true;
        }

        IntPtr Hook(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != WM_WINDOWPOSCHANGED || selfPlacement) return IntPtr.Zero;

            // The flags cannot be trusted as a "no z-change happened" shortcut:
            // system-initiated owned-group maintenance moves windows while carrying
            // SWP_NOZORDER, so a band violation must be detected by looking at the
            // actual chain. InBand is an idempotent chain walk — cheap enough to
            // run on every placement of the window.
            if (InBand()) return IntPtr.Zero;

            if (!correctionPending)
            {
                correctionPending = true;
                Dispatcher.UIThread.Post(PlaceAtTarget, DispatcherPriority.Send);
            }
            return IntPtr.Zero;
        }

        Win32Properties.AddWndProcHookCallback(window, Hook);
        PlaceAtTarget();
    }

    private static IntPtr HwndOf(Window window) =>
        window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>SetWindowLongPtr that falls back to SetWindowLong on 32-bit.</summary>
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : new IntPtr(SetWindowLong32(hWnd, nIndex, unchecked((int)dwNewLong.ToInt64())));
}
