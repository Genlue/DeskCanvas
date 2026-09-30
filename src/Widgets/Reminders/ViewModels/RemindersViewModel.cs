using Avalonia.Collections;
using ReactiveUI;
using Reminders.Models;

namespace Reminders.ViewModels;

/// <summary>
/// Single, long-lived view model for the widget. Kept in sync with the model
/// in place (no DataContext swaps) so the editing session, focus and caret
/// survive saves and external updates.
/// <para>
/// <see cref="Reminders"/> is a <b>stable</b> <see cref="AvaloniaList{T}"/> instance
/// that the <c>ItemsControl</c> binds to exactly once: the widget's items live there
/// and are surfaced through incremental collection changes (Add/Replace/Remove).
/// The list is never re-assigned and the <c>ItemsSource</c> is never re-published:
/// replacing the source makes Avalonia's <c>ItemCollection.SetSource</c> raise a
/// Remove-all/Add-all pair whose removed count comes from the old source — if the
/// panel's containers were not materialized 1:1 (e.g. an item was added to the
/// model but only surfaced by the next notification) that Remove walks past the
/// panel children and throws (crash_log: "Index was out of range" in
/// <c>PanelContainerGenerator.Remove</c>), which is what happened when a reminder
/// was added to the list.
/// </para>
/// </summary>
public class RemindersViewModel : ReactiveObject
{
    private RemindersListModel model;

    public RemindersViewModel(RemindersListModel model)
    {
        this.model = model;
        foreach (var reminder in model.Reminders)
            Reminders.Add(reminder);
        Reminders.CollectionChanged += (_, _) => this.RaisePropertyChanged(nameof(Count));
    }

    /// <summary>
    /// The persisted model snapshot. The items are always taken from the live
    /// <see cref="Reminders"/> list, so the observable list and the saved model
    /// cannot diverge.
    /// </summary>
    public RemindersListModel Model => model with { Reminders = Reminders.ToList() };

    /// <summary>
    /// The live reminder list bound to the <c>ItemsControl</c> — the single
    /// source of truth for the items during the session. Stable instance;
    /// mutated in place only.
    /// </summary>
    public AvaloniaList<ReminderModel> Reminders { get; } = new();

    /// <summary>
    /// Bring the view model in line with the given model (external refresh /
    /// after a save round-trip): the live list is reconciled <b>in place</b>
    /// with minimal operations and its reference is never replaced.
    /// </summary>
    public void Update(RemindersListModel newModel)
    {
        model = newModel;
        SyncReminders(newModel.Reminders);
        this.RaisePropertyChanged(nameof(ListName));
        this.RaisePropertyChanged(nameof(AllowInlineEdit));
        this.RaisePropertyChanged(nameof(Count));
    }

    /// <summary>
    /// Whether the card's text boxes may be activated for editing (widget setting).
    /// Bound to <c>ClickThroughTextBox.AllowEdit</c> by the card views; flipping the
    /// setting live re-evaluates every box without rebuilding the card.
    /// </summary>
    public bool AllowInlineEdit => model.AllowInlineEdit;

    /// <summary>
    /// Reconcile <see cref="Reminders"/> with the given model list through
    /// minimal, index-safe operations. Records compare by value, so content
    /// that did not change (e.g. the model arrived from a save round-trip) is
    /// a no-op and the existing containers stay untouched.
    /// </summary>
    private void SyncReminders(List<ReminderModel> source)
    {
        // 1. Drop items that are no longer in the model, one by one at their
        //    current index (never a Remove with a stale count).
        for (var i = Reminders.Count - 1; i >= 0; i--)
        {
            if (!source.Contains(Reminders[i]))
                Reminders.RemoveAt(i);
        }

        // 2. Align the remaining items with the source: replace changed items
        //    in place, append new trailing items.
        for (var i = 0; i < source.Count; i++)
        {
            if (i < Reminders.Count)
            {
                if (!Equals(Reminders[i], source[i]))
                    Reminders[i] = source[i];
            }
            else
            {
                Reminders.Add(source[i]);
            }
        }
    }

    /// <summary>
    /// The list title. Writable so the TwoWay TextBox binding keeps the model
    /// in sync while typing (the view still persists on focus loss).
    /// </summary>
    public string? ListName
    {
        get => model.ListName;
        set
        {
            if (value == model.ListName) return;
            model = model with { ListName = value };
            this.RaisePropertyChanged();
        }
    }

    public int Count => Reminders.Count;
}
