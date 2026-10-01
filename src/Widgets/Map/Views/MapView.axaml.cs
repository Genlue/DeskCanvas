using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Map.Models;
using Map.Services;
using Map.ViewModels;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace Map.Views;

public partial class MapView : UserControl, IWidgetSelfRefreshing
{
    public static readonly StyledProperty<IBrush> ControlIconBrushProperty =
        AvaloniaProperty.Register<MapView, IBrush>(nameof(ControlIconBrush), Brushes.Black);

    public static readonly StyledProperty<IBrush> ProviderIconBrushProperty =
        AvaloniaProperty.Register<MapView, IBrush>(nameof(ProviderIconBrush), Brushes.Black);

    public static readonly StyledProperty<IBrush> PinIconBrushProperty =
        AvaloniaProperty.Register<MapView, IBrush>(nameof(PinIconBrush), new SolidColorBrush(Color.Parse("#FF3B30")));

    public IBrush ControlIconBrush
    {
        get => GetValue(ControlIconBrushProperty);
        set => SetValue(ControlIconBrushProperty, value);
    }

    public IBrush ProviderIconBrush
    {
        get => GetValue(ProviderIconBrushProperty);
        set => SetValue(ProviderIconBrushProperty, value);
    }

    public IBrush PinIconBrush
    {
        get => GetValue(PinIconBrushProperty);
        set => SetValue(PinIconBrushProperty, value);
    }

    public Border SmallPillControl => SmallPill;
    public Border MediumCardControl => MediumCard;
    public Border LargeCardControl => LargeCard;
    public Button RecenterButton => RecenterBtn;
    public Button ZoomInButton => ZoomInBtn;
    public Button ZoomOutButton => ZoomOutBtn;
    public Button LayerButton => LayerBtn;

    private readonly MapViewModel viewModel;
    private readonly IWidgetLayoutProvider? widgetLayoutProvider;
    private readonly IAppSettingsProvider? appSettingsProvider;
    private readonly MapTileService tileService;

    public MapView() : this(new MapModel(), null, null) { }

    public MapView(IWidgetLayoutProvider widgetLayoutProvider) : this(new MapModel(), widgetLayoutProvider, null) { }

    public MapView(MapModel model, IWidgetLayoutProvider? widgetLayoutProvider, IAppSettingsProvider? appSettingsProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.appSettingsProvider = appSettingsProvider;
        viewModel = new MapViewModel(model);
        DataContext = viewModel;

        tileService = new MapTileService();

        InitializeComponent();

        Canvas.AttachTileService(tileService);
        Canvas.CenterCoordinatesChanged += OnCanvasCoordinatesChanged;
        Canvas.ZoomChanged += OnCanvasZoomChanged;

        SizeChanged += OnSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        geocodeDebounceTimer.Tick += OnGeocodeDebounceTick;

        if (appSettingsProvider != null)
            appSettingsProvider.DataChanged += OnAppSettingsChanged;

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged += OnThemeVariantChanged;

        ApplyCurrentTheme();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        ApplySizeTier(Bounds.Size);
        ApplyCurrentTheme();

        if (viewModel.Model.AutoLocate)
        {
            AutoLocateAsync();
        }
    }

    private async void AutoLocateAsync()
    {
        try
        {
            var result = await GeoLocationService.GetCurrentLocationAsync();
            if (result.Success)
            {
                viewModel.CurrentLatitude = result.Latitude;
                viewModel.CurrentLongitude = result.Longitude;
                if (!string.IsNullOrWhiteSpace(result.City))
                {
                    viewModel.LocationName = result.City;
                }
                Canvas.InvalidateVisual();
            }
        }
        catch
        {
            // Ignore background auto-locate error
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplySizeTier(e.NewSize);
    }

    private void ApplySizeTier(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        var tier = SizeTiers.ResolveTier(this, size);
        viewModel.SizeTier = tier;
    }

    private void OnAppSettingsChanged(object? sender, AppSettings? oldSettings, AppSettings newSettings)
    {
        ApplyCurrentTheme();
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyCurrentTheme();
    }

    private void ApplyCurrentTheme()
    {
        var settings = appSettingsProvider?.Get();
        var theme = settings?.Theme;
        var surface = theme?.EffectiveSurface ?? SurfaceStyle.Acrylic;

        bool isDark;
        if (theme != null)
        {
            if (theme.AutoTheme)
            {
                isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
            }
            else if (theme.DarkMode.HasValue)
            {
                isDark = theme.DarkMode.Value;
            }
            else
            {
                isDark = (ActualThemeVariant == ThemeVariant.Dark)
                         || (Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
            }
        }
        else
        {
            isDark = (ActualThemeVariant == ThemeVariant.Dark)
                     || (Application.Current?.ActualThemeVariant == ThemeVariant.Dark);
        }

        viewModel.Surface = surface;
        viewModel.IsDarkMode = isDark;
        Canvas.IsDarkMode = isDark;

        if (isDark)
        {
            Classes.Add("DarkMode");
            SmallPill.Classes.Add("DarkMode");
            MediumCard.Classes.Add("DarkMode");
            LargeCard.Classes.Add("DarkMode");
            LayerBtn.Classes.Add("DarkMode");
            RecenterBtn.Classes.Add("DarkMode");
            ZoomInBtn.Classes.Add("DarkMode");
            ZoomOutBtn.Classes.Add("DarkMode");
        }
        else
        {
            Classes.Remove("DarkMode");
            SmallPill.Classes.Remove("DarkMode");
            MediumCard.Classes.Remove("DarkMode");
            LargeCard.Classes.Remove("DarkMode");
            LayerBtn.Classes.Remove("DarkMode");
            RecenterBtn.Classes.Remove("DarkMode");
            ZoomInBtn.Classes.Remove("DarkMode");
            ZoomOutBtn.Classes.Remove("DarkMode");
        }

        // 4 种主题卡片与悬浮按钮样式类切换（柔光玻璃与液态玻璃同属自绘材质，共用透明卡片样式）
        string themeClass = surface switch
        {
            SurfaceStyle.LiquidGlass or SurfaceStyle.SoftGlow => "LiquidGlass",
            SurfaceStyle.Solid => "Solid",
            SurfaceStyle.Colorful => "Vibrant",
            _ => "Acrylic"
        };

        UpdateCardTheme(SmallPill, themeClass);
        UpdateCardTheme(MediumCard, themeClass);
        UpdateCardTheme(LargeCard, themeClass);

        UpdateButtonTheme(LayerBtn, themeClass);
        UpdateButtonTheme(RecenterBtn, themeClass);
        UpdateButtonTheme(ZoomInBtn, themeClass);
        UpdateButtonTheme(ZoomOutBtn, themeClass);

        // 图标颜色：多彩模式使用纯正 Apple Blue；其他模式使用根据明暗自适应黑白
        if (surface == SurfaceStyle.Colorful)
        {
            var appleBlue = new SolidColorBrush(Color.Parse(isDark ? "#0A84FF" : "#007AFF"));
            ControlIconBrush = appleBlue;
            ProviderIconBrush = appleBlue;
            PinIconBrush = appleBlue;
        }
        else
        {
            var defaultBrush = isDark ? new SolidColorBrush(Color.Parse("#FFFFFF")) : new SolidColorBrush(Color.Parse("#1C1C1E"));
            ControlIconBrush = defaultBrush;
            ProviderIconBrush = defaultBrush;
            PinIconBrush = new SolidColorBrush(Color.Parse("#FF3B30"));
        }

        Canvas.InvalidateVisual();
    }

    private static void UpdateCardTheme(Border card, string themeClass)
    {
        card.Classes.Remove("Acrylic");
        card.Classes.Remove("LiquidGlass");
        card.Classes.Remove("Solid");
        card.Classes.Remove("Vibrant");
        card.Classes.Add(themeClass);
    }

    private static void UpdateButtonTheme(Button btn, string themeClass)
    {
        btn.Classes.Remove("Acrylic");
        btn.Classes.Remove("LiquidGlass");
        btn.Classes.Remove("Solid");
        btn.Classes.Remove("Vibrant");
        btn.Classes.Add(themeClass);
    }

    private readonly DispatcherTimer geocodeDebounceTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private System.Threading.CancellationTokenSource? geocodeCts;

    private void OnCanvasCoordinatesChanged(double lat, double lon)
    {
        viewModel.CurrentLatitude = lat;
        viewModel.CurrentLongitude = lon;

        // 1. 实时 0ms 零延迟本地粗略地名更新 (拖拽地图时 60fps 实时改变信息卡片地区)
        var (coarseName, coarseDesc) = GeoLocationService.ResolveCoarseRegion(lat, lon);
        viewModel.LocationName = coarseName;
        viewModel.LocationDescription = coarseDesc ?? viewModel.CoordinatesDisplay;

        // 2. 重置防抖计时器，拖动悬停或松手 250ms 后异步执行精细逆地理编码精细化地名与街道
        geocodeDebounceTimer.Stop();
        geocodeDebounceTimer.Start();
    }

    private async void OnGeocodeDebounceTick(object? sender, EventArgs e)
    {
        geocodeDebounceTimer.Stop();
        geocodeCts?.Cancel();
        geocodeCts = new System.Threading.CancellationTokenSource();

        var lat = viewModel.CurrentLatitude;
        var lon = viewModel.CurrentLongitude;

        try
        {
            var (name, desc) = await GeoLocationService.ReverseGeocodeAsync(lat, lon, geocodeCts.Token);
            if (Math.Abs(viewModel.CurrentLatitude - lat) < 0.005 &&
                Math.Abs(viewModel.CurrentLongitude - lon) < 0.005)
            {
                if (!string.IsNullOrWhiteSpace(name))
                    viewModel.LocationName = name;
                if (!string.IsNullOrWhiteSpace(desc))
                    viewModel.LocationDescription = desc;
            }
        }
        catch
        {
            // Ignore geocoding errors
        }
    }

    private void OnCanvasZoomChanged(int zoom)
    {
        viewModel.CurrentZoom = zoom;
    }

    // ---------- 按钮点击交互 ----------

    private void OnRecenterClicked(object? sender, RoutedEventArgs e)
    {
        if (viewModel.Model.AutoLocate)
        {
            AutoLocateAsync();
        }
        else
        {
            viewModel.Recenter();
            Canvas.InvalidateVisual();
        }
        e.Handled = true;
    }

    private void OnZoomInClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.ZoomIn();
        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    private void OnZoomOutClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.ZoomOut();
        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    private void OnCycleProviderClicked(object? sender, RoutedEventArgs e)
    {
        viewModel.CycleProvider();
        Canvas.InvalidateVisual();
        e.Handled = true;
    }

    // ---------- IWidgetSelfRefreshing 契约 ----------

    public void Refresh(WidgetLayout layout)
    {
        var newModel = layout.GetModel<MapModel>();
        if (newModel == null) return;

        viewModel.UpdateModel(newModel);
        Canvas.InvalidateVisual();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        SizeChanged -= OnSizeChanged;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;

        Canvas.CenterCoordinatesChanged -= OnCanvasCoordinatesChanged;
        Canvas.ZoomChanged -= OnCanvasZoomChanged;

        if (appSettingsProvider != null)
            appSettingsProvider.DataChanged -= OnAppSettingsChanged;

        if (Application.Current != null)
            Application.Current.ActualThemeVariantChanged -= OnThemeVariantChanged;

        geocodeDebounceTimer.Stop();
        geocodeDebounceTimer.Tick -= OnGeocodeDebounceTick;
        geocodeCts?.Cancel();

        tileService.Dispose();
    }
}
