namespace FixedWidgets.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("FixedWidgets.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string FixedWidgets => ResourceManager.GetString("FixedWidgets", resourceCulture) ?? "Panoramic Hub";
        public static string Fixed_Aggregate_Title => ResourceManager.GetString("Fixed_Aggregate_Title", resourceCulture) ?? "Aggregate Info";
        public static string Fixed_Aggregate_Subtitle => ResourceManager.GetString("Fixed_Aggregate_Subtitle", resourceCulture) ?? "4x2 dashboard widget aggregating city weather, digital clock, and calendar";
        public static string Setting_City => ResourceManager.GetString("Setting_City", resourceCulture) ?? "Location / City";
        public static string Setting_CustomCity => ResourceManager.GetString("Setting_CustomCity", resourceCulture) ?? "Custom City Name";
        public static string Setting_SearchWatermark => ResourceManager.GetString("Setting_SearchWatermark", resourceCulture) ?? "Search city (English / Chinese)...";
        public static string Setting_CoordFormat => ResourceManager.GetString("Setting_CoordFormat", resourceCulture) ?? "Coordinates: {0:F2}°, {1:F2}°";
        public static string Setting_CityMatchedFormat => ResourceManager.GetString("Setting_CityMatchedFormat", resourceCulture) ?? "Matched: {0}";
        public static string Setting_CitySearching => ResourceManager.GetString("Setting_CitySearching", resourceCulture) ?? "Searching weather source...";
        public static string Setting_CityNotFound => ResourceManager.GetString("Setting_CityNotFound", resourceCulture) ?? "City not found in weather source";
        public static string Setting_TempUnit => ResourceManager.GetString("Setting_TempUnit", resourceCulture) ?? "温度单位";
        public static string Setting_Unit_Celsius => ResourceManager.GetString("Setting_Unit_Celsius", resourceCulture) ?? "摄氏度 (°C)";
        public static string Setting_Unit_Fahrenheit => ResourceManager.GetString("Setting_Unit_Fahrenheit", resourceCulture) ?? "华氏度 (°F)";
        public static string Setting_24Hours => ResourceManager.GetString("Setting_24Hours", resourceCulture) ?? "24 小时制";
        public static string Setting_ShowSeconds => ResourceManager.GetString("Setting_ShowSeconds", resourceCulture) ?? "显示秒数";
        public static string Setting_FirstDayOfWeek => ResourceManager.GetString("Setting_FirstDayOfWeek", resourceCulture) ?? "每周首日";
        public static string Setting_FirstDay_Monday => ResourceManager.GetString("Setting_FirstDay_Monday", resourceCulture) ?? "星期一";
        public static string Setting_FirstDay_Sunday => ResourceManager.GetString("Setting_FirstDay_Sunday", resourceCulture) ?? "星期日";
        public static string Setting_Padding => ResourceManager.GetString("Setting_Padding", resourceCulture) ?? "卡片内边距";
        public static string Setting_HollowToday => ResourceManager.GetString("Setting_HollowToday", resourceCulture) ?? "镂空当前日期";
        public static string Setting_HollowToday_Subtitle => ResourceManager.GetString("Setting_HollowToday_Subtitle", resourceCulture) ?? "把今天的日期数字从圆圈中镂空挖出，关闭后数字显示在圆圈上";
        public static string Setting_TodayColor => ResourceManager.GetString("Setting_TodayColor", resourceCulture) ?? "今日圆圈颜色";
        public static string Setting_TodayColor_Accent => ResourceManager.GetString("Setting_TodayColor_Accent", resourceCulture) ?? "跟随系统强调色";
        public static string Setting_TodayColor_Custom => ResourceManager.GetString("Setting_TodayColor_Custom", resourceCulture) ?? "自定义颜色";
        public static string Setting_TodayColor_Light => ResourceManager.GetString("Setting_TodayColor_Light", resourceCulture) ?? "浅色模式颜色";
        public static string Setting_TodayColor_Dark => ResourceManager.GetString("Setting_TodayColor_Dark", resourceCulture) ?? "深色模式颜色";
    }
}
