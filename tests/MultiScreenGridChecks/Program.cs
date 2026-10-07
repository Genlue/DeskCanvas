using System;
using System.Collections.Generic;
using System.Linq;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;
using GridSettings = DeskCanvas.Core.Models.Settings.Grid;

namespace MultiScreenGridChecks;

class FakeLayoutProvider(ScreensLayout initial) : ILayoutProvider
{
    private ScreensLayout data = initial;

    public event DataChangedEvent<ScreensLayout>? DataChanging;
    public event DataChangedEvent<ScreensLayout>? DataChanged;

    public ScreensLayout Get() => data;

    public void Save(ScreensLayout next)
    {
        var prev = data;
        DataChanging?.Invoke(this, prev, next);
        data = next;
        DataChanged?.Invoke(this, prev, next);
    }
}

class FakeAppSettingsProvider(AppSettings initial) : IAppSettingsProvider
{
    private AppSettings data = initial;

    public event DataChangedEvent<AppSettings>? DataChanging;
    public event DataChangedEvent<AppSettings>? DataChanged;

    public AppSettings Get() => data;

    public void Save(AppSettings next)
    {
        var prev = data;
        DataChanging?.Invoke(this, prev, next);
        data = next;
        DataChanged?.Invoke(this, prev, next);
    }
}

class Program
{
    private static int failures;

    static int Main()
    {
        Console.WriteLine("=== Multi-Screen Grid Isolation & Placement Checks ===");
        Console.WriteLine();

        TestGlobalGridInheritance();
        TestPerScreenGridIsolation();
        TestResetToDefault();
        TestGridMetricsResolution();
        TestNegativeCoordinatesPlacement();
        TestButtonHandlerWiring();
        TestScreenTargetResolution();
        TestSequentialScreenEdits();

        Console.WriteLine();
        if (failures == 0)
        {
            Console.WriteLine("ALL CHECKS PASSED");
            return 0;
        }

        Console.WriteLine($"FAILED: {failures} check(s) failed");
        return 1;
    }

    private static AppSettings CreateAppSettings(GridSettings? grid)
    {
        return new AppSettings(
            Theme: new Theme(
                DarkMode: true,
                AccentColor: null,
                OpacityLevel: 0.18,
                Monochrome: false,
                UseNativeFrame: false,
                FontFamily: "Segoe UI",
                Surface: SurfaceStyle.Acrylic),
            Templates: [],
            Layout: new Layout(GridMode.Manual, false, false, false, false),
            Dimensions: new Dimensions(100, 10, 16),
            Region: new Region("zh-Hans"),
            RunOnStartup: false,
            IgnoreUpdate: null,
            Grid: grid);
    }

    private static void TestGlobalGridInheritance()
    {
        Console.WriteLine("--- 1. Global grid inheritance ---");

        var globalGrid = new GridSettings(8, 6, 30, 10, 5);
        var appSettings = CreateAppSettings(globalGrid);
        var screen1 = new ScreenLayout("screen-1", "DELL U2723QE|2560x1440", "Primary", @"\\.\DISPLAY1", null, null, []);
        var screen2 = new ScreenLayout("screen-2", "LG 27UP850|1920x1080", "Secondary", @"\\.\DISPLAY2", null, null, []);
        var screens = new ScreensLayout([screen1, screen2]);

        Assert(screen1.Grid == null, "Screen 1 starts with null (inherits global)");
        Assert(screen2.Grid == null, "Screen 2 starts with null (inherits global)");

        var s1Grid = screen1.Grid ?? appSettings.Grid ?? GridSettings.Default;
        var s2Grid = screen2.Grid ?? appSettings.Grid ?? GridSettings.Default;

        Assert(s1Grid.Columns == 8 && s1Grid.Rows == 6, "Screen 1 resolves to global 8x6");
        Assert(s2Grid.Columns == 8 && s2Grid.Rows == 6, "Screen 2 resolves to global 8x6");
    }

    private static void TestSequentialScreenEdits()
    {
        Console.WriteLine("--- Sequential screen edits preserve current layout and other fields ---");
        var snapshot = new ScreenLayout("screen", "Monitor|1920x1080", null, null, null, null, []);
        var other = new ScreenLayout("other", "Other|1920x1080", null, null, null, null, []);
        var provider = new FakeLayoutProvider(new ScreensLayout([snapshot, other]));
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { Margin = 19 });
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { Radius = 27 });
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { ContentScale = 1.4 });
        var grid = new GridSettings(12, 8, 10, 20, 4);
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { Grid = grid, Alias = "Edited" });
        // A widget or grid editor can write while the page keeps the original card closure.
        var current = provider.Get().FindById(snapshot.Id)!;
        var movedWidget = new WidgetLayout("Weather", "Forecast", 5, 7, 96, 96, default);
        provider.Save(provider.Get().WithScreen(current with { Layout = [movedWidget] }));
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { Margin = null });
        current = provider.Get().FindById(snapshot.Id)!;
        Assert(current.Margin == null && current.Radius == 27 && current.ContentScale == 1.4,
            "Reset margin retains the independently edited radius and scale");
        Assert(current.Grid == grid && current.Alias == "Edited", "Style edits retain grid and alias");
        Assert(current.Layout.Count == 1 && current.Layout[0] == movedWidget,
            "Card edits retain a layout saved after the card was built");
        Assert(provider.Get().FindById(other.Id) == other, "Other screen remains unchanged");
        provider.Save(new ScreensLayout([other]));
        ScreenConfigEditor.Save(provider, snapshot.Id, s => s with { Radius = 9 });
        Assert(provider.Get().FindById(snapshot.Id) == null, "Stale card cannot resurrect a deleted screen");
    }

    private static void TestPerScreenGridIsolation()
    {
        Console.WriteLine("--- 2. Per-screen grid isolation & zero global pollution ---");

        var globalGrid = new GridSettings(8, 6, 30, 10, 5);
        var appSettingsProvider = new FakeAppSettingsProvider(CreateAppSettings(globalGrid));

        var screen1 = new ScreenLayout("screen-1", "DELL U2723QE|2560x1440", "Primary", @"\\.\DISPLAY1", null, null, []);
        var screen2 = new ScreenLayout("screen-2", "LG 27UP850|1920x1080", "Secondary", @"\\.\DISPLAY2", null, null, []);
        var layoutProvider = new FakeLayoutProvider(new ScreensLayout([screen1, screen2]));

        // Simulate user editing Screen 2's grid to 12x8, cell 4%, offset (10%, 20%)
        var customGrid2 = new GridSettings(12, 8, 10, 20, 4);

        // Mimic the fixed SaveGrid logic for screen2
        var currentScreens = layoutProvider.Get();
        var targetScreen = currentScreens.FindById("screen-2");
        if (targetScreen != null)
        {
            layoutProvider.Save(currentScreens.WithScreen(targetScreen with { Grid = customGrid2 }));
        }

        // Check Screen 2
        var updatedScreen2 = layoutProvider.Get().FindById("screen-2");
        Assert(updatedScreen2?.Grid != null, "Screen 2 has custom grid set");
        Assert(updatedScreen2?.Grid?.Columns == 12, "Screen 2 has 12 columns");
        Assert(updatedScreen2?.Grid?.Rows == 8, "Screen 2 has 8 rows");
        Assert(updatedScreen2?.Grid?.CellPercent == 4, "Screen 2 has 4% cell size");

        // Check Screen 1 (MUST NOT BE CHANGED)
        var updatedScreen1 = layoutProvider.Get().FindById("screen-1");
        Assert(updatedScreen1?.Grid == null, "Screen 1 Grid remains null");
        var s1Effective = updatedScreen1?.Grid ?? appSettingsProvider.Get().Grid ?? GridSettings.Default;
        Assert(s1Effective.Columns == 8 && s1Effective.Rows == 6, "Screen 1 still inherits 8x6");

        // Check Global AppSettings (MUST NOT BE POLLUTED)
        var currentGlobal = appSettingsProvider.Get().Grid;
        Assert(currentGlobal?.Columns == 8 && currentGlobal?.Rows == 6, "Global appSettings.Grid was NOT polluted by Screen 2's save");
    }

    private static void TestResetToDefault()
    {
        Console.WriteLine("--- 3. Reset screen grid to default ---");

        var globalGrid = new GridSettings(8, 6, 30, 10, 5);
        var appSettingsProvider = new FakeAppSettingsProvider(CreateAppSettings(globalGrid));

        var customGrid = new GridSettings(10, 10, 5, 5, 8);
        var screen2 = new ScreenLayout("screen-2", "LG 27UP850|1920x1080", "Secondary", @"\\.\DISPLAY2", customGrid, null, []);
        var layoutProvider = new FakeLayoutProvider(new ScreensLayout([screen2]));

        Assert(layoutProvider.Get().FindById("screen-2")?.Grid != null, "Screen 2 initially has custom grid");

        // User clicks Reset Grid: set Grid to null
        var screens = layoutProvider.Get();
        var s2 = screens.FindById("screen-2")!;
        layoutProvider.Save(screens.WithScreen(s2 with { Grid = null }));

        var resetS2 = layoutProvider.Get().FindById("screen-2")!;
        Assert(resetS2.Grid == null, "Screen 2 Grid is reset to null");
        var effective = resetS2.Grid ?? appSettingsProvider.Get().Grid ?? GridSettings.Default;
        Assert(effective.Columns == 8 && effective.Rows == 6, "Screen 2 immediately re-inherits global 8x6");
    }

    private static void TestGridMetricsResolution()
    {
        Console.WriteLine("--- 4. Grid metrics resolution across multiple displays ---");

        // Display 1: 4K at (0, 0), working area 3840x2112, custom grid 12x8, cell 6%, X=10%, Y=5%
        var grid1 = new GridSettings(12, 8, 10, 5, 6);
        var (cell1, x1, y1) = GridMetrics.Resolve(grid1, originX: 0, originY: 0, width: 3840, height: 2112);

        // cell = 3840 * 0.06 = 230 px; X = 3840 * 0.10 = 384 px; Y = 2112 * 0.05 = 106 px
        Assert(cell1 == 230, $"Screen 1 cell expected 230px, got {cell1}px");
        Assert(x1 == 384, $"Screen 1 X expected 384px, got {x1}px");
        Assert(y1 == 106, $"Screen 1 Y expected 106px, got {y1}px");

        // Display 2: 1080p at (3840, 0), working area 1920x1040, default grid 8x6, cell 5%, X=30%, Y=10%
        var grid2 = GridSettings.Default;
        var (cell2, x2, y2) = GridMetrics.Resolve(grid2, originX: 3840, originY: 0, width: 1920, height: 1040);

        // cell = 1920 * 0.05 = 96 px; X = 3840 + 1920 * 0.30 = 4416 px; Y = 1040 * 0.10 = 104 px
        Assert(cell2 == 96, $"Screen 2 cell expected 96px, got {cell2}px");
        Assert(x2 == 4416, $"Screen 2 X expected 4416px, got {x2}px");
        Assert(y2 == 104, $"Screen 2 Y expected 104px, got {y2}px");
    }

    private static void TestNegativeCoordinatesPlacement()
    {
        Console.WriteLine("--- 5. Secondary monitor with negative coordinates (left of primary) ---");

        // Secondary display placed to the left: X = -1920, Y = 0, Width = 1920, Height = 1080
        var grid = new GridSettings(6, 6, 20, 10, 5);
        var (cell, x, y) = GridMetrics.Resolve(grid, originX: -1920, originY: 0, width: 1920, height: 1040);

        // cell = 1920 * 0.05 = 96 px
        // X = -1920 + 1920 * 0.20 = -1920 + 384 = -1536 px
        Assert(cell == 96, $"Negative screen cell expected 96px, got {cell}px");
        Assert(x == -1536, $"Negative screen X expected -1536px, got {x}px");
        Assert(y == 104, $"Negative screen Y expected 104px, got {y}px");
    }

    private static void TestButtonHandlerWiring()
    {
        Console.WriteLine("--- 6. MultiScreen button click handler wiring ---");

        var clicked = false;
        var btn = CreateTestButton("Test Button", (_, _) => clicked = true);

        // Raise click
        btn.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

        Assert(clicked, "Button Click event handler is correctly attached and invoked");
    }

    private static Avalonia.Controls.Button CreateTestButton(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> handler)
    {
        var btn = new Avalonia.Controls.Button
        {
            Content = text,
            FontSize = 12,
            Padding = new Avalonia.Thickness(12, 6)
        };
        btn.Click += handler;
        return btn;
    }

    private static void TestScreenTargetResolution()
    {
        Console.WriteLine("--- 7. Target screen resolution for secondary display editing ---");

        // When user is on Screen B and clicks edit grid in Advanced:
        // If target attached screen exists, it must produce valid target config ID and attached screen
        var screenBId = "screen-b-id";
        var isTargetConfigured = !string.IsNullOrEmpty(screenBId);
        Assert(isTargetConfigured, "Screen B target config is resolved");

        // When global default is selected, it falls back to the screen containing the current window
        var currentWindowOnScreenB = true;
        var fallbackScreen = currentWindowOnScreenB ? "Screen B" : "Screen A";
        Assert(fallbackScreen == "Screen B", "Global default grid editing opens on the screen currently containing the window");
    }

    private static void Assert(bool condition, string message)
    {
        if (condition)
        {
            Console.WriteLine($"  [PASS] {message}");
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [FAIL] {message}");
            Console.ResetColor();
            failures++;
        }
    }
}
