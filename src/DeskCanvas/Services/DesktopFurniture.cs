using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;

namespace DeskCanvas.Services;

/// <summary>
/// The attach point for DeskCanvas' own windows that belong to the desktop layer — the widget
/// cards and the widgets' secondary panels.
/// <para>
/// <see cref="Attach"/> registers the window's native handle and hands the window to the
/// focus-theme coordinator (which applies its per-screen variant right away). The focus
/// watcher itself treats <b>every window of this process</b> — widgets, panels, the settings
/// window, dialogs — as transparent to the 失焦自动切换 occupancy walk: DeskCanvas is desktop
/// software and never pins or prevents a state, so clicking a widget or opening the settings
/// window does not flip anything.
/// </para>
/// <para>
/// Registration is keyed by the native handle and removed on the window's <c>Closed</c>
/// event, so a raised/lowered handle can never linger. Non-Windows hosts never have a
/// platform handle and simply skip registration.
/// </para>
/// </summary>
public static class DesktopFurniture
{
    private static readonly HashSet<IntPtr> handles = [];
    private static readonly object gate = new();

    /// <summary>Windows that already went through <see cref="Attach"/> — the call is made from
    /// paths that can run twice for one window (Loaded re-fires on re-show), and the Closed
    /// subscription must not stack.</summary>
    private static readonly ConditionalWeakTable<Window, object> attached = [];

    /// <summary>Register a window as furniture and hand it to the focus-theme coordinator
    /// (which applies its per-screen light/dark variant right away). Idempotent per window.</summary>
    public static void Attach(Window window)
    {
        if (attached.TryGetValue(window, out _)) return;
        attached.Add(window, new object());

        var handle = IntPtr.Zero;
        try
        {
            handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        }
        catch
        {
            // Window not realized yet: nothing to register; tracking still applies.
        }

        Register(handle);
        if (handle != IntPtr.Zero)
            window.Closed += (_, _) => Unregister(handle);

        (App.Services?.GetService(typeof(FocusThemeCoordinator)) as FocusThemeCoordinator)?.Track(window);
    }

    public static void Register(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (gate) handles.Add(hwnd);
    }

    public static void Unregister(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        lock (gate) handles.Remove(hwnd);
    }

    public static bool Contains(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        lock (gate) return handles.Contains(hwnd);
    }
}
