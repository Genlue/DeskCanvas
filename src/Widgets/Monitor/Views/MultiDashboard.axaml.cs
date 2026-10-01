using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Monitor.Models;
using Monitor.ViewModels;
using DeskCanvas.Services;

namespace Monitor.Views;

public partial class MultiDashboard : UserControl
{
    private readonly MultiDashboardModel model;
    private MultiDashboardViewModel? viewModel;

    public MultiDashboard() :
        this(new MultiDashboardModel(MultiDashboardModel.DefaultMetrics)) {}

    public MultiDashboard(MultiDashboardModel model)
    {
        this.model = model;
        var vm = new MultiDashboardViewModel(model);
        viewModel = vm;
        DataContext = vm;
        InitializeComponent();

        BindItems(vm);

        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ApplyLayout();
    }

    /// <summary>
    /// The view can be detached and re-added later (the settings window caches pages and the
    /// Gallery keeps a live preview control), so everything <see cref="OnUnloaded"/> released
    /// is restarted here — on a *new* view model, because the released one was disposed.
    /// </summary>
    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel != null) return;
        var vm = new MultiDashboardViewModel(model);
        viewModel = vm;
        DataContext = vm;
        BindItems(vm);
        ApplyLayout();
    }

    /// <summary>
    /// Release the view model (its one-second timer subscription, which otherwise keeps the
    /// WMI polling alive forever) without detaching this handler: the control stays usable
    /// and is unloaded again on every later removal from the visual tree.
    /// </summary>
    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        viewModel?.Dispose();
        viewModel = null;
    }

    /// <summary>Bind the four fixed item slots to the rings of the given view model.</summary>
    private void BindItems(MultiDashboardViewModel viewModel)
    {
        Item0.DataContext = viewModel.Items[0];
        Item1.DataContext = viewModel.Items[1];
        Item2.DataContext = viewModel.Items[2];
        Item3.DataContext = viewModel.Items[3];

        Ring0.ApplyMetricType(viewModel.Items[0].Type);
        Ring1.ApplyMetricType(viewModel.Items[1].Type);
        Ring2.ApplyMetricType(viewModel.Items[2].Type);
        Ring3.ApplyMetricType(viewModel.Items[3].Type);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var items = new[] { Item0, Item1, Item2, Item3 };
        var values = new[] { Value0, Value1, Value2, Value3 };
        var (columns, rows, showNumbers) = ResolveLayout();

        Board.ColumnDefinitions = new ColumnDefinitions(
            columns == 1 ? "*" : columns == 4 ? "*,*,*,*" : "*,*");
        Board.RowDefinitions = new RowDefinitions(rows == 1 ? "*" : rows == 4 ? "*,*,*,*" : "*,*");

        (Board.Width, Board.Height) = columns == 4
            ? showNumbers ? (360d, 150d) : (360d, 90d)
            : rows == 4
                ? showNumbers ? (120d, 360d) : (90d, 360d)
                : showNumbers ? (220d, 220d) : (200d, 200d);

        var index = 0;
        for (var row = 0; row < rows && index < items.Length; row++)
        {
            for (var col = 0; col < columns && index < items.Length; col++)
            {
                var item = items[index];
                Grid.SetColumn(item, col);
                Grid.SetRow(item, row);
                values[index].IsVisible = showNumbers;
                index++;
            }
        }
    }

    /// <summary>
    /// Choose the dashboard arrangement from the hosting widget's grid span:
    /// 2×2 → 2×2 rings (no numbers); 4×2 → one row of four rings + aligned
    /// numbers below; 4×4 → 2×2 rings + numbers below; everything else adapts
    /// by aspect (wide 1-row → four rings, tall → four rings stacked).
    /// </summary>
    private (int Columns, int Rows, bool ShowNumbers) ResolveLayout()
    {
        var span = FindHostSpan();
        if (span is { } s)
        {
            if (s.Columns >= 3 && s.Rows >= 3) return (2, 2, true);
            if (s.Columns >= 3 && s.Rows == 2) return (4, 1, true);
            if (s.Columns >= 3 && s.Rows == 1) return (4, 1, false);
            if (s.Columns == 1 && s.Rows >= 3) return (1, 4, true);
            if (s.Columns == 1 && s.Rows == 2) return (2, 2, false);
            if (s.Columns == 2 && s.Rows == 1) return (4, 1, false);
            return (2, 2, false);
        }

        // No widget host (gallery preview): coarse aspect-ratio fallback.
        var size = Bounds.Size;
        if (size.Width > 0 && size.Width > size.Height * 1.8)
            return (4, 1, size.Height >= 100);
        if (size.Height > 0 && size.Height > size.Width * 1.8)
            return (1, 4, false);
        return (2, 2, size.Width > 180 && size.Height > 180);
    }

    private (int Columns, int Rows)? FindHostSpan()
    {
        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
            if (node is DeskCanvas.Views.Widget widget)
                return widget.CurrentSpan;
        return null;
    }
}
