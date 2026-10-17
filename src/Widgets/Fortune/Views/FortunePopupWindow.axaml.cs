using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Fortune.Models;
using Fortune.Services;
using Fortune.ViewModels;

namespace Fortune.Views;

public partial class FortunePopupWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly FortuneViewModel viewModel;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public FortunePopupWindow() : this(new FortuneViewModel(), null) { }

    public FortunePopupWindow(FortuneViewModel viewModel, Point? screenCenter, double? cornerRadius = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        DateBadge.Text = DateTime.Today.ToString("M月d日");
        HistoryListBox.ItemsSource = viewModel.History;

        UpdateDisplay(viewModel.CurrentFortune);

        FortuneStore.FortuneDrawn += OnFortuneDrawn;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(FortuneViewModel vm, Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        // The PanelService owns the single-instance state: cooldown, toggle-close and the
        // pinned show — including the z-band pinning the old bare Show() skipped. PerOwner
        // keys each fortune widget's panel to its own component instance (two widgets no
        // longer evict each other's panel), the same rule the weather panels follow.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "Fortune.Details",
            PanelScope.PerOwner,
            () => new FortunePopupWindow(vm, screenCenter, cornerRadius)));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        FortuneStore.FortuneDrawn -= OnFortuneDrawn;
        Closed -= OnWindowClosed;
    }

    private void OnFortuneDrawn(DailyFortuneRecord record)
    {
        UpdateDisplay(record.Fortune);
    }

    private void UpdateDisplay(FortuneItem? item)
    {
        if (item == null)
        {
            var today = FortuneStore.GetTodayFortune();
            item = today?.Fortune ?? FortuneRepository.GetRandomFortune(viewModel.Model.PoolType);
        }

        GradeStampText.Text = item.GradeText;
        PoemTitleText.Text = $"{item.PoemTitle} · {item.NumberName}";
        FortuneSubText.Text = $"运气指数: {item.Score} ★★★★★";
        PoemText.Text = item.Poem;
        ExplanationText.Text = $"【解签】{item.Explanation}";
        SuitableText.Text = $"宜: {item.Suitable}";
        TabooText.Text = $"忌: {item.Taboo}";
        LuckyColorText.Text = item.LuckyColor;
        LuckyNumberText.Text = item.LuckyNumber.ToString();
        LuckyThingText.Text = item.LuckyThing;

        if (Color.TryParse(item.LuckyColorHex, out var col))
        {
            LuckyColorDot.Fill = new SolidColorBrush(col);
        }
    }

    private async void OnCopyClicked(object? sender, RoutedEventArgs e)
    {
        var item = viewModel.CurrentFortune;
        if (item == null) return;

        string text = $"🎋【{item.GradeText}】{item.PoemTitle} ({item.NumberName})\n\n{item.Poem}\n\n解签: {item.Explanation}\n宜: {item.Suitable} | 忌: {item.Taboo}\n幸运色: {item.LuckyColor} | 幸运数: {item.LuckyNumber} | 幸运物: {item.LuckyThing}";
        if (Clipboard != null)
        {
            await Clipboard.SetTextAsync(text);
        }
    }

    private async void OnFreeDrawClicked(object? sender, RoutedEventArgs e)
    {
        FreeDrawButtonText.Text = "祈福占问中...";
        try
        {
            var item = await viewModel.FreeDrawAsync();
            UpdateDisplay(item);
        }
        finally
        {
            FreeDrawButtonText.Text = "随心再求一签 (诸事占问)";
        }
    }
}
