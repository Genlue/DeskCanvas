using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using Weather.Models;
using Weather.ViewModels;

namespace Weather.Views;

public partial class FourDayForecast : UserControl, IFixedSizeWidget, IWidgetSelfRefreshing, IDisposable
{
    private ForecastModel model;
    private ForecastViewModel? viewModel;

    public static readonly IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpansOf = [(2, 2)];
    public static readonly IReadOnlyList<(int Columns, int Rows)> PresetSpansOf = [(2, 2), (3, 3), (4, 4)];

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => SnapSpanOf(columns, rows);

    public static bool IsAllowedSpanOf(int columns, int rows) =>
        columns >= 2 && rows >= 2 && columns == rows;

    public static (int Columns, int Rows) SnapSpanOf(int columns, int rows)
    {
        int k = Math.Max(2, (int)Math.Round((columns + rows) / 2.0, MidpointRounding.AwayFromZero));
        return (k, k);
    }

    public FourDayForecast() : this(new ForecastModel("杭州", 30.2741, 120.1551, "celsius")) {}

    public FourDayForecast(ForecastModel model)
    {
        this.model = model;
        viewModel = new ForecastViewModel(model);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        InitializeComponent();
    }

    public void OpenPopup(object? sender, RoutedEventArgs e)
    {
        OpenPopupWindow();
    }

    public void OpenPopupWindow()
    {
        if (viewModel == null) return;
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        WeatherPopupWindow.ShowPopup(viewModel, screenCenter, owner, SpawnCornerRadius);
    }

    private double? SpawnCornerRadius
    {
        get
        {
            var r = (VisualRoot as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (viewModel != null) return;

        viewModel = new ForecastViewModel(model);
        DataContext = viewModel;
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel?.Dispose();
        viewModel = null;
    }

    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<ForecastModel>() ?? new ForecastModel("杭州", 30.2741, 120.1551, "celsius");
        model = newModel;
        if (viewModel != null)
        {
            viewModel.Dispose();
            viewModel = new ForecastViewModel(model);
            DataContext = viewModel;
        }
    }

    public void Dispose()
    {
        viewModel?.Dispose();
        viewModel = null;
        GC.SuppressFinalize(this);
    }
}
