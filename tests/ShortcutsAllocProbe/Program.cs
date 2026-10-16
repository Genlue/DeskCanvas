using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.ViewModels;

namespace ShortcutsAllocProbe;

/// <summary>
/// Local, gitignored allocation probe (NOT part of the plugin). Measures per-call managed
/// allocations of the cached helpers against their uncached equivalents, using
/// GC.GetAllocatedBytesForCurrentThread so the comparison is apples-to-apples on one thread.
/// </summary>
internal static class Program
{
    private const int N = 200_000;

    [STAThread]
    private static int Main()
    {
        AppBuilder.Configure<Application>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions())
            .SetupWithoutStarting();

        var hex = "#534AB7";

        // ---- Visuals.Brush --------------------------------------------------------
        for (var i = 0; i < 1000; i++) Visuals.Brush(hex);
        var brushBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) Visuals.Brush(hex);
        var brushCached = GC.GetAllocatedBytesForCurrentThread() - brushBefore;

        for (var i = 0; i < 1000; i++) _ = new SolidColorBrush(Visuals.ParseColor(hex));
        var brushUncachedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) _ = new SolidColorBrush(Visuals.ParseColor(hex));
        var brushUncached = GC.GetAllocatedBytesForCurrentThread() - brushUncachedBefore;

        // ---- Visuals.Tint ---------------------------------------------------------
        for (var i = 0; i < 1000; i++) Visuals.Tint(hex, 0x2E);
        var tintBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) Visuals.Tint(hex, 0x2E);
        var tintCached = GC.GetAllocatedBytesForCurrentThread() - tintBefore;

        for (var i = 0; i < 1000; i++) _ = new SolidColorBrush(Color.FromArgb(0x2E, 0x53, 0x4A, 0xB7));
        var tintUncachedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) _ = new SolidColorBrush(Color.FromArgb(0x2E, 0x53, 0x4A, 0xB7));
        var tintUncached = GC.GetAllocatedBytesForCurrentThread() - tintUncachedBefore;

        // ---- Visuals.Shade --------------------------------------------------------
        for (var i = 0; i < 1000; i++) Visuals.Shade(hex);
        var shadeBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) Visuals.Shade(hex);
        var shadeCached = GC.GetAllocatedBytesForCurrentThread() - shadeBefore;

        for (var i = 0; i < 1000; i++) _ = new SolidColorBrush(Color.FromRgb(0x3B, 0x35, 0x83));
        var shadeUncachedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) _ = new SolidColorBrush(Color.FromRgb(0x3B, 0x35, 0x83));
        var shadeUncached = GC.GetAllocatedBytesForCurrentThread() - shadeUncachedBefore;

        // ---- ShortcutTileViewModel.StateBrush ------------------------------------
        var tile = new ShortcutTileViewModel(new ShortcutDefinition { Name = "probe", Glyph = "bolt" });
        tile.SetFinished(success: true, detail: null); // -> TileState.Done
        for (var i = 0; i < 1000; i++) _ = tile.StateBrush;
        var stateBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) _ = tile.StateBrush;
        var stateCached = GC.GetAllocatedBytesForCurrentThread() - stateBefore;

        for (var i = 0; i < 1000; i++) _ = new SolidColorBrush(Color.Parse("#3B6D11"));
        var stateUncachedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < N; i++) _ = new SolidColorBrush(Color.Parse("#3B6D11"));
        var stateUncached = GC.GetAllocatedBytesForCurrentThread() - stateUncachedBefore;

        Report("Visuals.Brush", brushCached, brushUncached);
        Report("Visuals.Tint", tintCached, tintUncached);
        Report("Visuals.Shade", shadeCached, shadeUncached);
        Report("TileVM.StateBrush", stateCached, stateUncached);

        var totalCached = brushCached + tintCached + shadeCached + stateCached;
        var totalUncached = brushUncached + tintUncached + shadeUncached + stateUncached;
        Console.WriteLine();
        Console.WriteLine($"TOTAL over 4 x {N:N0} calls: cached {totalCached:N0} B vs uncached {totalUncached:N0} B " +
                          $"({(totalUncached == 0 ? "0" : (100.0 * totalCached / totalUncached).ToString("F1"))}%)");
        return 0;
    }

    private static void Report(string name, long cached, long uncached)
    {
        var perCall = (uncached - cached) / (double)N;
        Console.WriteLine($"{name,-20} cached {cached,12:N0} B   uncached {uncached,12:N0} B   saved {perCall,6:F1} B/call");
    }
}
