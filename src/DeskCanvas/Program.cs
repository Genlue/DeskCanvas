using Avalonia;
using System;
using System.IO;
using DeskCanvas.Core;
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
            // windows and the auto-start entry. The same helper is used by the WinUI entry.
            var singleInstance = AppStartup.AcquireSingleInstance();
            if (singleInstance == null)
                return;

            try
            {
                // Default data / widget bundle extraction and the auto-start re-assert happen
                // before any UI framework is initialized; behavior and order are unchanged.
                AppStartup.InitializeDataAndStartup();

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

    /// <summary>
    /// The effective corner radius for top-level windows on the running OS.
    /// Evaluates to <see cref="WindowCornerRadius"/> on Windows 11 (build 22000+) where DWM natively rounds
    /// windows, and <c>0</c> on Windows 10 and other platforms where windows are rectangular.
    /// </summary>
    public static double EffectiveWindowCornerRadius =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? WindowCornerRadius : 0;

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
                WinUICompositionBackdropCornerRadius = (float)EffectiveWindowCornerRadius
            })
            .LogToTrace();
    }
}
