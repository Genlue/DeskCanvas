using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Services;
using DeskCanvas.Views;
using DeskCanvas.Views.Controls;
using Weather.ViewModels;

namespace Weather.Views;

/// <summary>
/// The weather widget's secondary panel (hourly strip, 7-day forecast, environment metrics).
/// The shell (W7) plus <see cref="SecondaryPanelWindow"/> and
/// <see cref="PanelAppearanceResolver"/> own the card material, the chrome and the open/close
/// transitions. This class keeps the data binding, the horizontal wheel scrolling of the hourly
/// strip, and the plugin-owned palette: the weather text brushes — and, in the 多彩 theme, the
/// weather-blue card colour that differs from the shared macOS baseline — are asserted on top of
/// every shell paint refresh, never computed by the shared resolver.
/// </summary>
public partial class WeatherPopupWindow : SecondaryPanelWindow
{
    private readonly ForecastViewModel viewModel;
    private readonly Point? spawnScreenCenter;
    private IAppSettingsProvider? settingsProvider;

    protected override Point? SpawnScreenCenter => spawnScreenCenter;

    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    public WeatherPopupWindow() : this(new ForecastViewModel(Services.DefaultLocation.Resolve(fallbackName: IsZh ? "北京" : "Beijing")), null) { }

    public WeatherPopupWindow(ForecastViewModel viewModel, Point? screenCenter = null, double? cornerRadius = null)
    {
        this.viewModel = viewModel;
        spawnScreenCenter = screenCenter;

        InitializeComponent();
        InitializePanel(cornerRadius);

        HourlyScroll.AddHandler(PointerWheelChangedEvent, OnHourlyWheel, RoutingStrategies.Bubble, true);

        BindData();

        // Business theming. The shell's card paint is written by the appearance binder — which
        // also refreshes live on settings/variant changes — so the plugin-owned palette
        // re-asserts itself on every shell paint change instead of only once at construction.
        if (PanelShell is { } shell)
        {
            shell.PropertyChanged += OnShellPaintChanged;
            ApplyContentTheme(shell);
        }
        Closed += OnWindowClosed;
    }

    public static void ShowPopup(ForecastViewModel viewModel, Point? screenCenter, Window? owner = null, double? cornerRadius = null)
    {
        // PerOwner: every weather widget owns its own panel — two weather components no longer
        // evict each other's panel. The service handles cooldown, toggle-close and the pinned
        // show; the decision (mirroring the old early returns) is deliberately ignored.
        PanelService.Open(new PanelRequest(
            owner is Widget widget ? widget.InstanceId : default,
            owner,
            "weather",
            PanelScope.PerOwner,
            () => new WeatherPopupWindow(viewModel, screenCenter, cornerRadius)));
    }

    private void BindData()
    {
        CityText.Text = viewModel.CityName;
        TempText.Text = viewModel.CurrentTemperature;
        ConditionText.Text = viewModel.CurrentCondition;
        ConditionIcon.Data = viewModel.CurrentIcon;
        MinMaxText.Text = IsZh ? $"最高/最低: {viewModel.CurrentMinMax}" : $"High/Low: {viewModel.CurrentMinMax}";

        HourlyList.ItemsSource = viewModel.HourlyForecast;
        DailyList.ItemsSource = viewModel.DailyForecast;

        UVText.Text = $"{viewModel.UVIndex.Value:0.0}";
        UVLevelText.Text = viewModel.UVIndex.Value switch
        {
            < 3 => IsZh ? "弱 · 无需特殊防护" : "Low · No protection needed",
            < 6 => IsZh ? "中等 · 建议涂防晒霜" : "Moderate · Sunscreen advised",
            < 8 => IsZh ? "高 · 建议遮阳伞与防晒" : "High · Shade & sunscreen advised",
            _ => IsZh ? "极高 · 尽量避免直晒" : "Very High · Avoid direct sun"
        };

        PressureText.Text = $"{viewModel.Pressure.Value:0} hPa";
        SunText.Text = viewModel.SunsetSunrise.Time;
    }

    // ---------- theme ----------

    private void OnShellPaintChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // Every binder Refresh assigns a fresh PanelAppearance record, so this fires on each
        // settings/variant/focus-state change — including material-only swaps where the card
        // brushes stay value-equal (透明→透明) and a CardBackground listener would miss.
        if (e.Property == SecondaryPanelShell.SurfaceAppearanceProperty)
        {
            ApplyContentTheme(PanelShell);
        }
    }

    /// <summary>The plugin-owned palette. Content text follows the historic weather rule
    /// (多彩 keeps white-on-blue regardless of the variant); in the 多彩 theme the card's
    /// weather blue is asserted over the shell baseline. The compare-before-write guard keeps
    /// the PropertyChanged feedback from looping. The theme is the panel's EFFECTIVE one —
    /// the focus-state theme while 失焦自动切换 is live, the shared theme otherwise — resolved
    /// through the same entry point the appearance binder uses, so the content and the shell
    /// can never paint different states.</summary>
    private void ApplyContentTheme(SecondaryPanelShell? shell)
    {
        var theme = PanelAppearanceResolver.ResolveEffectiveTheme(settingsProvider ??= PanelAppearanceResolver.TryResolveSettingsProvider());
        bool isDark = PanelAppearanceResolver.IsDark(theme, ActualThemeVariant);

        IBrush fallbackText = (theme.IsColorful || isDark) ? Brushes.White : new SolidColorBrush(Color.FromRgb(30, 30, 30));
        IBrush textBrush = theme.Monochrome
            ? ThemeScopeResolver.ResolveBrush(this, "WeatherForegroundBrush", ActualThemeVariant, fallbackText)
            : fallbackText;
        IBrush subTextBrush = (theme.IsColorful || isDark) ? new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)) : new SolidColorBrush(Color.FromArgb(180, 0, 0, 0));
        Resources["WeatherForegroundBrush"] = textBrush;
        CityText.Foreground = textBrush;
        TempText.Foreground = textBrush;

        if (theme.IsColorful && shell != null)
        {
            SetShellBrush(shell, SecondaryPanelShell.CardBackgroundProperty,
                isDark ? Color.Parse("#1A2B42") : Color.Parse("#2B6CB0"));
            SetShellBrush(shell, SecondaryPanelShell.CardBorderBrushProperty,
                Color.FromArgb((byte)(isDark ? 60 : 40), 255, 255, 255));
        }
    }

    private static void SetShellBrush(SecondaryPanelShell shell, StyledProperty<IBrush?> property, Color color)
    {
        if (shell.GetValue(property) is ISolidColorBrush current && current.Color == color) return;
        shell.SetValue(property, new SolidColorBrush(color));
    }

    // ---------- interaction ----------

    private void OnHourlyWheel(object? sender, PointerWheelEventArgs e)
    {
        var maxX = HourlyScroll.Extent.Width - HourlyScroll.Viewport.Width;
        if (maxX <= 0) return;
        var delta = e.Delta.Y;
        if (delta == 0) return;
        HourlyScroll.Offset = new Vector(Math.Clamp(HourlyScroll.Offset.X - delta * 50, 0, maxX), HourlyScroll.Offset.Y);
        e.Handled = true;
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        if (PanelShell is { } shell)
            shell.PropertyChanged -= OnShellPaintChanged;
    }
}
