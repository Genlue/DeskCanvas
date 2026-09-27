using System;
using System.ComponentModel;
using System.Diagnostics;
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
using uWidgets.Core.Models.Settings;
using uWidgets.Core.Services;
using uWidgets.Services;
using Weather.ViewModels;

namespace Weather.Views;

public partial class WeatherPopupWindow : Window
{
    private static WeatherPopupWindow? activePopup;
    private static DateTime lastCloseTime = DateTime.MinValue;

    private readonly ForecastViewModel viewModel;
    private readonly Point? spawnScreenCenter;
    private DateTime loadedTime = DateTime.MinValue;

    private ScaleTransform? ZoomTransform => CardBorder.RenderTransform as ScaleTransform;

    public WeatherPopupWindow() : this(new ForecastViewModel(new Models.ForecastModel("北京", 39.9042, 116.4074, "celsius")), null) { }

    public WeatherPopupWindow(ForecastViewModel viewModel, Point? screenCenter = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

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

        HourlyScroll.AddHandler(PointerWheelChangedEvent, OnHourlyWheel, RoutingStrategies.Bubble, true);

        BindData();
        ApplyTheme();

        PopupLiquidGlassService.PreRenderCompleted += OnPreRenderCompleted;
    }

    public static void ShowPopup(ForecastViewModel viewModel, Point? screenCenter, Window? owner = null)
    {
        if ((DateTime.UtcNow - lastCloseTime).TotalMilliseconds < 250)
            return;

        if (activePopup != null)
        {
            try { activePopup.Close(); } catch { }
            activePopup = null;
            return;
        }

        var popup = new WeatherPopupWindow(viewModel, screenCenter);
        activePopup = popup;

        if (owner != null)
            popup.Show(owner);
        else
            popup.Show();

        // Secondary panel: above the widget band, below ordinary application windows.
        WidgetZOrder.PinPanelAboveWidgets(popup);

        popup.Activate();
    }

    private void BindData()
    {
        CityText.Text = viewModel.CityName;
        TempText.Text = viewModel.CurrentTemperature;
        ConditionText.Text = viewModel.CurrentCondition;
        ConditionIcon.Data = viewModel.CurrentIcon;
        MinMaxText.Text = $"最高/最低: {viewModel.CurrentMinMax}";

        HourlyList.ItemsSource = viewModel.HourlyForecast;
        DailyList.ItemsSource = viewModel.DailyForecast;

        UVText.Text = $"{viewModel.UVIndex.Value:0.0}";
        UVLevelText.Text = viewModel.UVIndex.Value switch
        {
            < 3 => "弱 · 无需特殊防护",
            < 6 => "中等 · 建议涂防晒霜",
            < 8 => "高 · 建议遮阳伞与防晒",
            _ => "极高 · 尽量避免直晒"
        };

        PressureText.Text = $"{viewModel.Pressure.Value:0} hPa";
        SunText.Text = viewModel.SunsetSunrise.Time;
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
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1A2B42") : Color.Parse("#2B6CB0"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
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

        IBrush textBrush = (theme.IsColorful || isDark) ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = (theme.IsColorful || isDark) ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        Resources["WeatherForegroundBrush"] = textBrush;
        CityText.Foreground = textBrush;
        TempText.Foreground = textBrush;
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
            Debug.WriteLine($"[WeatherPopupWindow] TriggerDirectLiquidGlassRender failed: {ex.Message}");
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

    private void OnHourlyWheel(object? sender, PointerWheelEventArgs e)
    {
        var maxX = HourlyScroll.Extent.Width - HourlyScroll.Viewport.Width;
        if (maxX <= 0) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        HourlyScroll.Offset = new Vector(Math.Clamp(HourlyScroll.Offset.X - delta * 50, 0, maxX), HourlyScroll.Offset.Y);
        e.Handled = true;
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
        PopupLiquidGlassService.PreRenderCompleted -= OnPreRenderCompleted;
        if (activePopup == this)
            activePopup = null;
    }
}
