using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Tools.Models;
using Tools.Services;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using DeskCanvas.Views;

namespace Tools.Views;

/// <summary>
/// Clipboard history panel (W7 pilot for the shared <see cref="SecondaryPanelShell"/>).
/// The window declares no card, glass surface, title chrome or close button of its own —
/// the shell provides them, and <see cref="SecondaryPanelWindow"/> plus
/// <see cref="PanelAppearanceResolver"/> own the material and the open/close transitions.
/// This class keeps only the business behaviour: the clipboard-history subscription,
/// category tabs, search filtering, the counter badge and the initial focus.
/// </summary>
public partial class ClipboardPopupWindow : SecondaryPanelWindow
{
    private readonly ClipboardMonitorService monitor;
    private readonly Point? spawnScreenCenter;
    private string activeCategory = "All";
    private string searchQuery = string.Empty;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public ClipboardPopupWindow() : this(null) { }

    public ClipboardPopupWindow(Point? screenCenter = null, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;
        monitor = ClipboardMonitorService.Instance;

        InitializeComponent();
        InitializePanel(cornerRadius);

        monitor.HistoryChanged += OnHistoryChanged;

        RefreshList();

        Closed += OnWindowClosed;
    }

    public static void ShowPopup(Point? screenCenter, Window? owner = null, double? cornerRadius = null)
    {
        if (PanelCoolingDown<ClipboardPopupWindow>())
            return;

        if (TryCloseActivePanel<ClipboardPopupWindow>())
            return;

        var popup = new ClipboardPopupWindow(screenCenter, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
    }

    protected override void OnPanelLoaded()
    {
        // Initialization focus stays with the business window (the shell's drag area, the
        // glass render request and the open animation are handled by the base class).
        SearchBox.Focus();
    }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(RefreshList);
    }

    private void RefreshList()
    {
        var items = monitor.History.AsEnumerable();

        // 1. Filter by category
        if (activeCategory == "Text")
        {
            items = items.Where(i => i.Type == ClipboardType.Text);
        }
        else if (activeCategory == "Image")
        {
            items = items.Where(i => i.Type == ClipboardType.Image);
        }
        else if (activeCategory == "Files")
        {
            items = items.Where(i => i.Type == ClipboardType.Files);
        }

        // 2. Filter by search query
        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var query = searchQuery.Trim().ToLowerInvariant();
            items = items.Where(i =>
                (!string.IsNullOrEmpty(i.Text) && i.Text.ToLowerInvariant().Contains(query)) ||
                (!string.IsNullOrEmpty(i.DisplayTitle) && i.DisplayTitle.ToLowerInvariant().Contains(query)) ||
                (i.FilePaths != null && i.FilePaths.Any(p => p.ToLowerInvariant().Contains(query))));
        }

        var result = items.OrderByDescending(i => i.IsPinned).ThenByDescending(i => i.Timestamp).ToList();
        ItemsControl.ItemsSource = result;

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        CountBadge.Text = isZh ? $"{result.Count} 项" : $"{result.Count} items";
        EmptyPanel.IsVisible = result.Count == 0;
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        searchQuery = SearchBox.Text ?? string.Empty;
        RefreshList();
    }

    private void OnTabClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            activeCategory = tag;

            TabAll.Classes.Set("active", tag == "All");
            TabText.Classes.Set("active", tag == "Text");
            TabImage.Classes.Set("active", tag == "Image");
            TabFiles.Classes.Set("active", tag == "Files");

            RefreshList();
        }
    }

    private void OnItemCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;

        if (sender is Border { DataContext: ClipboardItem item })
        {
            monitor.CopyToClipboard(item);
            Close();
        }
    }

    private void OnTogglePinClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: ClipboardItem item })
        {
            monitor.TogglePin(item);
        }
    }

    private void OnDeleteItemClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { DataContext: ClipboardItem item })
        {
            monitor.RemoveItem(item);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        monitor.HistoryChanged -= OnHistoryChanged;
    }
}
