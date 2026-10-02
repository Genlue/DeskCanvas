using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace DeskCanvas.Services;

/// <summary>
/// Lets the wheel scroll a sidebar from anywhere inside it — the gaps between the cards and the
/// strip's empty areas included, not only the widgets themselves.
/// <para>
/// In 毛玻璃 the sidebar's window region is clipped to its cards, because that is the only way to
/// confine the OS acrylic to them. A GDI window region governs <b>hit-testing</b> as much as it
/// governs drawing, so a pointer over a gap is over no window at all: the wheel event goes to
/// whatever happens to be behind the sidebar and the strip does not scroll. The other materials
/// composite through per-pixel alpha, keep the whole window interactive, and never had this problem.
/// </para>
/// <para>
/// A low-level wheel hook is the one mechanism that can reach those events without putting frost —
/// or a click sink — where it does not belong. It runs only while at least one sidebar is open, and
/// it swallows an event solely when the pointer is inside an open sidebar's strip <b>and</b> outside
/// every one of its cards: those are exactly the places the window never hears about. Everything
/// else — every other event, and the wheel over a card — is handed straight on untouched, so a
/// widget keeps its own scroller, Ctrl keeps bypassing it, and a click on a gap still reaches the
/// desktop.
/// </para>
/// </summary>
public static class SidebarWheelHook
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEWHEEL = 0x020A;

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    /// <summary>
    /// The live registrations, one per open sidebar. Published as an immutable array so the hook can
    /// walk it without a lock: it runs on the UI thread's message pump and must never block.
    /// </summary>
    private static volatile Registration[] registrations = [];

    /// <summary>Rooted so the delegate can never be collected while the hook is installed.</summary>
    private static readonly LowLevelMouseProc callback = Proc;

    private static readonly object Gate = new();
    private static IntPtr hook;
    private static int users;

    private sealed class Registration(Func<int, int, bool> hitTest, Action<int> scroll)
    {
        /// <summary>Whether the physical-pixel point sits on a gap an open sidebar should scroll.</summary>
        public readonly Func<int, int, bool> HitTest = hitTest;

        /// <summary>Scroll that sidebar by <c>delta</c> wheel notches.</summary>
        public readonly Action<int> Scroll = scroll;
    }

    /// <summary>
    /// Take one reference on the hook. The first caller installs it; the reference is what keeps a
    /// second open sidebar from tearing it down under the first — and every sidebar registers its own
    /// hit-test, because with more than one screen there is more than one strip to check.
    /// </summary>
    public static void Acquire(Func<int, int, bool> hitTest, Action<int> scroll)
    {
        lock (Gate)
        {
            registrations = [.. registrations, new Registration(hitTest, scroll)];
            if (Interlocked.Increment(ref users) == 1 && hook == IntPtr.Zero)
                hook = SetWindowsHookEx(WH_MOUSE_LL, callback, GetModuleHandle(null), 0);
        }
    }

    /// <summary>Drop one reference. The last one out uninstalls the hook.</summary>
    public static void Release()
    {
        lock (Gate)
        {
            if (Interlocked.Decrement(ref users) > 0 || registrations.Length == 0) return;
            registrations = [.. registrations.Skip(1)];
            var handle = hook;
            hook = IntPtr.Zero;
            if (handle != IntPtr.Zero) UnhookWindowsHookEx(handle);
        }
    }

    private static IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WM_MOUSEWHEEL)
        {
            var list = registrations;
            if (list.Length > 0)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                // The wheel delta rides in the high word of mouseData; a non-zero delta is the only
                // thing this hook has any business acting on.
                var delta = (short)((info.MouseData >> 16) & 0xFFFF);
                if (delta != 0)
                {
                    foreach (var registration in list)
                    {
                        if (!registration.HitTest(info.X, info.Y)) continue;
                        registration.Scroll(delta);
                        return new IntPtr(1);
                    }
                }
            }
        }

        return CallNextHookEx(hook, code, wParam, lParam);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);
}
