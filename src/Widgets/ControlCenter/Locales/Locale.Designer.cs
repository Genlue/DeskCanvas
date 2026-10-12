namespace ControlCenter.Locales
{
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("System.Resources.Tools.StronglyTypedResourceBuilder", "4.0.0.0")]
    [global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    public class Locale
    {
        private static global::System.Resources.ResourceManager resourceMan;
        private static global::System.Globalization.CultureInfo resourceCulture;

        internal Locale()
        {
        }

        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Resources.ResourceManager ResourceManager
        {
            get
            {
                if (object.ReferenceEquals(resourceMan, null))
                {
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("ControlCenter.Locales.Locale", typeof(Locale).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }

        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Globalization.CultureInfo Culture
        {
            get => resourceCulture;
            set => resourceCulture = value;
        }

        public static string ControlCenter => ResourceManager.GetString("ControlCenter", resourceCulture) ?? "控制中心";
        public static string ControlCenter_Title => ResourceManager.GetString("ControlCenter_Title", resourceCulture) ?? "控制中心";
        public static string ControlCenter_Subtitle => ResourceManager.GetString("ControlCenter_Subtitle", resourceCulture) ?? "自由拼装的系统控制面板：亮度、音量、音频输出、网络、蓝牙、深浅色与桌面图标一键调控";
        public static string SingleControl_Title => ResourceManager.GetString("SingleControl_Title", resourceCulture) ?? "快捷控制磁贴";
        public static string SingleControl_Subtitle => ResourceManager.GetString("SingleControl_Subtitle", resourceCulture) ?? "独立放置的单项系统开关或音量/亮度滑块磁贴";
    }
}
