// Headless screenshot of the Countdown secondary panel with the inline event editor open,
// so the festival-preset chips can be verified visually (they only exist inside the editor).
// Run: dotnet run --project tests/_countdownshot -c Release [-- <out.png>]
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Countdown.Views;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var outPath = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "countdown.png");

        var sandbox = Path.Combine(Path.GetTempPath(),
            "DeskCanvas-CountdownShot-" + Guid.NewGuid().ToString("N")[..8]);
        Environment.SetEnvironmentVariable("DESKCANVAS_DATA_FOLDER", sandbox);
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER", sandbox);

        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSkia()
            .SetupWithoutStarting();

        Application.Current!.Styles.Add(new FluentTheme());
        Application.Current.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;

        var window = new CountdownPopupWindow();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        // Open the inline editor exactly like the "add event" button does.
        var open = typeof(CountdownPopupWindow).GetMethod(
            "OnAddEventClicked",
            BindingFlags.NonPublic | BindingFlags.Instance);
        open?.Invoke(window, [null, new RoutedEventArgs()]);

        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        var chips = window.FindControl<StackPanel>("PresetChipsPanel");
        Console.WriteLine($"preset chips: {chips?.Children.Count ?? -1}");

        var editor = window.FindControl<Border>("EditorPanel");
        Console.WriteLine($"editor visible: {editor?.IsVisible}  size={editor?.Bounds.Width:F0}x{editor?.Bounds.Height:F0}");

        window.CaptureRenderedFrame()?.Save(outPath);
        Console.WriteLine("saved " + outPath);
    }
}
