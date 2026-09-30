using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using uWidgets.Core.Models.Settings;
using uWidgets.Core.Services;
using uWidgets.Services;
using uWidgets.Views;
using Weather.ViewModels;

namespace Weather.Views;

public partial class WeatherPopupWindow : SecondaryPanelWindow
{
    private readonly ForecastViewModel viewModel;
    private readonly Point? spawnScreenCenter;

    protected override Visual? PanelCard => CardBorder;
    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    public WeatherPopupWindow() : this(new ForecastViewModel(new Models.ForecastModel("北京", 39.9042, 116.4074, "celsius")), null) { }

    public WeatherPopupWindow(ForecastViewModel viewModel, Point? screenCenter = null, double? cornerRadius = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        HourlyScroll.AddHandler(PointerWheelChangedEvent, OnHourlyWheel, RoutingStrategies.Bubble, true);

        BindData();
        ApplyTheme();

        Closed += OnWindowClosed;
    }

    public static void ShowPopup(ForecastViewModel viewModel, Point? screenCenter, Window? owner = null, double? cornerRadius = null)
    {
        if (PanelCoolingDown<WeatherPopupWindow>())
            return;

        if (TryCloseActivePanel<WeatherPopupWindow>())
            return;

        var popup = new WeatherPopupWindow(viewModel, screenCenter, cornerRadius);
        popup.ShowAsSecondaryPanel(owner);
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

    protected override void OnPanelLoaded()
    {
        if (LiquidGlassSurfaceControl.IsVisible)
        {
            LiquidGlassSurfaceControl.RequestRender(immediate: true);
        }
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
            CardBorder.Background = new SolidColorBrush(isDark ? Color.Parse("#1A2B42") : Color.Parse("#2B6CB0"));
            CardBorder.BorderBrush = new SolidColorBrush(isDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(40, 255, 255, 255));
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

        IBrush textBrush = (theme.IsColorful || isDark) ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush subTextBrush = (theme.IsColorful || isDark) ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        Resources["WeatherForegroundBrush"] = textBrush;
        CityText.Foreground = textBrush;
        TempText.Foreground = textBrush;
        CloseButton.Foreground = subTextBrush;
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

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        LiquidGlassSurfaceControl.IsVisible = false;
    }
}
