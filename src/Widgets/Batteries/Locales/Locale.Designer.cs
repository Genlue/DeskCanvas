namespace Batteries.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Batteries.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Batteries => ResourceManager.GetString("Batteries", resourceCulture) ?? "Batteries";
        public static string Batteries_Title => ResourceManager.GetString("Batteries_Title", resourceCulture) ?? "Batteries & Peripherals";
        public static string Batteries_Subtitle => ResourceManager.GetString("Batteries_Subtitle", resourceCulture) ?? "macOS-style battery monitor for host PC and Bluetooth peripherals";
        public static string Batteries_MainDevice => ResourceManager.GetString("Batteries_MainDevice", resourceCulture) ?? "PC Battery";
        public static string Batteries_AcPower => ResourceManager.GetString("Batteries_AcPower", resourceCulture) ?? "AC Power";
        public static string Batteries_Charging => ResourceManager.GetString("Batteries_Charging", resourceCulture) ?? "Charging";
        public static string Batteries_ShowPercentage => ResourceManager.GetString("Batteries_ShowPercentage", resourceCulture) ?? "Show percentage numbers";
        public static string Batteries_ShowPeripherals => ResourceManager.GetString("Batteries_ShowPeripherals", resourceCulture) ?? "Show Bluetooth peripherals";
        public static string Batteries_ShowRemainingTime => ResourceManager.GetString("Batteries_ShowRemainingTime", resourceCulture) ?? "Show estimated remaining time";
        public static string Batteries_Mouse => ResourceManager.GetString("Batteries_Mouse", resourceCulture) ?? "Mouse";
        public static string Batteries_Keyboard => ResourceManager.GetString("Batteries_Keyboard", resourceCulture) ?? "Keyboard";
        public static string Batteries_Headphones => ResourceManager.GetString("Batteries_Headphones", resourceCulture) ?? "Headphones";
    }
}
