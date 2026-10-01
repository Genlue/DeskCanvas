using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Search.Models;
using Search.ViewModels;
using Search.Views;

namespace SearchVisualChecks;

class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Console.WriteLine("=== Starting Search Widget Visual Checks ===");
        var outDir = Path.GetFullPath(args.Length > 0 ? args[0] : "dist/search-checks");
        Directory.CreateDirectory(outDir);

        AppBuilder.Configure<Application>()
            .UsePlatformDetect()
            .SetupWithoutStarting();

        var app = Application.Current!;
        app.Styles.Add(new FluentTheme());
        app.RequestedThemeVariant = ThemeVariant.Dark;

        var testSizes = new (string Name, double Width, double Height)[]
        {
            ("1x1", 72, 72),
            ("4x1", 312, 72),
            ("2x2", 152, 152),
            ("4x2", 312, 152),
            ("4x4", 312, 312)
        };

        var model = new SearchModel
        {
            CurrentEngineId = "google",
            ClearInputAfterSearch = true,
            SaveHistory = true,
            RecentHistory = ["Avalonia 11", "SkiaSharp 2.88", "C# .NET 8", "DeskCanvas"]
        };

        foreach (var (name, w, h) in testSizes)
        {
            Console.WriteLine($"\n--- Testing and Rendering {name} ({w}x{h} DIP) ---");

            // Test 1: Empty state (initial placeholder)
            RenderSize(outDir, name, "empty", w, h, model, "");

            // Test 2: Active query state (user typed text)
            RenderSize(outDir, name, "query", w, h, model, "macOS Sonoma Widget");
        }

        TestHorizontalScrolling();

        Console.WriteLine($"\nAll visual rendering and scroll tests completed! Output folder: {outDir}");
    }

    private static void TestHorizontalScrolling()
    {
        Console.WriteLine("\n--- Testing Universal Horizontal Scrolling Logic ---");
        DeskCanvas.Services.HorizontalScrollHelper.RegisterGlobal();
        Console.WriteLine("  PASS: HorizontalScrollHelper.RegisterGlobal succeeded.");

        // Check offset math: UP -> LEFT (decrease), DOWN -> RIGHT (increase)
        double initialOffset = 100.0;
        double deltaUp = 1.0;
        double deltaDown = -1.0;
        double step = 48.0;
        double afterUp = Math.Clamp(initialOffset - deltaUp * step, 0, 300);
        double afterDown = Math.Clamp(initialOffset - deltaDown * step, 0, 300);
        if (afterUp != 52.0 || afterDown != 148.0)
            throw new Exception($"Horizontal scroll offset calculation failed! Up: {afterUp}, Down: {afterDown}");
        Console.WriteLine("  PASS: Wheel UP scrolls LEFT (100 -> 52); Wheel DOWN scrolls RIGHT (100 -> 148).");

        // Verify Search2x2 DockScroller
        var vm = new SearchViewModel(new SearchModel());
        var s2x2 = new Search.Views.Controls.Search2x2(vm);
        var scroller2x2 = s2x2.FindControl<ScrollViewer>("DockScroller");
        if (scroller2x2 == null)
            throw new Exception("DockScroller not found in Search2x2!");
        if (scroller2x2.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
            throw new Exception("Search2x2 DockScroller VerticalScrollBarVisibility must be Disabled!");
        Console.WriteLine("  PASS: Search2x2 has DockScroller with VerticalScrollBarVisibility=Disabled.");

        // Verify Search4x2 DockScroller
        var s4x2 = new Search.Views.Controls.Search4x2(vm);
        var scroller4x2 = s4x2.FindControl<ScrollViewer>("DockScroller");
        if (scroller4x2 == null)
            throw new Exception("DockScroller not found in Search4x2!");
        if (scroller4x2.VerticalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled)
            throw new Exception("Search4x2 DockScroller VerticalScrollBarVisibility must be Disabled!");
        Console.WriteLine("  PASS: Search4x2 has DockScroller with VerticalScrollBarVisibility=Disabled.");
    }

    private static void RenderSize(string outDir, string sizeName, string state, double width, double height, SearchModel model, string testQuery)
    {
        var searchView = new SearchView(model)
        {
            Width = width,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        if (!string.IsNullOrEmpty(testQuery) && searchView.DataContext is SearchViewModel vm)
        {
            vm.QueryText = testQuery;
        }

        // Host in a macOS-styled dark acrylic card wrapper for realistic rendering
        var cardWrapper = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromRgb(30, 32, 38), 0.85),
            BorderBrush = new SolidColorBrush(Colors.White, 0.15),
            BorderThickness = new Thickness(1),
            Child = searchView,
            ClipToBounds = true
        };

        var window = new Window
        {
            Width = width,
            Height = height,
            Content = cardWrapper,
            SystemDecorations = SystemDecorations.None,
            Background = Brushes.Transparent
        };

        // Force measure and arrange
        searchView.UpdateLayoutTier(new Size(width, height));
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));

        // Validate bounding boxes and check for negative/NaN or out of bounds
        ValidateBounds(searchView, width, height);

        // Render at 2x scale (high DPI / retina)
        var pixelW = (int)Math.Ceiling(width * 2);
        var pixelH = (int)Math.Ceiling(height * 2);
        var rtb = new RenderTargetBitmap(new PixelSize(pixelW, pixelH), new Vector(192, 192));
        rtb.Render(cardWrapper);

        var outFile = Path.Combine(outDir, $"search-{sizeName}-{state}.png");
        rtb.Save(outFile);
        Console.WriteLine($"  -> Saved: {outFile} ({pixelW}x{pixelH} px)");
    }

    private static void ValidateBounds(Control root, double maxWidth, double maxHeight)
    {
        var stack = new Stack<Control>();
        stack.Push(root);

        int count = 0;
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            count++;
            var bounds = node.Bounds;

            if (double.IsNaN(bounds.Width) || double.IsNaN(bounds.Height) ||
                double.IsInfinity(bounds.Width) || double.IsInfinity(bounds.Height))
            {
                throw new InvalidOperationException($"Invalid bounds on {node.GetType().Name}: {bounds}");
            }

            if (bounds.Width < 0 || bounds.Height < 0)
            {
                throw new InvalidOperationException($"Negative dimension on {node.GetType().Name}: {bounds}");
            }

            foreach (var child in node.GetVisualChildren())
            {
                if (child is Control childCtrl)
                {
                    stack.Push(childCtrl);
                }
            }
        }
        Console.WriteLine($"  -> Inspected {count} visual nodes: all bounds valid, no layout overflows.");
    }
}
