using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Batteries.Models;
using Batteries.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;

namespace Batteries.Views;

public partial class BatteriesView : UserControl, IWidgetSelfRefreshing, IWidgetSuspendable
{
    private readonly IWidgetLayoutProvider? layoutProvider;
    private BatteriesViewModel? viewModel;
    private BatteriesModel model;

    public BatteriesView() : this(new BatteriesModel(), null) { }

    public BatteriesView(IWidgetLayoutProvider layoutProvider) : this(new BatteriesModel(), layoutProvider) { }

    public BatteriesView(BatteriesModel model) : this(model, null) { }

    public BatteriesView(BatteriesModel model, IWidgetLayoutProvider? layoutProvider)
    {
        this.model = model;
        this.layoutProvider = layoutProvider;
        viewModel = new BatteriesViewModel(model);
        DataContext = viewModel;

        InitializeComponent();
        BindItems(viewModel);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        ActualThemeVariantChanged += (_, _) => viewModel?.RefreshStatusBrushes();
        ApplyLayout();
    }

    private void BindItems(BatteriesViewModel vm)
    {
        Item0.DataContext = vm.Items[0];
        Item1.DataContext = vm.Items[1];
        Item2.DataContext = vm.Items[2];
        Item3.DataContext = vm.Items[3];
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (viewModel == null)
        {
            viewModel = new BatteriesViewModel(model);
            DataContext = viewModel;
            BindItems(viewModel);
        }
        ApplyLayout();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        viewModel?.Dispose();
        viewModel = null;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyLayout();

    private void ApplyLayout()
    {
        var items = new[] { Item0, Item1, Item2, Item3 };
        var valueContainers = new[] { ValueContainer0, ValueContainer1, ValueContainer2, ValueContainer3 };
        var (columns, rows, showNumbers) = ResolveLayout();
        var show = showNumbers && model.ShowPercentage;

        Board.ColumnDefinitions = new ColumnDefinitions(
            columns == 1 ? "*" : columns == 4 ? "*,*,*,*" : "*,*");
        Board.RowDefinitions = new RowDefinitions(rows == 1 ? "*" : rows == 4 ? "*,*,*,*" : "*,*");

        (Board.Width, Board.Height) = columns == 4
            ? show ? (360d, 150d) : (360d, 90d)
            : rows == 4
                ? show ? (120d, 360d) : (90d, 360d)
                : show ? (220d, 220d) : (200d, 200d);

        var index = 0;
        for (var row = 0; row < rows && index < items.Length; row++)
        {
            for (var col = 0; col < columns && index < items.Length; col++)
            {
                var item = items[index];
                Grid.SetColumn(item, col);
                Grid.SetRow(item, row);
                valueContainers[index].IsVisible = show;
                index++;
            }
        }
    }

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

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<BatteriesModel>();
            if (updated != null)
            {
                model = updated;
                viewModel?.UpdateModel(updated);
            }
        }
        catch { }
    }

    /// <summary>Stops the 3 s power/peripheral poll while the desktop is covered by a fullscreen
    /// or maximized window (see <c>IWidgetSuspendable</c>).</summary>
    public void Suspend() => viewModel?.Suspend();

    /// <summary>Restarts the poll after <see cref="Suspend"/>, refreshing once immediately.</summary>
    public void Resume() => viewModel?.Resume();
}
