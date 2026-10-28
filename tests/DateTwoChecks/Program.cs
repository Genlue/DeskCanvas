using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Calendar.Models;
using Calendar.Views;
using Calendar.Views.Controls;
using SkiaSharp;

namespace DateTwoChecks;

/// <summary>
/// Visual contract for the 日期 card: the reference's ink proportions must survive at both a
/// normal card size and a small card, with a four- and five-character lunar date.
/// </summary>
class Program
{
    private static int failures;
    private static string outputDir = "dist/date-two-checks";

    private const double HeaderInkRatio = 0.115;
    private const double HeaderCenterRatio = 0.208;
    private const double BodyInkRatio = 0.455;
    private const double BodyCenterRatio = 0.602;

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0) outputDir = args[0];
        Directory.CreateDirectory(outputDir);

        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();
        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.Resources["WidgetCardCornerRadius"] = new CornerRadius(16);
        // The face must derive every colour from the app theme tokens, not from a baked-in
        // reference palette. These are the Colorful dark values the real host would expose.
        app.Resources["CalendarCardBackground"] = new SolidColorBrush(Color.Parse("#1C1C1E"));
        app.Resources["CalendarHeaderBrush"] = new SolidColorBrush(Color.Parse("#FF453A"));
        app.Resources["WidgetForeground"] = new SolidColorBrush(Colors.White);
        app.Resources["SystemControlForegroundBaseMediumBrush"] = new SolidColorBrush(Color.Parse("#98989D"));

        Console.WriteLine("=== 日期 face checks ===");
        Console.WriteLine();

        Verify(300, 300, "300", "周二 2月", "3", "腊月十六");
        Console.WriteLine();
        Verify(300, 300, "300-two-digit", "周二 2月", "31", "腊月十六");
        Console.WriteLine();
        Verify(150, 150, "150", "周二 2月", "3", "腊月十六");
        Console.WriteLine();
        Verify(150, 150, "150-two-digit", "周二 2月", "31", "腊月十六");
        Console.WriteLine();
        Verify(150, 150, "150-two-digit-leap", "周二 2月", "31", "闰腊月十六");
        Console.WriteLine();
        Verify(300, 300, "300-leap", "周二 2月", "3", "闰腊月十六");
        Console.WriteLine();
        Verify(300, 300, "300-hidden", "周二 2月", "3", "腊月十六", showLunar: false);
        Console.WriteLine();
        Verify(150, 150, "150-hidden-two-digit", "周二 2月", "31", "腊月十六", showLunar: false);
        Console.WriteLine();

        Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static void Verify(int width, int height, string name, string header, string day, string lunar, bool showLunar = true)
    {
        Console.WriteLine($"[{width}×{height} {name}: {header} / {day} / {lunar}, lunar={showLunar}]");
        var view = BuildArranged(width, height, header, day, lunar, showLunar);
        var path = Render(view, width, height, name);
        using var bitmap = SKBitmap.Decode(path);
        if (bitmap == null)
        {
            Check($"{name}: bitmap decodes", false);
            return;
        }

        var pixels = Decode(bitmap);
        var ink = FindInk(pixels, bitmap.Width, bitmap.Height);
        if (ink.IsEmpty)
        {
            Check($"{name}: text ink is drawn", false);
            return;
        }

        // Header lives alone in the upper third; the body band contains day + lunar only.
        var headerBox = BBoxIn(pixels, bitmap.Width, bitmap.Height, 0, 0, bitmap.Width, (int)(height * 0.35));
        var bodyBox = BBoxIn(pixels, bitmap.Width, bitmap.Height, 0, (int)(height * 0.35), bitmap.Width, height);
        Console.WriteLine($"  header bbox: ({headerBox.MinX},{headerBox.MinY})..({headerBox.MaxX},{headerBox.MaxY})");
        Console.WriteLine($"  body bbox:   ({bodyBox.MinX},{bodyBox.MinY})..({bodyBox.MaxX},{bodyBox.MaxY})");

        Check($"{name}: header exists", !headerBox.IsEmpty);
        Check($"{name}: body exists", !bodyBox.IsEmpty);
        if (headerBox.IsEmpty || bodyBox.IsEmpty) return;

        var headerHeight = headerBox.Height / (double)height;
        var headerCenterY = (headerBox.MinY + headerBox.MaxY) / 2.0 / height;
        var headerCenterX = (headerBox.MinX + headerBox.MaxX) / 2.0 / width;
        Console.WriteLine($"  header height {headerHeight:0.###}, center ({headerCenterX:0.###}, {headerCenterY:0.###})");
        Check($"{name}: header ink ≈ {HeaderInkRatio:0.000} card height (got {headerHeight:0.###})",
            Math.Abs(headerHeight - HeaderInkRatio) <= 0.025);
        Check($"{name}: header center ≈ {HeaderCenterRatio:0.000} card height (got {headerCenterY:0.###})",
            Math.Abs(headerCenterY - HeaderCenterRatio) <= 0.025);
        Check($"{name}: header horizontally centered (got {headerCenterX:0.###})",
            Math.Abs(headerCenterX - 0.5) <= 0.03);

        // Split the body into its two columns at the widest blank gap. The day glyph is much
        // wider than the lunar column, so the left run is the day and the right one the stack.
        var bodyTop = (int)(height * 0.35);
        BBox dayBox;
        if (!showLunar)
        {
            dayBox = BBoxIn(pixels, bitmap.Width, bitmap.Height, 0, bodyTop, width, height);
            var hiddenHeight = dayBox.Height / (double)height;
            var hiddenCenterY = (dayBox.MinY + dayBox.MaxY) / 2.0 / height;
            var hiddenWidth = dayBox.Width / (double)height;
            var hiddenCenterX = (dayBox.MinX + dayBox.MaxX) / 2.0 / width;
            var expectedHiddenAspect = day.Length >= 2 ? 1.09 : 0.58;
            Console.WriteLine($"  hidden-lunar day ink {hiddenWidth:0.###}×{hiddenHeight:0.###}, center ({hiddenCenterX:0.###}, {hiddenCenterY:0.###})");
            Check($"{name}: hidden lunar keeps day ink height ≈ {BodyInkRatio:0.000} (got {hiddenHeight:0.###})",
                Math.Abs(hiddenHeight - BodyInkRatio) <= 0.03);
            Check($"{name}: hidden lunar keeps day ink center ≈ {BodyCenterRatio:0.000} (got {hiddenCenterY:0.###})",
                Math.Abs(hiddenCenterY - BodyCenterRatio) <= 0.025);
            Check($"{name}: hidden lunar keeps the day aspect (≈ {expectedHiddenAspect:0.00}, got {hiddenWidth / hiddenHeight:0.###})",
                Math.Abs(hiddenWidth / hiddenHeight - expectedHiddenAspect) <= 0.10);
            Check($"{name}: hidden lunar centres the day horizontally (got {hiddenCenterX:0.###})",
                Math.Abs(hiddenCenterX - 0.5) <= 0.03);
            Check($"{name}: hidden lunar draws no secondary-colour ink",
                !HasColor(pixels, bitmap.Width, bodyBox, Color.Parse("#98989D")));
        }
        else
        {
            var runs = XRuns(pixels, bitmap.Width, bitmap.Height, bodyTop, height);
            if (runs.Count < 2)
            {
                Check($"{name}: day and lunar are separate columns (found {runs.Count})", false);
                return;
            }

            // A two-digit day can split into one run per glyph, so the day column is everything
            // left of the lunar column (the rightmost run), not just the first run.
            var lunarRun = runs[^1];
            var dayStart = runs[0].Start;
            var dayEnd = Math.Max(dayStart, lunarRun.Start - 1);
            dayBox = BBoxIn(pixels, bitmap.Width, bitmap.Height, dayStart, bodyTop, dayEnd, height);
            var lunarBox = BBoxIn(pixels, bitmap.Width, bitmap.Height, lunarRun.Start, bodyTop, lunarRun.End, height);
            var dayHeight = dayBox.Height / (double)height;
            var dayCenterY = (dayBox.MinY + dayBox.MaxY) / 2.0 / height;
            var dayWidth = dayBox.Width / (double)height;
            var lunarHeight = lunarBox.Height / (double)height;
            var lunarCenterY = (lunarBox.MinY + lunarBox.MaxY) / 2.0 / height;
            var groupCenterX = (dayBox.MinX + lunarBox.MaxX) / 2.0 / width;
            var lunarLineRuns = YRuns(pixels, bitmap.Width, bitmap.Height, lunarRun.Start, lunarRun.End, bodyTop, height);
            var expectedLunarHeight = lunar.Length >= 5 ? 0.5 : BodyInkRatio;
            Console.WriteLine($"  day ink {dayWidth:0.###}×{dayHeight:0.###}, center y {dayCenterY:0.###}");
            Console.WriteLine($"  lunar height {lunarHeight:0.###}, center y {lunarCenterY:0.###}, lines {lunarLineRuns.Count}");
            Console.WriteLine($"  group center x {groupCenterX:0.###}, gap {lunarBox.MinX - dayBox.MaxX}px");

            Check($"{name}: day ink height ≈ {BodyInkRatio:0.000} card height (got {dayHeight:0.###})",
                Math.Abs(dayHeight - BodyInkRatio) <= 0.03);
            Check($"{name}: day ink center ≈ {BodyCenterRatio:0.000} card height (got {dayCenterY:0.###})",
                Math.Abs(dayCenterY - BodyCenterRatio) <= 0.025);
            var expectedDayAspect = day.Length >= 2 ? 1.09 : 0.58;
            Check($"{name}: day ink aspect follows its glyph count (≈ {expectedDayAspect:0.00}, got {dayWidth / dayHeight:0.###})",
                Math.Abs(dayWidth / dayHeight - expectedDayAspect) <= 0.10);
            Check($"{name}: lunar ink height ≈ {expectedLunarHeight:0.000} card height (got {lunarHeight:0.###})",
                Math.Abs(lunarHeight - expectedLunarHeight) <= 0.035);
            Check($"{name}: each lunar line is separated (got {lunarLineRuns.Count} runs for {lunar.Length} glyphs)",
                lunarLineRuns.Count >= lunar.Length);
            Check($"{name}: lunar ink center ≈ {BodyCenterRatio:0.000} card height (got {lunarCenterY:0.###})",
                Math.Abs(lunarCenterY - BodyCenterRatio) <= 0.03);
            Check($"{name}: whole day/lunar group centered (got {groupCenterX:0.###})",
                Math.Abs(groupCenterX - 0.5) <= 0.025);
            Check($"{name}: day/lunar gap is a real column (>{height * 0.02:0.#}px, got {lunarBox.MinX - dayBox.MaxX}px)",
                lunarBox.MinX - dayBox.MaxX > height * 0.02);
            Check($"{name}: lunar colour follows the secondary text token",
                HasColor(pixels, bitmap.Width, lunarBox, Color.Parse("#98989D")));
        }

        Check($"{name}: all ink stays inside the card",
            ink.MinX >= 0 && ink.MinY >= 0 && ink.MaxX < width && ink.MaxY < height);
        Check($"{name}: empty bottom band remains (>{height * 0.1:0.#}px, got {height - 1 - bodyBox.MaxY}px)",
            height - 1 - bodyBox.MaxY > height * 0.1);
        Check($"{name}: card uses the Calendar theme surface",
            IsBackground(pixels, bitmap.Width, width / 2, (int)(height * 0.92)));
        Check($"{name}: header colour follows CalendarHeaderBrush",
            HasColor(pixels, bitmap.Width, headerBox, Color.Parse("#FF453A")));
        Check($"{name}: day colour follows WidgetForeground",
            HasColor(pixels, bitmap.Width, dayBox, Colors.White));
    }

    private static Date BuildArranged(int width, int height, string header, string day, string lunar, bool showLunar)
    {
        var view = new Date(new DateModel(showLunar));
        view.Width = width;
        view.Height = height;

        // Check the model binding before detaching it. Clearing DataContext removes the binding,
        // so keep the sample's value on the face explicitly for the pixel checks below.
        var face = view.FindControl<DateFace>("Face") ?? throw new InvalidOperationException("DateFace not found");
        Check($"model ShowLunar={showLunar} reaches the face", face.ShowLunar == showLunar);
        view.DataContext = null;
        face.ShowLunar = showLunar;
        face.Header = header;
        face.Day = day;
        face.Lunar = lunar;

        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();
        return view;
    }

    private static string Render(Control view, int width, int height, string name)
    {
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

        using var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(view);
        var path = Path.Combine(outputDir, $"{name}.png");
        bitmap.Save(path);
        return path;
    }

    private static uint[] Decode(SKBitmap bitmap)
    {
        var pixels = new uint[bitmap.Width * bitmap.Height];
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
            pixels[y * bitmap.Width + x] = (uint)bitmap.GetPixel(x, y);
        return pixels;
    }

    private static bool IsInk(uint pixel)
    {
        var r = (int)((pixel >> 16) & 0xff);
        var g = (int)((pixel >> 8) & 0xff);
        var b = (int)(pixel & 0xff);
        var a = (int)((pixel >> 24) & 0xff);
        // Text can be the calendar accent (red), primary white or secondary grey; all of
        // them are far brighter than the #1C1C1E card. A fixed "pale blue" threshold would
        // hide the accent header and defeat the theme-token check.
        return a > 128 && r + g + b > 300;
    }

    private static bool IsBackground(uint[] pixels, int stride, int x, int y)
    {
        var pixel = pixels[y * stride + x];
        var r = (int)((pixel >> 16) & 0xff);
        var g = (int)((pixel >> 8) & 0xff);
        var b = (int)(pixel & 0xff);
        return Math.Abs(r - 28) <= 2 && Math.Abs(g - 28) <= 2 && Math.Abs(b - 30) <= 2;
    }

    private static bool HasColor(uint[] pixels, int stride, BBox box, Color expected, int tolerance = 4, int minPixels = 24)
    {
        var matches = 0;
        for (var y = box.MinY; y <= box.MaxY; y++)
        for (var x = box.MinX; x <= box.MaxX; x++)
        {
            var pixel = pixels[y * stride + x];
            var a = (int)((pixel >> 24) & 0xff);
            if (a < 250) continue;
            var r = (int)((pixel >> 16) & 0xff);
            var g = (int)((pixel >> 8) & 0xff);
            var b = (int)(pixel & 0xff);
            if (Math.Abs(r - expected.R) <= tolerance &&
                Math.Abs(g - expected.G) <= tolerance &&
                Math.Abs(b - expected.B) <= tolerance &&
                ++matches >= minPixels)
                return true;
        }

        return false;
    }

    private static BBox FindInk(uint[] pixels, int width, int height)
        => BBoxIn(pixels, width, height, 0, 0, width, height);

    private static BBox BBoxIn(uint[] pixels, int width, int height, int x0, int y0, int x1, int y1)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
        for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
        {
            if (!IsInk(pixels[y * width + x])) continue;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }

        return maxX < 0 ? default : new BBox(minX, minY, maxX, maxY);
    }

    private static List<(int Start, int End)> YRuns(uint[] pixels, int width, int height, int x0, int x1,
        int y0, int y1)
    {
        var runs = new List<(int Start, int End)>();
        var start = -1;
        var lastInk = -100;
        for (var y = y0; y < y1; y++)
        {
            var hasInk = false;
            for (var x = x0; x <= x1; x++)
            {
                if (!IsInk(pixels[y * width + x])) continue;
                hasInk = true;
                break;
            }

            if (hasInk)
            {
                if (start < 0) start = y;
                else if (y - lastInk > 1)
                {
                    runs.Add((start, lastInk));
                    start = y;
                }
                lastInk = y;
            }
        }

        if (start >= 0) runs.Add((start, lastInk));
        return runs;
    }

    private static List<(int Start, int End)> XRuns(uint[] pixels, int width, int height, int y0, int y1)
    {
        var runs = new List<(int Start, int End)>();
        var start = -1;
        var lastInk = -100;
        for (var x = 0; x < width; x++)
        {
            var hasInk = false;
            for (var y = y0; y < y1; y++)
            {
                if (!IsInk(pixels[y * width + x])) continue;
                hasInk = true;
                break;
            }

            if (hasInk)
            {
                if (start < 0) start = x;
                else if (x - lastInk > 5)
                {
                    runs.Add((start, lastInk));
                    start = x;
                }
                lastInk = x;
            }
        }

        if (start >= 0) runs.Add((start, lastInk));
        return runs;
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
        if (!ok) failures++;
    }

    private readonly record struct BBox(int MinX, int MinY, int MaxX, int MaxY)
    {
        public bool IsEmpty => MaxX < 0;
        public int Width => MaxX - MinX + 1;
        public int Height => MaxY - MinY + 1;
    }
}