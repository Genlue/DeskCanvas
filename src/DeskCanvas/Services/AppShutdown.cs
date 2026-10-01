using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

namespace DeskCanvas.Services;

/// <summary>
/// The one place that asks the application to exit.
/// <para>
/// The settings window is a reusable singleton that hides instead of closing (a closed Avalonia
/// window can never be shown again), so every real exit has to be announced here first —
/// <see cref="Views.Settings"/> lets its close through only while <see cref="IsShuttingDown"/> is
/// set, and otherwise merely hides itself.
/// </para>
/// </summary>
public static class AppShutdown
{
    /// <summary>True once an exit has been requested; windows may really close from now on.</summary>
    public static bool IsShuttingDown { get; private set; }

    /// <summary>Close every window and terminate the application.</summary>
    public static void Request()
    {
        IsShuttingDown = true;

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}
