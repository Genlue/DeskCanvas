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
        public static string Storage_SectionAppearance => ResourceManager.GetString("Storage_SectionAppearance", resourceCulture) ?? "Appearance & Interaction";
        public static string Storage_SectionFilter => ResourceManager.GetString("Storage_SectionFilter", resourceCulture) ?? "Drive Filtering";
        public static string Storage_SectionDrives => ResourceManager.GetString("Storage_SectionDrives", resourceCulture) ?? "Local Drives & Visibility";
        public static string Storage_RefreshDrives => ResourceManager.GetString("Storage_RefreshDrives", resourceCulture) ?? "Refresh Drives";
        public static string Storage_DrivesHint => ResourceManager.GetString("Storage_DrivesHint", resourceCulture) ?? "Check or uncheck drives to show or hide them in the widget; extra drives are truncated by drive letter order to fit the card size.";
        public static string Storage_NoDrives => ResourceManager.GetString("Storage_NoDrives", resourceCulture) ?? "No available disk drives detected";
        public static string Storage_ShowDriveTooltip => ResourceManager.GetString("Storage_ShowDriveTooltip", resourceCulture) ?? "Check to show this drive in widget";
        public static string Storage_EnableTypeFirstTooltip => ResourceManager.GetString("Storage_EnableTypeFirstTooltip", resourceCulture) ?? "Please enable this drive type above first";
        public static string Storage_Open => ResourceManager.GetString("Storage_Open", resourceCulture) ?? "Open";
        public static string Storage_DriveTypeSystem => ResourceManager.GetString("Storage_DriveTypeSystem", resourceCulture) ?? "System";
        public static string Storage_DriveTypeRemovable => ResourceManager.GetString("Storage_DriveTypeRemovable", resourceCulture) ?? "Removable";
        public static string Storage_DriveTypeNetwork => ResourceManager.GetString("Storage_DriveTypeNetwork", resourceCulture) ?? "Network";
        public static string Storage_DriveTypeLocal => ResourceManager.GetString("Storage_DriveTypeLocal", resourceCulture) ?? "Local";
        public static string Storage_UsedFreeFormat => ResourceManager.GetString("Storage_UsedFreeFormat", resourceCulture) ?? "Used {0} / {1} · {2} free";
        public static string Storage_Mode_ProgressBarList => ResourceManager.GetString("Storage_Mode_ProgressBarList", resourceCulture) ?? "Progress Bar List";
        public static string Storage_Mode_RingDashboard => ResourceManager.GetString("Storage_Mode_RingDashboard", resourceCulture) ?? "Ring Dashboard";
        public static string Storage_Format_FreeSpace => ResourceManager.GetString("Storage_Format_FreeSpace", resourceCulture) ?? "Free Space (e.g. 138G free)";
        public static string Storage_Format_UsedAndTotal => ResourceManager.GetString("Storage_Format_UsedAndTotal", resourceCulture) ?? "Used / Total (e.g. 374G / 512G)";
        public static string Storage_Format_Percentage => ResourceManager.GetString("Storage_Format_Percentage", resourceCulture) ?? "Percentage (e.g. 73%)";
    }
}
