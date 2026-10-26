using System;
using System.Collections.Generic;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// Where the glass actually is, in virtual-desktop physical pixels — the input that turns
/// "capture the desktop" into "capture the neighbourhood".
/// <para>
/// Every live glass surface reports its own rectangle (plus the margin its blur needs) on the UI
/// thread. The capture layer asks for the union and samples <b>that</b>, which is the DeskCanvas
/// equivalent of LiquidGlassCanvas' per-window <c>interest</c> rectangle: a handful of cards on a
/// 2560×1440 desktop need roughly a tenth of the pixels the full screen would cost, and the cost
/// scales with how much glass is on screen rather than with the monitor count.
/// </para>
/// <para>
/// The union is <b>snapped to a coarse grid</b> before it is used. Without that, a widget dragged by
/// one pixel would change the capture region every frame and force a re-crop each time; with it, the
/// region only moves when the glass really leaves its cell.
/// </para>
/// <para>
/// A surface that renders a <i>synthetic</i> desktop (the settings preview cards, which draw a
/// 120×90 miniature rather than the real desktop) forces the full virtual screen: its backdrop
/// addresses positions that have nothing to do with where the control sits.
/// </para>
/// </summary>
public static class GlassInterest
{
    /// <summary>Grid the sampling region is snapped to (physical pixels).</summary>
    private const int Grid = 32;

    /// <summary>Upper bound on the blur margin a single surface may ask for (physical pixels).</summary>
    private const int MaxMargin = 512;

    private readonly record struct Entry(CaptureRect Rect, int Margin, bool ScreenSampling, bool FullDesktop);

    private static readonly object Gate = new();
    private static readonly Dictionary<object, Entry> surfaces = new();

    /// <summary>Report (or refresh) one surface's sampling footprint. UI thread.</summary>
    public static void Report(object owner, CaptureRect rect, int margin, bool screenSampling, bool fullDesktop)
    {
        if (owner == null) return;
        lock (Gate)
        {
            surfaces[owner] = new Entry(rect, Math.Clamp(margin, 0, MaxMargin), screenSampling, fullDesktop);
        }
    }

    /// <summary>Forget a surface (it was detached or hidden). Safe to call repeatedly.</summary>
    public static void Remove(object owner)
    {
        if (owner == null) return;
        lock (Gate) surfaces.Remove(owner);
    }

    /// <summary>Forget every surface (a display change or a full reset).</summary>
    public static void Reset()
    {
        lock (Gate) surfaces.Clear();
    }

    /// <summary>
    /// The region the wallpaper chain should sample: the union of every wallpaper-sampling surface
    /// inflated by its blur margin, snapped to <see cref="Grid"/> and clipped to the desktop.
    /// Falls back to the whole virtual screen when nothing is reported.
    /// </summary>
    public static CaptureRect SamplingRegion() => Union(screenSampling: false);

    /// <summary>The region the screen chain should sample (the launcher's overlay).</summary>
    public static CaptureRect ScreenSamplingRegion() => Union(screenSampling: true);

    /// <summary>
    /// The coordinate space every sampling region is expressed in: the virtual desktop, in physical
    /// pixels. Exposed because a caller that wants to reason about a region it just read back needs
    /// the same frame of reference the sampler used.
    /// </summary>
    public static CaptureRect VirtualScreen => CaptureSpace.VirtualScreen();

    /// <summary>Number of surfaces currently reporting (diagnostics).</summary>
    public static int Count { get { lock (Gate) return surfaces.Count; } }

    private static CaptureRect Union(bool screenSampling)
    {
        CaptureRect union = default;
        var any = false;
        var full = false;

        lock (Gate)
        {
            foreach (var entry in surfaces.Values)
            {
                if (entry.ScreenSampling != screenSampling) continue;
                any = true;
                if (entry.FullDesktop) full = true;
                union = union.Union(entry.Rect.Inflate(entry.Margin, entry.Margin));
            }
        }

        if (!any || full) return CaptureSpace.VirtualScreen();
        if (union.IsEmpty) return CaptureSpace.VirtualScreen();

        var clipped = union.Intersect(CaptureSpace.VirtualScreen());
        return clipped.IsEmpty ? CaptureSpace.VirtualScreen() : Snap(clipped);
    }

    /// <summary>Expand the rectangle outwards to the next grid line.</summary>
    private static CaptureRect Snap(CaptureRect rect) => new(
        Floor(rect.Left), Floor(rect.Top), Ceiling(rect.Right), Ceiling(rect.Bottom));

    private static int Floor(int value) => (int)Math.Floor(value / (double)Grid) * Grid;

    private static int Ceiling(int value) => (int)Math.Ceiling(value / (double)Grid) * Grid;
}
