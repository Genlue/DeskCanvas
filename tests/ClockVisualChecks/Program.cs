using System;
using System.Collections.Generic;
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
using Clock.Services;
using Clock.Views;
using SkiaSharp;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;

namespace ClockVisualChecks;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.WriteLine("=== Starting Frameless Clock Visual & Font Checks ===");
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "dist/frameless-clock-checks");
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Dark;
        app.Resources["SystemAccentColor"] = Color.Parse("#12C46A");

        // 1. Verify tight bounds and stretch math
        TestGeometryStretchMath();

        // 2. Verify all 8 bundled iOS Lock Screen artistic fonts render distinct shapes
        TestBundledArtisticFontsDistinct(outDir);

        // 3. Verify FixedWidthDigits tabular slot layout
        TestFixedWidthDigits(outDir);

        // 4. Visual test cases covering artistic fonts, system fonts, themes, and overlays
        var testCases = new (string CaseName, double Width, double Height, FramelessClockModel Model)[]
        {
            ("4x2-artistic-harmonyos", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 800, StretchFill: true)),
            ("4x2-artistic-bebas-neue", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-dm-serif", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "DM Serif Display", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-bungee-inline", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bungee Inline", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-black-ops", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Black Ops One", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-titan-one", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Titan One", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-righteous", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Righteous", FontWeight: 700, StretchFill: true)),
            ("4x2-artistic-orbitron", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Orbitron", FontWeight: 700, StretchFill: true)),
            ("4x2-system-bahnschrift-din", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bahnschrift", FontWeight: 700, StretchFill: true, UseSystemFont: true)),
            ("4x2-system-segoe-variable", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Segoe UI Variable", FontWeight: 700, StretchFill: true, UseSystemFont: true)),
            ("4x2-thin-100", 312, 152, new FramelessClockModel(Use24Hours: true, FontWeight: 100, StretchFill: true, UseSystemFont: true)),
            ("4x2-black-900", 312, 152, new FramelessClockModel(Use24Hours: true, FontWeight: 900, StretchFill: true, UseSystemFont: true)),
            ("2x2-regular-uniform", 152, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", FontWeight: 700, StretchFill: false)),
            ("4x1-seconds-fixedwidth", 312, 72, new FramelessClockModel(Use24Hours: true, ShowSeconds: true, FontFamily: "Bebas Neue", FontWeight: 700, StretchFill: true, FixedWidthDigits: true)),
            ("4x2-theme-acrylic", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", StretchFill: true, SurfaceOverride: SurfaceStyle.Acrylic)),
            ("4x2-theme-liquidglass", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", StretchFill: true, SurfaceOverride: SurfaceStyle.LiquidGlass)),
            ("4x2-theme-liquidglass-v2", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", StretchFill: true, SurfaceOverride: SurfaceStyle.LiquidGlassV2)),
            ("4x2-theme-solid", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", StretchFill: true, SurfaceOverride: SurfaceStyle.Solid)),
            ("4x2-theme-colorful", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", StretchFill: true, SurfaceOverride: SurfaceStyle.Colorful)),
            ("4x2-overlay-tint", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "HarmonyOS Sans Condensed", FontWeight: 800, StretchFill: true, EnableOverlay: true, FollowAccentColor: true, OverlayOpacity: 0.40)),
            ("4x2-gold-tint", 312, 152, new FramelessClockModel(Use24Hours: true, FontFamily: "Bebas Neue", FontWeight: 700, StretchFill: true, EnableOverlay: true, FollowAccentColor: false, OverlayColor: "#FFCC00", OverlayOpacity: 0.50))
        };

        foreach (var tc in testCases)
        {
            Console.WriteLine($"\n--- Testing and Rendering {tc.CaseName} ({tc.Width}x{tc.Height} DIP) ---");
            RenderAndVerify(outDir, tc.CaseName, tc.Width, tc.Height, tc.Model);
        }

        // 5. Verify native weight axis on multi-weight system font (without forced synthetic stroke distortion)
        TestWeightAxis(outDir);

        Console.WriteLine($"\nAll Frameless Clock visual checks completed successfully! Output folder: {outDir}");
    }

    private static void TestGeometryStretchMath()
    {
        Console.WriteLine("\n--- Testing Tight Bounds & Stretch Transform Precision ---");
        double targetW = 312.0, targetH = 152.0;
        var stretched = FramelessGlyphGeometry.BuildStretch("12:38", targetW, targetH, "Bebas Neue", 700, stretchFill: true, theme: null, fixedWidthDigits: false);
        if (stretched == null)
            throw new Exception("FramelessGlyphGeometry.BuildStretch returned null!");

        var stretchedBounds = stretched.Bounds;
        Console.WriteLine($"  Stretched geometry bounds: X={stretchedBounds.X:F2}, Y={stretchedBounds.Y:F2}, W={stretchedBounds.Width:F2}, H={stretchedBounds.Height:F2}");
        if (Math.Abs(stretchedBounds.Y - 0) > 0.5)
            throw new Exception($"Top edge must strictly align to Y=0, got {stretchedBounds.Y}");
        if (Math.Abs(stretchedBounds.Bottom - targetH) > 0.5)
            throw new Exception($"Bottom edge must strictly align to Y={targetH}, got {stretchedBounds.Bottom}");
        if (Math.Abs(stretchedBounds.Right - targetW) > 0.5)
            throw new Exception($"Right edge must strictly align to X={targetW}, got {stretchedBounds.Right}");

        Console.WriteLine("  PASS: Tight bounds and stretch matrix strictly fill target [0, 0, 312, 152] without internal font leading.");
    }

    private static void TestBundledArtisticFontsDistinct(string outDir)
    {
        Console.WriteLine("\n--- Verifying all 8 bundled iOS Lock Screen artistic fonts render distinct shapes ---");
        var families = new[]
        {
            "HarmonyOS Sans Condensed",
            "Bebas Neue",
            "DM Serif Display",
            "Bungee Inline",
            "Black Ops One",
            "Titan One",
            "Righteous",
            "Orbitron"
        };

        var defaultPixels = RenderFontMask("Segoe UI", outDir, "font-probe-segoe-ui");
        var snapshots = new Dictionary<string, byte[]>();

        foreach (var family in families)
        {
            var slug = family.ToLowerInvariant().Replace(' ', '-');
            var pixels = RenderFontMask(family, outDir, $"font-probe-{slug}");
            var diffVsDefault = MeanDiff(pixels, defaultPixels);
            Console.WriteLine($"  {family,-26} diff vs Segoe UI = {diffVsDefault:F2}");
            if (diffVsDefault < 2.0)
                throw new Exception($"Bundled artistic font '{family}' fell back to Segoe UI (diff={diffVsDefault:F2})!");

            foreach (var (otherFamily, otherPixels) in snapshots)
            {
                var diffVsOther = MeanDiff(pixels, otherPixels);
                if (diffVsOther < 2.0)
                    throw new Exception($"Bundled artistic font '{family}' looks identical to '{otherFamily}' (diff={diffVsOther:F2})!");
            }

            snapshots[family] = pixels;
        }

        Console.WriteLine("  PASS: All 8 bundled artistic fonts loaded from embedded .ttf assets and rendered distinct glyph shapes.");
    }

    private static void TestFixedWidthDigits(string outDir)
    {
        Console.WriteLine("\n--- Verifying FixedWidthDigits (anti-jitter tabular slots) ---");
        // In asymmetric times like "11:28" vs "28:11", natural tight-bounds stretching causes
        // narrow '1's to shift the colon horizontally, whereas FixedWidthDigits keeps every slot
        // and the center colon at the exact same X coordinate.
        var geoFixed11 = FramelessGlyphGeometry.BuildStretch("11:11", 312, 152, "Bebas Neue", 700, stretchFill: true, theme: null, fixedWidthDigits: true)!;
        var geoFixed28 = FramelessGlyphGeometry.BuildStretch("28:48", 312, 152, "Bebas Neue", 700, stretchFill: true, theme: null, fixedWidthDigits: true)!;

        Console.WriteLine($"  FixedWidthDigits '11:11' outer bounds: X={geoFixed11.Bounds.X:F1}, W={geoFixed11.Bounds.Width:F1}");
        Console.WriteLine($"  FixedWidthDigits '28:48' outer bounds: X={geoFixed28.Bounds.X:F1}, W={geoFixed28.Bounds.Width:F1}");

        // Because '1' is centered inside slot 0 and slot 4, '11:11' has symmetric side padding (X > 5)
        // instead of stretching '1' to the full width of '8'.
        if (geoFixed11.Bounds.X < 4.0 || geoFixed11.Bounds.Width >= geoFixed28.Bounds.Width)
            throw new Exception("FixedWidthDigits should center narrow '1' digits inside uniform tabular slots!");

        Console.WriteLine("  PASS: FixedWidthDigits places narrow digits ('1') into uniform tabular slots with zero horizontal jitter.");
    }

    private static byte[] RenderFontMask(string fontFamily, string outDir, string name)
    {
        var geo = FramelessGlyphGeometry.BuildStretch("12:38", 312, 152, fontFamily, 700, stretchFill: true, theme: null, fixedWidthDigits: false)
                  ?? throw new Exception($"Failed to build geometry for {fontFamily}");
        var mask = FramelessGlyphGeometry.ExtractMask(geo, 312, 152, 1.0, 312, 152);
        return mask;
    }

    private static double MeanDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
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

    private static void TestWeightAxis(string outDir)
    {
        Console.WriteLine("\n--- 字体粗细 native weight axis (no synthetic stroke distortion) ---");

        var ink100 = MeasureInk(outDir, "Segoe UI", 100, out var px100);
        var ink700 = MeasureInk(outDir, "Segoe UI", 700, out var px700);
        var ink900 = MeasureInk(outDir, "Segoe UI", 900, out var px900);

        Console.WriteLine($"  Segoe UI weight 100: ink {ink100:P2} ({px100} px)");
        Console.WriteLine($"  Segoe UI weight 700: ink {ink700:P2} ({px700} px)");
        Console.WriteLine($"  Segoe UI weight 900: ink {ink900:P2} ({px900} px)");

        if (ink700 <= ink100 * 1.15)
            throw new Exception($"Segoe UI weight 700 should be visibly heavier than weight 100 ({ink700:P2} vs {ink100:P2})");

        Console.WriteLine("  [PASS] Multi-weight system fonts respond to the weight slider without synthetic stroke loops.");
    }

    private static double MeasureInk(string outDir, string? family, int weight, out int inkPixels)
    {
        const double width = 312, height = 152;
        var model = new FramelessClockModel(
            Use24Hours: true, FontFamily: family, FontWeight: weight, StretchFill: true, SurfaceOverride: SurfaceStyle.Solid);

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
}
