using System;
using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using Dice.Models;
using Dice.Services;
using Dice.ViewModels;
using Dice.Views.Controls;

namespace Dice.Views;

public partial class DiceView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints
{
    public string? ColorfulBackgroundResourceKey => "DiceCardBackground";

    private readonly IWidgetLayoutProvider? layoutProvider;
    private readonly DiceViewModel viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);
    private readonly ObservableCollection<DiceHistoryItemViewModel> recentHistory = [];

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

        WideHistoryList.ItemsSource = recentHistory;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DiceViewModel.CurrentDice) ||
                e.PropertyName == nameof(DiceViewModel.Sum) ||
                e.PropertyName == nameof(DiceViewModel.Model))
            {
                UpdateDiceVisuals();
            }
        };

        DiceStore.RollCompleted += OnRollCompleted;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        UpdateDiceVisuals();
        LoadHistory();
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

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        DiceStore.RollCompleted -= OnRollCompleted;
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

        // 2. Update 2x2 Small & 4x2 Wide containers
        SmallDiceContainer.Items.Clear();
        WideDiceContainer.Items.Clear();

        foreach (var val in viewModel.CurrentDice)
        {
            var face1 = new DiceFaceControl
            {
                Value = val,
                DiceType = viewModel.Model.Type,
                FaceStyle = viewModel.Model.FaceStyle,
                Width = 50,
                Height = 50,
                Margin = new Thickness(4)
            };
            SmallDiceContainer.Items.Add(face1);

            var face2 = new DiceFaceControl
            {
                Value = val,
                DiceType = viewModel.Model.Type,
                FaceStyle = viewModel.Model.FaceStyle,
                Width = 46,
                Height = 46,
                Margin = new Thickness(3)
            };
            WideDiceContainer.Items.Add(face2);
        }

        SumPill.IsVisible = viewModel.Model.ShowSum;
        WideSumText.IsVisible = viewModel.Model.ShowSum;

        SmallSumText.Text = $"总和: {viewModel.Sum}" + (viewModel.IsCriticalMax ? " ★满点!" : "");
        WideSumText.Text = $"总和: {viewModel.Sum}" + (viewModel.IsCriticalMax ? " ★满点!" : "");
    }

    private void LoadHistory()
    {
        recentHistory.Clear();
        var data = DiceStore.GetData();
        foreach (var r in data.History)
        {
            recentHistory.Add(new DiceHistoryItemViewModel(r));
            if (recentHistory.Count >= 6) break;
        }
    }

    private void OnRollCompleted(DiceRollRecord record)
    {
        recentHistory.Insert(0, new DiceHistoryItemViewModel(record));
        if (recentHistory.Count > 6) recentHistory.RemoveAt(recentHistory.Count - 1);
    }

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
