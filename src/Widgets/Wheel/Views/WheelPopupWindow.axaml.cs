using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Views;
using Wheel.Models;
using Wheel.Services;
using Wheel.ViewModels;

namespace Wheel.Views;

public partial class WheelPopupWindow : SecondaryPanelWindow
{
    private static WheelPopupWindow? activeInstance;
    private readonly Point? spawnScreenCenter;
    private readonly WheelViewModel viewModel;
    private readonly ObservableCollection<WheelOption> currentOptions = [];
    private readonly ObservableCollection<WheelDecisionRecord> historyItems = [];
    private bool isUpdatingCombo;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public WheelPopupWindow() : this(new WheelViewModel(), null) { }

    public WheelPopupWindow(WheelViewModel viewModel, Point? screenCenter, double? cornerRadius = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        OptionsItemsControl.ItemsSource = currentOptions;
        HistoryListBox.ItemsSource = historyItems;

        PopulateTemplates();
        LoadHistory();

        WheelStore.DecisionRecorded += OnDecisionRecorded;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(WheelViewModel vm, Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        if (PanelCoolingDown<WheelPopupWindow>()) return;
        if (TryCloseActivePanel<WheelPopupWindow>()) return;

        var popup = new WheelPopupWindow(vm, screenCenter, cornerRadius);
        activeInstance = popup;
        // Pinned above the widget band + activated (the bare Show() skipped both).
        popup.ShowAsSecondaryPanel(owner);
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        WheelStore.DecisionRecorded -= OnDecisionRecorded;
        Closed -= OnWindowClosed;
        if (ReferenceEquals(activeInstance, this)) activeInstance = null;
    }

    private void PopulateTemplates()
    {
        isUpdatingCombo = true;
        TemplateComboBox.Items.Clear();

        var data = WheelStore.GetData();
        int selectedIndex = 0;
        for (int i = 0; i < data.Templates.Count; i++)
        {
            var t = data.Templates[i];
            var item = new ComboBoxItem { Content = t.Title, Tag = t.Id };
            TemplateComboBox.Items.Add(item);
            if (t.Id == viewModel.CurrentTemplate.Id) selectedIndex = i;
        }

        TemplateComboBox.SelectedIndex = selectedIndex;
        isUpdatingCombo = false;

        LoadCurrentTemplateOptions();
    }

    private void LoadCurrentTemplateOptions()
    {
        currentOptions.Clear();
        foreach (var opt in viewModel.CurrentTemplate.Options)
        {
            currentOptions.Add(opt);
        }
        EliminateCheckBox.IsChecked = viewModel.CurrentTemplate.EliminateOnSelected;
    }

    private void LoadHistory()
    {
        historyItems.Clear();
        var data = WheelStore.GetData();
        foreach (var r in data.History)
        {
            historyItems.Add(r);
        }
    }

    private void OnDecisionRecorded(WheelDecisionRecord record)
    {
        historyItems.Insert(0, record);
    }

    private void OnTemplateSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (isUpdatingCombo) return;
        if (TemplateComboBox.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            var tmpl = WheelStore.GetTemplate(id);
            viewModel.SwitchTemplate(tmpl);
            LoadCurrentTemplateOptions();
        }
    }

    private void OnNewTemplateClicked(object? sender, RoutedEventArgs e)
    {
        string newId = Guid.NewGuid().ToString("N");
        var newTmpl = new WheelTemplate
        {
            Id = newId,
            Title = $"新决策主题 {TemplateComboBox.Items.Count + 1}",
            Options =
            [
                new WheelOption { Text = "选项 A", Weight = 1, ColorHex = WheelStore.Palette[0] },
                new WheelOption { Text = "选项 B", Weight = 1, ColorHex = WheelStore.Palette[1] },
                new WheelOption { Text = "选项 C", Weight = 1, ColorHex = WheelStore.Palette[2] }
            ]
        };

        WheelStore.SaveTemplate(newTmpl);
        viewModel.SwitchTemplate(newTmpl);
        PopulateTemplates();
    }

    private void OnDeleteTemplateClicked(object? sender, RoutedEventArgs e)
    {
        var data = WheelStore.GetData();
        if (data.Templates.Count <= 1) return; // Keep at least one

        WheelStore.DeleteTemplate(viewModel.CurrentTemplate.Id);
        viewModel.SwitchTemplate(WheelStore.GetData().Templates[0]);
        PopulateTemplates();
    }

    private void OnAddOptionClicked(object? sender, RoutedEventArgs e)
    {
        int colorIdx = currentOptions.Count % WheelStore.Palette.Length;
        var opt = new WheelOption
        {
            Text = $"新选项 {currentOptions.Count + 1}",
            Weight = 1,
            ColorHex = WheelStore.Palette[colorIdx]
        };
        currentOptions.Add(opt);
    }

    private void OnDeleteOptionClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            if (currentOptions.Count <= 2) return; // Keep at least 2
            var found = currentOptions.FirstOrDefault(o => o.Id == id);
            if (found != null) currentOptions.Remove(found);
        }
    }

    private void OnEliminateChanged(object? sender, RoutedEventArgs e)
    {
        viewModel.CurrentTemplate.EliminateOnSelected = EliminateCheckBox.IsChecked ?? false;
    }

    private void OnSaveTemplateClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.CurrentTemplate.Options = currentOptions.ToList();
        viewModel.CurrentTemplate.EliminateOnSelected = EliminateCheckBox.IsChecked ?? false;
        WheelStore.SaveTemplate(viewModel.CurrentTemplate);
        viewModel.LoadTemplates();
    }

    private void OnClearHistoryClicked(object? sender, RoutedEventArgs e)
    {
        WheelStore.ClearHistory();
        historyItems.Clear();
    }
}
