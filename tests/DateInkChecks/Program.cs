using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Calendar.Views;
using SkiaSharp;

namespace DateInkChecks;

/// <summary>
/// Verifies the 1x1 date widget's day-number font contract: the FontSize is derived from the
/// widget's actual rendered pixel count (DIP × DPI × host ContentScale) with a target ink
/// height of <see cref="DateOne.DayInkCardHeightRatio"/> of the card — no stretching, no line-box
/// surgery (the old TextBlock-in-Viewbox with a negative top Margin was what truncated the ink
/// at the top), and a baseline-anchoring top margin keeps the ink centered in its row box at any
/// size, so it can never be clipped.
///
/// Checks:
/// <list type="bullet">
/// <item>the real Date view at 200×200: DayLabel.FontSize equals the value the pixel formula
/// prescribes, and the number's ink bbox sits fully inside its row box, centered, within the
/// prescribed ink/card fraction — the no-truncation contract;</item>
/// <item>the same at a tiny 96×96 card — the derivation is size-independent;</item>
/// <item>rasterization-density invariance: rendering the same view at 2× DPI yields the same
/// ink/card fraction (guards against pixel-hardcoded regressions).</item>
/// </list>
/// </summary>
class Program
{
    private static int failures;
    private static string outputDir = "dist/date-ink-checks";

    private const double ReferenceFontSize = 100;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) outputDir = args[0];
        Directory.CreateDirectory(outputDir);

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        // Silence every TextBlock so the real-widget render isolates the number's ink: the
        // date view's header is a TextBlock pair, and a late app-level style beats the Fluent
        // default foreground without depending on resource-resolution order.
        app.Styles.Add(new Style(x => x.OfType<TextBlock>())
        {
            Setters = { new Setter(TextBlock.ForegroundProperty, Brushes.Transparent) }
        });

        Console.WriteLine("=== date widget ink checks (DPI-derived day-number font size) ===");
        Console.WriteLine();

        VerifyDate(200, 200, "real-date");
        Console.WriteLine();
        VerifyDate(96, 96, "tiny-date");
        Console.WriteLine();

        // ---- Rasterization density invariance (2× DPI bitmap) -------------------------------
        Console.WriteLine("[rasterization density invariance: 200 DIP @1x vs @2x]");
        var view1 = BuildArrangedDate(200, 200, out var font1);
        var ink1 = Analyze(Render(view1, 200, 200, 1, "density-1x"), 200, 200);
        var view2 = BuildArrangedDate(200, 200, out var font2);
        var ink2 = Analyze(Render(view2, 200, 200, 2, "density-2x"), 400, 400);
        if (ink1.Empty || ink2.Empty)
        {
            Check("ink is drawn at both rasterization densities", false);
        }
        else
        {
            var fraction1 = (double)ink1.Height / 200;
            var fraction2 = (double)ink2.Height / 400;
            Console.WriteLine($"  ink/card fraction: 1x {fraction1:0.###}, 2x {fraction2:0.###}");
            Check($"same ink/card fraction at 1x and 2x (Δ {Math.Abs(fraction1 - fraction2):0.####} ≤ 0.02)",
                Math.Abs(fraction1 - fraction2) <= 0.02);
            Check($"the font size itself is DPI-independent in DIP (1x {font1:0.#} vs 2x {font2:0.#})",
                Math.Abs(font2 - font1) < 0.5);
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Arrange the view directly (fonts apply via SizeChanged during this pass), then force a
    /// second full measure/arrange so the text re-measures at the final font size.
    /// </summary>
    private static DateOne BuildArrangedDate(int width, int height, out double fontSize)
    {
        var view = new DateOne();
        view.FindControl<TextBlock>("DayLabel")!.Foreground = Brushes.White;
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        RemeasureArrange(view, width, height);
        fontSize = view.FindControl<TextBlock>("DayLabel")?.FontSize ?? 0;
        return view;
    }

    private static void RemeasureArrange(Control view, int width, int height)
    {
        view.InvalidateMeasure();
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
    }

    private static void VerifyDate(int width, int height, string name)
    {
        Console.WriteLine($"[real Date view {width}×{height}, header silenced]");
        var view = BuildArrangedDate(width, height, out _);
        var bytes = Render(view, width, height, 1, name);
        var label = view.FindControl<TextBlock>("DayLabel");

        Check($"{name}: the day label is found", label != null);
        if (label == null || bytes == null) return;

        // The formula under test: target ink height = the view's own ratio of the card
        // (DateOne.DayInkCardHeightRatio, the single source of truth), converted through the
        // measured ink ratio; the tight-ink-width constraint (0.94 card) takes the minimum.
        var metrics = Measure(label.Text ?? "21", label.FontFamily, label.FontWeight);
        var expected = Math.Min(
            height * DateOne.DayInkCardHeightRatio / metrics.ink,
            width * DateOne.DayInkCardWidthRatio / metrics.width);
        Check($"{name}: FontSize matches the pixel derivation (got {label.FontSize:0.#}, want {expected:0.#})",
            Math.Abs(label.FontSize - expected) < Math.Max(0.5, expected * 0.01));

        var ink = Analyze(bytes, width, height);
        if (ink.Empty)
        {
            Check($"{name}: the number is drawn at all", false);
            return;
        }

        Console.WriteLine($"  ink bbox: ({ink.MinX},{ink.MinY})..({ink.MaxX},{ink.MaxY}) in {width}×{height}");

        // Row 1 (the invisible box) spans from 20% height to the card bottom; the ink must sit
        // fully inside it — that IS the no-truncation contract.
        Check($"{name}: the number's ink stays inside its row box (top {ink.MinY} ≥ {height * 0.2 - 2:0})",
            ink.MinY >= height * 0.2 - 2);
        Check($"{name}: the number's ink stays inside the card (bottom {ink.MaxY} ≤ {height - 1})",
            ink.MaxY <= height - 1);
        Check($"{name}: the number's ink stays inside the card width (left {ink.MinX} ≥ 0, right {ink.MaxX} ≤ {width - 1})",
            ink.MinX >= 0 && ink.MaxX <= width - 1);

        var expectedInkHeight = label.FontSize * metrics.ink;
        Check($"{name}: the ink height matches the prescribed pixel target (got {ink.Height:0.#}, want ≈{expectedInkHeight:0.#})",
            Math.Abs(ink.Height - expectedInkHeight) <= Math.Max(2, expectedInkHeight * 0.08));
        // Upper bound: the ink never eats more than the configured share of the card (the
        // width constraint may legitimately make it smaller — "21" is wider than it is tall).
        var budget = DateOne.DayInkCardHeightRatio;
        Check($"{name}: the ink stays within the {budget:0.00} card-height budget",
            ink.Height <= (budget + 0.04) * height && ink.Height > 0.5 * height);
        var cellCenter = height * 0.6; // row 1 spans 20%..100% of the card
        var inkCenter = (ink.MinY + ink.MaxY) / 2.0;
        Check($"{name}: the ink is vertically centered in its row box (center {inkCenter:0.#} ≈ {cellCenter:0.#})",
            Math.Abs(inkCenter - cellCenter) <= 3);
    }

    private static (double width, double ink) Measure(string text, FontFamily family, FontWeight weight)
    {
        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, weight),
            ReferenceFontSize,
            Brushes.Black);
        // Tight ink width (BuildGeometry bounds), NOT the advance width — the width budget
        // must budget the visible glyph pixels, and advance includes side bearings that would
        // silently clamp the requested ink height.
        var tightWidth = formatted.BuildGeometry(default).Bounds.Width / ReferenceFontSize;
        return (tightWidth, formatted.Extent / ReferenceFontSize);
    }

    /// <summary>
    /// Host the view in a window (not measured bare) so bindings realize, then render it to a
    /// PNG at the requested rasterization density over a transparent backdrop and decode pixels.
    /// </summary>
    private static byte[]? Render(Control view, int width, int height, int scaling, string name)
    {
        view.Width = width;
        view.Height = height;

        // Grayscale text AA like the widget host (Widget.axaml.cs) — keeps ClearType fringes
        // out of the pixel analysis.
        RenderOptions.SetTextRenderingMode(view, TextRenderingMode.Antialias);

        var host = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent
        };

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        // The day-number FontSize is applied from SizeChanged during the first arrange; that
        // invalidation is deferred to the layout manager's next pass. Force a full synchronous
        // re-measure/arrange so the bitmap reflects the final metrics.
        view.InvalidateMeasure();
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        using var bitmap = new RenderTargetBitmap(
            new PixelSize(width * scaling, height * scaling),
            new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(view);

        var path = Path.Combine(outputDir, $"{name}.png");
        bitmap.Save(path);
        SaveWhiteComposite(path);

        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;
        var pixels = new byte[decoded.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>White-composited copy for human review (the raw render is on transparency).</summary>
    private static void SaveWhiteComposite(string path)
    {
        using var src = SKBitmap.Decode(path);
        if (src == null) return;
        using var white = new SKBitmap(src.Width, src.Height);
        using (var canvas = new SKCanvas(white))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawBitmap(src, 0, 0);
        }
        using var image = SKImage.FromBitmap(white);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.OpenWrite(Path.Combine(
            outputDir, Path.GetFileNameWithoutExtension(path) + ".white.png"));
        data.SaveTo(file);
    }

    /// <summary>
    /// Bounding box of the ink: the backdrop is transparent and the header is silenced, so
    /// everything with alpha is the number.
    /// </summary>
    private static BBox Analyze(byte[]? bgra, int width, int height)
    {
        if (bgra == null) return default;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (bgra[i + 3] < 100) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? default : new BBox(minX, minY, maxX, maxY);
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    private readonly record struct BBox(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool Empty => MaxX < 0;
        public int Height => MaxY - MinY + 1;
    }
}
