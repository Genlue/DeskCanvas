using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Reminders.Locales;
using Reminders.Models;
using Reminders.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Reminders.Views;

public class ReminderItemViewModel : INotifyPropertyChanged
{
    private bool completed;
    private string title;

    public ReminderItemViewModel(ReminderModel model)
    {
        completed = model.Completed;
        title = model.Title;
    }

    public bool Completed
    {
        get => completed;
        set
        {
            if (completed != value)
            {
                completed = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TextOpacity));
                OnPropertyChanged(nameof(TextDecorations));
            }
        }
    }

    public string Title
    {
        get => title;
        set
        {
            if (title != value)
            {
                title = value;
                OnPropertyChanged();
            }
        }
    }

    public double TextOpacity => Completed ? 0.45 : 1.0;
    public TextDecorationCollection? TextDecorations => Completed ? Avalonia.Media.TextDecorations.Strikethrough : null;

    public ReminderModel ToModel() => new(Completed, Title);

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? propName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propName));
}

/// <summary>
/// Reminders secondary panel: the editable list title, the check-off/delete list and the
/// bottom add bar. The card, the glass surface, the header chrome and the close button live
/// in the shared <see cref="SecondaryPanelShell"/> now — the five-branch material ApplyTheme,
/// the root drag handler and the per-open AppSettingsProvider are gone:
/// <see cref="SecondaryPanelWindow"/> plus <see cref="DeskCanvas.Services.PanelAppearanceResolver"/>
/// own the material and the transitions. This class keeps only business behaviour.
/// </summary>
public partial class RemindersPopupWindow : SecondaryPanelWindow
{
    private RemindersListModel currentModel;
    private readonly Point? spawnScreenCenter;
    private readonly Action<RemindersListModel>? onModelChanged;
    private readonly ObservableCollection<ReminderItemViewModel> items = [];

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public RemindersPopupWindow() : this(new RemindersListModel(null, []), null, null) { }

    public RemindersPopupWindow(
        RemindersListModel model,
        Point? screenCenter = null,
        Action<RemindersListModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        currentModel = model;
        spawnScreenCenter = screenCenter;
        this.onModelChanged = onModelChanged;

        InitializeComponent();
        InitializePanel(cornerRadius);

        ItemsList.ItemsSource = items;

        TitleBox.Text = GetDisplayListName(currentModel);

        PopulateItems();

        RemindersStore.ModelChanged += OnStoreModelChanged;
        Closed += OnWindowClosed;
    }

    private static string GetDisplayListName(RemindersListModel model)
    {
        if (string.IsNullOrWhiteSpace(model.ListName))
            return Locale.Reminders_List_Title;
        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        if (!isZh && (model.ListName == "待办清单" || model.ListName == "清单"))
            return Locale.Reminders_DefaultListName;
        return model.ListName;
    }

    public static void ShowPopup(
        RemindersListModel model,
        Point? screenCenter,
        Window? owner = null,
        Action<RemindersListModel>? onModelChanged = null,
        double? cornerRadius = null)
    {
        // PerOwner identity (the weather panel precedent): every reminders widget owns its own
        // panel. The service handles the cooldown, the toggle-close and the pinned show — the
        // decision is deliberately ignored, mirroring the old bare early returns.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "reminders.popup",
            PanelScope.PerOwner,
            () => new RemindersPopupWindow(model, screenCenter, onModelChanged, cornerRadius)));
    }

    protected override void OnPanelLoaded()
    {
        // Initialization focus stays with the business window (the shell's drag area, the
        // glass render request and the open animation are handled by the base class).
        NewItemTextBox.Focus();
    }

    private void PopulateItems()
    {
        items.Clear();
        foreach (var r in currentModel.Reminders)
            items.Add(new ReminderItemViewModel(r));

        UpdateStatsAndVisibility();
    }

    private void UpdateStatsAndVisibility()
    {
        int total = items.Count;
        int completed = items.Count(i => i.Completed);

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        SubtitleText.Text = isZh
            ? $"共 {total} 项" + (completed > 0 ? $" · {completed} 项已完成" : "")
            : $"{total} items" + (completed > 0 ? $" · {completed} completed" : "");
        EmptyPanel.IsVisible = total == 0;
        ClearCompletedButton.IsVisible = completed > 0;
    }

    private void CommitChanges()
    {
        var updatedList = items.Select(i => i.ToModel()).ToList();
        currentModel = currentModel with { Reminders = updatedList };
        onModelChanged?.Invoke(currentModel);
        UpdateStatsAndVisibility();
    }

    // ---------- 条目交互 ----------

    private void OnItemCheckClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ReminderItemViewModel item)
        {
            item.Completed = !item.Completed;
            if (item.Completed && currentModel.DeleteOnCheck)
            {
                items.Remove(item);
            }
            else if (item.Completed)
            {
                var index = items.IndexOf(item);
                if (index >= 0 && index < items.Count - 1)
                {
                    items.RemoveAt(index);
                    items.Add(item);
                }
            }
            CommitChanges();
        }
    }

    private void OnItemTextLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox tb && tb.DataContext is ReminderItemViewModel item)
        {
            var text = tb.Text?.Trim() ?? "";
            if (string.IsNullOrEmpty(text))
            {
                items.Remove(item);
            }
            else
            {
                item.Title = text;
            }
            CommitChanges();
        }
    }

    private void OnItemTextKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
            e.Handled = true;
        }
    }

    private void OnItemDeleteClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is ReminderItemViewModel item)
        {
            items.Remove(item);
            CommitChanges();
        }
    }

    private void OnClearCompletedClicked(object? sender, RoutedEventArgs e)
    {
        var toRemove = items.Where(i => i.Completed).ToList();
        foreach (var item in toRemove)
            items.Remove(item);

        CommitChanges();
    }

    // ---------- 新建待办 ----------

    private void OnNewItemKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddNewItem();
            e.Handled = true;
        }
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        AddNewItem();
    }

    private void AddNewItem()
    {
        var text = NewItemTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        var newItem = new ReminderItemViewModel(new ReminderModel(false, text));
        items.Add(newItem);
        NewItemTextBox.Clear();
        CommitChanges();

        Dispatcher.UIThread.Post(() => NewItemTextBox.Focus());
    }

    // ---------- 标题修改 ----------

    private void OnTitleLostFocus(object? sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text?.Trim();
        if (title != currentModel.ListName)
        {
            currentModel = currentModel with { ListName = title };
            onModelChanged?.Invoke(currentModel);
        }
    }

    private void OnTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
            e.Handled = true;
        }
    }

    // ---------- 窗口生命周期 ----------
    // The window declares no close button of its own any more: the shared shell's close
    // button, the backdrop press, Escape and the outside-click hook all collapse into the
    // base class's single animated close.

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        RemindersStore.ModelChanged -= OnStoreModelChanged;
    }

    private void OnStoreModelChanged(RemindersListModel newModel, object? sender)
    {
        if (Equals(currentModel, newModel)) return;
        currentModel = newModel;
        TitleBox.Text = GetDisplayListName(currentModel);
        PopulateItems();
    }
}
