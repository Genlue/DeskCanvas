using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Clock.Models;
using Clock.ViewModels;

namespace Clock.Views;

public partial class World : UserControl
{
    private readonly WorldClockViewModel viewModel;

    public World() : this(new WorldClockModel(new List<string?>())) {}

    public World(WorldClockModel worldClockModel)
    {
        viewModel = new WorldClockViewModel(worldClockModel);
        DataContext = viewModel;
        InitializeComponent();

        Item0.DataContext = viewModel.First;
        Item1.DataContext = viewModel.Second;
        Item2.DataContext = viewModel.Third;
        Item3.DataContext = viewModel.Fourth;

        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += OnUnloaded;
        ApplyLayout();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var (columns, rows, showBottom, showCenterOverlay, showNumbers) = ResolveLayout();

        Board.ColumnDefinitions = new ColumnDefinitions(columns switch
        {
            4 => "*,*,*,*",
            2 => "*,*",
            _ => "*"
        });
        Board.RowDefinitions = new RowDefinitions(rows switch
        {
            4 => "*,*,*,*",
            2 => "*,*",
            _ => "*"
        });

        (Board.Width, Board.Height) = ResolveBoardSize(columns, rows, showBottom);

        var items = new[] { Item0, Item1, Item2, Item3 };
        var clocks = new[] { Clock0, Clock1, Clock2, Clock3 };
        var cities = new[] { City0, City1, City2, City3 };
        var digitals = new[] { Digital0, Digital1, Digital2, Digital3 };

        var index = 0;
        for (var row = 0; row < rows && index < items.Length; row++)
        {
            for (var col = 0; col < columns && index < items.Length; col++)
            {
                items[index].IsVisible = true;
                // 4x2: symmetric top/bottom margin so the dial top and the digital
                // text bottom keep the same distance from the card edges.
                items[index].Margin = showBottom ? new Thickness(3, 8) : new Thickness(4);
                Grid.SetColumn(items[index], col);
                Grid.SetRow(items[index], row);
                cities[index].IsVisible = showBottom;
                digitals[index].IsVisible = showBottom;
                clocks[index].ShowNumbers = showNumbers;
                clocks[index].ShowCenterOverlay = showCenterOverlay;
                index++;
            }
        }
        for (; index < items.Length; index++) items[index].IsVisible = false;
    }

    /// <summary>
    /// Choose the world-clock arrangement from the hosting widget's grid span:
    /// 2x2 and 4x4 -> 2x2 dials, each with a centered two-line readout (custom
    /// city name + that time zone's digital time); 4x2 -> one row of four dials
    /// with city name + digital time underneath; everything else adapts by
    /// span/aspect ratio.
    /// </summary>
    private (int Columns, int Rows, bool ShowBottom, bool ShowCenterOverlay, bool ShowNumbers) ResolveLayout()
    {
        var span = FindHostSpan();
        if (span is { } s)
        {
            if (s.Columns == 1 && s.Rows == 1) return (1, 1, false, false, false);
            if (s.Columns >= 3 && s.Rows >= 3) return (2, 2, false, true, true);
            if (s.Columns >= 3 && s.Rows == 2) return (4, 1, true, false, true);
            if (s.Columns >= 3 && s.Rows == 1) return (4, 1, false, false, true);
            if (s.Columns == 1 && s.Rows >= 3) return (1, 4, true, false, true);
            if (s.Columns == 1 && s.Rows == 2) return (2, 2, false, true, false);
            if (s.Columns == 2 && s.Rows == 1) return (4, 1, false, false, false);
            if (s.Columns == 2 && s.Rows == 2) return (2, 2, false, true, false);
        }

        // No widget host (gallery preview): coarse aspect-ratio fallback.
        var size = Bounds.Size;
        if (size.Width > 0 && size.Width > size.Height * 1.8)
            return (4, 1, size.Height >= 120, false, true);
        if (size.Height > 0 && size.Height > size.Width * 1.8)
            return (1, 4, size.Width >= 100, false, true);
        return (2, 2, false, true, false);
    }

    private static (double Width, double Height) ResolveBoardSize(int columns, int rows, bool showBottom)
    {
        if (columns == 4) return showBottom ? (320, 160) : (320, 90);
        if (rows == 4) return showBottom ? (120, 360) : (90, 360);
        if (columns == 1 && rows == 1) return (200, 200);
        // 2x2 and 4x4 share the same board; the Viewbox scales 4x4 up proportionally.
        return showBottom ? (220, 220) : (200, 200);
    }

    private (int Columns, int Rows)? FindHostSpan()
    {
        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
            if (node is DeskCanvas.Views.Widget widget)
                return widget.CurrentSpan;
        return null;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Stop();
    }
}
