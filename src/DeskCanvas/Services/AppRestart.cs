using System;
using System.Diagnostics;
using System.Threading;

namespace DeskCanvas.Services;

/// <summary>
/// Restarts the application (used when a change must be applied at startup,
/// e.g. after importing a backup).
/// </summary>
public static class AppRestart
{
    /// <summary>
    /// Launch a new instance (with the settings window) and shut down the
    /// current one once the new instance owns a window, so the switch is
    /// seamless.
    /// </summary>
    public static void Restart()
    {
        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (executablePath == null) return;

        // Hand the single-instance scope over BEFORE the successor starts: it would otherwise
        // read the still-held scope as "another instance is running" and exit immediately,
        // leaving no app at all.
        SingleInstance.Current?.Release();

        var process = Process.Start(executablePath, "--settings");
        if (process == null)
        {
            // Nothing took over — keep this instance guarded and running.
            SingleInstance.Current?.TryReacquire();
            return;
        }

        // Wait up to 5 s for the new instance's settings window (longer than the
        // old 1 s: on a slow start the old instance used to stay alive, leaving
        // BOTH processes showing widgets = the doubled/stacked cards reported
        // after an import).
        var tryCount = 0;
        var maxTryCount = 50;

        while (process.MainWindowHandle == IntPtr.Zero && !process.HasExited && tryCount++ < maxTryCount)
        {
            Thread.Sleep(100);
            process.Refresh();
        }

        AppShutdown.Request();
    }
}
