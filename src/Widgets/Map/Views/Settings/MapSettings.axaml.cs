using System;
using System.Globalization;
using Avalonia.Controls;
using Map.Locales;
using Map.ViewModels.Settings;
using DeskCanvas.Core.Interfaces;

namespace Map.Views.Settings;

public partial class MapSettings : UserControl
{
    public MapSettings() : this(null!) { }

    public MapSettings(IWidgetLayoutProvider widgetLayoutProvider)
    {
        DataContext = new MapSettingsViewModel(widgetLayoutProvider);
        InitializeComponent();

        var isZh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);
        if (!isZh)
        {
            ProviderSetting.Title = Locale.Map_Provider;
            ProviderSetting.Subtitle = "Choose base map tile provider and visual style";

            PresetSetting.Title = Locale.Map_PresetLocations;
            PresetSetting.Subtitle = "Quickly jump to iconic cities or landmarks worldwide";

            AutoLocateSetting.Title = "Auto Network Location";
            AutoLocateSetting.Subtitle = "Automatically detect and locate your current city via network IP";
            LocateNowBtn.Content = "Locate Now";
            AutoLocateToggle.OnContent = "Auto-locate on startup";
            AutoLocateToggle.OffContent = "Manual preset";

            LocationNameSetting.Title = Locale.Map_LocationName;
            LocationNameSetting.Subtitle = "Main title displayed on the iPad-style card";

            LocationDescSetting.Title = Locale.Map_LocationDescription;
            LocationDescSetting.Subtitle = "Subtitle displayed on the floating info card";

            LatitudeSetting.Title = $"{Locale.Map_Latitude} (Latitude)";
            LatitudeSetting.Subtitle = "Map center latitude (-85.0 ~ 85.0)";

            LongitudeSetting.Title = $"{Locale.Map_Longitude} (Longitude)";
            LongitudeSetting.Subtitle = "Map center longitude (-180.0 ~ 180.0)";

            ZoomSetting.Title = Locale.Map_Zoom;
            ZoomSetting.Subtitle = "Level 3 (Global) ~ 18 (Street detail)";

            AllowDragSetting.Title = Locale.Map_AllowDrag;
            AllowDragSetting.Subtitle = "Allow panning the map directly by dragging with the mouse on the desktop";

            ShowPinSetting.Title = Locale.Map_ShowPin;
            ShowPinSetting.Subtitle = "Display an iPad-style red landmark pin at the center of the map";

            ShowInfoPillSetting.Title = Locale.Map_ShowInfoPill;
            ShowInfoPillSetting.Subtitle = "Show current place, coordinates, and zoom badge in the corner";

            ShowControlsSetting.Title = Locale.Map_ShowControls;
            ShowControlsSetting.Subtitle = "Show recenter, zoom in/out, and layer switch buttons on the right";

            ShowScaleBarSetting.Title = Locale.Map_ShowScaleBar;
            ShowScaleBarSetting.Subtitle = "Show Apple-style metric scale bar on medium and large widgets";

            BaiduKeySetting.Title = Locale.Map_BaiduKey;
            BaiduKeySetting.Subtitle = "Enter a developer API Key for official high-DPI Baidu tiles";
            BaiduKeyBox.Watermark = "Leave blank to use Amap/open-source fallback";
        }
    }

    private async void OnLocateNowClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is MapSettingsViewModel vm)
        {
            await vm.LocateNowAsync();
        }
    }
}
