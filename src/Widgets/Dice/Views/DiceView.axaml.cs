using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using Dice.Locales;
using Dice.Models;
using Dice.ViewModels;
using Dice.Views.Controls;

namespace Dice.Views;

public partial class DiceView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "DiceCardBackground";

    private readonly IWidgetLayoutProvider? layoutProvider;
    private readonly DiceViewModel viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);

    public DiceView() : this((IWidgetLayoutProvider?)null, null) { }

    public DiceView(IWidgetLayoutProvider layoutProvider) : this(layoutProvider, null) { }

    public DiceView(DiceModel model) : this((IWidgetLayoutProvider?)null, model) { }

    public DiceView(DiceModel model, IWidgetLayoutProvider? layoutProvider) : this(layoutProvider, model) { }

    public DiceView(IWidgetLayoutProvider? layoutProvider, DiceModel? model)
    {
        this.layoutProvider = layoutProvider;
        var resolvedModel = ResolveModel(layoutProvider, model);
        viewModel = new DiceViewModel(resolvedModel);
        DataContext = viewModel;

        InitializeComponent();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiceViewModel.CurrentDice) ||
                e.PropertyName == nameof(DiceViewModel.Sum) ||
                e.PropertyName == nameof(DiceViewModel.Model))
            {
                UpdateDiceVisuals();
            }
        };

        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;

        UpdateDiceVisuals();
    }

    private static DiceModel ResolveModel(IWidgetLayoutProvider? layoutProvider, DiceModel? model)
    {
        if (model != null) return model;
        if (layoutProvider != null)
        {
            try
            {
                var layout = layoutProvider.Get();
                if (layout.Settings is { } s && s.ValueKind == JsonValueKind.Object)
                {
                    var parsed = s.Deserialize<DiceModel>();
                    if (parsed != null) return parsed;
                }
            }
            catch { }
        }
        return new DiceModel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            ApplySize(Bounds.Size);
        }
        UpdateDiceVisuals();
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

    private void UpdateDiceVisuals()
    {
        // 1. Update 1x1 Cell
        if (viewModel.CurrentDice.Count > 0)
        {
            CellDiceFace.Value = viewModel.CurrentDice[0];
            CellDiceFace.DiceType = viewModel.Model.Type;
            CellDiceFace.FaceStyle = viewModel.Model.FaceStyle;
        }

        // 2. Update 2x2 Small & 4x2 Wide trays
        SmallDiceTray.Children.Clear();
        WideDiceTray.Children.Clear();

        foreach (var val in viewModel.CurrentDice)
        {
            // 尺寸由 DiceTrayPanel 按可用空间统一分配（FaceSize），两个布局各自持有独立实例。
            SmallDiceTray.Children.Add(CreateFace(val));
            WideDiceTray.Children.Add(CreateFace(val));
        }

        SmallSumPill.IsVisible = viewModel.Model.ShowSum;
        WideSumPill.IsVisible = viewModel.Model.ShowSum;

        string sumText = $"{Locale.Dice_Sum}: {viewModel.Sum}"
            + (viewModel.IsCriticalMax ? $" · {Locale.Dice_NaturalMax}" : "");
        SmallSumText.Text = sumText;
        WideSumText.Text = sumText;
    }

    private DiceFaceControl CreateFace(int value) => new()
    {
        Value = value,
        DiceType = viewModel.Model.Type,
        FaceStyle = viewModel.Model.FaceStyle
    };

    private async void OnRollActionClicked(object? sender, RoutedEventArgs e)
    {
        await viewModel.RollAsync();
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // If in 1x1 cell mode, tap rolls directly
            if (currentTier == WidgetTier.Cell)
            {
                _ = viewModel.RollAsync();
                e.Handled = true;
            }
        }
    }

    private void OnExpandDetailsClicked(object? sender, RoutedEventArgs e)
    {
        var origin = this.PointToScreen(new Point(0, 0));
        var center = new Point(origin.X + Bounds.Width / 2, origin.Y + Bounds.Height / 2);
        var owner = TopLevel.GetTopLevel(this) as Window;

        DicePopupWindow.ShowPopup(viewModel, center, owner);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<DiceModel>();
            if (updated != null)
            {
                viewModel.Model = updated;
                UpdateDiceVisuals();
            }
        }
        catch { }
    }
}
