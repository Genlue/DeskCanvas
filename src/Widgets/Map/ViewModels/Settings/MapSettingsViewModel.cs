using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Map.Models;
using DeskCanvas.Core.Interfaces;

namespace Map.ViewModels.Settings;

public record PresetCity(string Name, double Latitude, double Longitude, int Zoom, string Description);

public record ProviderOption(MapProvider Provider, string DisplayName);

public class MapSettingsViewModel : INotifyPropertyChanged
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private MapModel model;

    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly IReadOnlyList<PresetCity> PresetCities =
    [
        new("自定义 (手动经纬度)", 39.9042, 116.4074, 14, ""),
        new("北京 · 故宫博物院", 39.9163, 116.3972, 15, "北京市东城区景山前街4号"),
        new("上海 · 外滩观景台", 31.2397, 121.4998, 15, "上海市黄浦区中山东一路"),
        new("广州 · 广州塔 (小蛮腰)", 23.1065, 113.3245, 15, "广州市海珠区阅江西路222号"),
        new("深圳 · 市民中心", 22.5431, 114.0579, 15, "深圳市福田区福中三路"),
        new("杭州 · 西湖断桥残雪", 30.2588, 120.1510, 14, "杭州市西湖区白堤东端"),
        new("成都 · 春熙路太古里", 30.6558, 104.0818, 15, "成都市锦江区中纱帽街8号"),
        new("香港 · 维多利亚港", 22.2933, 114.1717, 14, "香港九龙尖沙咀海滨长廊"),
        new("东京 · 东京铁塔", 35.6586, 139.7454, 15, "东京都港区芝公园4-2-8"),
        new("伦敦 · 大本钟 & 泰晤士河", 51.5007, -0.1246, 15, "Westminster, London SW1A 0AA"),
        new("纽约 · 时代广场", 40.7580, -73.9855, 15, "Manhattan, NY 10036"),
        new("巴黎 · 埃菲尔铁塔", 48.8584, 2.2945, 15, "Champ de Mars, 75007 Paris")
    ];

    public static readonly IReadOnlyList<ProviderOption> ProviderOptions =
    [
        new(MapProvider.Amap, "高德地图 (标准路网 · 推荐)"),
        new(MapProvider.AmapSatellite, "高德地图 (卫星影像)"),
        new(MapProvider.AppleLight, "iPad 苹果风格 (CartoDB 极简浅色)"),
        new(MapProvider.AppleDark, "iPad 苹果深色 (CartoDB 极简暗黑)"),
        new(MapProvider.Google, "Google Maps (谷歌街道)"),
        new(MapProvider.GoogleSatellite, "Google Maps (谷歌卫星)"),
        new(MapProvider.GoogleHybrid, "Google Maps (谷歌混合)"),
        new(MapProvider.OpenStreetMap, "OpenStreetMap (开源标准)"),
        new(MapProvider.Baidu, "百度地图 (Baidu Maps)")
    ];

    private PresetCity selectedCity;
    private ProviderOption selectedProviderOption;

    public MapSettingsViewModel(IWidgetLayoutProvider widgetLayoutProvider)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        var layout = widgetLayoutProvider.Get();
        model = layout?.GetModel<MapModel>() ?? new MapModel();

        selectedProviderOption = ProviderOptions.FirstOrDefault(p => p.Provider == model.Provider) ?? ProviderOptions[0];

        // Match existing coordinates to preset city if close
        selectedCity = PresetCities.FirstOrDefault(c =>
            Math.Abs(c.Latitude - model.Latitude) < 0.005 &&
            Math.Abs(c.Longitude - model.Longitude) < 0.005) ?? PresetCities[0];
    }

    public IReadOnlyList<PresetCity> Cities => PresetCities;
    public IReadOnlyList<ProviderOption> Providers => ProviderOptions;

    public PresetCity SelectedCity
    {
        get => selectedCity;
        set
        {
            if (selectedCity != value && value != null)
            {
                selectedCity = value;
                OnPropertyChanged();

                if (value.Latitude != 0 && value.Name != PresetCities[0].Name)
                {
                    Latitude = value.Latitude;
                    Longitude = value.Longitude;
                    Zoom = value.Zoom;
                    LocationName = value.Name;
                    LocationDescription = value.Description;
                }
            }
        }
    }

    public ProviderOption SelectedProviderOption
    {
        get => selectedProviderOption;
        set
        {
            if (selectedProviderOption != value && value != null)
            {
                selectedProviderOption = value;
                OnPropertyChanged();
                Provider = value.Provider;
            }
        }
    }

    public MapProvider Provider
    {
        get => model.Provider;
        set
        {
            if (model.Provider != value)
            {
                model = model with { Provider = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public double Latitude
    {
        get => model.Latitude;
        set
        {
            if (Math.Abs(model.Latitude - value) > 0.0001)
            {
                model = model with { Latitude = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public double Longitude
    {
        get => model.Longitude;
        set
        {
            if (Math.Abs(model.Longitude - value) > 0.0001)
            {
                model = model with { Longitude = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public int Zoom
    {
        get => model.Zoom;
        set
        {
            var clamped = Math.Clamp(value, MapModel.MinZoom, MapModel.MaxZoom);
            if (model.Zoom != clamped)
            {
                model = model with { Zoom = clamped };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public string LocationName
    {
        get => model.LocationName ?? "";
        set
        {
            if (model.LocationName != value)
            {
                model = model with { LocationName = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public string LocationDescription
    {
        get => model.LocationDescription ?? "";
        set
        {
            if (model.LocationDescription != value)
            {
                model = model with { LocationDescription = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool ShowPin
    {
        get => model.ShowPin;
        set
        {
            if (model.ShowPin != value)
            {
                model = model with { ShowPin = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool ShowInfoPill
    {
        get => model.ShowInfoPill;
        set
        {
            if (model.ShowInfoPill != value)
            {
                model = model with { ShowInfoPill = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool ShowControls
    {
        get => model.ShowControls;
        set
        {
            if (model.ShowControls != value)
            {
                model = model with { ShowControls = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool ShowScaleBar
    {
        get => model.ShowScaleBar;
        set
        {
            if (model.ShowScaleBar != value)
            {
                model = model with { ShowScaleBar = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool AllowMapDrag
    {
        get => model.AllowMapDrag;
        set
        {
            if (model.AllowMapDrag != value)
            {
                model = model with { AllowMapDrag = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public string BaiduApiKey
    {
        get => model.BaiduApiKey ?? "";
        set
        {
            if (model.BaiduApiKey != value)
            {
                model = model with { BaiduApiKey = string.IsNullOrWhiteSpace(value) ? null : value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    public bool AutoLocate
    {
        get => model.AutoLocate;
        set
        {
            if (model.AutoLocate != value)
            {
                model = model with { AutoLocate = value };
                OnPropertyChanged();
                Save();
            }
        }
    }

    private string? locationStatusText;
    public string? LocationStatusText
    {
        get => locationStatusText;
        set
        {
            locationStatusText = value;
            OnPropertyChanged();
        }
    }

    private bool isLocating;
    public bool IsLocating
    {
        get => isLocating;
        set
        {
            isLocating = value;
            OnPropertyChanged();
        }
    }

    public async System.Threading.Tasks.Task LocateNowAsync()
    {
        if (IsLocating) return;
        IsLocating = true;
        LocationStatusText = "正在获取网络定位...";
        try
        {
            var result = await Map.Services.GeoLocationService.GetCurrentLocationAsync();
            if (result.Success)
            {
                Latitude = result.Latitude;
                Longitude = result.Longitude;
                if (!string.IsNullOrWhiteSpace(result.City))
                {
                    LocationName = result.City;
                    model = model with { LocatedCity = result.City };
                }
                if (!string.IsNullOrWhiteSpace(result.FormattedAddress))
                {
                    LocationDescription = result.FormattedAddress;
                }
                LocationStatusText = $"已定位至：{result.City ?? "当前位置"} ({result.Latitude:F4}, {result.Longitude:F4})";
                Save();
            }
            else
            {
                LocationStatusText = result.ErrorMessage ?? "定位失败";
            }
        }
        catch (Exception ex)
        {
            LocationStatusText = $"定位出错: {ex.Message}";
        }
        finally
        {
            IsLocating = false;
        }
    }

    private void Save()
    {
        var currentLayout = widgetLayoutProvider?.Get();
        if (currentLayout == null) return;

        var element = JsonSerializer.SerializeToElement(model);
        var newLayout = currentLayout with { Settings = element };
        widgetLayoutProvider?.Save(newLayout);
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
