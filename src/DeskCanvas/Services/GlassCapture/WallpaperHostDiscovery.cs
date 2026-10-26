using System;
using System.Collections.Generic;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// One candidate surface in the desktop's "wallpaper band" (pure data; the selection function
/// performs no window calls, so it is unit-testable).
/// </summary>
public readonly record struct WallpaperCandidate(
    IntPtr Window,
    CaptureRect Bounds,
    bool Visible,
    bool WallpaperClass,
    bool HostsDesktopIcons,
    bool IsProgman,
    bool BelowIconsHost);

/// <summary>
/// Finds the window that actually paints the desktop wallpaper, by <b>z-order band inference</b>
/// rather than by a hard-coded class list.
/// <para>
/// This is the sampling-object core taken from LiquidGlassCanvas' <c>WallpaperBackdrop</c>. The
/// rule is structural: the desktop icons live in <c>SHELLDLL_DefView</c>; the wallpaper band is
/// everything <i>below</i> that host; the wallpaper surface is the topmost visible window in that
/// band that covers the sampling area. A static desktop answers <c>Progman</c> (wallpaper and icons
/// share one host); a dynamic wallpaper engine answers its own window in the band. Nothing here
/// fires <c>0x052C</c> — triggering that message mutates the desktop structure and on some builds
/// leaves an unpainted <c>WorkerW</c> that captures as pure black.
/// </para>
/// <para>
/// The previous implementation walked a fixed candidate list (WorkerW-in-Progman → WorkerW after
/// the icons → Progman) and printed each one until a frame looked like wallpaper. That list is a
/// guess about a particular shell version; the band rule is the actual invariant, and it also
/// skips windows that are visible but do not cover the area the glass needs.
/// </para>
/// </summary>
public static class WallpaperHostDiscovery
{
    /// <summary>
    /// A candidate must cover at least this fraction of the sampling area to count as the wallpaper
    /// surface. Below it, the window is a wallpaper-engine preview/overlay rather than the desktop.
    /// </summary>
    public const double CoverThreshold = 0.6;

    /// <summary>
    /// Pick the wallpaper surface. Candidates must be ordered top-down in z-order.
    /// <list type="number">
    ///   <item><description>the first visible, non-icon-host window strictly below the icons host
    ///   that covers ≥ <see cref="CoverThreshold"/> of <paramref name="interest"/> — the topmost
    ///   visible surface of the wallpaper band (a dynamic wallpaper engine lands here);</description></item>
    ///   <item><description>otherwise <c>Progman</c>, which is both icon host and wallpaper surface
    ///   on a default desktop;</description></item>
    ///   <item><description>otherwise the best-covering wallpaper-class window, still needing the
    ///   threshold; otherwise -1 (the caller degrades).</description></item>
    /// </list>
    /// </summary>
    public static int SelectHost(IReadOnlyList<WallpaperCandidate> candidates, CaptureRect interest)
    {
        if (interest.Area <= 0) return -1;

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Visible || !candidate.BelowIconsHost || candidate.HostsDesktopIcons) continue;
            if (candidate.Bounds.CoverageOf(interest) >= CoverThreshold) return i;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Visible || !candidate.IsProgman) continue;
            if (candidate.Bounds.CoverageOf(interest) >= CoverThreshold) return i;
        }

        var best = -1;
        var bestCoverage = CoverThreshold;
        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            if (!candidate.Visible || !candidate.WallpaperClass || candidate.HostsDesktopIcons) continue;
            var coverage = candidate.Bounds.CoverageOf(interest);
            if (coverage <= bestCoverage) continue;
            bestCoverage = coverage;
            best = i;
        }

        return best;
    }

    /// <summary>
    /// Enumerate the real desktop and return the wallpaper host for <paramref name="interest"/>
    /// (virtual-desktop physical pixels — normally the monitor the glass sits on). Returns
    /// <see cref="IntPtr.Zero"/> when nothing usable was found.
    /// </summary>
    public static IntPtr Discover(CaptureRect interest, out CaptureRect hostBounds)
    {
        hostBounds = default;

        var iconsHost = FindIconsHost();
        if (iconsHost == IntPtr.Zero) iconsHost = NativeMethods.FindWindow("Progman", null);

        var candidates = new List<WallpaperCandidate>(16);

        // The wallpaper band: every visible window below the icons host, in z-order.
        for (var window = iconsHost != IntPtr.Zero ? NativeMethods.GetWindow(iconsHost, NativeMethods.GwHwndNext) : IntPtr.Zero;
             window != IntPtr.Zero;
             window = NativeMethods.GetWindow(window, NativeMethods.GwHwndNext))
        {
            if (!NativeMethods.IsWindowVisible(window) || NativeMethods.IsCloaked(window)) continue;
            if (!NativeMethods.GetWindowRect(window, out var rect)) continue;

            var className = NativeMethods.ClassNameOf(window);
            candidates.Add(new WallpaperCandidate(
                Window: window,
                Bounds: new CaptureRect(rect.Left, rect.Top, rect.Right, rect.Bottom),
                Visible: true,
                WallpaperClass: IsWallpaperClass(className),
                HostsDesktopIcons: HasDesktopIcons(window),
                IsProgman: string.Equals(className, "Progman", StringComparison.Ordinal),
                BelowIconsHost: true));
        }

        // Progman is both icon host and wallpaper surface on a default desktop, so it is added
        // explicitly (it is not "below" itself).
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != IntPtr.Zero && NativeMethods.GetWindowRect(progman, out var progmanRect))
        {
            candidates.Add(new WallpaperCandidate(
                Window: progman,
                Bounds: new CaptureRect(progmanRect.Left, progmanRect.Top, progmanRect.Right, progmanRect.Bottom),
                Visible: NativeMethods.IsWindowVisible(progman) && !NativeMethods.IsCloaked(progman),
                WallpaperClass: true,
                HostsDesktopIcons: iconsHost == progman || HasDesktopIcons(progman),
                IsProgman: true,
                BelowIconsHost: iconsHost != progman));
        }

        var picked = SelectHost(candidates, interest);
        if (picked < 0) return IntPtr.Zero;
        hostBounds = candidates[picked].Bounds;
        return candidates[picked].Window;
    }

    /// <summary>The top-level window that directly owns the desktop icon view.</summary>
    internal static IntPtr FindIconsHost()
    {
        var found = IntPtr.Zero;
        NativeMethods.EnumWindows((window, _) =>
        {
            if (NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero) return true;
            found = window;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    private static bool HasDesktopIcons(IntPtr window) =>
        NativeMethods.FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

    private static bool IsWallpaperClass(string className) =>
        className is "Progman" or "WorkerW";
}
