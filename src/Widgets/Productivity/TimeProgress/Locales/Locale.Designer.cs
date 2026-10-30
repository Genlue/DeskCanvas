namespace Progress.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Progress.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Progress => ResourceManager.GetString("Progress", resourceCulture) ?? "时间进度";
        public static string Progress_Title => ResourceManager.GetString("Progress_Title", resourceCulture) ?? "时间进度";
        public static string Progress_Subtitle => ResourceManager.GetString("Progress_Subtitle", resourceCulture) ?? "以灵动点阵直观呈现年、月、周、日与时光流逝";
        public static string Progress_Mode => ResourceManager.GetString("Progress_Mode", resourceCulture) ?? "进度模式";
        public static string Progress_Mode_Year => ResourceManager.GetString("Progress_Mode_Year", resourceCulture) ?? "年进度 (365/366 点)";
        public static string Progress_Mode_Month => ResourceManager.GetString("Progress_Mode_Month", resourceCulture) ?? "月进度 (28~31 点)";
        public static string Progress_Mode_Week => ResourceManager.GetString("Progress_Mode_Week", resourceCulture) ?? "周进度 (7 点)";
        public static string Progress_Mode_Day => ResourceManager.GetString("Progress_Mode_Day", resourceCulture) ?? "日进度";
        public static string Progress_Mode_Life => ResourceManager.GetString("Progress_Mode_Life", resourceCulture) ?? "人生时光 (以月为单位)";
        public static string Progress_DayGranularity => ResourceManager.GetString("Progress_DayGranularity", resourceCulture) ?? "日进度粒度";
        public static string Progress_DayGranularity_5Min => ResourceManager.GetString("Progress_DayGranularity_5Min", resourceCulture) ?? "每 5 分钟 (288 点)";
        public static string Progress_DayGranularity_10Min => ResourceManager.GetString("Progress_DayGranularity_10Min", resourceCulture) ?? "每 10 分钟 (144 点 · 12×12)";
        public static string Progress_DayGranularity_15Min => ResourceManager.GetString("Progress_DayGranularity_15Min", resourceCulture) ?? "每 15 分钟 (96 点)";
        public static string Progress_DayGranularity_30Min => ResourceManager.GetString("Progress_DayGranularity_30Min", resourceCulture) ?? "每 30 分钟 (48 点)";
        public static string Progress_DayGranularity_Hour => ResourceManager.GetString("Progress_DayGranularity_Hour", resourceCulture) ?? "每 1 小时 (24 点)";
        public static string Progress_DotShape => ResourceManager.GetString("Progress_DotShape", resourceCulture) ?? "圆点形状";
        public static string Progress_DotShape_Circle => ResourceManager.GetString("Progress_DotShape_Circle", resourceCulture) ?? "纯圆 (Circle)";
        public static string Progress_DotShape_Squircle => ResourceManager.GetString("Progress_DotShape_Squircle", resourceCulture) ?? "圆角方糖 (Squircle)";
        public static string Progress_FollowAccent => ResourceManager.GetString("Progress_FollowAccent", resourceCulture) ?? "跟随系统强调色";
        public static string Progress_PassedColor => ResourceManager.GetString("Progress_PassedColor", resourceCulture) ?? "已过圆点颜色";
        public static string Progress_CurrentColor => ResourceManager.GetString("Progress_CurrentColor", resourceCulture) ?? "当前圆点高亮色";
        public static string Progress_RemainingColor => ResourceManager.GetString("Progress_RemainingColor", resourceCulture) ?? "未过圆点颜色";
        public static string Progress_ShowHeader => ResourceManager.GetString("Progress_ShowHeader", resourceCulture) ?? "显示顶部信息栏";
        public static string Progress_CustomTitle => ResourceManager.GetString("Progress_CustomTitle", resourceCulture) ?? "自定义标题";
        public static string Progress_CustomTitle_Placeholder => ResourceManager.GetString("Progress_CustomTitle_Placeholder", resourceCulture) ?? "留空则按模式自动命名";
        public static string Progress_ShowPercentage => ResourceManager.GetString("Progress_ShowPercentage", resourceCulture) ?? "显示百分比";
        public static string Progress_ShowCount => ResourceManager.GetString("Progress_ShowCount", resourceCulture) ?? "显示计数进度";
        public static string Progress_BirthDate => ResourceManager.GetString("Progress_BirthDate", resourceCulture) ?? "出生日期 (YYYY-MM-DD)";
        public static string Progress_LifeExpectancy => ResourceManager.GetString("Progress_LifeExpectancy", resourceCulture) ?? "预期寿命 (年)";
    }
}
