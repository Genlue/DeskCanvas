using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Countdown.FestivalPreset;
using Countdown.Locales;
using Countdown.Models;
using Countdown.Services;
using Countdown.ViewModels;
using DeskCanvas.Services;
using DeskCanvas.Views;
using FestivalPresetDefinition = Countdown.FestivalPreset.FestivalPreset;

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
    // “自定义”不是一条真实预设，用一个不会与 festival./Guid 冲突的哨兵 id 表示。
    private const string CustomPresetChipId = "__custom__";

    private readonly Point? spawnScreenCenter;
    private readonly ObservableCollection<CountdownPopupItemViewModel> items = [];
    private readonly Dictionary<string, Button> presetChipButtons = new(StringComparer.Ordinal);
    private string? editingEventId;
    private string? selectedPresetId;
    private string selectedColorTag = "#FF5A5F";

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public CountdownPopupWindow() : this(null, null) { }

    public CountdownPopupWindow(Point? screenCenter, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);
        RebuildPresetChips();

        EventsItemsControl.ItemsSource = items;
        PopulateItems();

        CountdownStore.ModelChanged += OnStoreModelChanged;
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        // PerOwner identity (the weather panel precedent): every countdown widget owns its own
        // panel. The service handles the cooldown, the toggle-close and the pinned show — the
        // decision is deliberately ignored, mirroring the old bare early returns.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "countdown.popup",
            PanelScope.PerOwner,
            () => new CountdownPopupWindow(screenCenter, cornerRadius)));
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
        SetSelectedPreset(null);

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
            SelectCategory(evt.Category);

            selectedColorTag = evt.ColorTag ?? "#FF5A5F";
            // 事件不记录来源预设，编辑旧事件时回到“自定义”状态。
            SetSelectedPreset(null);

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
        var evt = BuildItemFromEditor(editingEventId ?? Guid.NewGuid().ToString("N"));
        CountdownStore.AddOrUpdate(evt, this);
        PopulateItems();

        EditorPanel.IsVisible = false;
        ListViewPanel.IsVisible = true;
    }

    private void OnSaveAsPresetClicked(object? sender, RoutedEventArgs e)
    {
        // 预设只是当前草稿的快照；用户仍可继续修改编辑器，不会反向写回预设。
        var draft = BuildItemFromEditor(Guid.NewGuid().ToString("N"));
        CustomPresetStore.Add(CustomPreset.FromItem(draft));
        RebuildPresetChips();
    }

    /// <summary>把编辑器里的字段收成一个事件草稿，保存事件与保存预设共用同一套解析。</summary>
    private CountdownItem BuildItemFromEditor(string id)
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

        return new CountdownItem(
            Id: id,
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
    }

    private void SelectCategory(string category)
    {
        for (int i = 0; i < CategoryComboBox.Items.Count; i++)
        {
            if (CategoryComboBox.Items[i] is ComboBoxItem item && item.Tag?.ToString() == category)
            {
                CategoryComboBox.SelectedIndex = i;
                return;
            }
        }

        CategoryComboBox.SelectedIndex = 0;
    }

    /// <summary>“自定义”永远排第一并默认选中，预设是可选加速器而不是必选项。</summary>
    private void RebuildPresetChips()
    {
        presetChipButtons.Clear();
        PresetChipsPanel.Children.Clear();

        AddPresetChip(CustomPresetChipId, Locale.Countdown_Festival_Custom, colorTag: null, removable: false);

        foreach (var preset in FestivalPresetCatalog.All)
        {
            AddPresetChip(preset.Id, preset.Title, preset.ColorTag, removable: false);
        }

        foreach (var preset in CustomPresetStore.Get())
        {
            AddPresetChip(preset.Id, preset.Title, preset.ColorTag, removable: true);
        }

        UpdatePresetChipSelection();
    }

    private void AddPresetChip(string id, string title, string? colorTag, bool removable)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (!string.IsNullOrEmpty(colorTag))
        {
            content.Children.Add(new Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = CountdownItemViewModel.ParseColorBrush(colorTag),
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            MaxWidth = 150,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        });

        var apply = new Button
        {
            Classes = { "pill-btn", "preset-chip" },
            Content = content,
            Tag = id,
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(apply, title);
        apply.Click += OnPresetChipClicked;
        presetChipButtons[id] = apply;

        if (!removable)
        {
            PresetChipsPanel.Children.Add(apply);
            return;
        }

        // 我的预设用末尾的小叉区分，且删除不会触发应用。
        var remove = new Button
        {
            Classes = { "action-icon-btn", "danger" },
            Content = new TextBlock { Text = "✕", FontSize = 9 },
            Tag = id,
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(remove, Locale.Countdown_DeletePreset);
        remove.Click += OnDeleteCustomPresetClicked;

        var group = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 0
        };
        group.Children.Add(apply);
        group.Children.Add(remove);
        PresetChipsPanel.Children.Add(group);
    }

    private void OnPresetChipClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        if (id == CustomPresetChipId)
        {
            SetSelectedPreset(null);
            return;
        }

        var festival = FestivalPresetCatalog.Find(id);
        if (festival != null)
        {
            ApplyFestivalPreset(festival);
            return;
        }

        var custom = CustomPresetStore.Get().FirstOrDefault(preset => preset.Id == id);
        if (custom != null)
        {
            ApplyCustomPreset(custom);
        }
    }

    private void ApplyFestivalPreset(FestivalPresetDefinition preset)
    {
        EventTitleTextBox.Text = preset.Title;
        TargetDatePicker.SelectedDate = new DateTimeOffset(
            FestivalPresetResolver.ResolveNextOccurrence(preset, DateTime.Today));
        IsLunarCheckBox.IsChecked = preset.IsLunar;
        RepeatComboBox.SelectedIndex = (int)preset.Repeat;
        DirectionComboBox.SelectedIndex = (int)CountDirection.Auto;
        SelectCategory(preset.Category);
        selectedColorTag = preset.ColorTag;
        SetSelectedPreset(preset.Id);
    }

    private void ApplyCustomPreset(CustomPreset preset)
    {
        var draft = preset.ToCountdownItem();
        var next = CountdownCalculationService.ResolveEffectiveTargetDate(draft, DateTime.Now);

        EventTitleTextBox.Text = preset.Title;
        TargetDatePicker.SelectedDate = new DateTimeOffset(next);
        IsLunarCheckBox.IsChecked = preset.IsLunar;
        RepeatComboBox.SelectedIndex = (int)preset.Repeat;
        DirectionComboBox.SelectedIndex = (int)preset.Direction;
        SelectCategory(preset.Category);
        selectedColorTag = preset.ColorTag;
        SetSelectedPreset(preset.Id);
    }

    private void OnDeleteCustomPresetClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        if (!CustomPresetStore.Delete(id))
        {
            return;
        }

        if (selectedPresetId == id)
        {
            selectedPresetId = null;
        }

        RebuildPresetChips();
    }

    private void SetSelectedPreset(string? id)
    {
        selectedPresetId = id;
        UpdatePresetChipSelection();
    }

    private void UpdatePresetChipSelection()
    {
        string activeId = selectedPresetId ?? CustomPresetChipId;
        foreach (var (id, button) in presetChipButtons)
        {
            if (id == activeId)
            {
                if (!button.Classes.Contains("active"))
                {
                    button.Classes.Add("active");
                }
            }
            else
            {
                button.Classes.Remove("active");
            }
        }
    }
}
