using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Themes.Fluent;
using DeskCanvas.Views;
using Tools.Models;
using Tools.Views;

namespace WidgetAdaptiveChecks;

/// <summary>
/// Checks the widget adaptive-sizing contract from the user-visible defect: the translator
/// widget's secondary-panel (expand) button was cut in half at a 160px free-mode size, because
/// the control bar's fixed Auto columns simply overflowed and the card clipped them.
///
/// <para>
/// Two layers pin two contracts:
/// 1. <see cref="Widget.ComputeContentMinWindowSize"/> — the host's content-driven minimum
///    window size math (content DesiredSize + card margins + outline, floored at 48).
/// 2. The translator control bar's measure-driven degradation — at ANY width the bar either
///    fits (natural width ≤ available) after hiding low-frequency buttons, or has exhausted
///    its degradation sequence; the expand button (the panel's only entry point) survives
///    everywhere except the very last level.
/// </para>
///
/// The host loop (DesiredSize → MinWidth/MinHeight → grow-to-fit) and the view's degradation
/// close each other: degradation shrinks DesiredSize, so the host's minimum never exceeds
/// what the view has already tried to fit. That closure is asserted here via the same
/// DesiredSize the host reads.
///
/// Usage: dotnet run --project tests/WidgetAdaptiveChecks -c Release
/// </summary>
internal static class Program
{
    private static int failures;

    private static void Check(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {message}");
        }
        else
        {
            Console.WriteLine($"  [FAIL] {message}");
            failures++;
        }
    }

    [STAThread]
    private static int Main()
    {
        Console.WriteLine("=== Widget adaptive sizing checks ===");
        Console.WriteLine();

        AppBuilder.Configure<Application>().UseHeadless(new AvaloniaHeadlessPlatformOptions()).SetupWithoutStarting();
        Application.Current!.Styles.Add(new FluentTheme());

        CheckMinSizeMath();
        CheckControlBarDegradation();

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED!");
            return 0;
        }
        Console.WriteLine($"{failures} CHECK(S) FAILED!");
        return 1;
    }

    // ---- 1. Host-side minimum-size math ----

    private static void CheckMinSizeMath()
    {
        Console.WriteLine("--- Content-driven minimum window size math ---");
        var method = typeof(Widget).GetMethod("ComputeContentMinWindowSize",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        Check(method != null, "Widget.ComputeContentMinWindowSize exists");

        if (method == null) return;

        // No margins, no outline: the window minimum is the content minimum.
        var bare = InvokeMin(method, new Size(160, 120), new Thickness(0), new Thickness(0));
        Check(bare == (160, 120), $"no margin/outline → min == desired (got {bare})");

        // The real card geometry: 12px margin on each side + a 1px outline ring.
        var carded = InvokeMin(method, new Size(160, 120), new Thickness(12), new Thickness(1));
        Check(carded == (186, 146), $"12 margin + 1 outline → min = desired + 26 (got {carded})");

        // Fractional desired sizes round up — a fractional pixel short would keep the
        // content 0.4px truncated forever.
        var rounded = InvokeMin(method, new Size(100.4, 99.6), new Thickness(0), new Thickness(0));
        Check(rounded == (101, 100), $"fractional desired rounds up (got {rounded})");

        // The 48px floor: a view that reports nearly no minimum (Viewbox clocks) keeps
        // the historical 48px free-resize floor.
        var floored = InvokeMin(method, new Size(10, 8), new Thickness(0), new Thickness(0));
        Check(floored == (48, 48), $"tiny desired keeps the 48px floor (got {floored})");
    }

    private static (double, double) InvokeMin(
        MethodInfo method, Size desired, Thickness margin, Thickness outline) =>
        ((double, double))method.Invoke(null,
            [desired, margin, outline, 48.0])!;

    // ---- 2. Translator control bar measure-driven degradation ----

    private static void CheckControlBarDegradation()
    {
        Console.WriteLine();
        Console.WriteLine("--- Translator control bar degradation (bug: expand button truncated at 160px) ---");

        // One fresh window+view per width: a shown window is only ever sized once here
        // (no message pump is running, so resize churn on a live window is not a supported
        // path), and a fresh view doubles as per-case isolation for the monotonicity read.
        (bool engine, bool swap, bool translate, bool expand)? previous = null;

        foreach (var (w, h) in new[] { (320, 170), (260, 160), (200, 160), (160, 160), (140, 140), (110, 110), (96, 96) })
        {
            var (view, bar, engine, swap, translate, expand, window) = LayoutAt(w, h);

            // The bar lives inside the view's own small-tier margin (tier-small adds
            // Margin 6,5 to the view), so the bar's budget is the view's arranged width,
            // while the view's DesiredSize — which is what the host's minimum-size loop
            // reads, own margin included — is budgeted by the window client area.
            var availableForBar = view.Bounds.Width;
            var clientWidth = window.Bounds.Width;
            var natural = NaturalBarWidth(bar);

            // Fit holds above the floor; at the floor the last degradation level may
            // legitimately still overflow in a bare window (the real host's minimum-size
            // clamp grows the window instead — tested via ComputeContentMinWindowSize).
            Check(natural <= availableForBar + 0.5 || !expand.IsVisible,
                $"{w}×{h}: control bar fits after degradation (natural {natural:F0} ≤ available {availableForBar:F0})");
            Check(view.DesiredSize.Width <= clientWidth + 0.5,
                $"{w}×{h}: view DesiredSize {view.DesiredSize.Width:F0} ≤ client {clientWidth:F0} (host min-size loop sees no overflow)");

            if (view.DesiredSize.Width > clientWidth + 0.5)
            {
                DumpOverflow(view, availableForBar);
            }

            var current = (engine: engine.IsVisible, swap: swap.IsVisible,
                translate: translate.IsVisible, expand: expand.IsVisible);
            if (previous is { } prev)
            {
                Check(NeverReappearing(prev.engine, current.engine) && NeverReappearing(prev.swap, current.swap)
                      && NeverReappearing(prev.translate, current.translate) && NeverReappearing(prev.expand, current.expand),
                    $"{w}×{h}: visibility degrades monotonically " +
                    $"(engine {current.engine} swap {current.swap} translate {current.translate} expand {current.expand})");
            }
            previous = current;

            if (w == 160)
            {
                // The reported bug, pinned literally: at the 160px size from the screenshot
                // the expand button — the secondary panel's only entry point — must be
                // intact (inside the card, covered by the fit invariant) and on screen.
                Check(expand.IsVisible, "160×160: expand button (secondary panel entry) still visible");
                Check(swap.IsVisible, "160×160: swap button still visible");
            }

            window.Close();
        }

        // The degradation bottom: at the floor, only the last resort may hide the expand
        // button — a half-drawn button (the original defect) must never be the outcome.
        var (floorView, floorBar, _, _, _, floorExpand, floorWindow) = LayoutAt(64, 64);
        Check(NaturalBarWidth(floorBar) <= floorView.Bounds.Width + 0.5 || !floorExpand.IsVisible,
            "64×64: bar either fits or has collapsed to the last degradation level (never half-drawn)");
        floorWindow.Close();
    }

    /// <summary>
    /// The bar's natural width, read (never measured) off the live tree: a manual
    /// Measure(infinity) on an already-measured control overwrites its DesiredSize and
    /// ping-pongs the running layout forever — the very defect this view once had.
    /// </summary>
    private static double NaturalBarWidth(Grid bar)
    {
        double natural = 0;
        foreach (var child in bar.Children)
        {
            if (child is not Layoutable { IsVisible: true } element) continue;
            natural += element.DesiredSize.Width + element.Margin.Left + element.Margin.Right;
        }
        return natural;
    }

    private static (
        TranslatorView View, Grid Bar, Button Engine, Button Swap, Button Translate, Button Expand, Window Window)
        LayoutAt(double width, double height)
    {
        var window = new Window
        {
            SystemDecorations = SystemDecorations.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-32000, -32000),
            Width = width,
            Height = height,
            Content = new TranslatorView(new TranslatorModel())
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var view = (TranslatorView)window.Content!;
        return (
            view,
            GetField<Grid>(view, "ControlBarGrid")!,
            GetField<Button>(view, "EngineBtn")!,
            GetField<Button>(view, "SwapBtn")!,
            GetField<Button>(view, "TranslateBtn")!,
            GetField<Button>(view, "ExpandBtn")!,
            window);
    }

    /// <summary>Visibility as width SHRINKS (the loop descends): a hidden button must never reappear.</summary>
    private static bool NeverReappearing(bool previous, bool current) => previous || !current;

    /// <summary>Print the subtree branches whose DesiredSize exceeds the constraint, widest offender first.</summary>
    private static void DumpOverflow(TranslatorView view, double available)
    {
        Console.WriteLine($"      [trace] overflow hunting: available {available:F1}, view desired {view.DesiredSize.Width:F1}");
        void Walk(Visual visual, string indent)
        {
            foreach (var child in visual.GetVisualChildren())
            {
                if (child is not Layoutable l) continue;
                if (l.DesiredSize.Width > available + 0.5)
                {
                    Console.WriteLine($"      [trace] {indent}{l.GetType().Name}{(l is TextBlock tb ? $"(\"{(tb.Text ?? "")[..Math.Min(16, tb.Text?.Length ?? 0)]}\")" : "")} desired {l.DesiredSize.Width:F1} (bounds {l.Bounds.Width:F1})");
                    Walk(child, indent + "  ");
                }
            }
        }
        Walk(view, "");
    }

    private static T? GetField<T>(object instance, string name) where T : class
    {
        var field = instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        return field?.GetValue(instance) as T;
    }
}
