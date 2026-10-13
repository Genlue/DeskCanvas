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

        public static string ControlCenter => ResourceManager.GetString("ControlCenter", resourceCulture) ?? "Control Center";
        public static string ControlCenter_Title => ResourceManager.GetString("ControlCenter_Title", resourceCulture) ?? "Control Center";
        public static string ControlCenter_Subtitle => ResourceManager.GetString("ControlCenter_Subtitle", resourceCulture) ?? "Customizable system control panel: brightness, volume, audio output, Wi-Fi, Bluetooth, theme & desktop icons";
        public static string SingleControl_Title => ResourceManager.GetString("SingleControl_Title", resourceCulture) ?? "Quick Control Tile";
        public static string SingleControl_Subtitle => ResourceManager.GetString("SingleControl_Subtitle", resourceCulture) ?? "Standalone system toggle or volume/brightness slider tile";
    }
}
