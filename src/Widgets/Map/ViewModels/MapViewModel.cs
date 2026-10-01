using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Map.Models;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Services;

namespace Map.ViewModels;

public class MapViewModel : INotifyPropertyChanged
{
    private MapModel model;
    private double currentLatitude;
    private double currentLongitude;
    private int currentZoom;
    private MapProvider currentProvider;
    private SurfaceStyle currentSurface = SurfaceStyle.Acrylic;
    private bool isDarkMode;
    private WidgetTier sizeTier = WidgetTier.Small;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MapViewModel(MapModel model)
    {
        this.model = model;
        currentLatitude = model.Latitude;
        currentLongitude = model.Longitude;
        currentZoom = model.ClampedZoom;
        currentProvider = model.Provider;
    }

    public MapModel Model => model;

    public void UpdateModel(MapModel newModel)
    {
        model = newModel;
        CurrentLatitude = newModel.Latitude;
        CurrentLongitude = newModel.Longitude;
        CurrentZoom = newModel.ClampedZoom;
        CurrentProvider = newModel.Provider;
        runtimeLocationName = null;
        runtimeLocationDescription = null;
        OnPropertyChanged(nameof(LocationName));
        OnPropertyChanged(nameof(LocationDescription));
        OnPropertyChanged(nameof(ShowPin));
        OnPropertyChanged(nameof(ShowInfoPill));
        OnPropertyChanged(nameof(ShowControls));
        OnPropertyChanged(nameof(ShowScaleBar));
        OnPropertyChanged(nameof(AllowMapDrag));
        OnPropertyChanged(nameof(BaiduApiKey));
    }

    public double CurrentLatitude
    {
        get => currentLatitude;
        set
        {
            if (Math.Abs(currentLatitude - value) > 0.000001)
            {
                currentLatitude = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CoordinatesDisplay));
            }
        }
    }

    public double CurrentLongitude
    {
        get => currentLongitude;
        set
        {
            if (Math.Abs(currentLongitude - value) > 0.000001)
            {
                currentLongitude = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CoordinatesDisplay));
            }
        }
    }

    public int CurrentZoom
    {
        get => currentZoom;
        set
        {
            var clamped = Math.Clamp(value, MapModel.MinZoom, MapModel.MaxZoom);
            if (currentZoom != clamped)
            {
                currentZoom = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ZoomDisplay));
            }
        }
    }

    public MapProvider CurrentProvider
    {
        get => currentProvider;
        set
        {
            if (currentProvider != value)
            {
                currentProvider = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProviderDisplayName));
            }
        }
    }

    private string? runtimeLocationName;
    public string LocationName
    {
        get => !string.IsNullOrWhiteSpace(runtimeLocationName)
            ? runtimeLocationName
            : (!string.IsNullOrWhiteSpace(model.LocationName) ? model.LocationName : "当前位置");
        set
        {
            if (runtimeLocationName != value)
            {
                runtimeLocationName = value;
                OnPropertyChanged();
            }
        }
    }

    private string? runtimeLocationDescription;
    public string LocationDescription
    {
        get => !string.IsNullOrWhiteSpace(runtimeLocationDescription)
            ? runtimeLocationDescription
            : (model.LocationDescription ?? "");
        set
        {
            if (runtimeLocationDescription != value)
            {
                runtimeLocationDescription = value;
                OnPropertyChanged();
            }
        }
    }
    public bool ShowPin => model.ShowPin;
    public bool ShowInfoPill => model.ShowInfoPill;
    public bool ShowControls => model.ShowControls;
    public bool ShowScaleBar => model.ShowScaleBar;
    public bool AllowMapDrag => model.AllowMapDrag;
    public string? BaiduApiKey => model.BaiduApiKey;

    public string CoordinatesDisplay =>
        $"{Math.Abs(CurrentLatitude):F4}°{(CurrentLatitude >= 0 ? "N" : "S")}, {Math.Abs(CurrentLongitude):F4}°{(CurrentLongitude >= 0 ? "E" : "W")}";

    public string ZoomDisplay => $"Z{CurrentZoom}";

    public string ProviderDisplayName => CurrentProvider switch
    {
        MapProvider.Amap => "高德地图",
        MapProvider.AmapSatellite => "高德卫星",
        MapProvider.Google => "Google Maps",
        MapProvider.GoogleSatellite => "Google 卫星",
        MapProvider.GoogleHybrid => "Google 混合",
        MapProvider.AppleLight => "iPad 浅色",
        MapProvider.AppleDark => "iPad 深色",
        MapProvider.Baidu => "百度地图",
        MapProvider.OpenStreetMap => "OSM 开源",
        _ => "地图"
    };

    // ---------- 4 种主题适配属性 ----------

    public SurfaceStyle Surface
    {
        get => currentSurface;
        set
        {
            if (currentSurface != value)
            {
                currentSurface = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsAcrylicTheme));
                OnPropertyChanged(nameof(IsLiquidGlassTheme));
                OnPropertyChanged(nameof(IsSolidTheme));
                OnPropertyChanged(nameof(IsVibrantTheme));
            }
        }
    }

    public bool IsDarkMode
    {
        get => isDarkMode;
        set
        {
            if (isDarkMode != value)
            {
                isDarkMode = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsAcrylicTheme => Surface is SurfaceStyle.Acrylic or SurfaceStyle.OutlinedAcrylic;
    public bool IsLiquidGlassTheme => Surface is SurfaceStyle.LiquidGlass or SurfaceStyle.SoftGlow or SurfaceStyle.LiquidGlassV2;
    public bool IsSolidTheme => Surface == SurfaceStyle.Solid;
    public bool IsVibrantTheme => Surface == SurfaceStyle.Colorful;

    // ---------- 尺寸分级适配 (2x2, 4x2, 4x4) ----------

    public WidgetTier SizeTier
    {
        get => sizeTier;
        set
        {
            if (sizeTier != value)
            {
                sizeTier = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSmallSize));
                OnPropertyChanged(nameof(IsMediumSize));
                OnPropertyChanged(nameof(IsLargeSize));
            }
        }
    }

    /// <summary>2×2 (Small square, ~180x180): minimal pin and top pill, compact buttons</summary>
    public bool IsSmallSize => SizeTier == WidgetTier.Small || SizeTier == WidgetTier.Cell;

    /// <summary>4×2 (Medium landscape, ~380x180): wide landscape map with left info card</summary>
    public bool IsMediumSize => SizeTier == WidgetTier.Medium;

    /// <summary>4×4 (Large square, ~380x380): full iPad map showcase with scale bar and rich controls</summary>
    public bool IsLargeSize => SizeTier == WidgetTier.Large || SizeTier == WidgetTier.Other;

    // ---------- 控制操作 ----------

    public void Recenter()
    {
        CurrentLatitude = model.Latitude;
        CurrentLongitude = model.Longitude;
        CurrentZoom = model.ClampedZoom;
        runtimeLocationName = null;
        runtimeLocationDescription = null;
        OnPropertyChanged(nameof(LocationName));
        OnPropertyChanged(nameof(LocationDescription));
    }

    public void ZoomIn()
    {
        CurrentZoom = Math.Min(CurrentZoom + 1, MapModel.MaxZoom);
    }

    public void ZoomOut()
    {
        CurrentZoom = Math.Max(CurrentZoom - 1, MapModel.MinZoom);
    }

    public void CycleProvider()
    {
        CurrentProvider = CurrentProvider switch
        {
            MapProvider.Amap => MapProvider.Google,
            MapProvider.Google => MapProvider.AppleLight,
            MapProvider.AppleLight => MapProvider.AppleDark,
            MapProvider.AppleDark => MapProvider.AmapSatellite,
            MapProvider.AmapSatellite => MapProvider.GoogleSatellite,
            MapProvider.GoogleSatellite => MapProvider.OpenStreetMap,
            MapProvider.OpenStreetMap => MapProvider.Baidu,
            MapProvider.Baidu => MapProvider.Amap,
            _ => MapProvider.Amap
        };
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
