using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DeskCanvas.Services;

/// <summary>
/// Reads the process that owns the current foreground window — the identity the 聚焦搜索 blacklist
/// compares against when the hotkey fires.
/// <para>
/// <b>Known limitation:</b> a normal-privilege process cannot query the process name behind an
/// elevated window (<c>OpenProcess</c> is denied), so an app running as administrator is invisible
/// to the blacklist. <see cref="GetProcessName"/> returns <c>null</c> in that case rather than
/// guessing, and the launcher treats "unknown" as "not blacklisted".
/// </para>
/// </summary>
public static class ForegroundProcess
{
    /// <summary>
    /// The foreground window's process name without <c>.exe</c> ("chrome"), or <c>null</c> when it
    /// cannot be determined (no foreground window, access denied, process already gone).
    /// </summary>
    public static string? GetProcessName() => GetProcessName(GetForegroundWindow());

    /// <summary>
    /// The specified window's process name without <c>.exe</c> ("chrome"), or <c>null</c> when it
    /// cannot be determined (no window, access denied, process already gone).
    /// </summary>
    public static string? GetProcessName(IntPtr window)
    {
        try
        {
            if (window == IntPtr.Zero) return null;

            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0) return null;

            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            // Access denied (elevated target), the process exited mid-call, or the id is stale.
            return null;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}
