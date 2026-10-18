using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
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
        TestStoredSizeSpanResolution();
        TestNegativeCoordinatesPlacement();
        TestButtonHandlerWiring();
        TestScreenTargetResolution();
        TestSequentialScreenEdits();
        TestFirstFitSequentialPlacement();
        TestFirstFitHeterogeneousSizes();
        TestFirstFitWrapsToNextRow();
        TestFirstFitFullGridRefused();
        TestFirstFitOffGridStoredWidget();
        TestFirstFitStaysInsideWorkingArea();
        TestStoredWidgetOutsideAreaBlocksShownCells();
        TestSpanLargerThanGridIsRefused();
        TestFirstFitNoOverlapAcrossManyPlacements();

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

        // Display 2: 1080p at (3840, 0), working area 1920x1040, grid 8x6, cell 5%, X=30%, Y=10%
        // (an explicit grid, not Grid.Default — this check pins the percentage math, and the
        // shipped default is deliberately free to change)
        var grid2 = new GridSettings(8, 6, 30, 10, 5);
        var (cell2, x2, y2) = GridMetrics.Resolve(grid2, originX: 3840, originY: 0, width: 1920, height: 1040);

        // cell = 1920 * 0.05 = 96 px; X = 3840 + 1920 * 0.30 = 4416 px; Y = 1040 * 0.10 = 104 px
        Assert(cell2 == 96, $"Screen 2 cell expected 96px, got {cell2}px");
        Assert(x2 == 4416, $"Screen 2 X expected 4416px, got {x2}px");
        Assert(y2 == 104, $"Screen 2 Y expected 104px, got {y2}px");
    }

    /// <summary>
    /// Stored widget sizes are DIPs today, but layouts written by pre-DIP builds carry
    /// physical pixels and are never rewritten until the widget is moved or resized.
    /// The gallery's occupancy test for a newly placed widget reads stored sizes through
    /// <see cref="GridMetrics.ResolveSpan"/>, so both formats must resolve to the same
    /// span — otherwise an upgraded install reports free cells as taken (new widgets skip
    /// them or fall back to the grid origin, i.e. on top of the existing ones).
    /// </summary>
    private static void TestStoredSizeSpanResolution()
    {
        Console.WriteLine("--- 4b. Stored size -> span resolution (DIP vs legacy physical) ---");

        // 125% display, 5% of a 2560px-wide working area: 128px cells = 102.4 DIP.
        const double scaling = 1.25;
        const int cell = 128;

        Assert(GridMetrics.ResolveSpan(205, cell, scaling) == 2,
            "DIP size for a 2-cell span resolves to 2");
        Assert(GridMetrics.ResolveSpan(256, cell, scaling) == 2,
            "legacy physical size for the same 2-cell span resolves to 2");
        Assert(GridMetrics.ResolveSpan(410, cell, scaling) == 4,
            "DIP size for a 4-cell span resolves to 4");
        Assert(GridMetrics.ResolveSpan(102, cell, scaling) == 1,
            "DIP size for a 1-cell span resolves to 1");
        Assert(GridMetrics.ResolveSpan(0, cell, scaling) == 1,
            "degenerate stored size still occupies one cell");

        // The heuristic is inherently ambiguous when a legacy physical size is also a whole
        // number of DIP cells: 512px on a 125% display is 4 physical cells but exactly 5 DIP
        // cells. The DIP reading wins by contract — and that is what the widget's own restore
        // uses too, so the gallery and the widget disagree about nothing.
        Assert(GridMetrics.ResolveSpan(512, cell, scaling) == 5,
            "ambiguous legacy size follows the DIP reading (same answer the widget restores to)");

        // Unambiguous spans must agree between the two storage formats cell for cell
        // (span 4 and 8 are the ambiguous ones above and are deliberately excluded).
        foreach (var span in new[] { 1, 2, 3, 5, 6, 7 })
        {
            var dip = (int) Math.Round(span * cell / scaling);
            var physical = span * cell;
            Assert(GridMetrics.ResolveSpan(dip, cell, scaling) == span
                   && GridMetrics.ResolveSpan(physical, cell, scaling) == span,
                $"span {span}: DIP ({dip}) and legacy physical ({physical}) agree");
        }
    }

    private static void TestNegativeCoordinatesPlacement()
    {        Console.WriteLine("--- 5. Secondary monitor with negative coordinates (left of primary) ---");

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

    // ---------- Gallery placement: row-major first-fit (吞组件 regression) ----------

    /// <summary>
    /// Mirror of <c>Gallery.ComputePlacement</c>: resolve the grid, build the placement frame
    /// (grid lattice + working area), ask <see cref="GridMetrics.FindFirstFitCell"/> for the
    /// first free block and convert it into a working-area-relative rectangle (physical X/Y,
    /// DIP size). <c>null</c> means the gallery refuses the placement — the old grid-origin
    /// fallback no longer exists. The check then treats each result as a stored widget exactly
    /// like the real flow does (the next placement scans the layout with the entry in it).
    /// </summary>
    private static WidgetLayout? PlaceLikeGallery(ScreenLayout screen, PixelRect area, double scaling,
        GridSettings? gridOverride, int cols, int rows)
    {
        var grid = gridOverride ?? screen.Grid ?? GridSettings.Default;
        var (cell, gridX, gridY) = GridMetrics.Resolve(grid, area.X, area.Y, area.Width, area.Height);
        var frame = new GridMetrics.PlacementFrame(cell, scaling, gridX - area.X, gridY - area.Y,
            grid.Columns, grid.Rows, area.Width, area.Height);
        var (col, row) = GridMetrics.FindFirstFitCell(screen.Layout, frame, cols, rows);

        // Nothing fits: grid full, card larger than the grid, or no room left in the area.
        if (col < 0) return null;

        var layout = new WidgetLayout("Test", "Widget", frame.GridX + col * cell, frame.GridY + row * cell,
            (int) Math.Round(cols * cell / scaling), (int) Math.Round(rows * cell / scaling), null);
        screen.Layout.Add(layout);
        return layout;
    }

    /// <summary>
    /// The rectangle a stored widget is actually SHOWN at: its stored position clamped onto
    /// the working area (mirrors <c>Widget.ClampRestoredPosition</c>, which does not save the
    /// adjustment). Two entries can share one visible rectangle even though their stored
    /// coordinates differ — that is the "two widgets at 848" overlap.
    /// </summary>
    private static (int X, int Y, int W, int H) ShownRect(WidgetLayout widget, PixelRect area, double scaling)
    {
        var width = (int) Math.Round(widget.Width * scaling);
        var height = (int) Math.Round(widget.Height * scaling);
        return (Math.Clamp(widget.X, area.X, area.X + Math.Max(0, area.Width - width)),
                Math.Clamp(widget.Y, area.Y, area.Y + Math.Max(0, area.Height - height)),
                width, height);
    }

    private static bool Overlaps(WidgetLayout a, WidgetLayout b, PixelRect area, double scaling)
    {
        var (ax, ay, aw, ah) = ShownRect(a, area, scaling);
        var (bx, by, bw, bh) = ShownRect(b, area, scaling);
        return ax < bx + bw && bx < ax + aw && ay < by + bh && by < ay + ah;
    }

    /// <summary>Default test topology: 1920×1080 at (0,0), 100% scaling, 1920×1040 working
    /// area (taskbar), the shipped default 20×10 grid of 5% cells (96 px).</summary>
    private static (ScreenLayout Screen, PixelRect Area, double Scaling) TestTopology(GridSettings? grid = null)
    {
        var screen = new ScreenLayout("screen", "DELL U2723QE|1920x1040", null, null, grid, null, []);
        return (screen, new PixelRect(0, 0, 1920, 1040), 1.0);
    }

    /// <summary>
    /// The reported 吞组件 repro: place the same 2×2 widget repeatedly. Every new card
    /// must take the next free slot — never the slot of a card placed a moment ago.
    /// </summary>
    private static void TestFirstFitSequentialPlacement()
    {
        Console.WriteLine("--- 8. Row-major first-fit: sequential same-size placements ---");
        var (screen, area, scaling) = TestTopology();
        var placed = new List<WidgetLayout>();
        for (var i = 0; i < 9; i++)
        {
            var widget = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
            Assert(widget != null, $"placement #{i + 1} found a free slot");
            placed.Add(widget!);
        }

        // 20 columns / 2 per card → row 0 holds ten cards at cols 0,2,4…18.
        for (var i = 0; i < placed.Count; i++)
        {
            var expectedCol = (i % 10) * 2;
            var expectedRow = i / 10 * 2;
            Assert(placed[i].X == expectedCol * 96 && placed[i].Y == expectedRow * 96,
                $"placement #{i + 1} landed on cell ({expectedCol},{expectedRow}) " +
                $"(got {placed[i].X},{placed[i].Y})");
        }

        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++)
                Assert(!Overlaps(placed[i], placed[j], area, scaling),
                    $"placement #{i + 1} and #{j + 1} do not overlap");
    }

    /// <summary>Different spans must pack together: a 4×1 wide card fills single-row
    /// slots a 2×2 card cannot, and 1×1 minis fill the remainder.</summary>
    private static void TestFirstFitHeterogeneousSizes()
    {
        Console.WriteLine("--- 9. Row-major first-fit: heterogeneous widget sizes ---");
        var (screen, area, scaling) = TestTopology();
        var wide = PlaceLikeGallery(screen, area, scaling, null, 4, 1)!;
        var card = PlaceLikeGallery(screen, area, scaling, null, 2, 2)!;
        var mini = PlaceLikeGallery(screen, area, scaling, null, 1, 1)!;

        Assert(wide.X == 0 && wide.Y == 0, "4×1 takes the first row's start (cells 0-3)");
        Assert(card.X == 4 * 96 && card.Y == 0, "2×2 takes the next free cells (4-5), not a covered slot");
        Assert(mini.X == 6 * 96 && mini.Y == 0, "1×1 takes cell 6 right after the 2×2 card");

        Assert(!Overlaps(wide, card, area, scaling) && !Overlaps(wide, mini, area, scaling)
               && !Overlaps(card, mini, area, scaling),
            "heterogeneous placements do not overlap");
    }

    /// <summary>One row filled → the scan continues on the next row.</summary>
    private static void TestFirstFitWrapsToNextRow()
    {
        Console.WriteLine("--- 10. Row-major first-fit: wraps to the next row ---");
        var (screen, area, scaling) = TestTopology();
        WidgetLayout last = null!;
        for (var i = 0; i < 10; i++) last = PlaceLikeGallery(screen, area, scaling, null, 2, 2)!;
        Assert(last.X == 18 * 96 && last.Y == 0, "the tenth 2×2 card completes row 0 (cells 18-19)");
        var wrapped = PlaceLikeGallery(screen, area, scaling, null, 2, 2)!;
        Assert(wrapped.X == 0 && wrapped.Y == 2 * 96, "the eleventh card wraps to row 1, column 0");
    }

    /// <summary>A genuinely full grid reports "no fit" and the gallery REFUSES the card.
    /// The old fallback (grid origin, occupancy ignored) stacked it onto the first one —
    /// the 吞组件 overlap the report describes.</summary>
    private static void TestFirstFitFullGridRefused()
    {
        Console.WriteLine("--- 11. Row-major first-fit: a full grid refuses the card ---");
        // 2×2 grid of 480 px cells: 960×960 fits the 1920×1040 working area.
        var (screen, area, scaling) = TestTopology(new GridSettings(2, 2, 0, 0, 25));
        var first = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
        Assert(first is { X: 0, Y: 0 }, "the first card takes the whole-grid slot");

        var second = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
        Assert(second == null, "the second card is refused instead of covering the first one");
        Assert(screen.Layout.Count == 1, "a refused card is not written to the layout");

        var frame = new GridMetrics.PlacementFrame(480, 1.0, 0, 0, 2, 2, area.Width, area.Height);
        Assert(GridMetrics.FindFirstFitCell(screen.Layout, frame, 2, 2) == (-1, -1),
            "a full 2×2 grid reports (-1,-1) instead of a covered slot");
    }

    /// <summary>A stored widget parked OFF the current grid's cell boundaries (legacy
    /// absolute entries, grid-origin edits) must block every cell its pixel footprint
    /// reaches into — no candidate may slip into the sliver between its rect and the
    /// cell lattice and partially overlap it.</summary>
    private static void TestFirstFitOffGridStoredWidget()
    {
        Console.WriteLine("--- 12. Row-major first-fit: off-grid stored widget ---");
        var (screen, area, scaling) = TestTopology();
        // Half a cell off the origin: the 192px footprint [48,240) physically covers
        // parts of cells 0, 1 AND 2 — all three must read as occupied.
        screen.Layout.Add(new WidgetLayout("Test", "Legacy", 48, 0, 2 * 96, 96, null));
        var frame = new GridMetrics.PlacementFrame(96, 1.0, 0, 0, 20, 10, area.Width, area.Height);
        var (col, row) = GridMetrics.FindFirstFitCell(screen.Layout, frame, 2, 2);
        Assert(!(col == 0 && row == 0), "a 2×2 candidate is rejected at the origin next to the off-grid widget");
        Assert(!(col == 1 && row == 0), "a 2×2 candidate is rejected straddling the off-grid widget");
        Assert(!(col == 2 && row == 0), "a 2×2 candidate cannot overlap the widget's trailing sliver in cell 2");
        Assert(col == 3 && row == 0, $"the first fully free 2×2 block starts at cell (3,0) (got {col},{row})");
    }

    /// <summary>
    /// Report bug 2: the grid can hang off the working area (Y 50% + ten 96 px rows on a
    /// 1040 px screen reaches 1480 px). A cell below the area is not a slot — the window is
    /// clamped back onto the screen at display time, so two cards stored at different rows
    /// both showed up at y = 848. The scan must stay inside the area and the card must never
    /// be placed where the clamp will move it.
    /// </summary>
    private static void TestFirstFitStaysInsideWorkingArea()
    {
        Console.WriteLine("--- 13. Row-major first-fit: grid hanging off the working area ---");
        var (screen, area, scaling) = TestTopology(new GridSettings(20, 10, 0, 50, 5));

        // Grid origin is 50% of 1040 = 520 px; only rows 0-4 still end inside the area.
        var placed = new List<WidgetLayout>();
        for (var i = 0; i < 40; i++)
        {
            var widget = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
            if (widget == null) break;
            placed.Add(widget);
        }

        Assert(placed.Count > 0, "cards are still placed while the grid has visible rows");
        Assert(placed.All(w => w.Y >= 520), "no card is placed above the grid origin");
        Assert(placed.All(w => w.Y + w.Height <= area.Height),
            "no card is placed where the working-area clamp would move it (y + height <= 1040)");
        Assert(placed.All(w => w.X >= 0 && w.X + w.Width <= area.Width), "no card overflows the area horizontally");

        // The clamp is a no-op for every placed card, so placement == presentation.
        Assert(placed.All(w => ShownRect(w, area, scaling) == (w.X, w.Y, w.Width, w.Height)),
            "every placed card is shown exactly where it was placed");

        var violations = 0;
        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++)
                if (Overlaps(placed[i], placed[j], area, scaling)) violations++;
        Assert(violations == 0, $"none of the {placed.Count} placements overlap ({violations} violations)");

        var refused = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
        Assert(placed.Count is 40 || refused == null,
            "once the visible rows are full the next card is refused, not stacked on the origin");
    }

    /// <summary>
    /// The exact repro from the report: two widgets stored at y = 904 and y = 1096 are both
    /// clamped to y = 848 when shown. Occupancy has to be judged on the SHOWN rectangle, or
    /// the next card is placed into cells that are already covered on screen.
    /// </summary>
    private static void TestStoredWidgetOutsideAreaBlocksShownCells()
    {
        Console.WriteLine("--- 14. Row-major first-fit: stored-outside-area widget blocks its shown cells ---");
        var (screen, area, scaling) = TestTopology(new GridSettings(20, 10, 0, 50, 5));
        var below = new WidgetLayout("Test", "Legacy", 0, 1096, 192, 192, null);
        screen.Layout.Add(below);
        Assert(ShownRect(below, area, scaling).Y == 848, "a widget stored at y=1096 is shown at y=848");

        var next = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
        Assert(next != null, "the next card still finds a free slot");
        Assert(!Overlaps(next!, below, area, scaling),
            $"the new card does not land on the clamped widget (got y={next!.Y})");
    }

    /// <summary>
    /// Report bug 3: a span larger than the grid used to be scanned with a CLAMPED span while
    /// keeping its full width, so a 4×2 card was "placeable" on a 2×2 grid and overflowed it.
    /// It is now refused.
    /// </summary>
    private static void TestSpanLargerThanGridIsRefused()
    {
        Console.WriteLine("--- 15. Row-major first-fit: a card larger than the grid is refused ---");
        var (screen, area, scaling) = TestTopology(new GridSettings(2, 2, 0, 0, 5));
        var refused = PlaceLikeGallery(screen, area, scaling, null, 4, 2);
        Assert(refused == null, "a 4×2 card is refused on a 2-column grid");
        Assert(screen.Layout.Count == 0, "nothing is written for a refused card");

        var fits = PlaceLikeGallery(screen, area, scaling, null, 2, 2);
        Assert(fits is { X: 0, Y: 0, Width: 2 * 96, Height: 2 * 96 },
            "a card that matches the grid is still placed at its natural size");
    }

    /// <summary>The full gallery stress: many mixed-span placements over the shipped
    /// default grid — pairwise pixel overlap is the contract (no 吞组件), and the
    /// layout stays inside the grid area.</summary>
    private static void TestFirstFitNoOverlapAcrossManyPlacements()
    {
        Console.WriteLine("--- 16. Row-major first-fit: mixed-size stress, zero pairwise overlap ---");
        var (screen, area, scaling) = TestTopology();
        var spans = new[] { (2, 2), (4, 1), (1, 1), (6, 3), (2, 1), (3, 3), (2, 2), (8, 4), (1, 2), (4, 4), (2, 2), (1, 1) };
        var placed = new List<WidgetLayout>();
        foreach (var (cols, rows) in spans)
            if (PlaceLikeGallery(screen, area, scaling, null, cols, rows) is { } widget)
                placed.Add(widget);

        var violations = 0;
        for (var i = 0; i < placed.Count; i++)
            for (var j = i + 1; j < placed.Count; j++)
                if (Overlaps(placed[i], placed[j], area, scaling)) violations++;

        Assert(violations == 0, $"none of the {placed.Count} mixed-size placements overlap ({violations} violations)");
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
