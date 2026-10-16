using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Countdown.Locales;
using Countdown.Models;
using Countdown.Services;
using Countdown.ViewModels;
using DeskCanvas.Views;

namespace Countdown.Views;

public class CountdownPopupItemViewModel : INotifyPropertyChanged
{
    private CountdownItem model;
    private CountdownComputedResult computed;
    private IBrush colorBrush;

    public CountdownPopupItemViewModel(CountdownItem model, DateTime now)
    {
        this.model = model;
        computed = CountdownCalculationService.Compute(model, now, CountdownGranularity.DaysOnly);
        colorBrush = CountdownItemViewModel.ParseColorBrush(model.ColorTag);
    }

    public CountdownItem Model => model;

    public string Id => model.Id;
    public string Title => model.Title;
    public string Category => model.Category;
    public bool IsPinned => model.IsPinned;
    public bool IsLunar => model.IsLunar;

    public IBrush ColorBrush => colorBrush;
    public string MainDisplayNumber => computed.MainDisplayNumber;
    public string UnitLabel => computed.UnitLabel;
    public string FormattedDate => computed.FormattedDate;

    public void Update(CountdownItem newModel, DateTime now)
    {
        bool modelChanged = model != newModel;
        model = newModel;

        if (modelChanged)
        {
            colorBrush = CountdownItemViewModel.ParseColorBrush(model.ColorTag);
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(Category));
            OnPropertyChanged(nameof(IsPinned));
            OnPropertyChanged(nameof(IsLunar));
            OnPropertyChanged(nameof(ColorBrush));
        }

        var newComputed = CountdownCalculationService.Compute(model, now, CountdownGranularity.DaysOnly, computed);
        if (ReferenceEquals(newComputed, computed)) return;

        var oldComputed = computed;
        computed = newComputed;

        if (oldComputed.MainDisplayNumber != newComputed.MainDisplayNumber)
            OnPropertyChanged(nameof(MainDisplayNumber));
        if (oldComputed.UnitLabel != newComputed.UnitLabel)
            OnPropertyChanged(nameof(UnitLabel));
        if (oldComputed.FormattedDate != newComputed.FormattedDate)
            OnPropertyChanged(nameof(FormattedDate));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class CountdownPopupWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly ObservableCollection<CountdownPopupItemViewModel> items = [];
    private string? editingEventId;
    private string selectedColorTag = "#FF5A5F";

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public CountdownPopupWindow() : this(null, null) { }

    public CountdownPopupWindow(Point? screenCenter, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        EventsItemsControl.ItemsSource = items;
        PopulateItems();

        CountdownStore.ModelChanged += OnStoreModelChanged;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        if (PanelCoolingDown<CountdownPopupWindow>()) return;
        if (TryCloseActivePanel<CountdownPopupWindow>()) return;

        var popup = new CountdownPopupWindow(screenCenter, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
    }

    private void PopulateItems()
    {
        var rawEvents = CountdownStore.Get();
        var now = DateTime.Now;

        var currentDict = items.ToDictionary(i => i.Id);
        var newIds = new HashSet<string>(rawEvents.Select(e => e.Id));

        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!newIds.Contains(items[i].Id))
            {
                items.RemoveAt(i);
            }
        }

        int targetIndex = 0;
        foreach (var evt in rawEvents)
        {
            if (currentDict.TryGetValue(evt.Id, out var existing))
            {
                existing.Update(evt, now);
                int currentIndex = items.IndexOf(existing);
                if (currentIndex != targetIndex)
                {
                    items.Move(currentIndex, targetIndex);
                }
            }
            else
            {
                items.Insert(targetIndex, new CountdownPopupItemViewModel(evt, now));
            }
            targetIndex++;
        }

        UpdateSubtitle();
    }

    private void UpdateSubtitle()
    {
        SubtitleText.Text = string.Format(Locale.Countdown_TotalEvents, items.Count);
        EmptyNoticePanel.IsVisible = items.Count == 0;
    }

    private void OnStoreModelChanged(System.Collections.Generic.List<CountdownItem> rawEvents, object? sender)
    {
        if (ReferenceEquals(sender, this)) return;
        if (Dispatcher.UIThread.CheckAccess())
        {
            PopulateItems();
        }
        else
        {
            Dispatcher.UIThread.Post(PopulateItems);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        CountdownStore.ModelChanged -= OnStoreModelChanged;
        Closed -= OnWindowClosed;
        items.Clear();
    }

    private void OnAddEventClicked(object? sender, RoutedEventArgs e)
    {
        editingEventId = null;
        EditorHeaderTitle.Text = Locale.Countdown_AddEvent;
        EventTitleTextBox.Text = string.Empty;
        TargetDatePicker.SelectedDate = DateTimeOffset.Now.AddDays(7);
        IsLunarCheckBox.IsChecked = false;
        RepeatComboBox.SelectedIndex = 0;
        DirectionComboBox.SelectedIndex = 0;
        CategoryComboBox.SelectedIndex = 0;
        selectedColorTag = "#FF5A5F";

        ListViewPanel.IsVisible = false;
        EditorPanel.IsVisible = true;
    }

    private void OnEditItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CountdownPopupItemViewModel item })
        {
            var evt = item.Model;
            editingEventId = evt.Id;
            EditorHeaderTitle.Text = Locale.Countdown_EditEvent;
            EventTitleTextBox.Text = evt.Title;
            TargetDatePicker.SelectedDate = new DateTimeOffset(evt.TargetDate);
            IsLunarCheckBox.IsChecked = evt.IsLunar;

            RepeatComboBox.SelectedIndex = (int)evt.Repeat;
            DirectionComboBox.SelectedIndex = (int)evt.Direction;

            // Select category
            for (int i = 0; i < CategoryComboBox.Items.Count; i++)
            {
                if (CategoryComboBox.Items[i] is ComboBoxItem cbi && cbi.Tag?.ToString() == evt.Category)
                {
                    CategoryComboBox.SelectedIndex = i;
                    break;
                }
            }

            selectedColorTag = evt.ColorTag ?? "#FF5A5F";

            ListViewPanel.IsVisible = false;
            EditorPanel.IsVisible = true;
        }
    }

    private void OnDeleteItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CountdownPopupItemViewModel item })
        {
            CountdownStore.Delete(item.Id, this);
            PopulateItems();
        }
    }

    private void OnPinItemClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: CountdownPopupItemViewModel item })
        {
            CountdownStore.TogglePin(item.Id, this);
            PopulateItems();
        }
    }

    private void OnColorTagSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            selectedColorTag = hex;
        }
    }

    private void OnCancelEditClicked(object? sender, RoutedEventArgs e)
    {
        EditorPanel.IsVisible = false;
        ListViewPanel.IsVisible = true;
    }

    private void OnSaveEditClicked(object? sender, RoutedEventArgs e)
    {
        string title = EventTitleTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(title))
        {
            title = Locale.Countdown;
        }

        DateTime targetDate = TargetDatePicker.SelectedDate?.DateTime ?? DateTime.Now.AddDays(7);
        bool isLunar = IsLunarCheckBox.IsChecked == true;
        RepeatCycle repeat = (RepeatCycle)RepeatComboBox.SelectedIndex;
        CountDirection direction = (CountDirection)DirectionComboBox.SelectedIndex;

        string category = "General";
        if (CategoryComboBox.SelectedItem is ComboBoxItem item && item.Tag is string catTag)
        {
            category = catTag;
        }

        int lunarMonth = 1;
        int lunarDay = 1;
        bool isLeapMonth = false;
        if (isLunar)
        {
            LunarCalendarHelper.SolarToLunar(targetDate, out _, out lunarMonth, out lunarDay, out isLeapMonth);
        }

        CountdownItem evt = new(
            Id: editingEventId ?? Guid.NewGuid().ToString("N"),
            Title: title,
            TargetDate: targetDate,
            StartDate: DateTime.Now,
            IsLunar: isLunar,
            LunarMonth: lunarMonth,
            LunarDay: lunarDay,
            IsLeapMonth: isLeapMonth,
            Repeat: repeat,
            Direction: direction,
            Category: category,
            ColorTag: selectedColorTag,
            IsPinned: false
        );

        CountdownStore.AddOrUpdate(evt, this);
        PopulateItems();

        EditorPanel.IsVisible = false;
        ListViewPanel.IsVisible = true;
    }
}
