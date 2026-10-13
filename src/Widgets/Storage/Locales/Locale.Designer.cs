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
        
        public static string Storage => ResourceManager.GetString("Storage", resourceCulture) ?? "Storage";
        public static string Storage_Title => ResourceManager.GetString("Storage_Title", resourceCulture) ?? "Disk Space";
        public static string Storage_Subtitle => ResourceManager.GetString("Storage_Subtitle", resourceCulture) ?? "Real-time local disk partition capacity, usage and remaining space monitor";
        public static string Storage_HeaderTitle => ResourceManager.GetString("Storage_HeaderTitle", resourceCulture) ?? "Storage";
        public static string Storage_DisplayMode => ResourceManager.GetString("Storage_DisplayMode", resourceCulture) ?? "Layout Style";
        public static string Storage_TextFormat => ResourceManager.GetString("Storage_TextFormat", resourceCulture) ?? "Capacity Text Format";
        public static string Storage_ShowHeader => ResourceManager.GetString("Storage_ShowHeader", resourceCulture) ?? "Show top title & summary bar";
        public static string Storage_UseWarningColors => ResourceManager.GetString("Storage_UseWarningColors", resourceCulture) ?? "Show orange/red warning when space is low";
        public static string Storage_DoubleClickToOpen => ResourceManager.GetString("Storage_DoubleClickToOpen", resourceCulture) ?? "Double-click drive to open in Explorer";
        public static string Storage_ShowRemovableDrives => ResourceManager.GetString("Storage_ShowRemovableDrives", resourceCulture) ?? "Show removable drives (USB)";
        public static string Storage_ShowNetworkDrives => ResourceManager.GetString("Storage_ShowNetworkDrives", resourceCulture) ?? "Show network drives";
    }
}
