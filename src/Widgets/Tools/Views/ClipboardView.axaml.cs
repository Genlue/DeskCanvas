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
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Runtime;
using DeskCanvas.Services;

namespace Tools.Views;

public partial class ClipboardView : UserControl, IWidgetSelfRefreshing, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    public string? ColorfulBackgroundResourceKey => "ClipboardCardBackground";

    private readonly ClipboardMonitorService monitor;
    private ClipboardModel model;
    private readonly IWidgetSession? session;
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

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline: the view then relays the session's activity into the monitor's polling gate,
    /// so the shared 400 ms clipboard poll runs only while a clipboard card actually renders.
    /// Null keeps the legacy always-on behaviour (preview, legacy layout entries) — those
    /// hosts never push activity either.
    /// </summary>
    public ClipboardView(ClipboardModel model, IWidgetSession? session = null) : this(model, applySettingsToMonitor: true, session) { }

    private ClipboardView(ClipboardModel model, bool applySettingsToMonitor, IWidgetSession? session = null)
    {
        this.model = model;
        this.session = session;
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

        // Enter the monitor's polling gate: a session-gated view counts only while it renders,
        // a session-less view is an always-on consumer (the historic behaviour).
        monitor.AttachView(this, sessionGated: session != null, initialRenderActive: session?.Activity.RenderActive ?? true);

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

        // Leave the polling gate: with every gated view unloaded (and no always-on view left)
        // the monitor's 400 ms clipboard poll stops.
        monitor.DetachView(this);

        // Same teardown as TranslatorView: a toast caught mid-display would otherwise keep
        // this detached view alive (and hold its bindings) for up to the toast interval.
        toastTimer.Stop();
    }

    /// <summary>
    /// Host activity push (the <see cref="IWidgetActivityConsumer"/> path, invoked on the UI
    /// thread): the monitor's polling timer follows the snapshot's
    /// <see cref="ActivitySnapshot.RenderActive"/>. The relay only runs while the view is
    /// loaded — a teardown push racing the Unloaded detach must not re-enter the detached
    /// view into the monitor's gate sets. Session-less views ignore the push entirely (the
    /// host never sends one to them).
    /// </summary>
    public void OnSessionActivity(ActivitySnapshot activity)
    {
        if (session == null || !IsLoaded) return;
        monitor.OnViewRenderActivity(this, activity.RenderActive);
    }

    /// <summary>Check seam: the injected session (null on the legacy path).</summary>
    public IWidgetSession? SessionForChecks => session;

    /// <summary>Check seam: the monitor singleton this view pushes its render state into.</summary>
    public ClipboardMonitorService MonitorForChecks => monitor;

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
            "Text" => Locales.Locale.Tools_Clipboard_Text,
            "Image" => Locales.Locale.Tools_Clipboard_Image,
            "Files" => Locales.Locale.Tools_Clipboard_Files,
            _ => Locales.Locale.Tools_Clipboard_All
        };

        RefreshDisplay();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        monitor.ClearAll();
        ShowToast(Locales.Locale.Tools_Clipboard_HistoryCleared);
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
            ShowToast(Locales.Locale.Tools_Clipboard_Copied);
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBanner.IsVisible = true;
        toastTimer.Stop();
        toastTimer.Start();
    }

    /// <summary>
    /// The host widget's visual corner radius, so the popup's corners and its glass match the
    /// widget card exactly. Null keeps the popup's own default radius.
    /// </summary>
    private double? SpawnCornerRadius
    {
        get
        {
            var r = (TopLevel.GetTopLevel(this) as DeskCanvas.Views.Widget)?.Radius.TopLeft ?? 0;
            return r > 0 ? r : null;
        }
    }

    public void OnOpenPopupClicked(object? sender, RoutedEventArgs e)
    {
        var (screenCenter, _) = GetScreenCenterAndTopLevel();
        var owner = TopLevel.GetTopLevel(this) as Window;
        ClipboardPopupWindow.ShowPopup(screenCenter, owner, SpawnCornerRadius);
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
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            var bounds = Bounds;
            var centerLocal = new Point(bounds.Width / 2, bounds.Height / 2);
            var rootPoint = this.TranslatePoint(centerLocal, topLevel);
            if (rootPoint.HasValue)
            {
                var screenPoint = topLevel.PointToScreen(rootPoint.Value);
                return (new Point(screenPoint.X, screenPoint.Y), topLevel);
            }
        }
        return (null, null);
    }
}
