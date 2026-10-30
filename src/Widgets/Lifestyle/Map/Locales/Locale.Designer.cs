namespace Map.Locales {
    using System;
    
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    public class Locale {
        
        private static global::System.Resources.ResourceManager resourceMan;
        private static global::System.Globalization.CultureInfo resourceCulture;
        
        [global::System.Diagnostics.CodeAnalysis.SuppressMessageAttribute("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode")]
        internal Locale() {
        }
        
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Resources.ResourceManager ResourceManager {
            get {
                if (object.ReferenceEquals(resourceMan, null)) {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Map.Locales.Locale", typeof(Locale).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }
        
        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Globalization.CultureInfo Culture {
            get {
                return resourceCulture;
            }
            set {
                resourceCulture = value;
            }
        }
        
        public static string Map => ResourceManager.GetString("Map", resourceCulture) ?? "地图";
        public static string Map_Title => ResourceManager.GetString("Map_Title", resourceCulture) ?? "地图";
        public static string Map_Subtitle => ResourceManager.GetString("Map_Subtitle", resourceCulture) ?? "macOS / iPad 风格多图源桌面地图小组件";
        public static string Map_Provider => ResourceManager.GetString("Map_Provider", resourceCulture) ?? "地图图源";
        public static string Map_PresetLocations => ResourceManager.GetString("Map_PresetLocations", resourceCulture) ?? "预设城市";
        public static string Map_LocationName => ResourceManager.GetString("Map_LocationName", resourceCulture) ?? "地点名称";
        public static string Map_LocationDescription => ResourceManager.GetString("Map_LocationDescription", resourceCulture) ?? "详细地址 / 描述";
        public static string Map_Latitude => ResourceManager.GetString("Map_Latitude", resourceCulture) ?? "纬度";
        public static string Map_Longitude => ResourceManager.GetString("Map_Longitude", resourceCulture) ?? "经度";
        public static string Map_Zoom => ResourceManager.GetString("Map_Zoom", resourceCulture) ?? "默认缩放级别";
        public static string Map_ShowPin => ResourceManager.GetString("Map_ShowPin", resourceCulture) ?? "显示中心大头针";
        public static string Map_ShowInfoPill => ResourceManager.GetString("Map_ShowInfoPill", resourceCulture) ?? "显示浮动信息卡";
        public static string Map_ShowControls => ResourceManager.GetString("Map_ShowControls", resourceCulture) ?? "显示悬浮控制按钮";
        public static string Map_ShowScaleBar => ResourceManager.GetString("Map_ShowScaleBar", resourceCulture) ?? "显示比例尺";
        public static string Map_AllowDrag => ResourceManager.GetString("Map_AllowDrag", resourceCulture) ?? "允许鼠标拖拽平移";
        public static string Map_BaiduKey => ResourceManager.GetString("Map_BaiduKey", resourceCulture) ?? "百度地图 AK (可选)";
        public static string Map_RecenterTooltip => ResourceManager.GetString("Map_RecenterTooltip", resourceCulture) ?? "复位至预设中心";
        public static string Map_ZoomInTooltip => ResourceManager.GetString("Map_ZoomInTooltip", resourceCulture) ?? "放大";
        public static string Map_ZoomOutTooltip => ResourceManager.GetString("Map_ZoomOutTooltip", resourceCulture) ?? "缩小";
        public static string Map_LayerTooltip => ResourceManager.GetString("Map_LayerTooltip", resourceCulture) ?? "切换图源";
        public static string Map_Provider_Subtitle => ResourceManager.GetString("Map_Provider_Subtitle", resourceCulture) ?? "Choose base map tile provider and visual style";
        public static string Map_PresetSetting => ResourceManager.GetString("Map_PresetSetting", resourceCulture) ?? "Preset Landmarks";
        public static string Map_PresetSetting_Subtitle => ResourceManager.GetString("Map_PresetSetting_Subtitle", resourceCulture) ?? "Quickly jump to iconic cities or landmarks worldwide";
        public static string Map_AutoLocate => ResourceManager.GetString("Map_AutoLocate", resourceCulture) ?? "Auto Network Location";
        public static string Map_AutoLocate_Subtitle => ResourceManager.GetString("Map_AutoLocate_Subtitle", resourceCulture) ?? "Automatically detect and locate your current city via network IP";
        public static string Map_LocateNow => ResourceManager.GetString("Map_LocateNow", resourceCulture) ?? "Locate Now";
        public static string Map_AutoLocateOn => ResourceManager.GetString("Map_AutoLocateOn", resourceCulture) ?? "Auto-locate on startup";
        public static string Map_AutoLocateOff => ResourceManager.GetString("Map_AutoLocateOff", resourceCulture) ?? "Manual preset";
        public static string Map_LocationNameSetting => ResourceManager.GetString("Map_LocationNameSetting", resourceCulture) ?? "Location Title";
        public static string Map_LocationNameSetting_Subtitle => ResourceManager.GetString("Map_LocationNameSetting_Subtitle", resourceCulture) ?? "Main title displayed on the iPad-style card";
        public static string Map_LocationDesc_Subtitle => ResourceManager.GetString("Map_LocationDesc_Subtitle", resourceCulture) ?? "Subtitle displayed on the floating info card";
        public static string Map_LatitudeSetting => ResourceManager.GetString("Map_LatitudeSetting", resourceCulture) ?? "Latitude";
        public static string Map_LatitudeSetting_Subtitle => ResourceManager.GetString("Map_LatitudeSetting_Subtitle", resourceCulture) ?? "Map center latitude (-85.0 ~ 85.0)";
        public static string Map_LongitudeSetting => ResourceManager.GetString("Map_LongitudeSetting", resourceCulture) ?? "Longitude";
        public static string Map_LongitudeSetting_Subtitle => ResourceManager.GetString("Map_LongitudeSetting_Subtitle", resourceCulture) ?? "Map center longitude (-180.0 ~ 180.0)";
        public static string Map_ZoomSetting => ResourceManager.GetString("Map_ZoomSetting", resourceCulture) ?? "Default Zoom Level";
        public static string Map_ZoomSetting_Subtitle => ResourceManager.GetString("Map_ZoomSetting_Subtitle", resourceCulture) ?? "Level 3 (Global) ~ 18 (Street detail)";
        public static string Map_AllowDragSetting => ResourceManager.GetString("Map_AllowDragSetting", resourceCulture) ?? "Drag to Pan on Desktop";
        public static string Map_AllowDragSetting_Subtitle => ResourceManager.GetString("Map_AllowDragSetting_Subtitle", resourceCulture) ?? "Allow panning the map view directly by dragging with the mouse on the desktop";
        public static string Map_ShowPinSetting => ResourceManager.GetString("Map_ShowPinSetting", resourceCulture) ?? "Center Pin";
        public static string Map_ShowPinSetting_Subtitle => ResourceManager.GetString("Map_ShowPinSetting_Subtitle", resourceCulture) ?? "Display an iPad-style red landmark pin at the center of the map";
        public static string Map_ShowInfoPillSetting => ResourceManager.GetString("Map_ShowInfoPillSetting", resourceCulture) ?? "Floating Info Card";
        public static string Map_ShowInfoPillSetting_Subtitle => ResourceManager.GetString("Map_ShowInfoPillSetting_Subtitle", resourceCulture) ?? "Show current place, coordinates, and zoom badge in the corner";
        public static string Map_ShowControlsSetting => ResourceManager.GetString("Map_ShowControlsSetting", resourceCulture) ?? "Floating Controls";
        public static string Map_ShowControlsSetting_Subtitle => ResourceManager.GetString("Map_ShowControlsSetting_Subtitle", resourceCulture) ?? "Show recenter, zoom in/out, and layer switch buttons on the right";
        public static string Map_ShowScaleBarSetting => ResourceManager.GetString("Map_ShowScaleBarSetting", resourceCulture) ?? "Map Scale Bar";
        public static string Map_ShowScaleBarSetting_Subtitle => ResourceManager.GetString("Map_ShowScaleBarSetting_Subtitle", resourceCulture) ?? "Show Apple-style metric scale bar on medium and large widgets";
        public static string Map_BaiduKey_Subtitle => ResourceManager.GetString("Map_BaiduKey_Subtitle", resourceCulture) ?? "Enter a developer API Key for official high-DPI Baidu tiles";
        public static string Map_BaiduKeyWatermark => ResourceManager.GetString("Map_BaiduKeyWatermark", resourceCulture) ?? "Leave blank to use Amap/open-source fallback";
    }
}
