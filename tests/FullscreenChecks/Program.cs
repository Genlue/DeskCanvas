using System.Diagnostics;
using DeskCanvas.Services;

namespace FullscreenChecks;

/// <summary>
/// Verifies the fullscreen / maximized detection rule that suspends the widgets.
///
/// The decision function (<see cref="FullscreenWatcherService.AllScreensCovered"/>) is pure,
/// so every case is checked deterministically — including the coordinate pathology observed
/// on the development machine, where a DPI-unaware caller saw a normal window as
/// (480,142)-(2538,1306) on a 2048×1152 screen (DWM physical pixels next to virtualized
/// GetWindowRect values). That must NOT be mistaken for a covered desktop.
///
/// Multi-screen is part of the contract: the widgets are only suspended when <b>every</b>
/// attached screen is covered, so a maximized game on the primary monitor keeps the widgets
/// on the secondary one alive.
///
/// The live foreground-window signals are printed as well, so the Win32 layer is exercised.
/// </summary>
class Program
{
    private static int failures;

    private static readonly ScreenCoverage Primary = new(new ScreenRect(0, 0, 2048, 1152), new ScreenRect(0, 35, 2048, 1087));
    private static readonly ScreenCoverage Secondary = new(new ScreenRect(2048, 0, 4096, 1152), new ScreenRect(2048, 35, 4096, 1087));

    static int Main()
    {
        Console.WriteLine("=== Fullscreen / maximized detection checks ===");
        Console.WriteLine();

        // --- single screen: a maximized or fullscreen window is enough ---
        Covered("a maximized window suspends the widgets", Window(normal: true, maximized: true));
        Covered("a maximized window that does not fill its screen still suspends the widgets",
            Window(new ScreenRect(378, 114, 2037, 1052), maximized: true));
        Covered("a borderless window filling the monitor suspends the widgets", Window(Primary.Bounds));
        Covered("a borderless window filling the work area suspends the widgets", Window(Primary.WorkArea));
        Covered("1 px inaccuracy still counts as covering", Window(new ScreenRect(1, 1, 2047, 1151)));
        Covered("a maximized window nobody has focused still suspends the widgets",
            Window(Primary.WorkArea, maximized: true, foreground: false));

        // --- phantom full-screen windows must not suspend anything ---
        // Observed live on the development machine: the Windows input-experience host and the
        // NVIDIA GeForce Overlay both span the screen while nobody is looking at them. Counting
        // them froze every widget while the desktop was in plain sight — the monitor dials stopped
        // reading and the frameless clock stopped re-rendering its material.
        Console.WriteLine();
        Console.WriteLine("--- phantom full-screen windows ---");

        Visible("a screen-sized window that is not focused does not suspend",
            Window(Primary.Bounds, foreground: false));
        Visible("a screen-sized unfocused window on the secondary keeps the widgets alive",
            Window(Secondary.Bounds, foreground: false));

        Multi("unfocused screen-sized phantoms cannot cover any screen",
            [Primary, Secondary],
            [Window(Primary.Bounds, foreground: false), Window(Secondary.Bounds, foreground: false)],
            covered: false);

        // --- normal windows keep the widgets alive ---
        Visible("a normal floating window does not suspend", Window(new ScreenRect(378, 114, 2037, 1052)));
        Visible("a half-screen snapped window does not suspend", Window(new ScreenRect(0, 0, 1024, 1152)));
        Visible("a window taller than the work area but not maximized does not suspend",
            Window(new ScreenRect(200, 0, 1400, 1152)));

        // --- the real coordinate pathology (physical DWM bounds, virtualized everything else) ---
        Visible("a window extending past its monitor (DPI mixing) does not suspend",
            Window(new ScreenRect(480, 142, 2538, 1306)));
        Visible("a window starting right of the screen origin does not suspend",
            Window(new ScreenRect(64, 0, 2048, 1152)));

        // --- exclusions ---
        Visible("our own window never suspends", Window(Primary.Bounds, own: true));
        Visible("the desktop / taskbar never suspends", Window(Primary.Bounds, shell: true));
        Visible("a minimized window never suspends", Window(Primary.Bounds, minimized: true));
        Visible("an invisible window never suspends", Window(Primary.Bounds, visible: false));

        // --- multi-screen: every screen has to be covered ---
        Console.WriteLine();
        Console.WriteLine("--- multi-screen ---");

        Multi("a borderless window spanning both screens suspends",
            [Primary, Secondary],
            [Window(new ScreenRect(0, 0, 4096, 1152))], covered: true);

        Multi("a maximized window on each screen suspends",
            [Primary, Secondary],
            [Window(new ScreenRect(0, 35, 2048, 1087), maximized: true),
             Window(new ScreenRect(2048, 35, 4096, 1087), maximized: true)], covered: true);

        Multi("a fullscreen window on each screen suspends",
            [Primary, Secondary],
            [Window(Primary.Bounds), Window(Secondary.Bounds)], covered: true);

        Multi("a maximized window on the primary only keeps the widgets alive",
            [Primary, Secondary],
            [Window(new ScreenRect(0, 35, 2048, 1087), maximized: true)], covered: false);

        Multi("a fullscreen window on the secondary only keeps the widgets alive",
            [Primary, Secondary],
            [Window(Secondary.Bounds)], covered: false);

        Multi("a maximized primary plus an ordinary window on the secondary keeps the widgets alive",
            [Primary, Secondary],
            [Window(new ScreenRect(0, 35, 2048, 1087), maximized: true),
             Window(new ScreenRect(2200, 100, 3900, 1000))], covered: false);

        Multi("a maximized window is attributed by its centre, not by coordinates",
            [Primary, Secondary],
            [Window(new ScreenRect(-4, -4, 2050, 1150), maximized: true),
             Window(new ScreenRect(2044, -4, 4098, 1150), maximized: true)], covered: true);

        Multi("a cloaked (dropped) window cannot cover its screen",
            [Primary, Secondary],
            [Window(Primary.Bounds)], covered: false);

        Multi("no attached screens never suspends", [], [Window(Primary.Bounds)], covered: false);

        // --- live signals (diagnostic) ---
        Console.WriteLine();
        var observed = FullscreenWatcherService.Observe();
        Console.WriteLine($"live foreground window: exists={observed.HasWindow} class='{observed.ClassName}' " +
                          $"pid={observed.ProcessId} own={observed.IsOwnProcess} shell={observed.IsShellWindow} " +
                          $"maximized={observed.IsMaximized} minimized={observed.IsMinimized} visible={observed.IsVisible}");

        var watcher = new FullscreenWatcherService();
        watcher.Evaluate();
        watcher.Evaluate();
        Console.WriteLine($"live watcher verdict: suspended={watcher.IsFullscreen} " +
                          "(expected False while a normal window is focused, no anchor attached)");
        Check("a watcher without an anchor reports 'not covered'", !watcher.IsFullscreen);

        Console.WriteLine();
        Console.WriteLine("--- sidebar overlay: true-fullscreen rule ---");
        SidebarOverlayRule();

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static WindowCoverage Window(ScreenRect bounds,
        bool own = false, bool shell = false, bool minimized = false, bool visible = true, bool maximized = false,
        bool foreground = true) =>
        new(own, shell, minimized, visible, maximized, bounds, foreground);

    private static WindowCoverage Window(bool normal, bool maximized) =>
        Window(new ScreenRect(378, 114, 2037, 1052), maximized: maximized);

    private static void Covered(string what, WindowCoverage window)
    {
        var result = FullscreenWatcherService.AllScreensCovered([Primary], [window]);
        Check(what, result);
    }

    private static void Visible(string what, WindowCoverage window)
    {
        var result = FullscreenWatcherService.AllScreensCovered([Primary], [window]);
        Check(what, !result);
    }

    private static void Multi(string what, IReadOnlyList<ScreenCoverage> screens,
        IReadOnlyList<WindowCoverage> windows, bool covered)
    {
        var result = FullscreenWatcherService.AllScreensCovered(screens, windows);
        Check(what, result == covered);
    }

    /// <summary>
    /// The rule the sidebar uses to decide whether to stay: a screen-covering window is only
    /// "fullscreen" when it is a real borderless/exclusive app. The desktop shell (Progman/WorkerW)
    /// spans the monitor too, and counting it pulled the sidebar back a second after it opened.
    /// </summary>
    private static void SidebarOverlayRule()
    {
        Check("a borderless window covering the monitor counts",
            FullscreenWatcherService.IsTrueFullscreen(false, false, false, true, false, false, true));
        Check("a maximized window never counts",
            !FullscreenWatcherService.IsTrueFullscreen(false, false, false, true, false, true, true));
        Check("the desktop shell never counts (Progman/WorkerW)",
            !FullscreenWatcherService.IsTrueFullscreen(true, false, false, true, false, false, true));
        Check("a layered desktop overlay never counts",
            !FullscreenWatcherService.IsTrueFullscreen(false, true, false, true, false, false, true));
        Check("our own sidebar window never counts",
            !FullscreenWatcherService.IsTrueFullscreen(false, false, true, true, false, false, true));
        Check("a window not covering the monitor does not count",
            !FullscreenWatcherService.IsTrueFullscreen(false, false, false, true, false, false, false));
        Check("a minimized window does not count",
            !FullscreenWatcherService.IsTrueFullscreen(false, false, false, true, true, false, true));
        Check("an invisible window does not count",
            !FullscreenWatcherService.IsTrueFullscreen(false, false, false, false, false, false, true));
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }
}
