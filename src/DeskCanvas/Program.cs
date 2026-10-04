using Avalonia;
using System;
using System.IO;
using DeskCanvas.Core;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;

namespace DeskCanvas;

public class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        try
        {
            // Only one DeskCanvas+ process per session. A later launch hands the request over to
            // the running instance (which brings its settings window to the front) and exits,
            // instead of starting a rival copy that would fight over the data folder, the widget
            // windows and the auto-start entry.
            var singleInstance = SingleInstance.Acquire();
            if (singleInstance == null)
            {
                SingleInstance.Signal();
                return;
            }

            SingleInstance.Current = singleInstance;

            try
            {
                // Single-file mode: extract the embedded widget bundle & default settings first,
                // so the rest of the app sees a regular data folder. No-op in portable mode.
                WidgetBundle.ExtractIfNeeded();

                // Keep the Windows auto-start entry in sync with the saved preference so the
                // feature reliably takes effect (re-asserts the path each launch; safe when off).
                SyncRunOnStartup();

                BuildAvaloniaApp()
                    .StartWithClassicDesktopLifetime(args);
            }
            finally
            {
                SingleInstance.Current = null;
                singleInstance.Dispose();
            }
        }
        catch (Exception e)
        {
            var fileName = Path.Combine(Const.DataFolder, "crash_log.txt");
            File.WriteAllText(fileName, e.ToString());
            throw;
        }
    }

    /// <summary>
    /// Re-asserts the Windows auto-start registry entry when the user has enabled it.
    /// This is best-effort and never blocks app startup; it fixes a stale or missing
    /// entry (e.g. after the exe was moved) so "Run on startup" reliably takes effect.
    /// </summary>
    private static void SyncRunOnStartup()
    {
        try
        {
            if (new AppSettingsProvider().Get().RunOnStartup)
                new StartupService().SetRunOnStartup(true);
        }
        catch
        {
            // Auto-start is non-critical; ignore any failure here.
        }
    }

    /// <summary>
    /// Corner radius (DIP) of the application's own windows (the settings window and any dialog
    /// that shows the compositor backdrop).
    /// <para>
    /// Deliberately a window-sized constant and <b>not</b> <c>Dimensions.Radius</c>: that setting
    /// sizes the widget <i>cards</i> and is routinely configured much larger (36 on the author's
    /// desktop), which made the settings window's corners absurdly round.
    /// </para>
    /// <para>
    /// Keep it at or below the radius Windows itself rounds a top-level window with (8 DIP, scaled
    /// by the monitor DPI). Windows rounds the finished window — content and composited backdrop
    /// alike — with an antialiased arc and a matching shadow, so a backdrop radius that is
    /// <i>smaller</i> than that arc is simply invisible and the window shows one clean corner. A
    /// larger one draws a second arc inside the first, and the sliver between the two is glass-free
    /// (the "small, transparent corner"). It is also deliberately not enforced with
    /// <c>SetWindowRgn</c>: that region is a 1-bit mask, and clipping the window with it makes the
    /// corners visibly jagged.
    /// </para>
    /// </summary>
    public const double WindowCornerRadius = 8;

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UseWin32()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions
            {
                CompositionMode = new[] { Win32CompositionMode.WinUIComposition },
                // Read once, while the platform is built, so this radius is fixed for the session.
                WinUICompositionBackdropCornerRadius = (float)WindowCornerRadius
            })
            .LogToTrace();
    }
}
