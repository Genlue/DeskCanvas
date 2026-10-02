using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using static Avalonia.Controls.Win32Properties;

namespace DeskCanvas.Services;

/// <summary>
/// Native z-order / window-style handling for the sidebar.
/// <para>
/// The sidebar is intentionally <b>not</b> part of the desktop widget band
/// (<see cref="WidgetZOrder.PinWidgetToBottom"/>): it must sit above ordinary windows so the user
/// can read it, while remaining a non-activating tool window that never appears in the taskbar or
/// Alt-Tab. It also opts out of desktop capture so the glass sampling can never see the sidebar
/// itself (no recursion).
/// </para>
/// </summary>
public static class SidebarZOrder
{
    private const int GWL_EXSTYLE = -20;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_APPWINDOW = 0x00040000;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    /// <summary>Exclude this window from screen capture (Windows 10 2004+).</summary>
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    private const int GWLP_HWNDPARENT = -8;

    /// <summary>Windows that already carry the self-healing style callback.</summary>
    private static readonly HashSet<Window> guarded = new();

    /// <summary>
    /// Turn the window into an always-on-top tool window: no taskbar button, no Alt-Tab entry,
    /// never activated by a click, and topmost in its own band.
    /// <para>
    /// The extended-style work is self-healing, not one-shot: Avalonia recomputes the whole exStyle
    /// on every style update (show, state change, transparency switch) and writes
    /// <c>WS_EX_APPWINDOW</c> back in whenever the properties it holds say <c>ShowInTaskbar</c> —
    /// APPWINDOW outranks TOOLWINDOW, so a single stale bit brings the taskbar button back (the
    /// brief taskbar flash when the sidebar or one of its panels appears). The style callback
    /// re-asserts the bits, and the owner link is re-severed on demand because Avalonia
    /// re-parents an ownerless taskbar-less window onto its shared offscreen parent.
    /// </para>
    /// <para>
    /// <b>WS_EX_NOACTIVATE is deliberately NOT set</b> — the sidebar (and any panel it spawns) must
    /// be able to take focus, otherwise neither Esc nor "click elsewhere to dismiss" could ever
    /// fire, because a window that never activates never reports Deactivated. Keeping it out of the
    /// taskbar and Alt-Tab is what the tool-window bit is for.
    /// </para>
    /// </summary>
    public static void MakeToolWindow(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        ApplyToolWindowStyle(handle);

        // Make sure no owner window survives (an owner would drag the sidebar behind it or into
        // Alt-Tab). Avalonia does not set one for a plain Show(), but a stale owner can remain
        // after certain shows, so it is cleared defensively.
        SetWindowLongPtr(handle, GWLP_HWNDPARENT, IntPtr.Zero);

        PinTopmost(window, handle);
    }

    /// <summary>
    /// Raise a window into the topmost band next to the sidebar <b>without touching its native
    /// owner</b>.
    /// <para>
    /// This is what the sidebar's own secondary panels need. A panel shown as the sidebar's owned
    /// window is kept above its owner by the window manager, but that rule does not lift it into the
    /// owner's <i>band</i>: the sidebar is topmost and the panel is not, so the whole non-topmost
    /// band — every ordinary application window included — ended up between them and the panel
    /// looked like it had been pushed to the bottom. Making it topmost puts it back above the
    /// sidebar's cards, while keeping the owner link means it is still torn down with the sidebar
    /// and still inherits its taskbar/Alt-Tab-free existence.
    /// </para>
    /// </summary>
    public static void PinAboveTopmost(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;

        PinTopmost(window, handle);
    }

    /// <summary>
    /// The shared tail of both placement paths: keep the window out of the taskbar / Alt-Tab and
    /// park it at the top of the topmost band.
    /// </summary>
    private static void PinTopmost(Window window, IntPtr handle)
    {
        ApplyToolWindowStyle(handle);

        var firstTime = false;
        lock (guarded)
        {
            firstTime = guarded.Add(window);
            if (firstTime)
                window.Closed += (_, _) => { lock (guarded) guarded.Remove(window); };
        }

        // Only install the callback once per window — the placement is re-applied on every summon
        // (and on Opened), and the callback list would otherwise grow without bound.
        if (firstTime)
            Win32Properties.AddWindowStylesCallback(window, (style, exStyle) =>
                (style, ForceToolWindow(exStyle)));

        // The placement itself always runs: a re-summoned sidebar has to be raised back into the
        // topmost band, which the style callback alone does not do.
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// The exact extended style every sidebar surface must carry: <c>WS_EX_TOOLWINDOW</c> set (that
    /// is what keeps it out of the taskbar and Alt-Tab) and <c>WS_EX_APPWINDOW</c> clear.
    /// </summary>
    private static uint ForceToolWindow(uint exStyle) =>
        (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;

    /// <summary>Write <see cref="ForceToolWindow"/> back, but only when a bit is actually wrong.</summary>
    private static void ApplyToolWindowStyle(IntPtr handle)
    {
        var exStyle = unchecked((uint)GetWindowLongPtr(handle, GWL_EXSTYLE).ToInt64());
        var wanted = ForceToolWindow(exStyle);
        if (wanted != exStyle)
            SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(unchecked((int)wanted)));
    }

    /// <summary>
    /// Best-effort capture exclusion so the sidebar never appears inside its own glass.
    /// Unsupported/older systems simply ignore the call.
    /// </summary>
    public static bool TryExcludeFromCapture(Window window)
    {
        if (!OperatingSystem.IsWindows()) return false;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return false;
        return SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Re-apply topmost after it may have been cleared by an activation.</summary>
    public static void Raise(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(handle, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}
