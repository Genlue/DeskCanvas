using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Dice.Models;
using Dice.Services;
using Dice.ViewModels;

namespace Dice.Views;

public class DiceHistoryItemViewModel
{
    public DateTime Timestamp { get; set; }
    public string ValuesSummary { get; set; } = string.Empty;
    public string SumSummary { get; set; } = string.Empty;

    public DiceHistoryItemViewModel(DiceRollRecord record)
    {
        Timestamp = record.Timestamp;
        ValuesSummary = $"[{string.Join(", ", record.Values)}] ({record.Type})";
        SumSummary = $"总和: {record.Sum}" + (record.IsCriticalMax ? " ★满点!" : "");
    }
}

public partial class DicePopupWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly DiceViewModel viewModel;
    private readonly ObservableCollection<DiceHistoryItemViewModel> historyItems = [];
    private readonly Button[] countButtons;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public DicePopupWindow() : this(new DiceViewModel(), null) { }

    public DicePopupWindow(DiceViewModel viewModel, Point? screenCenter, double? cornerRadius = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        countButtons = [Count1Btn, Count2Btn, Count3Btn, Count4Btn, Count5Btn, Count6Btn];
        HistoryListBox.ItemsSource = historyItems;

        UpdateCountButtonSelection(viewModel.Model.DiceCount);
        SelectTypeCombo(viewModel.Model.Type);
        UpdateLatestResult();
        LoadHistory();

        DiceStore.RollCompleted += OnStoreRollCompleted;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(DiceViewModel vm, Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        // Explicit process-singleton (the historic dice behaviour), now declared through the
        // shared service: cooldown, toggle-close and the pinned show are handled there —
        // including the z-band pinning the old bare Show() skipped.
        PanelService.Open(new PanelRequest(
            default,
            owner,
            "dice",
            PanelScope.ProcessSingleton,
            () => new DicePopupWindow(vm, screenCenter, cornerRadius)));
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        DiceStore.RollCompleted -= OnStoreRollCompleted;
        Closed -= OnWindowClosed;
    }

    private void OnStoreRollCompleted(DiceRollRecord record)
    {
        historyItems.Insert(0, new DiceHistoryItemViewModel(record));
        UpdateLatestResult();
    }

    private void UpdateLatestResult()
    {
        LatestValuesText.Text = $"[ {string.Join(", ", viewModel.CurrentDice)} ] ({viewModel.Model.Type})";
        LatestSumText.Text = $"总和: {viewModel.Sum}" + (viewModel.IsCriticalMax ? " ★满点!" : "");
    }

    private void LoadHistory()
    {
        historyItems.Clear();
        var data = DiceStore.GetData();
        foreach (var r in data.History)
        {
            historyItems.Add(new DiceHistoryItemViewModel(r));
        }
    }

    private void OnCountSelect(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int count))
        {
            viewModel.Model = viewModel.Model with { DiceCount = count };
            UpdateCountButtonSelection(count);
        }
    }

    private void UpdateCountButtonSelection(int selected)
    {
        for (int i = 0; i < countButtons.Length; i++)
        {
            if (i + 1 == selected)
            {
                if (!countButtons[i].Classes.Contains("active"))
                    countButtons[i].Classes.Add("active");
            }
            else
            {
                countButtons[i].Classes.Remove("active");
            }
        }
    }

    private void SelectTypeCombo(DiceType type)
    {
        int tag = (int)type;
        for (int i = 0; i < TypeComboBox.Items.Count; i++)
        {
            if (TypeComboBox.Items[i] is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int itemTag) && itemTag == tag)
            {
                TypeComboBox.SelectedIndex = i;
                break;
            }
        }
    }

    private void OnTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (TypeComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out int tag))
        {
            viewModel.Model = viewModel.Model with { Type = (DiceType)tag };
        }
    }

    private async void OnRollClicked(object? sender, RoutedEventArgs e)
    {
        RollActionBtn.IsEnabled = false;
        RollButtonText.Text = "投掷中...";
        try
        {
            await viewModel.RollAsync();
            UpdateLatestResult();
        }
        finally
        {
            RollActionBtn.IsEnabled = true;
            RollButtonText.Text = "立即投掷 (Roll)";
        }
    }

    private void OnClearHistoryClicked(object? sender, RoutedEventArgs e)
    {
        DiceStore.ClearHistory();
        historyItems.Clear();
    }
}
