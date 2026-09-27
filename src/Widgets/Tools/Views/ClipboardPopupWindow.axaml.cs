using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
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

namespace Tools.Views;

public partial class ClipboardPopupWindow : Window
{
    private static ClipboardPopupWindow? activePopup;
    private static DateTime lastCloseTime = DateTime.MinValue;

    private readonly ClipboardMonitorService monitor;
    private readonly Point? spawnScreenCenter;
    private string activeCategory = "All";
    private string searchQuery = string.Empty;
    private DateTime loadedTime = DateTime.MinValue;

    private ScaleTransform? ZoomTransform => CardBorder.RenderTransform as ScaleTransform;

    public ClipboardPopupWindow() : this(null) { }

    public ClipboardPopupWindow(Point? screenCenter = null)
    {
        spawnScreenCenter = screenCenter;
        monitor = ClipboardMonitorService.Instance;

        InitializeComponent();

        CardBorder.Opacity = 0.0;
        if (ZoomTransform is { } t)
        {
            t.ScaleX = 0.90;
            t.ScaleY = 0.90;
        }

        Loaded += OnWindowLoaded;
        Deactivated += OnWindowDeactivated;
        Closing += OnWindowClosing;
        Closed += OnWindowClosed;
        KeyDown += OnWindowKeyDown;

        monitor.HistoryChanged += OnHistoryChanged;
        PopupLiquidGlassService.PreRenderCompleted += OnPreRenderCompleted;

        ApplyTheme();
        RefreshList();
    }

    public static void ShowPopup(Point? screenCenter, Window? owner = null)
    {
        if ((DateTime.UtcNow - lastCloseTime).TotalMilliseconds < 250)
            return;

        if (activePopup != null)
        {
            try { activePopup.Close(); } catch { }
            activePopup = null;
            return;
        }

        var popup = new ClipboardPopupWindow(screenCenter);
        activePopup = popup;

        if (owner != null)
            popup.Show(owner);
        else
            popup.Show();

        // Secondary panel: above the widget band, below ordinary application windows.
        WidgetZOrder.PinPanelAboveWidgets(popup);

        popup.Activate();
    }

    private void OnWindowLoaded(object? sender, RoutedEventArgs e)
    {
        loadedTime = DateTime.UtcNow;
        PositionWindow();
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
        PlayZoomInAnimation();
        SearchBox.Focus();
    }

    private void PositionWindow()
    {
        Screen? screen = null;
        if (spawnScreenCenter.HasValue)
        {
            screen = Screens.ScreenFromPoint(new PixelPoint(
                (int)Math.Round(spawnScreenCenter.Value.X),
                (int)Math.Round(spawnScreenCenter.Value.Y)));
        }
        screen ??= Screens.Primary;
        if (screen == null) return;

        double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
        double physWidth = Width * scale;
        double physHeight = Height * scale;

        double targetX;
        double targetY;

        if (spawnScreenCenter.HasValue)
        {
            targetX = spawnScreenCenter.Value.X - physWidth / 2.0;
            targetY = spawnScreenCenter.Value.Y - physHeight / 2.0;
        }
        else
        {
            targetX = screen.WorkingArea.X + (screen.WorkingArea.Width - physWidth) / 2.0;
            targetY = screen.WorkingArea.Y + (screen.WorkingArea.Height - physHeight) / 2.0;
        }

        var work = screen.WorkingArea;
        double margin = 16 * scale;
        targetX = Math.Clamp(targetX, work.X + margin, work.X + Math.Max(0, work.Width - physWidth - margin));
        targetY = Math.Clamp(targetY, work.Y + margin, work.Y + Math.Max(0, work.Height - physHeight - margin));

        Position = new PixelPoint((int)Math.Round(targetX), (int)Math.Round(targetY));
    }

    private void PlayZoomInAnimation()
    {
        CardBorder.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(200),
                Easing = new CubicEaseOut()
            }
        };

        if (ZoomTransform is { } transform)
        {
            transform.Transitions = new Transitions
            {
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleXProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new BackEaseOut()
                },
                new DoubleTransition
                {
                    Property = ScaleTransform.ScaleYProperty,
                    Duration = TimeSpan.FromMilliseconds(220),
                    Easing = new BackEaseOut()
                }
            };
            transform.ScaleX = 1.0;
            transform.ScaleY = 1.0;
        }

        CardBorder.Opacity = 1.0;
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

        bool isDark = ActualThemeVariant == ThemeVariant.Dark || (theme.DarkMode ?? true);

        if (theme.UsesRenderedGlass)
        {
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            if (LiquidGlassWallpaper.LiveSamplingEnabled)
            {
                LiquidGlassSurfaceControl.Material = theme;
                LiquidGlassSurfaceControl.CornerRadius = CardBorder.CornerRadius;
                LiquidGlassSurfaceControl.IsVisible = true;
                LiquidGlassBgImage.IsVisible = false;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
                LiquidGlassSurfaceControl.RequestRender(immediate: true);
            }
            else
            {
                LiquidGlassSurfaceControl.IsVisible = false;
                LiquidGlassBgImage.IsVisible = true;
                LiquidGlassOverlay.IsVisible = false;
                CardBorder.Background = Brushes.Transparent;
                CardBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));

                var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
                var bmp = PopupLiquidGlassService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);

                if (bmp != null)
                {
                    LiquidGlassBgImage.Source = bmp;
                }
                else
                {
                    CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(40, 28, 28, 32) : Color.FromArgb(40, 245, 245, 248));
                    _ = TriggerDirectLiquidGlassRender(theme, isDark, screen);
                }
            }
        }
        else if (theme.IsColorful)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1C1C1E") : Color.Parse("#FFFFFF"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 0, 0, 0));
        }
        else if (theme.EffectiveSurface == SurfaceStyle.Solid)
        {
            LiquidGlassSurfaceControl.IsVisible = false;
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
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
            LiquidGlassBgImage.IsVisible = false;
            LiquidGlassOverlay.IsVisible = false;
            byte alpha = (byte)Math.Clamp(Math.Round(theme.OpacityLevel * 220), 40, 240);
            CardBorder.Background = new SolidColorBrush(isDark ? Color.FromArgb(alpha, 28, 28, 32) : Color.FromArgb(alpha, 245, 245, 248));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(55, 255, 255, 255) : Color.FromArgb(35, 0, 0, 0));
        }

        IBrush subTextBrush = isDark ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        CloseButton.Foreground = subTextBrush;
    }

    private async Task TriggerDirectLiquidGlassRender(Theme theme, bool isDark, Screen? screen)
    {
        try
        {
            var bmp = await PopupLiquidGlassService.RenderDirectAsync(
                spawnScreenCenter,
                Width,
                Height,
                CardBorder.CornerRadius.TopLeft,
                theme,
                isDark,
                screen,
                Screens.All);

            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClipboardPopupWindow] TriggerDirectLiquidGlassRender failed: {ex.Message}");
        }
    }

    private void OnPreRenderCompleted()
    {
        if (LiquidGlassWallpaper.LiveSamplingEnabled) return;
        if (LiquidGlassBgImage.IsVisible)
        {
            var screen = spawnScreenCenter.HasValue ? Screens.ScreenFromPoint(new PixelPoint((int)spawnScreenCenter.Value.X, (int)spawnScreenCenter.Value.Y)) : Screens.Primary;
            var bmp = PopupLiquidGlassService.GetCachedBitmapFor(spawnScreenCenter, Width, Height, screen, Screens.All);
            if (bmp != null)
            {
                LiquidGlassBgImage.Source = bmp;
                CardBorder.Background = Brushes.Transparent;
            }
        }
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

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if ((DateTime.UtcNow - loadedTime).TotalMilliseconds > 300)
        {
            Close();
        }
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        lastCloseTime = DateTime.UtcNow;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LiquidGlassSurfaceControl.IsVisible = false;
        monitor.HistoryChanged -= OnHistoryChanged;
        PopupLiquidGlassService.PreRenderCompleted -= OnPreRenderCompleted;
        if (activePopup == this)
            activePopup = null;
    }
}
