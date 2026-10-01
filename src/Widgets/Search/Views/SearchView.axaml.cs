using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Search.Models;
using Search.ViewModels;
using Search.Views.Controls;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;

namespace Search.Views;

public enum SearchTier
{
    Cell1x1,
    Banner4x1,
    Small2x2,
    Medium4x2,
    Large4x4
}

public partial class SearchView : UserControl, IWidgetSelfRefreshing
{
    private readonly SearchViewModel viewModel;
    private SearchTier currentTier = (SearchTier)(-1);

    public SearchView() : this(new SearchModel()) { }

    public SearchView(SearchModel model)
    {
        this.viewModel = new SearchViewModel(model);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    internal SearchView(SearchViewModel vm)
    {
        this.viewModel = vm;
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        UpdateLayoutTier(Bounds.Size);
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        SizeChanged -= OnSizeChanged;
        Unloaded -= OnUnloaded;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateLayoutTier(e.NewSize);
    }

    public void UpdateLayoutTier(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        var tier = ResolveTier(size);
        if (tier == currentTier && TierContainer.Content != null)
            return;

        currentTier = tier;

        TierContainer.Content = tier switch
        {
            SearchTier.Cell1x1 => new Search1x1(viewModel),
            SearchTier.Banner4x1 => new Search4x1(viewModel),
            SearchTier.Small2x2 => new Search2x2(viewModel),
            SearchTier.Large4x4 => new Search4x4(viewModel),
            _ => new Search4x2(viewModel)
        };
    }

    private SearchTier ResolveTier(Size size)
    {
        var span = FindHostSpan();
        if (span != null)
        {
            var (cols, rows) = span.Value;
            if (cols <= 1 && rows <= 1) return SearchTier.Cell1x1;
            if (rows == 1 && cols >= 2) return SearchTier.Banner4x1;
            if (cols <= 2 && rows <= 2) return SearchTier.Small2x2;
            if (rows >= 3 && cols >= 3) return SearchTier.Large4x4;
            return SearchTier.Medium4x2;
        }

        // Fallback based on pixel sizes
        if (size.Width <= 110 && size.Height <= 110)
            return SearchTier.Cell1x1;

        if (size.Height <= 100 && size.Width >= 180)
            return SearchTier.Banner4x1;

        if (size.Width <= 200 && size.Height <= 200)
            return SearchTier.Small2x2;

        if (size.Width > 220 && size.Height > 220)
            return SearchTier.Large4x4;

        return SearchTier.Medium4x2;
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
        if (layout.Settings is { } settings && settings.ValueKind == JsonValueKind.Object)
        {
            try
            {
                var newModel = settings.Deserialize<SearchModel>();
                if (newModel != null)
                {
                    viewModel.UpdateModel(newModel);
                }
            }
            catch
            {
                // Ignored
            }
        }
    }

    private static SearchModel? ReadModel(WidgetLayout? layout)
    {
        if (layout?.Settings is not { } settings) return null;
        if (settings.ValueKind != JsonValueKind.Object) return null;
        try
        {
            return settings.Deserialize<SearchModel>();
        }
        catch
        {
            return null;
        }
    }
}
