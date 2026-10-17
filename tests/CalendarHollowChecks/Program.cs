using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Calendar.Models;
using Calendar.Views;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using FixedWidgets.Models;
using FixedWidgets.Views;
using SkiaSharp;

namespace CalendarHollowChecks;

/// <summary>
/// Verifies the 镂空 "today" marker of both calendars — the calendar widget (<see cref="Month"/>)
/// and the calendar inside the aggregate dashboard (<see cref="AggregateView"/>): the day number
/// is a hole punched out of the marker disc, so the pixels inside the numeral are transparent
/// (the surface behind the card shows through) while the disc itself stays filled with the
/// configured color.
///
/// Both widgets are rendered in the real view (<see cref="Month"/> / <see cref="AggregateView"/>),
/// in **both** light and dark, because the dark palette is exactly where this regressed once
/// (a "dark mode → no hollow" override in the view, plus a marker whose hole disappeared against
/// a bright backdrop). For each render:
/// <list type="bullet">
/// <item>hollow on  → the disc exists, pixels inside its bounds are transparent (the hole), the
/// hole is a numeral and not the whole disc, and nothing opaque is painted on top of it;</item>
/// <item>hollow off → the disc exists and nothing inside it is transparent (the number is simply
/// painted on top, the historic look).</item>
/// </list>
/// The semi-transparent hairline along the hole's edge is explicitly allowed: the numeral must
/// stay see-through, so only fully opaque glyph pixels count as "painted".
/// </summary>
class Program
{
    private static int failures;
    private static string outputDir = "dist/calendar-hollow-checks";

    /// <summary>Today marker color used by the checks (unambiguous to detect in the render).</summary>
    private const string MarkerHex = "#FF0000";

    private const int MonthSize = 368;
    private const int AggregateWidth = 936;
    private const int AggregateHeight = 408;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) outputDir = args[0];
        Directory.CreateDirectory(outputDir);

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        // The marker brush is resolved through the app's own resource dictionaries, so load the
        // same styles the app switches on (accent + monochrome + the rendered-glass material).
        // The monochrome include stays swappable: the accent-mode section below replays the
        // 黑白 ↔ 背景色 switch exactly like ThemeService does.
        app.Styles.Add(Style("Accent.axaml"));
        var monoStyle = Style("MonochromeBlackWhite.axaml");
        app.Styles.Add(monoStyle);
        app.Styles.Add(Style("LiquidGlass.axaml"));

        void SwitchMonoStyle(string file)
        {
            app.Styles.Remove(monoStyle);
            monoStyle = Style(file);
            app.Styles.Add(monoStyle);
        }

        static StyleInclude Style(string file) =>
            new(new Uri("avares://DeskCanvas/")) { Source = new Uri("avares://DeskCanvas/Styles/" + file) };

        app.Resources["SystemAccentColor"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorDark1"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorLight1"] = Color.Parse("#0A84FF");
        app.Resources["BackgroundOpacity"] = 0.18;
        app.Resources["FontFamily"] = new FontFamily("avares://Avalonia.Fonts.Inter#Inter");

        Console.WriteLine("=== 镂空 today-marker checks (calendar widget + aggregate dashboard) ===");
        Console.WriteLine($"marker {MarkerHex}");
        Console.WriteLine();

        foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            app.RequestedThemeVariant = variant;
            var label = variant == ThemeVariant.Dark ? "dark" : "light";
            Console.WriteLine($"[{label}]");

            var monthHollow = Analyze(RenderMonth(hollow: true, variant, $"{label}-month-hollow"), MonthSize, MonthSize);
            var monthPainted = Analyze(RenderMonth(hollow: false, variant, $"{label}-month-painted"), MonthSize, MonthSize);
            Report($"calendar {label}", monthHollow, monthPainted);

            var aggregateHollow = Analyze(RenderAggregate(hollow: true, variant, $"{label}-aggregate-hollow"), AggregateWidth, AggregateHeight);
            var aggregatePainted = Analyze(RenderAggregate(hollow: false, variant, $"{label}-aggregate-painted"), AggregateWidth, AggregateHeight);
            Report($"aggregate {label}", aggregateHollow, aggregatePainted);

            Console.WriteLine();
        }

        // ---- Accent-mode numeral contrast (今日颜色=Accent, hollow off) ----------------------
        // Regression: the monochrome ramps drive the disc through theme slots — 黑白 flips it to
        // pure white in the dark variant, 背景色 to the light-mode background color — where the
        // historic hard-coded white numeral vanished into the disc. The solid numeral must follow
        // the disc's polarity in every ramp/variant combination.
        Console.WriteLine("=== accent-mode numeral contrast (solid disc, 黑白/背景色 ramps) ===");
        Console.WriteLine();

        foreach (var (monoKey, monoName, monoFile, monoVariant) in new[]
        {
            ("bw", "黑白", "MonochromeBlackWhite.axaml", MonochromeStyle.BlackWhite),
            ("bg", "背景色", "MonochromeBackground.axaml", MonochromeStyle.BackgroundColor),
        })
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                var variantKey = variant == ThemeVariant.Dark ? "dark" : "light";
                SwitchMonoStyle(monoFile);
                ThemeResourceHelper.ClearAccentOverrides(app.Resources);
                ThemeResourceHelper.ApplyAccentRamp(
                    app.Resources, MonochromeTheme(monoVariant, variant == ThemeVariant.Dark), variant == ThemeVariant.Dark);
                app.RequestedThemeVariant = variant;

                // Anchor pass: a custom red marker locates the disc's bounds; the accent pass is
                // then color-sampled inside exactly those bounds (the accent disc can be black or
                // white itself — indistinguishable from the text without the anchor).
                void ContrastCase(string what, int width, int height, Func<Control> anchor, Func<Control> accent)
                {
                    var box = FindMarkerBounds(
                        Render(anchor(), width, height, variant, $"{monoKey}-{variantKey}-{what}-anchor"), width, height);
                    Check($"{what} {monoName} {variantKey}: the accent-mode marker disc is drawn", box != null);
                    if (box == null) return;

                    var (disc, numeral, ratio) = AnalyzeContrast(
                        Render(accent(), width, height, variant, $"{monoKey}-{variantKey}-{what}-accent"), width, box.Value);
                    Check($"{what} {monoName} {variantKey}: solid numeral contrasts the disc (disc {disc}, numeral {numeral ?? "≈disc"}, {ratio:0.0}:1)",
                        numeral != null && ratio >= 3.0);
                }

                ContrastCase("month", MonthSize, MonthSize,
                    () => new Month(new MonthCalendarModel(DayOfWeek.Monday, TodayColorMode: "Custom",
                        TodayColorLight: MarkerHex, TodayColorDark: MarkerHex, HollowTodayNumber: false)),
                    () => new Month(new MonthCalendarModel(DayOfWeek.Monday, TodayColorMode: "Accent", HollowTodayNumber: false)));
                ContrastCase("aggregate", AggregateWidth, AggregateHeight,
                    () => new AggregateView(new AggregateModel(City: "成都", TodayColorMode: "Custom",
                        TodayColorLight: MarkerHex, TodayColorDark: MarkerHex, HollowTodayNumber: false)),
                    () => new AggregateView(new AggregateModel(City: "成都", TodayColorMode: "Accent", HollowTodayNumber: false)));
            }
        }

        // Restore the startup accent flats so the section leaves no residue.
        ThemeResourceHelper.ClearAccentOverrides(app.Resources);
        app.Resources["SystemAccentColor"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorDark1"] = Color.Parse("#0A84FF");
        app.Resources["SystemAccentColorLight1"] = Color.Parse("#0A84FF");

        Console.WriteLine();

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Report(string what, Measurement hollow, Measurement painted)
    {
        Console.WriteLine($"  {what} hollow : disc px {hollow.DiscPixels}, hole px {hollow.HolePixels}, painted px {hollow.PaintedPixels}");
        Console.WriteLine($"  {what} painted: disc px {painted.DiscPixels}, hole px {painted.HolePixels}, painted px {painted.PaintedPixels}");

        Check($"{what}: the today marker disc is drawn", hollow.DiscPixels > 100 && painted.DiscPixels > 100);
        Check($"{what}: 镂空 punches today's number out of the disc", hollow.HolePixels >= 20);
        Check($"{what}: the hole is a numeral, not the whole disc",
            hollow.BboxArea > 0 && hollow.HolePixels < hollow.BboxArea * 0.5);
        Check($"{what}: nothing is painted inside the hole", hollow.PaintedPixels == 0);
        Check($"{what}: not hollow → nothing inside the disc is transparent", painted.HolePixels == 0);
        Check($"{what}: not hollow → the number is painted on the disc instead", painted.PaintedPixels >= 20);
    }

    /// <summary>Render the real calendar widget for one hollow setting and decode its pixels.</summary>
    private static byte[]? RenderMonth(bool hollow, ThemeVariant variant, string name)
    {
        var model = new MonthCalendarModel(
            DayOfWeek.Monday,
            TodayColorMode: "Custom",
            TodayColorLight: MarkerHex,
            TodayColorDark: MarkerHex,
            HollowTodayNumber: hollow);

        return Render(new Month(model), MonthSize, MonthSize, variant, name);
    }

    /// <summary>Render the real aggregate dashboard for one hollow setting and decode its pixels.</summary>
    private static byte[]? RenderAggregate(bool hollow, ThemeVariant variant, string name)
    {
        var model = new AggregateModel(
            City: "成都",
            TodayColorMode: "Custom",
            TodayColorLight: MarkerHex,
            TodayColorDark: MarkerHex,
            HollowTodayNumber: hollow);

        return Render(new AggregateView(model), AggregateWidth, AggregateHeight, variant, name);
    }

    /// <summary>
    /// Host the view in a window (not measured bare) so the month grid's item containers are
    /// realized — an ItemsControl without a top-level never generates them — then render it with
    /// the requested theme variant, which is what the marker's dark/light palette depends on.
    /// </summary>
    private static byte[]? Render(Control view, int width, int height, ThemeVariant variant, string name)
    {
        view.Width = width;
        view.Height = height;

        // The widget host renders text with grayscale antialiasing (Widget.axaml.cs sets
        // TextRenderingMode.Antialias); without it Skia may add ClearType subpixel fringes, whose
        // red/blue edges are indistinguishable from the marker colour in the pixel analysis below.
        RenderOptions.SetTextRenderingMode(view, TextRenderingMode.Antialias);

        var host = new Window
        {
            Width = width,
            Height = height,
            Content = view,
            WindowDecorations = WindowDecorations.None,
            Background = Brushes.Transparent,
            RequestedThemeVariant = variant
        };

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(view);

        var path = Path.Combine(outputDir, $"{name}.png");
        bitmap.Save(path);

        using var decoded = SKBitmap.Decode(path);
        if (decoded == null) return null;
        var pixels = new byte[decoded.ByteCount];
        System.Runtime.InteropServices.Marshal.Copy(decoded.GetPixels(), pixels, 0, pixels.Length);
        return pixels;
    }

    /// <summary>
    /// Marker measurements inside the disc: the disc pixels (the marker color), the transparent
    /// pixels (the punched-out number) and the opaque glyph pixels (a number painted on top).
    /// The corners of the disc's bounding box fall outside the circle — nothing else paints there
    /// — so only pixels within 92% of the radius are counted.
    /// </summary>
    private static Measurement Analyze(byte[]? bgra, int width, int height)
    {
        if (bgra == null) return new Measurement(0, 0, 0, 0);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var disc = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                // BGRA — the marker is pure red, so a strict threshold keeps antialiased text
                // fringes out of the disc mask.
                if (bgra[i + 2] < 200 || bgra[i + 1] > 60 || bgra[i] > 60 || bgra[i + 3] < 200) continue;

                disc++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0) return new Measurement(0, 0, 0, 0);

        var bboxArea = (maxX - minX + 1) * (maxY - minY + 1);
        var centerX = (minX + maxX) / 2.0;
        var centerY = (minY + maxY) / 2.0;
        var radius = (maxX - minX + 1) / 2.0;
        var innerRadius = radius * 0.92;

        var holes = 0;
        var painted = 0;
        for (var y = minY; y <= maxY; y++)
        {
            for (var x = minX; x <= maxX; x++)
            {
                var dx = x - centerX;
                var dy = y - centerY;
                if (dx * dx + dy * dy > innerRadius * innerRadius) continue;

                var i = (y * width + x) * 4;
                // The hole: nothing (or only the semi-transparent rim hairline) is there.
                if (bgra[i + 3] < 40) holes++;
                // Painted: a fully opaque light glyph covers the hole.
                else if (bgra[i + 3] > 200 && bgra[i] > 200 && bgra[i + 1] > 200 && bgra[i + 2] > 200) painted++;
            }
        }

        return new Measurement(disc, holes, painted, bboxArea);
    }

    private readonly record struct Measurement(int DiscPixels, int HolePixels, int PaintedPixels, int BboxArea);

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    /// <summary>
    /// The monochrome theme the accent-mode section replays; <see
    /// cref="ThemeResourceHelper.ApplyAccentRamp"/> then writes the same ramp ThemeService writes.
    /// </summary>
    private static Theme MonochromeTheme(MonochromeStyle variant, bool dark) => new(
        DarkMode: dark,
        AccentColor: null,
        OpacityLevel: 0.3,
        Monochrome: true,
        UseNativeFrame: false,
        FontFamily: "Inter",
        MonochromeVariant: variant);

    /// <summary>Bounding box of the red anchor marker (strict thresholds keep text fringes out).</summary>
    private static (int minX, int minY, int maxX, int maxY)? FindMarkerBounds(byte[]? bgra, int w, int h)
    {
        if (bgra == null) return null;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (bgra[i + 3] < 200 || bgra[i + 2] < 200 || bgra[i + 1] > 60 || bgra[i] > 60) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }

    /// <summary>
    /// Marker colors inside the anchored disc bounds: the mode disc color and the mode color of
    /// the pixels that differ from it (the numeral), as a WCAG contrast ratio. A null numeral
    /// means nothing readable differs from the disc — the reported white-on-white.
    /// </summary>
    private static (string Disc, string? Numeral, double Ratio) AnalyzeContrast(
        byte[]? bgra, int width, (int minX, int minY, int maxX, int maxY) box)
    {
        if (bgra == null) return ("none", null, 0);

        var cx = (box.minX + box.maxX) / 2.0;
        var cy = (box.minY + box.maxY) / 2.0;
        var radius = (box.maxX - box.minX + 1) / 2.0;
        var inner = radius * 0.92;

        var discHist = new Dictionary<(int, int, int), int>();
        var samples = new List<(int r, int g, int b)>();
        for (var y = box.minY; y <= box.maxY; y++)
            for (var x = box.minX; x <= box.maxX; x++)
            {
                var dx = x - cx; var dy = y - cy;
                if (dx * dx + dy * dy > inner * inner) continue;
                var i = (y * width + x) * 4;
                if (bgra[i + 3] < 200) continue;
                var (r, g, b) = (bgra[i + 2], bgra[i + 1], bgra[i]);
                samples.Add((r, g, b));
                var key = (r / 24, g / 24, b / 24);
                discHist[key] = discHist.GetValueOrDefault(key) + 1;
            }

        if (samples.Count == 0) return ("none", null, 0);

        var dk = discHist.OrderByDescending(kv => kv.Value).First().Key;
        var disc = (r: dk.Item1 * 24 + 12, g: dk.Item2 * 24 + 12, b: dk.Item3 * 24 + 12);
        var discHex = $"#{disc.r:X2}{disc.g:X2}{disc.b:X2}";

        var numeralHist = new Dictionary<(int, int, int), int>();
        var numeralTotal = 0;
        foreach (var (r, g, b) in samples)
        {
            var dr = r - disc.r; var dg = g - disc.g; var db = b - disc.b;
            if (dr * dr + dg * dg + db * db < 64 * 64) continue;
            numeralTotal++;
            var key = (r / 24, g / 24, b / 24);
            numeralHist[key] = numeralHist.GetValueOrDefault(key) + 1;
        }

        if (numeralHist.Count == 0 || numeralTotal <= samples.Count * 0.02)
            return (discHex, null, 0);

        var nk = numeralHist.OrderByDescending(kv => kv.Value).First().Key;
        var numeral = $"#{nk.Item1 * 24 + 12:X2}{nk.Item2 * 24 + 12:X2}{nk.Item3 * 24 + 12:X2}";
        return (discHex, numeral, ContrastRatio(discHex, numeral));
    }

    private static double ContrastRatio(string a, string b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
        var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
        var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;
        Lin(ref r); Lin(ref g); Lin(ref b);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;

        static void Lin(ref double c) => c = c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }
}
