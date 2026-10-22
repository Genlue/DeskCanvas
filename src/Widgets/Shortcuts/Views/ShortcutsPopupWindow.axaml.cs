using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using DeskCanvas.Services;
using DeskCanvas.Views;
using Shortcuts.Models;
using Shortcuts.Services;
using Shortcuts.ViewModels;

namespace Shortcuts.Views;

public partial class ShortcutsPopupWindow : SecondaryPanelWindow
{
    private readonly Point? spawnScreenCenter;
    private readonly ObservableCollection<ShortcutTileViewModel> items = [];
    private readonly Dictionary<string, ShortcutTileViewModel> cache = new(StringComparer.Ordinal);
    private bool eventsAttached;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public ShortcutsPopupWindow() : this(null, null)
    {
    }

    public ShortcutsPopupWindow(Point? screenCenter, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        ListControl.ItemsSource = items;
        Populate();

        // Subscribe on Opened, not in the constructor: a window that is never shown never fires
        // Closed (see SecondaryPanelWindow), so a constructor subscription would leak the handler
        // into the static store/runner events if the panel is abandoned before it is displayed.
        Opened += OnWindowOpened;
        Closed += OnWindowClosed;

        // Self-subscription (no static event): keeps the row text brushes on the panel's variant.
        ActualThemeVariantChanged += (_, _) => ApplyThemeToTiles();
    }

    public static void ShowPopup(Point? screenCenter = null, Window? owner = null, double? cornerRadius = null)
    {
        // The PanelService owns the single-instance state: cooldown, toggle-close and the
        // pinned show. PerOwner keys each shortcuts widget's panel to its own component
        // instance (two cards no longer evict each other's panel), the same rule the
        // weather panels follow since P6.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "Shortcuts.QuickPanel",
            PanelScope.PerOwner,
            () => new ShortcutsPopupWindow(screenCenter, cornerRadius)));
    }

    // ---- events ------------------------------------------------------------------

    private void AttachEvents()
    {
        if (eventsAttached) return;
        eventsAttached = true;
        ShortcutsStore.ModelChanged += OnStoreChanged;
        ShortcutRunner.RunEvent += OnRunEvent;
    }

    private void DetachEvents()
    {
        if (!eventsAttached) return;
        eventsAttached = false;
        ShortcutsStore.ModelChanged -= OnStoreChanged;
        ShortcutRunner.RunEvent -= OnRunEvent;
    }

    private void OnStoreChanged(ShortcutsDocument document, object? sender)
    {
        if (Dispatcher.UIThread.CheckAccess()) Populate();
        else Dispatcher.UIThread.Post(Populate);
    }

    private void OnRunEvent(RunEvent runEvent)
    {
        void Apply()
        {
            if (cache.TryGetValue(runEvent.ShortcutId, out var tile))
                RunFeedback.Apply(tile, runEvent);
        }

        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        ShortcutsRuntime.PublishTopLevel(this);
        AttachEvents();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ShortcutsRuntime.ReleaseTopLevel(this);
        DetachEvents();
        Opened -= OnWindowOpened;
        Closed -= OnWindowClosed;
        items.Clear();
        cache.Clear();
    }

    // ---- list --------------------------------------------------------------------

    private void Populate()
    {
        var document = ShortcutsStore.Get();
        var filter = (SearchBox.Text ?? string.Empty).Trim();
        var visible = document.Shortcuts
            .Where(s => filter.Length == 0 ||
                        s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!visible.Any(s => s.Id == items[i].ShortcutId))
            {
                cache.Remove(items[i].ShortcutId);
                items.RemoveAt(i);
            }
        }

        for (var target = 0; target < visible.Count; target++)
        {
            var shortcut = visible[target];
            if (!cache.TryGetValue(shortcut.Id, out var tile))
            {
                tile = new ShortcutTileViewModel(shortcut);
                tile.ApplyTheme(ActualThemeVariant == ThemeVariant.Dark);
                cache[shortcut.Id] = tile;
            }
            else
            {
                // Reused rows must pick up renames / new hotkeys from the shared document.
                tile.Apply(shortcut);
                tile.ApplyTheme(ActualThemeVariant == ThemeVariant.Dark);
            }

            var currentIndex = items.IndexOf(tile);
            if (currentIndex < 0) items.Insert(target, tile);
            else if (currentIndex != target) items.Move(currentIndex, target);
        }

        SubtitleText.Text = Loc.Format("Shortcuts_Popup_Subtitle", document.Shortcuts.Count);
        EmptyPanel.IsVisible = visible.Count == 0;
        ListControl.IsVisible = visible.Count > 0;
        EmptyTitleText.Text = Loc.Get(filter.Length == 0 ? "Shortcuts_Empty" : "Shortcuts_NoMatch");
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => Populate();

    /// <summary>Row text brushes follow the panel's active theme variant.</summary>
    private void ApplyThemeToTiles()
    {
        var isDark = ActualThemeVariant == ThemeVariant.Dark;
        foreach (var tile in cache.Values)
            tile.ApplyTheme(isDark);
    }

    // ---- interaction -------------------------------------------------------------

    private void OnRowPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile) return;
        e.Handled = true;
        _ = RunOrCancelAsync(tile);
    }

    private void OnRunClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile) return;
        _ = RunOrCancelAsync(tile);
    }

    private void OnEditClicked(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile) return;
        ShortcutsEditorWindow.ShowEditor(tile.ShortcutId, this);
    }

    private void OnMoveUpClicked(object? sender, RoutedEventArgs e) => MoveRow(sender, -1);

    private void OnMoveDownClicked(object? sender, RoutedEventArgs e) => MoveRow(sender, 1);

    /// <summary>
    /// 手动排序: the list order is the card order, so moving a row here also moves the tile on
    /// every Shortcuts card (the store broadcasts the change).
    /// </summary>
    private void MoveRow(object? sender, int delta)
    {
        if ((sender as Control)?.DataContext is not ShortcutTileViewModel tile || tile.IsCreate) return;
        ShortcutsStore.Move(tile.ShortcutId, delta, this);
    }

    private async Task RunOrCancelAsync(ShortcutTileViewModel tile)
    {
        try
        {
            if (ShortcutRunner.IsRunning(tile.ShortcutId))
            {
                ShortcutRunner.Cancel(tile.ShortcutId);
                return;
            }

            var shortcut = ShortcutsStore.Get().Shortcuts.FirstOrDefault(s => s.Id == tile.ShortcutId);
            if (shortcut == null) return;
            await ShortcutRunner.RunAsync(shortcut, ShortcutsRuntime.CreateRunServices(this));
        }
        catch
        {
            // Failures surface through run events.
        }
    }

    private void OnAddClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor) return;

        var flyout = new MenuFlyout();
        foreach (var (labelKey, create) in ShortcutTemplates.Menu)
        {
            var item = new MenuItem { Header = Loc.Get(labelKey) };
            item.Click += (_, _) =>
            {
                try
                {
                    var shortcut = create();
                    ShortcutsStore.Upsert(shortcut, this);
                    ShortcutsEditorWindow.ShowEditor(shortcut.Id, this);
                }
                catch
                {
                    // Store diagnostics cover persistence failures.
                }
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(anchor);
    }
}
