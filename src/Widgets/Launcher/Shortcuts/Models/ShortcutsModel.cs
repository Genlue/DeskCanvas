namespace Shortcuts.Models;

/// <summary>
/// Widget settings for the Shortcuts card, stored in <c>layout.json</c>.
/// </summary>
/// <param name="MaxTiles">Upper bound on the number of shortcut tiles the card shows.</param>
/// <param name="ConfirmBeforeRun">Ask for confirmation before running a shortcut from the card.</param>
public record ShortcutsModel(
    int MaxTiles = 8,
    bool ConfirmBeforeRun = false);
