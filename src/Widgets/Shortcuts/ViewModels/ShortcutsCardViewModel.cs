using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using DeskCanvas.Services;
using Shortcuts.Models;
using Shortcuts.Services;

namespace Shortcuts.ViewModels;

/// <summary>Card state: the tile list, grid columns and per-tile run feedback.</summary>
public sealed class ShortcutsCardViewModel : INotifyPropertyChanged
{
    private readonly Dictionary<string, ShortcutTileViewModel> tileCache = new(StringComparer.Ordinal);
    private ShortcutsModel model = new();
    private int capacity = 4;
    private bool compactTiles;
    private bool darkTheme;

    public ObservableCollection<ShortcutTileViewModel> Tiles { get; } = [];

    private int columns = 2;

    public int Columns
    {
        get => columns;
        private set
        {
            if (columns == value) return;
            columns = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Recompute capacity/columns for the tier and rebuild from the store.
    /// Small (2x2) is a single-column list — one shortcut per row, the iOS small-widget shape;
    /// wider/taller cards switch to a roomy two-column grid.
    /// </summary>
    public void ApplyLayout(ShortcutsModel settings, WidgetTier tier)
    {
        model = settings;

        var (tierColumns, tierCapacity, compact) = tier switch
        {
            WidgetTier.Cell => (1, 1, true),
            // Three rows is the most the 2x2 card can hold at the comfortable row height; four
            // would force the blocks under their content height and clip the icons.
            WidgetTier.Small => (1, 3, false),
            WidgetTier.Medium => (2, 4, false),
            WidgetTier.Large => (2, 8, false),
            _ => (1, 3, false),
        };

        Columns = tierColumns;
        compactTiles = compact;
        capacity = Math.Max(1, Math.Min(Math.Max(1, settings.MaxTiles), tierCapacity));
        Rebuild();
    }

    public ShortcutsModel Model => model;

    /// <summary>Rebuild the tile collection from the shared document, reusing tile view models.</summary>
    public void Rebuild()
    {
        var document = ShortcutsStore.Get();
        var wanted = document.Shortcuts.Take(capacity).ToList();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < Tiles.Count; i++)
        {
            var tile = Tiles[i];
            if (tile.IsCreate) continue;
            var stillThere = wanted.Any(s => s.Id == tile.ShortcutId);
            if (!stillThere)
            {
                Tiles.RemoveAt(i--);
                tileCache.Remove(tile.ShortcutId);
            }
        }

        for (var target = 0; target < wanted.Count; target++)
        {
            var shortcut = wanted[target];
            seen.Add(shortcut.Id);

            if (!tileCache.TryGetValue(shortcut.Id, out var tile))
            {
                tile = new ShortcutTileViewModel(shortcut);
                tileCache[shortcut.Id] = tile;
            }

            var currentIndex = Tiles.IndexOf(tile);
            if (currentIndex < 0) Tiles.Insert(target, tile);
            else if (currentIndex != target) Tiles.Move(currentIndex, target);
        }

        var createIndex = Math.Min(wanted.Count, Tiles.Count);
        var createTile = Tiles.FirstOrDefault(t => t.IsCreate);
        if (wanted.Count < capacity)
        {
            if (createTile == null)
            {
                createTile = ShortcutTileViewModel.CreateNew();
                Tiles.Add(createTile);
            }
            else
            {
                var index = Tiles.IndexOf(createTile);
                if (index != createIndex) Tiles.Move(index, createIndex);
            }
        }
        else if (createTile != null)
        {
            Tiles.Remove(createTile);
        }

        foreach (var tile in Tiles)
        {
            tile.ShowSubtitle = !compactTiles;
            tile.ApplyTheme(darkTheme);
        }
    }

    /// <summary>Push the active theme variant onto every tile (moves the text brushes with it).</summary>
    public void ApplyTheme(bool isDark)
    {
        darkTheme = isDark;
        foreach (var tile in Tiles)
            tile.ApplyTheme(isDark);
    }

    public ShortcutDefinition? FindShortcut(string id) =>
        ShortcutsStore.Get().Shortcuts.FirstOrDefault(s => s.Id == id);

    public ShortcutTileViewModel? FindTile(string shortcutId) =>
        tileCache.GetValueOrDefault(shortcutId);

    /// <summary>Feed one runner event into the matching tile.</summary>
    public void ApplyRunEvent(RunEvent runEvent)
    {
        var tile = FindTile(runEvent.ShortcutId);
        if (tile == null) return;
        RunFeedback.Apply(tile, runEvent);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
