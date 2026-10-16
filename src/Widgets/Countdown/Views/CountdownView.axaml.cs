using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Countdown.Models;
using Countdown.Services;
using Countdown.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using DeskCanvas.UI;

namespace Countdown.Views;

public partial class CountdownView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "CountdownCardBackground";

    private readonly IWidgetLayoutProvider? layoutProvider;
    private readonly CountdownStatusPresenter statusPresenter = new();
    private CountdownModel model;
    private CountdownViewModel? viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);

    public CountdownView() : this(new CountdownModel(), null) { }

    public CountdownView(IWidgetLayoutProvider layoutProvider) : this(new CountdownModel(), layoutProvider) { }

    public CountdownView(CountdownModel model) : this(model, null) { }

    public CountdownView(CountdownModel model, IWidgetLayoutProvider? layoutProvider)
    {
        this.layoutProvider = layoutProvider;
        this.model = model;
        viewModel = new CountdownViewModel(model);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (viewModel is { IsDisposed: true } or null)
        {
            viewModel = new CountdownViewModel(model);
            DataContext = viewModel;
        }

        viewModel.Start();
        statusPresenter.ShowCountdown("running");

        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            ApplySize(Bounds.Size);
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel?.Dispose();
        viewModel = null;
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
        {
            ApplySize(e.NewSize);
        }
    }

    private void ApplySize(Size size)
    {
        var tier = ResolveTier(size);
        if (tier == currentTier) return;
        currentTier = tier;

        CellLayout.IsVisible = tier == WidgetTier.Cell;
        SmallLayout.IsVisible = tier == WidgetTier.Small;
        WideLayout.IsVisible = tier == WidgetTier.Medium;
        LargeLayout.IsVisible = tier == WidgetTier.Large;
    }

    private WidgetTier ResolveTier(Size size)
    {
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier != WidgetTier.Other) return spanTier;
        }
        catch { }

        if (size.Width <= SizeTiers.CellSide && size.Height <= SizeTiers.CellSide)
            return WidgetTier.Cell;
        if (size.Width >= 280 && size.Height >= 280)
            return WidgetTier.Large;
        if (size.Width >= 260 && size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        return WidgetTier.Small;
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<CountdownModel>();
            if (updated != null)
            {
                model = updated;
                viewModel?.UpdateSettings(updated);
            }
        }
        catch { }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            var origin = this.PointToScreen(new Point(0, 0));
            var center = new Point(origin.X + Bounds.Width / 2, origin.Y + Bounds.Height / 2);
            var owner = TopLevel.GetTopLevel(this) as Window;

            CountdownPopupWindow.ShowPopup(center, owner);
            e.Handled = true;
        }
    }
}
