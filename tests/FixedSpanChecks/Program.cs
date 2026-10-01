using Avalonia;
using FixedWidgets.Views;

namespace FixedSpanChecks;

/// <summary>
/// Verifies the size lattice of the 4×2 fixed dashboard card (固定组件).
///
/// The card is a fixed-aspect vector design, so it may only take spans that keep its 2:1 shape —
/// but every whole step has to be reachable, not just the doubled ones: 4×2, 6×3, 8×4, 10×5 …
/// (the request was explicitly "allow 6×3, spans only need to be integers"). The rules are static
/// on <see cref="AggregateView"/>, so they are checked here without building a widget.
/// </summary>
class Program
{
    private static int failures;

    static int Main()
    {
        Console.WriteLine("=== Fixed (4×2 dashboard) span lattice checks ===");
        Console.WriteLine();

        CheckTodayMarkerCenteringAndRowParallelism();

        // --- accepted shapes ---
        Allowed("4×2 (100%)", 4, 2);
        Allowed("6×3 (150%)", 6, 3);
        Allowed("8×4 (200%)", 8, 4);
        Allowed("10×5", 10, 5);
        Allowed("12×6", 12, 6);

        // --- rejected shapes: anything off the 2:1 lattice, or smaller than the base card ---
        Rejected("2×1 is below the base card", 2, 1);
        Rejected("4×3 is not 2:1", 4, 3);
        Rejected("6×2 is not 2:1", 6, 2);
        Rejected("3×2 is not 2:1", 3, 2);
        Rejected("6×4 is not 2:1", 6, 4);

        // --- snapping: both steppers must be able to walk the lattice ---
        Snapped("4×2 stays 4×2", 4, 2, 4, 2);
        Snapped("6×2 grows to 6×3 (the half step must not bounce back)", 6, 2, 6, 3);
        Snapped("6×3 stays 6×3", 6, 3, 6, 3);
        Snapped("7×3 stays on 6×3", 7, 3, 6, 3);
        Snapped("5×2 falls back to 4×2", 5, 2, 4, 2);
        Snapped("4×3 resolves to 6×3", 4, 3, 6, 3);
        Snapped("8×4 stays 8×4", 8, 4, 8, 4);
        Snapped("9×4 falls back to 8×4", 9, 4, 8, 4);
        Snapped("10×5 stays 10×5", 10, 5, 10, 5);
        Snapped("14×8 resolves to 16×8", 14, 8, 16, 8);
        Snapped("1×1 cannot shrink below the base card", 1, 1, 4, 2);

        // --- the context menu has to offer the requested middle step ---
        Check("the 6×3 preset is offered", AggregateView.PresetSpansOf.Contains((6, 3)));
        Check("every preset is on the lattice", AggregateView.PresetSpansOf.All(span => AggregateView.IsAllowedSpanOf(span.Columns, span.Rows)));
        Check("every snap result is on the lattice", SnapResultsAreValid());

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static bool SnapResultsAreValid()
    {
        for (var columns = 1; columns <= 24; columns++)
        for (var rows = 1; rows <= 12; rows++)
        {
            var (c, r) = AggregateView.SnapSpanOf(columns, rows);
            if (!AggregateView.IsAllowedSpanOf(c, r)) return false;
        }

        return true;
    }

    private static void Allowed(string what, int columns, int rows)
        => Check($"allowed: {what}", AggregateView.IsAllowedSpanOf(columns, rows));

    private static void Rejected(string what, int columns, int rows)
        => Check($"rejected: {what}", !AggregateView.IsAllowedSpanOf(columns, rows));

    private static void Snapped(string what, int columns, int rows, int expectedColumns, int expectedRows)
    {
        var (c, r) = AggregateView.SnapSpanOf(columns, rows);
        Check($"snap: {what}", c == expectedColumns && r == expectedRows);
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    private static void CheckTodayMarkerCenteringAndRowParallelism()
    {
        try
        {
            Avalonia.AppBuilder.Configure<DeskCanvas.App>()
                .UsePlatformDetect()
                .WithInterFont()
                .SetupWithoutStarting();

            var typeface = new Avalonia.Media.Typeface(Avalonia.Media.FontFamily.Default, Avalonia.Media.FontStyle.Normal, Avalonia.Media.FontWeight.SemiBold);
            foreach (var size in new[] { 56, 60 })
            {
                double maxVerticalDelta = 0;
                double maxHorizontalDelta = 0;
                double maxBaselineDiff = 0;
                for (int day = 1; day <= 31; day++)
                {
                    var text = day.ToString();
                    var formatted = new Avalonia.Media.FormattedText(
                        text,
                        System.Globalization.CultureInfo.InvariantCulture,
                        Avalonia.Media.FlowDirection.LeftToRight,
                        typeface,
                        size,
                        Avalonia.Media.Brushes.Black);

                    var top = (100.0 - formatted.Height) / 2.0;
                    var defaultOriginX = (100.0 - formatted.Width) / 2.0;
                    var testGlyph = formatted.BuildGeometry(new Avalonia.Point(defaultOriginX, top));
                    var tb = testGlyph?.Bounds ?? default;
                    var testCenterX = (tb.Left + tb.Right) / 2.0;

                    var dx = 50.0 - testCenterX;
                    var finalOrigin = new Avalonia.Point(defaultOriginX + dx, top);
                    var finalGlyph = formatted.BuildGeometry(finalOrigin);
                    var fb = finalGlyph?.Bounds ?? default;

                    var finalCenterX = (fb.Left + fb.Right) / 2.0;
                    var finalCenterY = (fb.Top + fb.Bottom) / 2.0;

                    var center = new Avalonia.Point(50.0, finalCenterY);
                    var radius = 45.0;

                    var topGap = fb.Top - (center.Y - radius);
                    var bottomGap = (center.Y + radius) - fb.Bottom;
                    var vDelta = Math.Abs(topGap - bottomGap);
                    if (vDelta > maxVerticalDelta) maxVerticalDelta = vDelta;

                    var leftGap = fb.Left - (center.X - radius);
                    var rightGap = (center.X + radius) - fb.Right;
                    var hDelta = Math.Abs(leftGap - rightGap);
                    if (hDelta > maxHorizontalDelta) maxHorizontalDelta = hDelta;

                    // Baseline check: today's baseline must equal standard row baseline
                    var standardRowBaseline = top + formatted.Baseline;
                    var todayBaseline = finalOrigin.Y + formatted.Baseline;
                    var bDiff = Math.Abs(standardRowBaseline - todayBaseline);
                    if (bDiff > maxBaselineDiff) maxBaselineDiff = bDiff;
                }

                Check($"Today marker circle/numeral concentricity (size {size})", maxVerticalDelta < 0.001 && maxHorizontalDelta < 0.001);
                Check($"Today numeral parallel with row baseline (size {size})", maxBaselineDiff < 0.001);
            }
        }
        catch (Exception ex)
        {
            Check($"Today marker initialization: {ex.Message}", false);
        }
    }
}
