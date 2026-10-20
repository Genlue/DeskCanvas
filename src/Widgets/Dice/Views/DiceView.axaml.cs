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

    /// <summary>
    /// 2x2 内容区窄于此值时隐藏总和 pill，给详情按钮让出空间。
    /// 只在下限附近让位：pill(~68) + 详情按钮(28) + 边距(12) ≈ 108，
    /// 标题列是 * 列会先截断，所以 110 以上保留总和——旧阈值 150 会在正常
    /// 的 140 宽卡片上把总和藏掉，看起来像功能退步。
    /// </summary>
    private const double SumPillMinWidth = 112d;

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
        if (tier != currentTier)
        {
            currentTier = tier;

            CellLayout.IsVisible = tier == WidgetTier.Cell;
            SmallLayout.IsVisible = tier == WidgetTier.Small;
            WideLayout.IsVisible = tier is WidgetTier.Medium or WidgetTier.Large;
        }

        // 每次尺寸变化都要重估 header 的降级（不能只在换档时做）：2x2 卡片被拉窄时
        // 总和 pill 必须让位，否则标题 + pill + 按钮会顶出卡片，详情按钮被裁掉。
        ApplyHeaderDegradation(size.Width);
    }

    /// <summary>
    /// Header 的空间降级：宽度不足以同时容纳标题、总和与详情按钮时，先隐藏总和 pill
    /// （信息可在二级面板看全），保证详情按钮——卡片唯一的面板入口——永远在卡片内。
    /// </summary>
    private void ApplyHeaderDegradation(double width)
    {
        bool showSum = viewModel.Model.ShowSum;
        bool roomForSum = !double.IsFinite(width) || width <= 0 || width >= SumPillMinWidth;

        SmallSumPill.IsVisible = showSum && roomForSum;
        WideSumPill.IsVisible = showSum;
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

        // 总和 pill 的显隐同时受模型开关与当前宽度约束，统一走降级逻辑。
        ApplyHeaderDegradation(Bounds.Width);

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
