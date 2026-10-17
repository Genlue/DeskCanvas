using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using Path = System.IO.Path;

namespace ScrollBarHoverChecks;

/// <summary>
/// Regression check for the global scrollbar fix: hovering a scrollbar used to make the Fluent
/// theme paint its "expanded" state — a thick, dark track box across the whole scrollbar.
/// The fix (Styles/ScrollBar.axaml) pins every expanded-state visual back to the collapsed values,
/// so hover must be pixel-identical to idle, for both orientations.
///
/// The expanded state is reached without a mouse by setting AllowAutoHide=false, which makes
/// ScrollBar.IsExpanded true (exactly the state pointer-over produces).
/// A control case forces the same scrollbar back onto raw Fluent and asserts it DOES differ —
/// proving the comparison actually has the sensitivity to catch the bug.
/// </summary>
internal static class Program
{
    private const int Scale = 2;
    private const int SignificantDelta = 3;

    [STAThread]
    private static int Main(string[] args)
    {
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "dist/scrollbar-checks");
        Directory.CreateDirectory(outDir);
        Console.WriteLine("=== ScrollBar hover (expanded state) checks ===");

        AppBuilder.Configure<DeskCanvas.App>().UsePlatformDetect().SetupWithoutStarting();
        var app = Application.Current!;
        app.RequestedThemeVariant = ThemeVariant.Dark;

        if (!app.TryFindResource("DeskCanvasScrollBar", out var fixedTheme))
            throw new Exception("Resource 'DeskCanvasScrollBar' is not registered — Styles/ScrollBar.axaml is not merged.");
        var myTheme = (ControlTheme)fixedTheme!;

        if (!app.TryFindResource(typeof(ScrollBar), out var fluentTheme))
            throw new Exception("Fluent's ScrollBar control theme could not be resolved.");
        Console.WriteLine("  PASS: both the fixed theme and Fluent's base theme resolve.");

        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var variantTag = variant == ThemeVariant.Dark ? "dark" : "light";
            app.RequestedThemeVariant = variant;

            foreach (var orientation in new[] { Orientation.Vertical, Orientation.Horizontal })
            {
                var tag = $"{variantTag}-{orientation.ToString().ToLowerInvariant()}";
                var idlePath = Path.Combine(outDir, $"scrollbar-{tag}-idle.png");
                var hoverPath = Path.Combine(outDir, $"scrollbar-{tag}-hover.png");
                var nativePath = Path.Combine(outDir, $"scrollbar-{tag}-native-hover.png");

                Console.WriteLine($"\n--- {variantTag} / {orientation} ---");

                var idle = Render(orientation, variant, fixedTheme: true, expanded: false, idlePath);
                AssertTheme(idle.Bar, myTheme, $"{tag} idle");

                var hover = Render(orientation, variant, fixedTheme: true, expanded: true, hoverPath);
                AssertTheme(hover.Bar, myTheme, $"{tag} hover");

                if (hover.Track.Opacity != 0)
                    throw new Exception($"[{tag}] TrackRect is visible while expanded (Opacity={hover.Track.Opacity}); the dark track box is back.");
                Console.WriteLine("  PASS: expanded TrackRect stays transparent (Opacity = 0).");

                if (Alpha(hover.RootBackground) > Alpha(idle.RootBackground))
                    throw new Exception($"[{tag}] ScrollBar Root paints a background while expanded ({hover.RootBackground}).");
                Console.WriteLine($"  PASS: expanded Root paints no background (alpha {Alpha(hover.RootBackground):0.##}).");

                if (hover.MaxButtonOpacity > 0)
                    throw new Exception($"[{tag}] Page/arrow buttons are visible while expanded (Opacity={hover.MaxButtonOpacity}).");
                Console.WriteLine("  PASS: expanded page/arrow buttons stay hidden (Opacity = 0).");

                var transform = hover.Thumb.RenderTransform;
                if (transform is null || transform.Value.Equals(Matrix.Identity))
                    throw new Exception($"[{tag}] Thumb lost its collapsed scale while expanded (RenderTransform={transform?.Value}); it grows to full width again.");
                if (hover.ThumbTransform != idle.ThumbTransform)
                    throw new Exception($"[{tag}] Thumb scale differs between hover and idle ({hover.ThumbTransform} vs {idle.ThumbTransform}).");
                Console.WriteLine($"  PASS: expanded Thumb keeps the collapsed scale ({transform.Value}).");

                AssertPixels(hoverPath, idlePath, expectIdentical: true, $"{tag} expanded vs idle");

                // Control case: the same scrollbar forced onto Fluent's own theme must still show the
                // problem, otherwise this check could never have caught anything.
                var native = Render(orientation, variant, fixedTheme: false, expanded: true, nativePath);
                AssertTheme(native.Bar, myTheme, $"{tag} control", expectFixed: false);

                if (native.Track.Opacity != 1 && native.MaxButtonOpacity <= 0)
                    Console.WriteLine("  NOTE: Fluent's expanded track stayed hidden — the control case is weak.");
                else
                    Console.WriteLine($"  PASS: Fluent's own expanded state is visible (TrackRect.Opacity={native.Track.Opacity}, buttons={native.MaxButtonOpacity}).");

                AssertPixels(nativePath, idlePath, expectIdentical: false, $"{tag} Fluent expanded vs idle");
            }
        }

        Console.WriteLine($"\nAll scrollbar hover checks passed. Output: {outDir}");
        return 0;
    }

    /// <summary>Effective painted alpha of a brush: a Transparent-coloured brush still reports Opacity 1.</summary>
    private static double Alpha(IBrush? brush) => brush switch
    {
        null => 0,
        ISolidColorBrush solid => solid.Color.A / 255.0 * solid.Opacity,
        _ => 1,
    };

    private static void AssertTheme(ScrollBar bar, ControlTheme expected, string label, bool expectFixed = true)
    {
        var isFixed = ReferenceEquals(bar.Theme, expected);
        if (expectFixed && !isFixed)
            throw new Exception($"[{label}] ScrollBar did not pick up the DeskCanvas scrollbar theme.");
        if (!expectFixed && isFixed)
            throw new Exception($"[{label}] Control scrollbar was expected to use Fluent's theme, but it uses the fixed one.");
        Console.WriteLine($"  PASS: [{label}] theme = {(isFixed ? "DeskCanvasScrollBar" : "Fluent (control)")}");
    }

    private static void AssertPixels(string pathA, string pathB, bool expectIdentical, string label)
    {
        using var a = SKBitmap.Decode(pathA) ?? throw new Exception($"Cannot decode {pathA}");
        using var b = SKBitmap.Decode(pathB) ?? throw new Exception($"Cannot decode {pathB}");
        if (a.Width != b.Width || a.Height != b.Height)
            throw new Exception($"Size mismatch: {a.Width}x{a.Height} vs {b.Width}x{b.Height}");

        var significant = 0;
        var maxDelta = 0;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a.GetPixel(x, y);
                var pb = b.GetPixel(x, y);
                var delta = Math.Max(Math.Max(Math.Abs(pa.Red - pb.Red), Math.Abs(pa.Green - pb.Green)), Math.Abs(pa.Blue - pb.Blue));
                if (delta >= SignificantDelta) significant++;
                if (delta > maxDelta) maxDelta = delta;
            }
        }

        Console.WriteLine($"  {label}: {significant}/{a.Width * a.Height} pixels differ (max channel delta {maxDelta}).");

        if (expectIdentical && significant > 0)
            throw new Exception($"{label}: hover changed {significant} pixels — the expanded state is still being painted.");
        if (!expectIdentical && significant == 0)
            throw new Exception($"{label}: no difference at all — this check cannot detect the bug it exists for.");
        Console.WriteLine(expectIdentical
            ? "  PASS: hover is pixel-identical to idle."
            : "  PASS: Fluent's expanded state is clearly different (check has real sensitivity).");
    }

    private static ScrollBarParts Render(Orientation orientation, ThemeVariant variant, bool fixedTheme, bool expanded, string outPath)
    {
        Application.Current!.RequestedThemeVariant = variant;

        var vertical = orientation == Orientation.Vertical;
        // Along the scroll axis the bar is long, across it the Fluent size (16 DIP) applies.
        const double longSide = 200;
        const double shortSide = 72;
        var hostWidth = vertical ? shortSide : longSide;
        var hostHeight = vertical ? longSide : shortSide;

        var bar = new ScrollBar
        {
            Orientation = orientation,
            Minimum = 0,
            Maximum = 30,
            ViewportSize = 10,
            Value = 0,
            AllowAutoHide = !expanded,
            HorizontalAlignment = vertical ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Bottom,
        };
        if (vertical)
            bar.Height = hostHeight;
        else
            bar.Width = hostWidth;

        // Local value beats the application style, so the control case really runs on Fluent's theme.
        if (!fixedTheme)
        {
            Application.Current!.TryFindResource(typeof(ScrollBar), out var fluent);
            bar.Theme = (ControlTheme?)fluent;
        }

        var host = new Border
        {
            Width = hostWidth,
            Height = hostHeight,
            Background = new SolidColorBrush(Color.FromRgb(28, 28, 30)),
            Child = bar,
        };

        var window = new Window
        {
            Width = hostWidth,
            Height = hostHeight,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            Content = host,
        };

        Layout(window);
        Dispatcher.UIThread.RunJobs();
        Layout(window);

        var root = Find<Grid>(bar, "Root", "Root");
        var track = Find<Rectangle>(bar, "TrackRect", "TrackRect");
        var thumb = bar.GetVisualDescendants().OfType<Thumb>().FirstOrDefault()
            ?? throw new Exception("ScrollBar template part 'Thumb' was not realised.");
        var buttons = bar.GetVisualDescendants().OfType<RepeatButton>().ToList();
        var maxButtonOpacity = buttons.Count == 0 ? 0 : buttons.Max(b => b.Opacity);

        var rtb = new RenderTargetBitmap(new PixelSize((int)(hostWidth * Scale), (int)(hostHeight * Scale)), new Vector(192, 192));
        rtb.Render(host);
        rtb.Save(outPath);

        var thumbExtent = vertical ? thumb.Bounds.Height : thumb.Bounds.Width;
        Console.WriteLine($"  -> {Path.GetFileName(outPath)}: IsExpanded={bar.IsExpanded}, TrackRect.Opacity={track.Opacity}, Thumb={thumb.RenderTransform?.Value}, thumb={thumbExtent:0.#} DIP, buttons={maxButtonOpacity}");

        return new ScrollBarParts(bar, thumb, track, root, root.Background, maxButtonOpacity, thumb.RenderTransform?.Value.ToString() ?? "");
    }

    private static T Find<T>(ScrollBar bar, string name, string part) where T : Control =>
        bar.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new Exception($"ScrollBar template part '{part}' was not realised.");

    private static void Layout(Window window)
    {
        var size = new Size(window.Width, window.Height);
        window.Measure(size);
        window.Arrange(new Rect(size));
        window.UpdateLayout();
    }

    private sealed record ScrollBarParts(
        ScrollBar Bar,
        Thumb Thumb,
        Rectangle Track,
        Grid Root,
        IBrush? RootBackground,
        double MaxButtonOpacity,
        string ThumbTransform);
}
