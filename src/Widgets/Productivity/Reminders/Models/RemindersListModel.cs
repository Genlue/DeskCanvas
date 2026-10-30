namespace Reminders.Models;

/// <param name="ListName">The list title shown on the card.</param>
/// <param name="Reminders">The checklist items.</param>
/// <param name="DeleteOnCheck">Remove an item from the list when it gets checked.</param>
/// <param name="AllowInlineEdit">
/// Allow editing the list name and item titles directly on the widget card
/// (double-click activation). Default <c>false</c>: the card surface keeps only
/// the check buttons and the popup (secondary panel) entry — all text editing
/// happens in the popup. Can be turned on in the widget settings.
/// </param>
public record RemindersListModel(
    string? ListName,
    List<ReminderModel> Reminders,
    bool DeleteOnCheck = false,
    bool AllowInlineEdit = false);
