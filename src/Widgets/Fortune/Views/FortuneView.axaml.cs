using System;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Runtime;
using DeskCanvas.Services;
using Fortune.Models;
using Fortune.Services;
using Fortune.ViewModels;

namespace Fortune.Views;

public partial class FortuneView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    public string? ColorfulBackgroundResourceKey => "FortuneCardBackground";

    private readonly IWidgetLayoutProvider? layoutProvider;
    private readonly FortuneViewModel viewModel;
    private WidgetTier currentTier = (WidgetTier)(-1);

    public FortuneView() : this((IWidgetLayoutProvider?)null, null) { }

    public FortuneView(IWidgetLayoutProvider layoutProvider) : this(layoutProvider, null) { }

    public FortuneView(FortuneModel model) : this((IWidgetLayoutProvider?)null, model) { }

    public FortuneView(FortuneModel model, IWidgetLayoutProvider? layoutProvider) : this(layoutProvider, model) { }

    public FortuneView(FortuneModel model, IWidgetLayoutProvider? layoutProvider, IWidgetSession? session)
        : this(layoutProvider, model, session) { }

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (declared as a trailing optional constructor parameter). The view forwards the
    /// session and every activity push to the view model, which gates its daily-rollover
    /// watch on the snapshot's RenderActive. Null keeps the legacy always-on behaviour
    /// (preview, legacy layout entries without identity).
    /// </summary>
    public FortuneView(IWidgetLayoutProvider? layoutProvider, FortuneModel? model, IWidgetSession? session = null)
    {
        this.layoutProvider = layoutProvider;
        var resolvedModel = ResolveModel(layoutProvider, model);
        viewModel = new FortuneViewModel(resolvedModel, session);
        DataContext = viewModel;

        InitializeComponent();

        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FortuneViewModel.HasDrawnToday) or nameof(FortuneViewModel.CurrentFortune) or nameof(FortuneViewModel.Model))
            {
                UpdateVisuals();
            }
        };

        FortuneStore.FortuneDrawn += OnFortuneDrawn;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;

        UpdateVisuals();
    }

    private static FortuneModel ResolveModel(IWidgetLayoutProvider? layoutProvider, FortuneModel? model)
    {
        if (model != null) return model;
        if (layoutProvider != null)
        {
            try
            {
                var layout = layoutProvider.Get();
                if (layout.Settings is { } s && s.ValueKind == JsonValueKind.Object)
                {
                    var parsed = s.Deserialize<FortuneModel>();
                    if (parsed != null) return parsed;
                }
            }
            catch { }
        }
        return new FortuneModel();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Start();
        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            ApplySize(Bounds.Size);
        }
        UpdateVisuals();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        viewModel.Stop();
        FortuneStore.FortuneDrawn -= OnFortuneDrawn;
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

    private void UpdateVisuals()
    {
        bool drawn = viewModel.HasDrawnToday && viewModel.CurrentFortune != null;
        var item = viewModel.CurrentFortune;

        // 1x1 Cell
        CellUndrawnPanel.IsVisible = !drawn;
        CellDrawnPanel.IsVisible = drawn;
        if (drawn && item != null)
        {
            CellGradeText.Text = item.GradeText;
            CellScoreText.Text = $"运气 {item.Score}";
        }

        // 2x2 Small
        SmallUndrawnPanel.IsVisible = !drawn;
        SmallDrawnPanel.IsVisible = drawn;
        SmallDateText.Text = DateTime.Today.ToString("M月d日");

        if (drawn && item != null)
        {
            SmallGradeText.Text = item.GradeText;
            SmallTitleText.Text = item.PoemTitle;

            var poemParts = item.Poem.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            SmallPoemText.Text = poemParts.Length >= 2
                ? $"{poemParts[0]}\n{poemParts[1]}"
                : item.Poem;

            var suitParts = item.Suitable.Split(new[] { '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            var tabooParts = item.Taboo.Split(new[] { '、', ',', '，' }, StringSplitOptions.RemoveEmptyEntries);
            string s = suitParts.Length > 0 ? suitParts[0] : item.Suitable;
            string t = tabooParts.Length > 0 ? tabooParts[0] : item.Taboo;
            SmallAdviceText.Text = $"宜 {s} · 忌 {t}";
            SmallBottomTip.Text = "今日已定签 · 点击查看详细解签";
        }
        else
        {
            SmallBottomTip.Text = "点按求取今日运势灵签";
        }

        // 4x2 Wide
        if (item != null)
        {
            WideGradeText.Text = item.GradeText;
            WidePoemText.Text = item.Poem;
            WideSuitableText.Text = $"宜: {item.Suitable}";
            WideTabooText.Text = $"忌: {item.Taboo}";
            WideExplanationText.Text = item.Explanation;
            WideColorText.Text = item.LuckyColor;
            WideThingText.Text = item.LuckyThing;

            if (Color.TryParse(item.LuckyColorHex, out var col))
            {
                WideColorDot.Fill = new SolidColorBrush(col);
            }
        }
    }

    private void OnFortuneDrawn(DailyFortuneRecord record)
    {
        viewModel.CurrentFortune = record.Fortune;
        viewModel.HasDrawnToday = true;
        UpdateVisuals();
    }

    private async void OnDrawActionClicked(object? sender, RoutedEventArgs e)
    {
        SmallDrawButtonText.Text = "祈福摇签中...";
        try
        {
            await viewModel.DrawTodayAsync();
        }
        finally
        {
            SmallDrawButtonText.Text = "求取今日灵签";
        }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (currentTier == WidgetTier.Cell)
            {
                if (!viewModel.HasDrawnToday)
                {
                    _ = viewModel.DrawTodayAsync();
                }
                else
                {
                    OnExpandDetailsClicked(sender, e);
                }
                e.Handled = true;
            }
        }
    }

    private void OnExpandDetailsClicked(object? sender, RoutedEventArgs e)
    {
        var origin = this.PointToScreen(new Point(0, 0));
        var center = new Point(origin.X + Bounds.Width / 2, origin.Y + Bounds.Height / 2);
        var owner = TopLevel.GetTopLevel(this) as Window;

        FortunePopupWindow.ShowPopup(viewModel, center, owner);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<FortuneModel>();
            if (updated != null)
            {
                viewModel.Model = updated;
                UpdateVisuals();
            }
        }
        catch { }
    }
}
