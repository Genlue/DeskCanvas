namespace TickClock.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("TickClock.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string TickClock => ResourceManager.GetString("TickClock", resourceCulture) ?? "Tick Clock";
        public static string TickClock_Title => ResourceManager.GetString("TickClock_Title", resourceCulture) ?? "Tick Clock";
        public static string TickClock_Subtitle => ResourceManager.GetString("TickClock_Subtitle", resourceCulture) ?? "Dial ticks · weekday · oversized time";
        public static string TickClock_ShowWeekday => ResourceManager.GetString("TickClock_ShowWeekday", resourceCulture) ?? "Show weekday";
        public static string TickClock_ShowDate => ResourceManager.GetString("TickClock_ShowDate", resourceCulture) ?? "Show date";
        public static string TickClock_Use24Hour => ResourceManager.GetString("TickClock_Use24Hour", resourceCulture) ?? "24-hour time";
    }
}
