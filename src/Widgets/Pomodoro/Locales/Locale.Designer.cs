namespace Pomodoro.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Pomodoro.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Pomodoro => ResourceManager.GetString("Pomodoro", resourceCulture) ?? "Pomodoro";
        public static string Pomodoro_Title => ResourceManager.GetString("Pomodoro_Title", resourceCulture) ?? "Pomodoro Timer";
        public static string Pomodoro_Subtitle => ResourceManager.GetString("Pomodoro_Subtitle", resourceCulture) ?? "Focus & break timer with task tracking";
        public static string Pomodoro_Phase_Focus => ResourceManager.GetString("Pomodoro_Phase_Focus", resourceCulture) ?? "Focus";
        public static string Pomodoro_Phase_ShortBreak => ResourceManager.GetString("Pomodoro_Phase_ShortBreak", resourceCulture) ?? "Short Break";
        public static string Pomodoro_Phase_LongBreak => ResourceManager.GetString("Pomodoro_Phase_LongBreak", resourceCulture) ?? "Long Break";
        public static string Pomodoro_Tab_Focus => ResourceManager.GetString("Pomodoro_Tab_Focus", resourceCulture) ?? "Focus";
        public static string Pomodoro_Tab_ShortBreak => ResourceManager.GetString("Pomodoro_Tab_ShortBreak", resourceCulture) ?? "Short Break";
        public static string Pomodoro_Tab_LongBreak => ResourceManager.GetString("Pomodoro_Tab_LongBreak", resourceCulture) ?? "Long Break";
        public static string Pomodoro_Start => ResourceManager.GetString("Pomodoro_Start", resourceCulture) ?? "Start";
        public static string Pomodoro_Pause => ResourceManager.GetString("Pomodoro_Pause", resourceCulture) ?? "Pause";
        public static string Pomodoro_Reset => ResourceManager.GetString("Pomodoro_Reset", resourceCulture) ?? "Reset";
        public static string Pomodoro_Skip => ResourceManager.GetString("Pomodoro_Skip", resourceCulture) ?? "Skip";
        public static string Pomodoro_SkipPhase => ResourceManager.GetString("Pomodoro_SkipPhase", resourceCulture) ?? "Skip this phase";
        public static string Pomodoro_SwitchMode => ResourceManager.GetString("Pomodoro_SwitchMode", resourceCulture) ?? "Click to switch mode";
        public static string Pomodoro_FocusMinutes => ResourceManager.GetString("Pomodoro_FocusMinutes", resourceCulture) ?? "Focus duration (min)";
        public static string Pomodoro_ShortBreakMinutes => ResourceManager.GetString("Pomodoro_ShortBreakMinutes", resourceCulture) ?? "Short break duration (min)";
        public static string Pomodoro_LongBreakMinutes => ResourceManager.GetString("Pomodoro_LongBreakMinutes", resourceCulture) ?? "Long break duration (min)";
        public static string Pomodoro_LongBreakInterval => ResourceManager.GetString("Pomodoro_LongBreakInterval", resourceCulture) ?? "Long break interval (cycles)";
        public static string Pomodoro_AutoStartBreaks => ResourceManager.GetString("Pomodoro_AutoStartBreaks", resourceCulture) ?? "Auto-start breaks";
        public static string Pomodoro_AutoStartFocus => ResourceManager.GetString("Pomodoro_AutoStartFocus", resourceCulture) ?? "Auto-start focus";
    }
}
