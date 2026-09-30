using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Tools.Models;
using Tools.Services;
using uWidgets.Core.Models.Settings;
using uWidgets.Core.Services;
using uWidgets.Services;
using uWidgets.Views;

namespace Tools.Views;

public partial class ClipboardPopupWindow : SecondaryPanelWindow
{
    private readonly ClipboardMonitorService monitor;
    private readonly Point? spawnScreenCenter;
    private string activeCategory = "All";
    private string searchQuery = string.Empty;

    protected override Visual? PanelCard => CardBorder;
    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public ClipboardPopupWindow() : this(null) { }

    public ClipboardPopupWindow(Point? screenCenter = null, double? cornerRadius = null)
    {
        spawnScreenCenter = screenCenter;
        monitor = ClipboardMonitorService.Instance;

        InitializeComponent();
        InitializePanel(cornerRadius);

        monitor.HistoryChanged += OnHistoryChanged;

        ApplyTheme();
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
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        SearchBox.Focus();
    }

    private void ApplyTheme()
    {
        Theme theme;
        try
        {
            theme = new AppSettingsProvider().Get().Theme;
        }
        catch
        {
            theme = new Theme(DarkMode: true, AccentColor: null, OpacityLevel: 0.8, Monochrome: false, UseNativeFrame: false, FontFamily: "Inter");
        }

        // Colour mode "follow system" is DarkMode == null, which must resolve to the *live*
        // variant — the same single source of truth the widget card and the theme preview use
        // (see ThemeButton.IsDark). Treating null as dark painted every panel dark on a light
        // system.
        bool isDark = theme.DarkMode ?? ActualThemeVariant == ThemeVariant.Dark;

        if (theme.UsesRenderedGlass)
        {
            // Rendered glass always goes through the live surface — the very same path the
            // primary widget card uses. The surface re-parameterises its optics for the panel's
            // open/close zoom (SetAnimationFrameScale), so the animation shows real glass per
            // tick whether or not live sampling is on: with sampling off the shared wallpaper
            // frame is merely frozen, which is still a full-quality backdrop.
            // The old "sampling off → play a pre-rendered bitmap / frame strip" branch is gone:
            // it lagged behind the surface, masked the animated glass with a static bitmap, and
            // fell back to a flat translucent scrim whenever the pre-render had not finished.
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassSurfaceControl.Material = theme;
            LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
            LiquidGlassSurfaceControl.IsVisible = true;
            CardBorder.Background = Brushes.Transparent;
            CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            var hex = isDark ? theme.EffectiveSolidBackgroundDark : theme.EffectiveSolidBackgroundLight;
            var baseColor = Color.TryParse(hex, out var parsed) ? parsed : (isDark ? Color.FromRgb(46, 46, 46) : Colors.White);
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 255), 40, 255);
            CardBorder.Background = new SolidColorBrush(Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(30, 0, 0, 0));
        }
        else // Acrylic
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur];
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        CloseButton.Foreground = subTextBrush;
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

        CountBadge.Text = $"{result.Count} 项";
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

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnCloseClicked(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LiquidGlassSurfaceControl.IsVisible = false;
        monitor.HistoryChanged -= OnHistoryChanged;
    }
}
