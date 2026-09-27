using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using uWidgets.Services;

namespace BottomBandChecks;

/// <summary>
/// Checks the desktop z-order bands enforced by <see cref="WidgetZOrder"/>:
/// <list type="number">
/// <item>widget windows are pinned to the very bottom — no activation, drag or re-show
/// may raise them above ordinary application windows;</item>
/// <item>secondary panels (weather forecast &amp; co.) sit just above the widget band —
/// above every widget, still below ordinary application windows;</item>
/// <item>with no widget alive, a panel falls back to the bottom of the band.</item>
/// </list>
/// All windows live off-screen (the same -32000 rect the anchor window uses), so the
/// suite never disturbs the desktop. Enforcement is triggered with native
/// SetWindowPos(HWND_TOP) calls — the same z-order change every activation performs —
/// which makes the assertions deterministic even when the foreground transfer is denied
/// to a background process.
/// </summary>
internal static class Program
{
    private static int failures;

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("=== Widget z-order band checks ===");
        Console.WriteLine();

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        var widgetA = MakeWindow("widgetA");
        var widgetB = MakeWindow("widgetB");
        var panel = MakeWindow("panel");
        var normal = MakeWindow("normal");

        ShowPinned(widgetA, WidgetZOrder.PinWidgetToBottom);
        ShowPinned(widgetB, WidgetZOrder.PinWidgetToBottom);
        ShowPinned(panel, WidgetZOrder.PinPanelAboveWidgets);
        normal.Show();
        RunJobs();

        Check("the ordinary window stays above the panel", Above(normal, panel));
        Check("the panel is placed above both widgets", Above(panel, widgetA) && Above(panel, widgetB));

        // Any activation is just a SetWindowPos(HWND_TOP) under the hood — raise each
        // window to the top natively and require the band to absorb the change.
        RaiseToTop(widgetB);
        RunJobs();
        Check("raising a widget to the top keeps it below the ordinary window", Above(normal, widgetB));
        Check("raising a widget to the top rewrites it to the very bottom", Above(widgetA, widgetB));
        Check("raising a widget to the top keeps it below the panel", Above(panel, widgetB));

        RaiseToTop(panel);
        RunJobs();
        Check("raising the panel to the top keeps it below the ordinary window", Above(normal, panel));
        Check("raising the panel rewrites it to just above the widget band",
            Above(panel, widgetA) && Above(widgetA, widgetB));

        // Avalonia activation must be absorbed the same way. Whether the foreground
        // transfer is granted depends on the session; if it is denied no z-change
        // happens and the assertion trivially holds — it can never false-fail.
        widgetA.Activate();
        RunJobs();
        Check("window activation keeps the widget in the bottom band",
            Above(normal, widgetA) && Above(panel, widgetA));

        panel.Activate();
        RunJobs();
        Check("window activation keeps the panel above the band only",
            Above(normal, panel) && Above(panel, widgetA));

        // Fullscreen-suspend path: Hide() + Show() re-participates in the z-order and
        // must land back in the band.
        widgetB.Hide();
        RunJobs();
        widgetB.Show();
        RunJobs();
        // The band is a laissez-faire interior: a re-shown widget keeps its spot
        // relative to its fellow widgets, but must land back below the panels and
        // the ordinary windows.
        Check("re-showing a widget puts it back into the band",
            Above(normal, widgetB) && Above(panel, widgetB));

        // Real secondary-panel path: owned, taskbar-less popup shown by its widget,
        // historically Topmost=true in XAML. The flag must be demoted and the panel
        // absorbed into the band — with the flag active the old enforcement fought
        // Avalonia's property sync and froze the UI thread.
        var ownedPanel = MakeWindow("ownedPanel");
        ownedPanel.Topmost = true;
        ownedPanel.Show(widgetA);
        RunJobs();
        WidgetZOrder.PinPanelAboveWidgets(ownedPanel);
        ownedPanel.Activate();
        RunJobs();
        // "normal" is unpinned and drifts with the live desktop, so stand a FRESH
        // ordinary window in for "a window the user is working with". The bands
        // settle over a few dispatcher cycles — each placement can hand a
        // violation to the next window — so wait before asserting.
        var appWindow = MakeWindow("appWindow");
        appWindow.Show();
        RunJobs();
        var settled = WaitUntil(
            () => !ownedPanel.Topmost
                  && Above(ownedPanel, widgetA) && Above(ownedPanel, widgetB)
                  && Above(appWindow, ownedPanel),
            5000);
        Check("a Topmost owned panel is demoted and placed above the band", settled);
        appWindow.Close();
        RunJobs();
        ownedPanel.Close();
        RunJobs();

        // Band fallback: once no widget is alive, a newly pinned panel has nothing to
        // sit on and must go to the very bottom.
        widgetA.Close();
        widgetB.Close();
        RunJobs();
        var fallbackPanel = MakeWindow("fallbackPanel");
        ShowPinned(fallbackPanel, WidgetZOrder.PinPanelAboveWidgets);
        Check("a panel without live widgets falls back to the bottom",
            Above(normal, fallbackPanel) && Above(panel, fallbackPanel));

        panel.Close();
        normal.Close();
        fallbackPanel.Close();
        RunJobs();

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED");
            return 0;
        }

        Console.WriteLine($"{failures} CHECK(S) FAILED");
        return 1;
    }

    private static Window MakeWindow(string name) => new()
    {
        Title = $"BottomBandChecks {name}",
        Width = 200,
        Height = 120,
        ShowInTaskbar = false,
        ShowActivated = false,
        SystemDecorations = SystemDecorations.None,
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(-32000, -32000)
    };

    private static void ShowPinned(Window window, Action<Window> pin)
    {
        window.Show();
        RunJobs();
        pin(window);
        RunJobs();
    }

    private static void RunJobs() => Dispatcher.UIThread.RunJobs();

    /// <summary>Simulate what every activation does: request the top of the band.</summary>
    private static void RaiseToTop(Window window) =>
        SetWindowPos(HwndOf(window), IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    /// <summary>True when <paramref name="a"/> is closer to the top of the z-order than <paramref name="b"/>.</summary>
    private static bool Above(Window a, Window b) => ZIndex(HwndOf(a)) < ZIndex(HwndOf(b));

    private static int ZIndex(IntPtr target)
    {
        var index = 0;
        for (var hwnd = GetTopWindow(IntPtr.Zero); hwnd != IntPtr.Zero; hwnd = GetWindow(hwnd, GW_HWNDNEXT))
        {
            if (hwnd == target) return index;
            index++;
        }
        return int.MaxValue;
    }

    private static IntPtr HwndOf(Window window) =>
        window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint GW_HWNDNEXT = 2;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

    /// <summary>Wait until the condition holds (band enforcement is eventually consistent).</summary>
    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            RunJobs();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        RunJobs();
        return condition();
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }
}
