namespace Storage.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Storage.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Storage => ResourceManager.GetString("Storage", resourceCulture) ?? "磁盘存储";
        public static string Storage_Title => ResourceManager.GetString("Storage_Title", resourceCulture) ?? "磁盘存储";
        public static string Storage_Subtitle => ResourceManager.GetString("Storage_Subtitle", resourceCulture) ?? "直观展示本机各硬盘分区的存储容量、剩余空间与使用率";
        public static string Storage_HeaderTitle => ResourceManager.GetString("Storage_HeaderTitle", resourceCulture) ?? "存储空间";
        public static string Storage_DisplayMode => ResourceManager.GetString("Storage_DisplayMode", resourceCulture) ?? "显示样式";
        public static string Storage_TextFormat => ResourceManager.GetString("Storage_TextFormat", resourceCulture) ?? "容量数值格式";
        public static string Storage_ShowHeader => ResourceManager.GetString("Storage_ShowHeader", resourceCulture) ?? "显示顶部总览栏";
        public static string Storage_UseWarningColors => ResourceManager.GetString("Storage_UseWarningColors", resourceCulture) ?? "空间不足变色预警";
        public static string Storage_DoubleClickToOpen => ResourceManager.GetString("Storage_DoubleClickToOpen", resourceCulture) ?? "双击打开磁盘文件夹";
        public static string Storage_ShowRemovableDrives => ResourceManager.GetString("Storage_ShowRemovableDrives", resourceCulture) ?? "显示可移动磁盘 (U盘/移动硬盘)";
        public static string Storage_ShowNetworkDrives => ResourceManager.GetString("Storage_ShowNetworkDrives", resourceCulture) ?? "显示网络映射驱动器";
    }
}
