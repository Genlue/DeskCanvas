namespace Wheel.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Wheel.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Wheel => ResourceManager.GetString("Wheel", resourceCulture) ?? "Wheel";
        public static string Wheel_Title => ResourceManager.GetString("Wheel_Title", resourceCulture) ?? "Decision Wheel & Roulette";
        public static string Wheel_Subtitle => ResourceManager.GetString("Wheel_Subtitle", resourceCulture) ?? "Solve Indecision · Custom Slices · Weighted Wheel";
        public static string Wheel_Spin => ResourceManager.GetString("Wheel_Spin", resourceCulture) ?? "SPIN";
        public static string Wheel_Spinning => ResourceManager.GetString("Wheel_Spinning", resourceCulture) ?? "Spinning...";
        public static string Wheel_Result => ResourceManager.GetString("Wheel_Result", resourceCulture) ?? "Decision Result";
        public static string Wheel_Templates => ResourceManager.GetString("Wheel_Templates", resourceCulture) ?? "Decision Topics";
        public static string Wheel_AddOption => ResourceManager.GetString("Wheel_AddOption", resourceCulture) ?? "Add Option";
        public static string Wheel_Weight => ResourceManager.GetString("Wheel_Weight", resourceCulture) ?? "Weight";
        public static string Wheel_EliminateWinner => ResourceManager.GetString("Wheel_EliminateWinner", resourceCulture) ?? "Eliminate on Selected";
        public static string Wheel_Duration => ResourceManager.GetString("Wheel_Duration", resourceCulture) ?? "Spin Duration (sec)";
        public static string Wheel_EnableSound => ResourceManager.GetString("Wheel_EnableSound", resourceCulture) ?? "Sound Effects";
        public static string Wheel_History => ResourceManager.GetString("Wheel_History", resourceCulture) ?? "Decision History";
        public static string Wheel_ClearHistory => ResourceManager.GetString("Wheel_ClearHistory", resourceCulture) ?? "Clear History";
        public static string Wheel_PopupTitle => ResourceManager.GetString("Wheel_PopupTitle", resourceCulture) ?? "Decision Center";
        public static string Wheel_PopupHeader => ResourceManager.GetString("Wheel_PopupHeader", resourceCulture) ?? "🎡 Decision Center";
        public static string Wheel_CurrentTemplate => ResourceManager.GetString("Wheel_CurrentTemplate", resourceCulture) ?? "Current Topic:";
        public static string Wheel_NewTemplate => ResourceManager.GetString("Wheel_NewTemplate", resourceCulture) ?? "＋ New";
        public static string Wheel_Delete => ResourceManager.GetString("Wheel_Delete", resourceCulture) ?? "Delete";
        public static string Wheel_OptionsSection => ResourceManager.GetString("Wheel_OptionsSection", resourceCulture) ?? "📝 Options & Weights";
        public static string Wheel_AddNewOption => ResourceManager.GetString("Wheel_AddNewOption", resourceCulture) ?? "＋ Add New Option";
        public static string Wheel_WeightLabel => ResourceManager.GetString("Wheel_WeightLabel", resourceCulture) ?? "Weight:";
        public static string Wheel_EliminationMode => ResourceManager.GetString("Wheel_EliminationMode", resourceCulture) ?? "Elimination Mode (Auto-remove Winners)";
        public static string Wheel_SaveTemplate => ResourceManager.GetString("Wheel_SaveTemplate", resourceCulture) ?? "💾 Save Current Topic";
        public static string Wheel_HistorySection => ResourceManager.GetString("Wheel_HistorySection", resourceCulture) ?? "📜 Decision Records";
        public static string Wheel_Clear => ResourceManager.GetString("Wheel_Clear", resourceCulture) ?? "Clear";
        public static string Wheel_TapToStart => ResourceManager.GetString("Wheel_TapToStart", resourceCulture) ?? "Tap to Start";
        public static string Wheel_DefaultTemplateTitle => ResourceManager.GetString("Wheel_DefaultTemplateTitle", resourceCulture) ?? "What to Eat Today";
        public static string Wheel_ManageTooltip => ResourceManager.GetString("Wheel_ManageTooltip", resourceCulture) ?? "Manage Topics & Options";
        public static string Wheel_SpinHint => ResourceManager.GetString("Wheel_SpinHint", resourceCulture) ?? "Tap the Wheel to Decide";
        public static string Wheel_SpinNow => ResourceManager.GetString("Wheel_SpinNow", resourceCulture) ?? "Spin Now";
        public static string Wheel_Options => ResourceManager.GetString("Wheel_Options", resourceCulture) ?? "Options";
        public static string Wheel_EditTooltip => ResourceManager.GetString("Wheel_EditTooltip", resourceCulture) ?? "Edit";
        public static string Wheel_ResultPending => ResourceManager.GetString("Wheel_ResultPending", resourceCulture) ?? "Result: Waiting to Spin";
    }
}
