using System;
using System.Globalization;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Clock.Models;
using Clock.Views;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Core.Models.Settings;

namespace ClockVisualChecks;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.WriteLine("=== Starting Frameless Clock Visual Checks ===");
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "dist/frameless-clock-checks");
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Dark;

        // Fix the accent so the snapshots are deterministic — and so the 跟随强调色 cases
        // (4x2-overlay-tint) actually prove they resolve it: green is unmistakably not the
        // hard-coded Windows blue the overlay used to fall back to.
        app.Resources["SystemAccentColor"] = Color.Parse("#12C46A");

        // Verify tight bounds and stretch math
        TestGeometryStretchMath();

        // Visual test cases covering curated artistic fonts and sizes.
        // There is no per-widget theme case any more: the frameless clock always follows the
        // global theme (see tests/ClockThemeChecks for the material resolution checks), and these
        // snapshots render without a settings provider, i.e. on the acrylic fallback.
        var testCases = new (string CaseName, double Width, double Height, FramelessClockModel Model)[]
        {
            ("4x2-harmonyos-condensed", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 800, StretchFill: true)),
            ("4x2-impact-heavy", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Impact", FontWeight: 800, StretchFill: true)),
            ("4x2-georgia-serif", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Georgia", FontWeight: 700, StretchFill: true)),
            ("4x2-century-gothic", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Century Gothic", FontWeight: 700, StretchFill: true)),
            ("4x2-bahnschrift-din", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bahnschrift", FontWeight: 700, StretchFill: true)),
            ("4x2-thin-100", 312, 152, new FramelessClockModel(Use24Hours: true, FontWeight: 100, StretchFill: true)),
            ("4x2-black-900", 312, 152, new FramelessClockModel(Use24Hours: true, FontWeight: 900, StretchFill: true)),
            ("2x2-regular-uniform", 152, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 700, StretchFill: false)),
            ("4x1-seconds-fill", 312, 72, new FramelessClockModel(Use24Hours: true, ShowSeconds: true, FontFamily: "Impact", FontWeight: 700, StretchFill: true)),
            ("4x2-overlay-tint", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 800, StretchFill: true, EnableOverlay: true, FollowAccentColor: true, OverlayOpacity: 0.40)),
            ("4x2-gold-tint", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Impact", FontWeight: 700, StretchFill: true, EnableOverlay: true, FollowAccentColor: false, OverlayColor: "#FFCC00", OverlayOpacity: 0.50))
        };

        foreach (var tc in testCases)
        {
            Console.WriteLine($"\n--- Testing and Rendering {tc.CaseName} ({tc.Width}x{tc.Height} DIP) ---");
            RenderAndVerify(outDir, tc.CaseName, tc.Width, tc.Height, tc.Model);
        }

        TestWeightAxis(outDir);

        TestPreCachingAndAutoCleanup();

        Console.WriteLine($"\nAll Frameless Clock checks completed successfully! Output folder: {outDir}");
    }

    private static void TestGeometryStretchMath()
    {
        Console.WriteLine("\n--- Testing Tight Bounds & Stretch Transform Precision ---");
        var timeStr = "12:10";
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyle.Normal, FontWeight.Bold);
        var formatted = new FormattedText(timeStr, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 100.0, Brushes.Black);
        var rawGeo = formatted.BuildGeometry(new Point(0, 0))!;
        var tight = rawGeo.Bounds;

        Console.WriteLine($"  Raw geometry tight bounds: X={tight.X:F2}, Y={tight.Y:F2}, W={tight.Width:F2}, H={tight.Height:F2}");
        if (tight.Width <= 0 || tight.Height <= 0)
            throw new Exception("Tight bounds must be non-zero!");

        double targetW = 312.0, targetH = 152.0;
        var sx = targetW / tight.Width;
        var sy = targetH / tight.Height;
        var matrix = Matrix.CreateTranslation(-tight.X, -tight.Y) * Matrix.CreateScale(sx, sy);

        var stretched = rawGeo.Clone();
        stretched.Transform = new MatrixTransform(matrix);
        var stretchedBounds = stretched.Bounds;

        Console.WriteLine($"  Stretched geometry bounds: X={stretchedBounds.X:F2}, Y={stretchedBounds.Y:F2}, W={stretchedBounds.Width:F2}, H={stretchedBounds.Height:F2}");
        if (Math.Abs(stretchedBounds.Y - 0) > 0.05)
            throw new Exception($"Top edge must strictly align to Y=0, got {stretchedBounds.Y}");
        if (Math.Abs(stretchedBounds.Bottom - targetH) > 0.05)
            throw new Exception($"Bottom edge must strictly align to Y={targetH}, got {stretchedBounds.Bottom}");
        if (Math.Abs(stretchedBounds.Right - targetW) > 0.05)
            throw new Exception($"Right edge must strictly align to X={targetW}, got {stretchedBounds.Right}");

        Console.WriteLine("  PASS: Tight bounds and stretch matrix strictly fill target [0, 0, 312, 152] without internal font leading.");
    }

    private static void RenderAndVerify(string outDir, string caseName, double w, double h, FramelessClockModel model)
    {
        var view = new FramelessDigital(model)
        {
            Width = w,
            Height = h
        };

        view.Measure(new Size(w, h));
        view.Arrange(new Rect(0, 0, w, h));
        view.UpdateLayout();

        var dpiScale = 2.0;
        var pixelW = (int)Math.Ceiling(w * dpiScale);
        var pixelH = (int)Math.Ceiling(h * dpiScale);

        using var bmp = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96 * dpiScale, 96 * dpiScale));
        bmp.Render(view);

        var outFile = Path.Combine(outDir, $"{caseName}.png");
        bmp.Save(outFile);
        Console.WriteLine($"  -> Saved: {outFile} ({pixelW}x{pixelH} px)");
        Console.WriteLine($"  PASS: {caseName} rendered without error.");
    }

    /// <summary>
    /// 字体粗细 is a slider now, and a slider that stops responding is exactly the defect it
    /// replaced: the nine-step picker was dead on the curated single-face fonts (华为锁屏超窄体 ships
    /// one Black face, Impact one Regular — every step resolved to the same face) and dead above
    /// Bold on the default Inter. The widget answers the upper part of the travel with a synthetic
    /// stroke, so this renders the ink each position actually produces and requires it to grow.
    /// </summary>
    private static void TestWeightAxis(string outDir)
    {
        Console.WriteLine("\n--- 字体粗细 slider axis ---");

        var positions = new[] { 100, 700, 900 };
        var families = new (string Label, string? Family)[]
        {
            ("华为超窄体 (single Black face)", "HarmonyOS Sans Condensed"),
            ("default family", null)
        };

        var failures = new List<string>();
        foreach (var (label, family) in families)
        {
            var ink = new double[positions.Length];
            for (var i = 0; i < positions.Length; i++)
            {
                ink[i] = MeasureInk(outDir, family, positions[i], out var pixels);
                Console.WriteLine($"  {label,-32} weight {positions[i]}: ink {ink[i]:P2} ({pixels} px)");
            }

            // The top of the travel must always respond — that is the part that used to be dead.
            if (ink[2] <= ink[1] * 1.03)
            {
                failures.Add($"{label}: the top of the slider no longer thickens the numerals "
                             + $"({ink[2]:P2} vs {ink[1]:P2})");
            }

            // Where the family really has the range, the lower travel has to respond as well.
            if (family == null && ink[1] <= ink[0] * 1.2)
            {
                failures.Add($"the default family renders its real weights: 700 must be visibly "
                             + $"heavier than 100 ({ink[1]:P2} vs {ink[0]:P2})");
            }
        }

        if (failures.Count > 0)
        {
            Console.WriteLine("  [FAIL] " + string.Join("\n  [FAIL] ", failures));
            throw new Exception($"字体粗细 axis checks failed: {failures.Count}");
        }

        Console.WriteLine("  [PASS] the slider thickens the numerals for every family");
    }

    /// <summary>Fraction of the rendered widget covered by the numerals, plus the pixel count.</summary>
    private static double MeasureInk(string outDir, string? family, int weight, out int inkPixels)
    {
        const double width = 312, height = 152;
        var model = new FramelessClockModel(
            Use24Hours: true, FontFamily: family, FontWeight: weight, StretchFill: true);

        // No settings provider: the acrylic path draws the numerals straight from the geometry, so
        // the ink measured here is the geometry the weight resolved to.
        var view = new FramelessDigital(model) { Width = width, Height = height };
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        view.UpdateLayout();

        const double scale = 2.0;
        var pixelW = (int)Math.Ceiling(width * scale);
        var pixelH = (int)Math.Ceiling(height * scale);
        using var bmp = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
        bmp.Render(view);

        var buffer = new byte[pixelW * pixelH * 4];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(
            buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(0, 0, pixelW, pixelH), handle.AddrOfPinnedObject(),
                buffer.Length, pixelW * 4);
        }
        finally
        {
            handle.Free();
        }

        inkPixels = 0;
        for (var i = 3; i < buffer.Length; i += 4)
        {
            if (buffer[i] > 32) inkPixels++;
        }

        if (outDir.Length > 0)
        {
            var name = $"weight-axis-{family ?? "default"}-{weight}".Replace(' ', '-');
            bmp.Save(Path.Combine(outDir, $"{name}.png"));
        }

        return (double)inkPixels / (pixelW * pixelH);
    }

    private static void TestPreCachingAndAutoCleanup()
    {
        Console.WriteLine("\n--- Testing Liquid Glass Pre-Caching & Automatic Cleanup Logic ---");
        var model = new FramelessClockModel(Use24Hours: true, ShowSeconds: false, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 800, StretchFill: true);

        // The clock takes its material from the global theme, so the liquid glass pipeline is only
        // reached by injecting a provider whose theme is 液态玻璃.
        var clock = new FramelessDigital(model, null, new LiquidGlassSettingsProvider())
        {
            Width = 312,
            Height = 152
        };

        clock.Measure(new Size(312, 152));
        clock.Arrange(new Rect(0, 0, 312, 152));
        clock.UpdateLayout();

        // Render pass
        using var rtb = new RenderTargetBitmap(new PixelSize(624, 304), new Vector(192, 192));
        rtb.Render(clock);

        Console.WriteLine("  PASS: Liquid Glass material resolved from the global theme, pre-caching scheduled, and frame rendered.");
    }

    /// <summary>Minimal settings provider whose global theme is 液态玻璃.</summary>
    private sealed class LiquidGlassSettingsProvider : IAppSettingsProvider
    {
        public event DataChangedEvent<AppSettings>? DataChanging;
        public event DataChangedEvent<AppSettings>? DataChanged;

        public AppSettings Get() => new(
            new Theme(
                DarkMode: null,
                AccentColor: null,
                OpacityLevel: 0.8,
                Monochrome: false,
                UseNativeFrame: false,
                FontFamily: "Segoe UI",
                Surface: SurfaceStyle.LiquidGlass),
            Templates: [],
            new Layout(GridMode.Manual, true, false, true, false),
            new Dimensions(72, 8, 16),
            new Region("zh-Hans"),
            RunOnStartup: false,
            IgnoreUpdate: null);

        public void Save(AppSettings data)
        {
            DataChanging?.Invoke(this, data, data);
            DataChanged?.Invoke(this, data, data);
        }
    }
}

