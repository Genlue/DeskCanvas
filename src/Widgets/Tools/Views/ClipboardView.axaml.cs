using System;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Tools.Models;
using Tools.Services;
using uWidgets.Core.Interfaces;
using uWidgets.Core.Models;
using uWidgets.Services;

namespace Tools.Views;

public partial class ClipboardView : UserControl, IWidgetSelfRefreshing
{
    private readonly ClipboardMonitorService monitor;
    private ClipboardModel model;
    private string currentFilter = "All";
    private readonly DispatcherTimer toastTimer;
    private WidgetTier currentTier = (WidgetTier)(-1);

    /// <summary>
    /// Preview/designer path. Must NOT push the default model into the monitor singleton:
    /// the Gallery instantiates one real control per widget type, and an UpdateSettings from
    /// that preview used to reset the user's capture settings and permanently trim history
    /// entries (and their PNG files) down to the default limit.
    /// </summary>
    public ClipboardView() : this(new ClipboardModel(), applySettingsToMonitor: false) { }

    public ClipboardView(ClipboardModel model) : this(model, applySettingsToMonitor: true) { }

    private ClipboardView(ClipboardModel model, bool applySettingsToMonitor)
    {
        this.model = model;
        monitor = ClipboardMonitorService.Instance;
        if (applySettingsToMonitor)
        {
            monitor.UpdateSettings(model);
        }

        InitializeComponent();
        ApplyTier(WidgetTier.Small);

        toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
        };
        toastTimer.Tick += (_, _) =>
        {
            ToastBanner.IsVisible = false;
            toastTimer.Stop();
        };

        monitor.HistoryChanged += OnHistoryChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
        PointerEntered += OnPointerEntered;

        RefreshDisplay();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        // OnUnloaded drops this subscription (it roots the view through the process-lifetime monitor
        // singleton), so a cached page that comes back must re-attach or the history stops updating.
        monitor.HistoryChanged -= OnHistoryChanged;
        monitor.HistoryChanged += OnHistoryChanged;
        SizeChanged -= OnSizeChanged;
        SizeChanged += OnSizeChanged;

        if (Bounds.Width > 0 && Bounds.Height > 0)
        {
            var initialTier = ResolveTier(Bounds.Size);
            if (initialTier != currentTier)
            {
                currentTier = initialTier;
                ApplyTier(initialTier);
            }
            AdaptCompactControls(Bounds.Width);
        }

        RefreshDisplay();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        monitor.HistoryChanged -= OnHistoryChanged;
        SizeChanged -= OnSizeChanged;
        PointerEntered -= OnPointerEntered;

        // Same teardown as TranslatorView: a toast caught mid-display would otherwise keep
        // this detached view alive (and hold its bindings) for up to the toast interval.
        toastTimer.Stop();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var size = e.NewSize;
        if (size.Width <= 0 || size.Height <= 0) return;

        var tier = ResolveTier(size);
        if (tier != currentTier)
        {
            currentTier = tier;
            ApplyTier(tier);
        }

        AdaptCompactControls(size.Width);
    }

    private WidgetTier ResolveTier(Size size)
    {
        // 1. Try grid host span resolution first (highest accuracy)
        try
        {
            var spanTier = SizeTiers.ResolveTier(this, size);
            if (spanTier != WidgetTier.Other)
            {
                return spanTier;
            }
        }
        catch { }

        // 2. Physically narrow cards (<= 230 DIP) cannot fit medium/large layouts
        if (size.Width <= 230)
        {
            return WidgetTier.Small;
        }

        // 3. Pixel fallback calibrated for desktop grid
        if (size.Width >= 280 && size.Height >= 280)
            return WidgetTier.Large;
        if (size.Width >= 260 || size.Width > size.Height * 1.35)
            return WidgetTier.Medium;

        return WidgetTier.Small;
    }

    private void ApplyTier(WidgetTier tier)
    {
        bool isSmall = tier == WidgetTier.Small || tier == WidgetTier.Cell;

        Classes.Set("tier-small", isSmall);
        InlineFilterPanel.IsVisible = !isSmall;
        if (!isSmall)
        {
            CompactFilterBtn.IsVisible = false;
        }
        else
        {
            AdaptCompactControls(Bounds.Width > 0 ? Bounds.Width : 160);
        }
    }

    private void AdaptCompactControls(double width)
    {
        bool isSmall = currentTier == WidgetTier.Small || currentTier == WidgetTier.Cell;
        if (!isSmall)
        {
            CompactFilterBtn.IsVisible = false;
            return;
        }

        // When user increases widget margin, 2x2 width shrinks (down to 110~140 DIPs).
        // Responsive rules:
        // width >= 165: Full pill button with text and arrow ("全部 ▾")
        // 135 <= width < 165: Compact icon button (funnel icon, only 18px wide)
        // width < 135: Hide filter button completely to ensure Expand & Clear buttons have 100% visible room
        if (width >= 165)
        {
            CompactFilterBtn.IsVisible = true;
            CompactFilterIcon.IsVisible = false;
            CompactFilterText.IsVisible = true;
            CompactFilterArrow.IsVisible = true;
            CompactFilterBtn.Padding = new Thickness(4, 2);
        }
        else if (width >= 135)
        {
            CompactFilterBtn.IsVisible = true;
            CompactFilterIcon.IsVisible = true;
            CompactFilterText.IsVisible = false;
            CompactFilterArrow.IsVisible = false;
            CompactFilterBtn.Padding = new Thickness(3, 2);
        }
        else
        {
            CompactFilterBtn.IsVisible = false;
        }
    }

    private void OnHistoryChanged()
    {
        Dispatcher.UIThread.Post(RefreshDisplay);
    }

    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<ClipboardModel>();
            if (updated != null)
            {
                model = updated;
                monitor.UpdateSettings(model);
                RefreshDisplay();
            }
        }
        catch { }
    }

    private void RefreshDisplay()
    {
        var items = monitor.History.AsEnumerable();

        if (currentFilter == "Text")
        {
            items = items.Where(i => i.Type == ClipboardType.Text);
        }
        else if (currentFilter == "Image")
        {
            items = items.Where(i => i.Type == ClipboardType.Image);
        }
        else if (currentFilter == "Files")
        {
            items = items.Where(i => i.Type == ClipboardType.Files);
        }

        var list = items.ToList();
        HistoryList.ItemsSource = list;

        CountBadgeText.Text = $"{monitor.History.Count}";
        EmptyStatePanel.IsVisible = list.Count == 0;
        HistoryScrollViewer.IsVisible = list.Count > 0;
    }

    private void OnFilterClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string filter)
        {
            SetFilter(filter);
        }
    }

    private void OnCompactFilterBtnClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.Open(btn);
        }
    }

    private void OnSelectFilterMenuItem(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem item && item.Tag is string filter)
        {
            SetFilter(filter);
        }
    }

    private void SetFilter(string filter)
    {
        currentFilter = filter;

        FilterAllBtn.Classes.Set("active", filter == "All");
        FilterTextBtn.Classes.Set("active", filter == "Text");
        FilterImageBtn.Classes.Set("active", filter == "Image");
        FilterFilesBtn.Classes.Set("active", filter == "Files");

        CompactFilterText.Text = filter switch
        {
            "Text" => "文本",
            "Image" => "图片",
            "Files" => "文件",
            _ => "全部"
        };

        RefreshDisplay();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        monitor.ClearAll();
        ShowToast("已清空剪贴板历史");
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!model.AutoCopyOnClick) return;
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) return;

        if (sender is Border { DataContext: ClipboardItem item })
        {
            CopyItem(item);
        }
    }

    private void OnItemCopyClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: ClipboardItem item })
        {
            CopyItem(item);
        }
    }

    private void OnItemDeleteClicked(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: ClipboardItem item })
        {
            monitor.RemoveItem(item);
        }
    }

    private void CopyItem(ClipboardItem item)
    {
        if (monitor.CopyToClipboard(item))
        {
            ShowToast("已复制到剪贴板");
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    private void OnPointerEntered(object? sender, PointerEventArgs e)
    {
        PreRenderLiquidGlassPopup();
    }

    private void PreRenderLiquidGlassPopup()
    {
        try
        {
            var theme = new uWidgets.Core.Services.AppSettingsProvider().Get().Theme;
            if (theme.UsesRenderedGlass)
            {
                var (screenCenter, _) = GetScreenCenterAndTopLevel();
                var window = VisualRoot as Window;
                var screen = window?.Screens.ScreenFromWindow(window) ?? window?.Screens.Primary;
                bool isDark = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark || (theme.DarkMode ?? true);
                PopupLiquidGlassService.RequestPreRender(screenCenter, 440, 540, 18, theme, isDark, screen, window?.Screens.All);
            }
        }
        catch { }
    }

    public void OnOpenPopupClicked(object? sender, RoutedEventArgs e)
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = VisualRoot as Window;
        ClipboardPopupWindow.ShowPopup(screenCenter, owner);
    }

    private void OnOpenBadgePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            OnOpenPopupClicked(sender, e);
        }
    }

    private (Point? ScreenCenter, TopLevel? TopLevel) GetScreenCenterAndTopLevel()
    {
        if (VisualRoot is Visual rootVisual && VisualRoot is TopLevel topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, rootVisual);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }
}
