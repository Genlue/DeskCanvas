using System;

namespace DeskCanvas.Services.GlassCapture;

/// <summary>
/// Translates a region of the virtual desktop into the coordinate space a capture session works
/// in, and back.
/// <para>
/// A Windows.Graphics.Capture session is created for <b>one</b> item — one monitor or one window —
/// and its frames are in that item's local pixels. The glass, however, reasons in virtual-desktop
/// physical pixels. Everything that crosses that boundary goes through here, so the two spaces can
/// never be confused: <c>localBox</c> is what the session is asked to keep, and
/// <c>effectiveVirtual</c> is where that box actually sits on the desktop (after clamping at the
/// monitor edges).
/// </para>
/// </summary>
internal static class CaptureSpace
{
    /// <summary>The whole virtual desktop, in physical pixels.</summary>
    public static CaptureRect VirtualScreen() => CaptureRect.FromSize(
        NativeMethods.GetSystemMetrics(NativeMethods.SmXVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmYVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCxVirtualScreen),
        NativeMethods.GetSystemMetrics(NativeMethods.SmCyVirtualScreen));

    /// <summary>The monitor's bounds in virtual-desktop physical pixels.</summary>
    public static CaptureRect MonitorBounds(IntPtr monitor)
    {
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (monitor == IntPtr.Zero || !NativeMethods.GetMonitorInfo(monitor, ref info)) return default;
        return new CaptureRect(info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom);
    }

    /// <summary>The monitor a rectangle's centre lands on (never null for a non-empty rect).</summary>
    public static IntPtr MonitorOf(CaptureRect region)
    {
        var centre = new NativeMethods.Point((region.Left + region.Right) / 2, (region.Top + region.Bottom) / 2);
        return NativeMethods.MonitorFromPoint(centre, NativeMethods.MonitorDefaultToNearest);
    }

    /// <summary>
    /// Clip <paramref name="virtualRegion"/> to the monitor under its centre and express it in that
    /// monitor's local pixels.
    /// </summary>
    /// <returns>False when the region is unusable (empty, or entirely outside the monitor).</returns>
    public static bool RegionToLocal(CaptureRect virtualRegion, out IntPtr monitor, out CaptureRect localBox,
        out CaptureRect effectiveVirtual)
    {
        monitor = MonitorOf(virtualRegion);
        localBox = default;
        effectiveVirtual = default;
        if (monitor == IntPtr.Zero) return false;

        var bounds = MonitorBounds(monitor);
        if (bounds.IsEmpty) return false;

        localBox = new CaptureRect(
            Math.Max(0, virtualRegion.Left - bounds.Left),
            Math.Max(0, virtualRegion.Top - bounds.Top),
            Math.Min(bounds.Width, virtualRegion.Right - bounds.Left),
            Math.Min(bounds.Height, virtualRegion.Bottom - bounds.Top));
        if (localBox.IsEmpty) return false;

        effectiveVirtual = localBox.Offset(bounds.Left, bounds.Top);
        return true;
    }
}
