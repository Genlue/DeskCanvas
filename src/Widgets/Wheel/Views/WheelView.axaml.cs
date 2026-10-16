using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using Wheel.Models;
using Wheel.Services;
using Wheel.ViewModels;

namespace Wheel.Views;

public partial class WheelView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "WheelCardBackground";

    private readonly IWidgetLayoutProvider? layoutProvider;
    private readonly WheelViewModel viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);

    public WheelView() : this((IWidgetLayoutProvider?)null, null) { }

    public WheelView(IWidgetLayoutProvider layoutProvider) : this(layoutProvider, null) { }

    public WheelView(WheelModel model) : this((IWidgetLayoutProvider?)null, model) { }

    public WheelView(WheelModel model, IWidgetLayoutProvider? layoutProvider) : this(layoutProvider, model) { }

    public WheelView(IWidgetLayoutProvider? layoutProvider, WheelModel? model)
    {
        this.layoutProvider = layoutProvider;
        var resolvedModel = ResolveModel(layoutProvider, model);
        viewModel = new WheelViewModel(resolvedModel);
        DataContext = viewModel;

        InitializeComponent();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WheelViewModel.Angle))
            {
                CellWheel.RotationAngle = viewModel.Angle;
                SmallWheel.RotationAngle = viewModel.Angle;
                WideWheel.RotationAngle = viewModel.Angle;
            }
            else if (e.PropertyName == nameof(WheelViewModel.CurrentTemplate) || e.PropertyName == nameof(WheelViewModel.Model))
            {
                UpdateWheelOptions();
            }
            else if (e.PropertyName == nameof(WheelViewModel.WinningResult))
            {
                UpdateResultDisplay();
            }
            else if (e.PropertyName == nameof(WheelViewModel.IsSpinning))
            {
                UpdateSpinButtons();
            }
        };

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        UpdateWheelOptions();
    }

    private static WheelModel ResolveModel(IWidgetLayoutProvider? layoutProvider, WheelModel? model)
    {
        if (model != null) return model;
        if (layoutProvider != null)
        {
            try
            {
                var layout = layoutProvider.Get();
                if (layout.Settings is { } s && s.ValueKind == JsonValueKind.Object)
                {
                    var parsed = s.Deserialize<WheelModel>();
                    if (parsed != null) return parsed;
                }
            }
            catch { }
        }
        return new WheelModel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            ApplySize(Bounds.Size);
        }
        UpdateWheelOptions();
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
        WideLayout.IsVisible = tier is WidgetTier.Medium or WidgetTier.Large;
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
        if (size.Width >= 260 && size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        return WidgetTier.Small;
    }

    private void UpdateWheelOptions()
    {
        var options = viewModel.CurrentTemplate.Options;
        CellWheel.Options = options;
        SmallWheel.Options = options;
        WideWheel.Options = options;

        SmallTemplateTitle.Text = viewModel.CurrentTemplate.Title;
        WideTemplateTitle.Text = viewModel.CurrentTemplate.Title;
        WideOptionsList.ItemsSource = options;

        UpdateResultDisplay();
    }

    private void UpdateResultDisplay()
    {
        if (viewModel.IsSpinning)
        {
            CellResultText.Text = "转动中";
            SmallResultText.Text = "正在抉择中...";
            WideResultBanner.Text = "结果: 抉择中...";
        }
        else if (!string.IsNullOrEmpty(viewModel.WinningResult))
        {
            CellResultText.Text = viewModel.WinningResult;
            SmallResultText.Text = $"🎉 决定：{viewModel.WinningResult}";
            WideResultBanner.Text = $"🎉 结果: {viewModel.WinningResult}";
        }
        else
        {
            CellResultText.Text = "点按开始";
            SmallResultText.Text = "轻触轮盘开始决定";
            WideResultBanner.Text = "结果: 等待转动";
        }
    }

    private void UpdateSpinButtons()
    {
        UpdateResultDisplay();
        WideSpinBtnText.Text = viewModel.IsSpinning ? "转动中..." : "立即转动";
    }

    private async void OnWheelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            await viewModel.SpinAsync();
            e.Handled = true;
        }
    }

    private async void OnSpinActionClicked(object? sender, RoutedEventArgs e)
    {
        await viewModel.SpinAsync();
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (currentTier == WidgetTier.Cell)
            {
                _ = viewModel.SpinAsync();
                e.Handled = true;
            }
        }
    }

    private void OnExpandDetailsClicked(object? sender, RoutedEventArgs e)
    {
        var origin = this.PointToScreen(new Point(0, 0));
        var center = new Point(origin.X + Bounds.Width / 2, origin.Y + Bounds.Height / 2);
        var owner = TopLevel.GetTopLevel(this) as Window;

        WheelPopupWindow.ShowPopup(viewModel, center, owner);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<WheelModel>();
            if (updated != null)
            {
                viewModel.Model = updated;
                viewModel.LoadTemplates();
                UpdateWheelOptions();
            }
        }
        catch { }
    }
}
