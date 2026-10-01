using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace GlassGpuProbe;

/// <summary>
/// On-device bisect of the liquid-glass GPU material.
/// <para>
/// The device program for a Skia runtime effect is built at <i>draw</i> time, from a narrower
/// dialect than <c>SKRuntimeEffect.Create</c> accepts, and a program that fails there draws
/// nothing and reports nothing — the card just comes out transparent. That makes the failure
/// invisible to every offline test, so this probe drives a real window on the real GPU backend
/// (ANGLE/EGL, the same one the app uses) and measures which slice of the optical model still
/// produces pixels.
/// </para>
/// <para>
/// Usage: <c>dotnet run --project tests/GlassGpuProbe</c> writes <c>gpu-probe-log.txt</c> next to
/// the binary and prints the table to stdout.
/// </para>
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Probe.Reset();
        try
        {
            AppBuilder.Configure<ProbeApp>()
                .UsePlatformDetect()
                .With(new Win32PlatformOptions { RenderingMode = new[] { Win32RenderingMode.AngleEgl } })
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Probe.Write("FATAL: " + ex);
        }
        return Probe.Failures;
    }
}

internal sealed class ProbeApp : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        // A real, mapped top-level window: the custom draw operation only runs when Avalonia
        // actually composes a frame, which needs a live platform surface.
        var window = new Window
        {
            Width = 96,
            Height = 96,
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            Title = "DeskCanvas glass GPU probe",
            Content = new ProbeControl()
        };
        window.Show();

        DispatcherTimer.RunOnce(() =>
        {
            Probe.Write("--- probe finished, closing ---");
            window.Close();
        }, TimeSpan.FromSeconds(3));

        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ProbeControl : Control
{
    public override void Render(DrawingContext context) => context.Custom(new ProbeDrawOp(new Rect(Bounds.Size)));
}

internal sealed class ProbeDrawOp : ICustomDrawOperation
{
    private static int ran;

    public ProbeDrawOp(Rect bounds) => Bounds = bounds;
    public Rect Bounds { get; }
    public void Dispose() { }
    public bool Equals(ICustomDrawOperation? other) => false;
    public bool HitTest(Point p) => false;

    public void Render(ImmediateDrawingContext context)
    {
        if (Interlocked.Exchange(ref ran, 1) != 0) return;

        if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
        {
            Probe.Write("no ISkiaSharpApiLeaseFeature — cannot probe");
            return;
        }

        using var lease = feature.Lease();
        if (lease.GrContext == null)
        {
            Probe.Write("NO GrContext — the probe needs the GPU backend (ANGLE/EGL)");
            return;
        }

        Probe.Write($"GrContext present; SkiaSharp {typeof(SKCanvas).Assembly.GetName().Version}, " +
                    $"Avalonia {typeof(Application).Assembly.GetName().Version}");

        try
        {
            Probe.Run(lease.GrContext!);
        }
        catch (Exception ex)
        {
            Probe.Write("EXCEPTION: " + ex);
        }
        finally
        {
            Probe.Write($"=== {Probe.Failures} stage(s) produced no pixels ===");
        }
    }
}
