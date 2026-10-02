using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace DeskCanvas.Services;

/// <summary>
/// Keeps the memory the process holds flat over a long session.
///
/// A desktop-widget host runs for days. Almost all widgets are idle most of the time (a clock
/// ticks once a minute, the weather refreshes hourly), but every refresh — a folder icon
/// extraction, a weather payload, a pre-rendered glass frame — leaves garbage behind, and the
/// native side (Skia surfaces, shell icon handles, DWM composition buffers) is only released by
/// the finalizers that a mostly-idle GC may not run for a long while. The result is the classic
/// "it creeps up to several hundred megabytes" report.
///
/// So: a low-frequency, self-limiting sweep. It only does anything when the process is actually
/// holding more than <see cref="ThresholdBytes"/>, and the collection runs off the UI thread, so a
/// widget host that is sitting comfortably low pays nothing for it.
/// </summary>
public class MemoryTrimmerService : IDisposable
{
    /// <summary>
    /// Do nothing while the process is below this. Glass keeps a working set of buffers it genuinely
    /// reuses (a capture, a blurred backdrop, per-card materials), so the bar sits below what a
    /// healthy live-glass session holds — the sweep is for what the session <i>stopped</i> using.
    /// </summary>
    private const long ThresholdBytes = 80L * 1024 * 1024;

    /// <summary>Sweep interval. Long enough to be invisible, short enough to bound the creep.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);

    private DispatcherTimer? timer;

    /// <summary>Start the periodic sweep (idempotent).</summary>
    public void Start()
    {
        if (timer != null) return;
        timer = new DispatcherTimer(Interval, DispatcherPriority.Background, (_, _) => Sweep());
        timer.Start();
    }

    /// <summary>Trim now if the process is holding more than the threshold.</summary>
    public void Sweep()
    {
        long privateBytes;
        try
        {
            privateBytes = Process.GetCurrentProcess().PrivateMemorySize64;
        }
        catch
        {
            return;
        }

        if (privateBytes < ThresholdBytes) return;

        // Off the UI thread: a compacting gen-2 collection blocks for a few milliseconds and must
        // not stutter the widget surfaces.
        _ = Task.Run(InteropService.TrimProcessMemory);
    }

    public void Dispose()
    {
        timer?.Stop();
        timer = null;
        GC.SuppressFinalize(this);
    }
}
