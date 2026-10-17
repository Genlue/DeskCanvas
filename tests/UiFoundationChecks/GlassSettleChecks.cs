using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using DeskCanvas.Views.Controls;
using SkiaSharp;

namespace UiFoundationChecks;

/// <summary>
/// Wallpaper-change settle window vs the live-sampling switch (glass pipeline).
/// <para>
/// The settle contract (see LiquidGlassWallpaper.BeginSettle): after a wallpaper change the probe
/// keeps running <b>even with live sampling off</b> — the watcher path has no other publisher, and
/// the window's whole job is to freeze the glass on the settled wallpaper instead of the
/// change-instant frame. The probe guard once rejected every probe while live sampling was off,
/// which silently disabled that convergence: the change-instant capture (a mid-fade blend or an
/// MPO overlay artifact) froze on every glass card until an unrelated event — a widget drag, a
/// theme flip — happened to re-render it (the reported "换壁纸后边缘组件采样偏移，拖动后恢复").
/// These checks pin the guard decision itself, plus the 3.8.3 guarantees that followed the
/// "sometimes not a single widget updates" report: a wallpaper-change entry point retires the
/// temporal acceptance grid (a new wallpaper IS a wild jump against the scene it replaced), a
/// zero-publish window can no longer die silently at its deadline (bounded forced-publish
/// fallback), and closing the window sweeps every frame-demanding surface onto the final frame.
/// A real probe round needs a real desktop and stays a manual acceptance item.
/// </para>
/// </summary>
internal static partial class Program
{
    private static void CheckGlassSettleProbing()
    {
        Console.WriteLine("--- glass settle: the probe runs during a settle window with live sampling off ---");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("   [SKIP] non-Windows host: the sampling pipeline is Windows-only");
            return;
        }

        var probeWanted = typeof(LiquidGlassWallpaper).GetProperty("ProbeWanted",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("LiquidGlassWallpaper.ProbeWanted not found");
        var settleActive = typeof(LiquidGlassWallpaper).GetProperty("SettleActive",
            BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("LiquidGlassWallpaper.SettleActive not found");
        var closeSettle = typeof(LiquidGlassWallpaper).GetMethod("CloseSettle",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LiquidGlassWallpaper.CloseSettle not found");

        var originalSampling = LiquidGlassWallpaper.LiveSamplingEnabled;
        var originalInterval = LiquidGlassWallpaper.LiveSamplingIntervalMs;
        try
        {
            // Static-wallpaper mode (the default): the sampler idles between events.
            LiquidGlassWallpaper.ConfigureLiveSampling(false, 100);
            closeSettle.Invoke(null, null); // a stray window from an earlier check must not leak in
            Check(!(bool)probeWanted.GetValue(null)!,
                "live sampling off, no settle window → no probe rounds");

            // The wallpaper-change window must probe even with the switch off — the regression.
            LiquidGlassWallpaper.BeginSettle();
            Check((bool)settleActive.GetValue(null)! && (bool)probeWanted.GetValue(null)!,
                "BeginSettle opens the window and the probe runs with live sampling off (the reported freeze)");

            // The window closes on its own terms; force-closing returns to the idle state.
            closeSettle.Invoke(null, null);
            Check(!(bool)settleActive.GetValue(null)! && !(bool)probeWanted.GetValue(null)!,
                "closing the settle window returns the sampler to idle");

            // The user's switch must keep winning on its own, settle or not.
            LiquidGlassWallpaper.ConfigureLiveSampling(true, 100);
            Check((bool)probeWanted.GetValue(null)!, "live sampling on → probe rounds");
            LiquidGlassWallpaper.BeginSettle();
            Check((bool)probeWanted.GetValue(null)!, "live sampling on during a settle window → probe rounds");
        }
        finally
        {
            closeSettle.Invoke(null, null);
            LiquidGlassWallpaper.ConfigureLiveSampling(originalSampling, originalInterval);
        }

        CheckWallpaperChangeRetiresAcceptanceGrid();
        CheckSettleDeadlineFallback();
        CheckSettleCloseOutSweep();
    }

    // ---- 3.8.3: a wallpaper-change entry point retires the temporal acceptance grid ----------

    /// <summary>
    /// RC1, half one: the temporal acceptance grid is the reference the wild-jump check compares
    /// every incoming frame against. A wallpaper switch replaces nearly the whole scene at once —
    /// against the OLD grid that is the definition of a wild jump, so the first probes after the
    /// event used to be rejected as "MPO overlay transition" and only the unusable-capture grace
    /// could eventually clear it (often not within the settle window: the reported "sometimes not
    /// a single widget updates"). Both entry points must retire the grid so the incoming frame
    /// validates against nothing and publishes immediately.
    /// </summary>
    private static void CheckWallpaperChangeRetiresAcceptanceGrid()
    {
        Console.WriteLine("--- glass settle: Invalidate/BeginSettle retire the temporal acceptance grid ---");

        var type = typeof(LiquidGlassWallpaper);
        var samplesField = type.GetField("lastAcceptedSamples", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("lastAcceptedSamples not found");
        var hostField = type.GetField("lastAcceptedHost", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("lastAcceptedHost not found");
        var closeSettle = type.GetMethod("CloseSettle", BindingFlags.Static | BindingFlags.NonPublic)!;

        var originalSampling = LiquidGlassWallpaper.LiveSamplingEnabled;
        var originalInterval = LiquidGlassWallpaper.LiveSamplingIntervalMs;
        try
        {
            LiquidGlassWallpaper.ConfigureLiveSampling(false, 100);
            closeSettle.Invoke(null, null);

            void Plant()
            {
                samplesField.SetValue(null, new[] { 1, 2, 3, 4 });
                hostField.SetValue(null, (IntPtr)1234);
            }

            Plant();
            LiquidGlassWallpaper.Invalidate();
            Check(samplesField.GetValue(null) == null,
                "Invalidate retires lastAcceptedSamples (the new wallpaper must not be read as a wild jump)");
            Check((IntPtr)(hostField.GetValue(null) ?? IntPtr.Zero) == IntPtr.Zero,
                "Invalidate retires lastAcceptedHost");

            Plant();
            LiquidGlassWallpaper.BeginSettle();
            Check(samplesField.GetValue(null) == null,
                "BeginSettle retires lastAcceptedSamples too");
            Check((IntPtr)(hostField.GetValue(null) ?? IntPtr.Zero) == IntPtr.Zero,
                "BeginSettle retires lastAcceptedHost as well");
        }
        finally
        {
            closeSettle.Invoke(null, null);
            LiquidGlassWallpaper.ConfigureLiveSampling(originalSampling, originalInterval);
        }
    }

    // ---- 3.8.3: a zero-publish settle window can no longer die at its deadline ----------------

    /// <summary>
    /// RC1, half two: the deadline fallback. A settle window that never published used to run to
    /// its deadline and simply die — with slow captures and busy surfaces the whole 6 s could be
    /// spent without a publish, and every surface stayed on the old wallpaper until an unrelated
    /// event. The fallback must (a) retire the acceptance grid before probing, (b) reopen a short
    /// confirmation window after publishing, (c) close when the probe finds the desktop already
    /// equal to what the surfaces render, and (d) give up after a bounded number of unusable
    /// attempts instead of probing forever. Driven through the ForcedProbeOverride seam — a real
    /// forced round against a real desktop stays a manual acceptance item.
    /// </summary>
    private static void CheckSettleDeadlineFallback()
    {
        Console.WriteLine("--- glass settle: zero-publish deadline runs the forced-publish fallback ---");

        var type = typeof(LiquidGlassWallpaper);
        var settleUntil = type.GetField("settleUntil", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("settleUntil not found");
        var settlePublished = type.GetField("settlePublished", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("settlePublished not found");
        var settleFallbacks = type.GetField("settleFallbacks", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("settleFallbacks not found");
        var maxFallbacks = type.GetField("MaxSettleFallbacks", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MaxSettleFallbacks not found");
        var samplesField = type.GetField("lastAcceptedSamples", BindingFlags.Static | BindingFlags.NonPublic)!;
        var fallback = type.GetMethod("RunSettleDeadlineFallback", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("RunSettleDeadlineFallback not found");
        var closeSettle = type.GetMethod("CloseSettle", BindingFlags.Static | BindingFlags.NonPublic)!;
        var overrideField = type.GetField("ForcedProbeOverride", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ForcedProbeOverride not found");
        var outcomeType = type.GetNestedType("ProbeOutcome", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ProbeOutcome not found");
        int Outcome(string name) => Convert.ToInt32(Enum.Parse(outcomeType, name));

        var originalSampling = LiquidGlassWallpaper.LiveSamplingEnabled;
        var originalInterval = LiquidGlassWallpaper.LiveSamplingIntervalMs;
        try
        {
            LiquidGlassWallpaper.ConfigureLiveSampling(false, 100);
            closeSettle.Invoke(null, null);

            void ExpireWithZeroPublishes()
            {
                settleUntil.SetValue(null, Environment.TickCount64 - 50);
                settlePublished.SetValue(null, 0);
            }

            // (a)+(b): the forced round publishes → the window reopens for confirmation.
            ExpireWithZeroPublishes();
            samplesField.SetValue(null, new[] { 9, 9, 9 }); // must be retired by the fallback
            overrideField.SetValue(null, Outcome("Published"));
            fallback.Invoke(null, null);
            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10); // async void: poll until the fallback's bookkeeping landed
                if ((long)(settleUntil.GetValue(null) ?? 0L) > Environment.TickCount64) break;
                Dispatcher.UIThread.RunJobs();
            }

            Check(samplesField.GetValue(null) == null,
                "the fallback retires the acceptance grid before probing (the bypass exists for the wild-jump misclassification)");
            Check((int)(settlePublished.GetValue(null) ?? 0) == 1,
                "a published forced round marks the window as having delivered a frame");
            Check((long)(settleUntil.GetValue(null) ?? 0L) > Environment.TickCount64,
                "a published forced round reopens a short confirmation window (zero-publish death is impossible)");

            // (c): the probe verifiably finds the desktop unchanged → close for good.
            overrideField.SetValue(null, Outcome("Unchanged"));
            ExpireWithZeroPublishes();
            fallback.Invoke(null, null);
            deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                if ((long)(settleUntil.GetValue(null) ?? 0L) == 0) break;
                Dispatcher.UIThread.RunJobs();
            }

            Check((long)(settleUntil.GetValue(null) ?? 0L) == 0,
                "an Unchanged forced round closes the window (the surfaces already show the desktop)");

            // (d): the fallback budget is bounded — exhausting it closes instead of probing forever.
            overrideField.SetValue(null, Outcome("Unusable"));
            ExpireWithZeroPublishes();
            settleFallbacks.SetValue(null, (int)maxFallbacks.GetRawConstantValue()!);
            fallback.Invoke(null, null);
            deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline)
            {
                Thread.Sleep(10);
                if ((long)(settleUntil.GetValue(null) ?? 0L) == 0) break;
                Dispatcher.UIThread.RunJobs();
            }

            Check((long)(settleUntil.GetValue(null) ?? 0L) == 0,
                "an exhausted fallback budget closes the window (bounded retries, no infinite probing)");
        }
        finally
        {
            overrideField.SetValue(null, null);
            closeSettle.Invoke(null, null);
            LiquidGlassWallpaper.ConfigureLiveSampling(originalSampling, originalInterval);
        }
    }

    // ---- 3.8.3: closing the settle window sweeps stale materials onto the final frame ---------

    /// <summary>
    /// RC3: the unified publish drops frames on busy surfaces by design, and a static desktop
    /// never publishes again — so the one surface that was mid-prepare during the final publish
    /// used to stay on the old wallpaper forever ("统一发放后部分组件更新失败"). CloseSettle must
    /// verify every frame-demanding surface against the final snapshot's identity: stale idle
    /// surfaces re-render (observed through the seam here; the real render stays manual
    /// acceptance), stale busy surfaces get their catch-up flag armed, and surfaces already built
    /// on the final frame are left alone.
    /// </summary>
    private static void CheckSettleCloseOutSweep()
    {
        Console.WriteLine("--- glass settle: CloseSettle sweeps surfaces onto the final published frame ---");

        var surfaceType = typeof(LiquidGlassSurface);
        var preparedType = surfaceType.GetNestedType("Prepared", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("LiquidGlassSurface.Prepared not found");
        var preparedField = surfaceType.GetField("prepared", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("prepared field not found");
        var busyField = surfaceType.GetField("busy", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("busy field not found");
        var pendingField = surfaceType.GetField("pendingSampledFrame", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("pendingSampledFrame field not found");
        var sweepField = surfaceType.GetField("SweepRenderOverride", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("SweepRenderOverride not found");

        var wallpaperType = typeof(LiquidGlassWallpaper);
        var cachedField = wallpaperType.GetField("cached", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("cached field not found");
        var closeSettle = wallpaperType.GetMethod("CloseSettle", BindingFlags.Static | BindingFlags.NonPublic)!;

        var theme = new Theme(true, null, 0.8, false, false, "Inter", SurfaceStyle.LiquidGlassV2);
        var frame = new LiquidGlassRenderer.Frame(10, 10, 1f, 4f, 0, 0, 1920, 1080, 0, 0, 1920, 1080, theme, false);
        var preparedCtor = preparedType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length == 4);

        object MakePrepared(long captureId) =>
            preparedCtor.Invoke(new object?[] { null, frame, null, captureId });

        var host = new Window { Width = 400, Height = 300, WindowDecorations = WindowDecorations.None };
        var fresh = new LiquidGlassSurface { Material = theme, Width = 80, Height = 60 };
        var stale = new LiquidGlassSurface { Material = theme, Width = 80, Height = 60 };
        var staleBusy = new LiquidGlassSurface { Material = theme, Width = 80, Height = 60 };
        host.Content = new StackPanel { Children = { fresh, stale, staleBusy } };
        host.Show();
        Dispatcher.UIThread.RunJobs();

        var swept = new List<string>();
        var savedCached = cachedField.GetValue(null);
        var savedOverride = sweepField.GetValue(null);
        try
        {
            // The final published frame: a snapshot instance whose identity the sweep compares
            // against. FromBitmap(null) owns no pixels — nothing to dispose afterwards.
            var finalSnapshot = WallpaperSnapshot.FromBitmap(null, new SKColor(32, 38, 48), null);
            cachedField.SetValue(null, finalSnapshot);
            var finalId = finalSnapshot.CaptureId;

            preparedField.SetValue(fresh, MakePrepared(finalId));
            preparedField.SetValue(stale, MakePrepared(finalId - 1));
            preparedField.SetValue(staleBusy, MakePrepared(finalId - 2));
            busyField.SetValue(staleBusy, true);

            sweepField.SetValue(null, (Action<LiquidGlassSurface, long>)((s, _) => swept.Add(s.GetHashCode().ToString())));
            closeSettle.Invoke(null, null);
            Dispatcher.UIThread.RunJobs();

            Check(swept.Count == 1 && swept[0] == stale.GetHashCode().ToString(),
                $"the sweep re-renders exactly the stale idle surface ({swept.Count} rendered)");
            Check((int)(pendingField.GetValue(staleBusy) ?? 0) == 1,
                "the stale busy surface got its catch-up flag armed instead of a concurrent render");
            Check((int)(pendingField.GetValue(fresh) ?? 0) == 0,
                "the surface already built on the final frame is left alone");
        }
        finally
        {
            sweepField.SetValue(null, savedOverride);
            cachedField.SetValue(null, null);
            host.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
