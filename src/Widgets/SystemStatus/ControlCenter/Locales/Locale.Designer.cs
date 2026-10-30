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
        public static string ControlCenter_Tier2x2 => ResourceManager.GetString("ControlCenter_Tier2x2", resourceCulture) ?? "2×2 Grid";
        public static string ControlCenter_Tier4x2 => ResourceManager.GetString("ControlCenter_Tier4x2", resourceCulture) ?? "4×2 Grid";
        public static string ControlCenter_Tier4x4 => ResourceManager.GetString("ControlCenter_Tier4x4", resourceCulture) ?? "4×4 Grid";
        public static string ControlCenter_PresetVertical => ResourceManager.GetString("ControlCenter_PresetVertical", resourceCulture) ?? "iOS Vertical Preset";
        public static string ControlCenter_PresetVertical_Tip => ResourceManager.GetString("ControlCenter_PresetVertical_Tip", resourceCulture) ?? "Switch right-side brightness & volume to 1×2 vertical sliders";
        public static string ControlCenter_ResetDefault => ResourceManager.GetString("ControlCenter_ResetDefault", resourceCulture) ?? "Reset Default";
        public static string ControlCenter_ComposerHint => ResourceManager.GetString("ControlCenter_ComposerHint", resourceCulture) ?? "Drag tiles to rearrange; drag bottom-right ◢ or click buttons below to change size & orientation";
        public static string ControlCenter_Span1x1 => ResourceManager.GetString("ControlCenter_Span1x1", resourceCulture) ?? "1×1 Single";
        public static string ControlCenter_Span2x1 => ResourceManager.GetString("ControlCenter_Span2x1", resourceCulture) ?? "2×1 Wide";
        public static string ControlCenter_Span1x2 => ResourceManager.GetString("ControlCenter_Span1x2", resourceCulture) ?? "1×2 Tall";
        public static string ControlCenter_Span2x2 => ResourceManager.GetString("ControlCenter_Span2x2", resourceCulture) ?? "2×2 Large";
        public static string ControlCenter_DockHeader => ResourceManager.GetString("ControlCenter_DockHeader", resourceCulture) ?? "Available Controls (Click or drag into grid above):";
        public static string ControlCenter_WheelStepTitle => ResourceManager.GetString("ControlCenter_WheelStepTitle", resourceCulture) ?? "Mouse Wheel Step";
        public static string ControlCenter_WheelStepSubtitle => ResourceManager.GetString("ControlCenter_WheelStepSubtitle", resourceCulture) ?? "Increment per scroll notch when hovering over brightness, volume, or mic sliders";
        public static string ControlCenter_TileRemoveTip => ResourceManager.GetString("ControlCenter_TileRemoveTip", resourceCulture) ?? "Remove from grid";
        public static string ControlCenter_TileResizeTip => ResourceManager.GetString("ControlCenter_TileResizeTip", resourceCulture) ?? "Drag to resize span (1×1 / 2×1 / 1×2 / 2×2)";
        public static string ControlCenter_DockEmpty => ResourceManager.GetString("ControlCenter_DockEmpty", resourceCulture) ?? "All controls are placed on the grid (click ✕ on a tile to remove it)";
        public static string ControlCenter_SelectionNone => ResourceManager.GetString("ControlCenter_SelectionNone", resourceCulture) ?? "Click a tile on the grid to change its size and orientation";
        public static string ControlCenter_SelectionFormat => ResourceManager.GetString("ControlCenter_SelectionFormat", resourceCulture) ?? "Selected: {0} ({1})";
        public static string SingleControl_BindHeader => ResourceManager.GetString("SingleControl_BindHeader", resourceCulture) ?? "Select the system control bound to this tile:";
        public static string SingleControl_WheelStepSubtitle => ResourceManager.GetString("SingleControl_WheelStepSubtitle", resourceCulture) ?? "Increment per scroll notch for brightness, volume, or microphone";
    }
}
