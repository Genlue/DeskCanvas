using System;
using System.Reflection;
using Avalonia.Controls;
using DeskCanvas.Services;

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
/// These checks pin the guard decision itself; a real probe round needs a real desktop and stays a
/// manual acceptance item.
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
    }
}
