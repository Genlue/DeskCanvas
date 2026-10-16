namespace Dice.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Dice.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Dice => ResourceManager.GetString("Dice", resourceCulture) ?? "Dice";
        public static string Dice_Title => ResourceManager.GetString("Dice_Title", resourceCulture) ?? "Dice Roller";
        public static string Dice_Subtitle => ResourceManager.GetString("Dice_Subtitle", resourceCulture) ?? "Random Decisions · Party Board Games · Polyhedral Dice";
        public static string Dice_Roll => ResourceManager.GetString("Dice_Roll", resourceCulture) ?? "Roll";
        public static string Dice_Rolling => ResourceManager.GetString("Dice_Rolling", resourceCulture) ?? "Rolling...";
        public static string Dice_Sum => ResourceManager.GetString("Dice_Sum", resourceCulture) ?? "SUM";
        public static string Dice_History => ResourceManager.GetString("Dice_History", resourceCulture) ?? "Roll History";
        public static string Dice_ClearHistory => ResourceManager.GetString("Dice_ClearHistory", resourceCulture) ?? "Clear History";
        public static string Dice_Count => ResourceManager.GetString("Dice_Count", resourceCulture) ?? "Dice Count";
        public static string Dice_Type => ResourceManager.GetString("Dice_Type", resourceCulture) ?? "Dice Type";
        public static string Dice_FaceStyle => ResourceManager.GetString("Dice_FaceStyle", resourceCulture) ?? "Face Style";
        public static string Dice_FaceDots => ResourceManager.GetString("Dice_FaceDots", resourceCulture) ?? "Dots";
        public static string Dice_FaceNumbers => ResourceManager.GetString("Dice_FaceNumbers", resourceCulture) ?? "Numbers";
        public static string Dice_ShowSum => ResourceManager.GetString("Dice_ShowSum", resourceCulture) ?? "Show Sum";
        public static string Dice_EnableSound => ResourceManager.GetString("Dice_EnableSound", resourceCulture) ?? "Sound Effects";
        public static string Dice_Stats => ResourceManager.GetString("Dice_Stats", resourceCulture) ?? "Statistics";
        public static string Dice_TotalRolls => ResourceManager.GetString("Dice_TotalRolls", resourceCulture) ?? "Total Rolls";
        public static string Dice_AvgScore => ResourceManager.GetString("Dice_AvgScore", resourceCulture) ?? "Average Score";
        public static string Dice_NaturalMax => ResourceManager.GetString("Dice_NaturalMax", resourceCulture) ?? "CRITICAL MAX!";
        public static string Dice_PopupTitle => ResourceManager.GetString("Dice_PopupTitle", resourceCulture) ?? "Dice Roller · Decisions & History";
        public static string Dice_PopupHeader => ResourceManager.GetString("Dice_PopupHeader", resourceCulture) ?? "🎲 Dice Roller · Decisions & History";
        public static string Dice_CountLabel => ResourceManager.GetString("Dice_CountLabel", resourceCulture) ?? "Dice Count:";
        public static string Dice_TypeLabel => ResourceManager.GetString("Dice_TypeLabel", resourceCulture) ?? "Dice Type:";
        public static string Dice_TypeD6 => ResourceManager.GetString("Dice_TypeD6", resourceCulture) ?? "D6 (Classic 6-sided)";
        public static string Dice_TypeD4 => ResourceManager.GetString("Dice_TypeD4", resourceCulture) ?? "D4 (4-sided)";
        public static string Dice_TypeD8 => ResourceManager.GetString("Dice_TypeD8", resourceCulture) ?? "D8 (8-sided)";
        public static string Dice_TypeD10 => ResourceManager.GetString("Dice_TypeD10", resourceCulture) ?? "D10 (10-sided)";
        public static string Dice_TypeD12 => ResourceManager.GetString("Dice_TypeD12", resourceCulture) ?? "D12 (12-sided)";
        public static string Dice_TypeD20 => ResourceManager.GetString("Dice_TypeD20", resourceCulture) ?? "D20 (20-sided RPG)";
        public static string Dice_TypeD100 => ResourceManager.GetString("Dice_TypeD100", resourceCulture) ?? "D100 (Percentile)";
        public static string Dice_RollNow => ResourceManager.GetString("Dice_RollNow", resourceCulture) ?? "Roll Now";
        public static string Dice_RollAction => ResourceManager.GetString("Dice_RollAction", resourceCulture) ?? "Roll Now";
        public static string Dice_LatestResult => ResourceManager.GetString("Dice_LatestResult", resourceCulture) ?? "Latest Result:";
        public static string Dice_SumPlaceholder => ResourceManager.GetString("Dice_SumPlaceholder", resourceCulture) ?? "Sum: 8";
        public static string Dice_HistoryTitle => ResourceManager.GetString("Dice_HistoryTitle", resourceCulture) ?? "📜 Roll History";
        public static string Dice_TapToRoll => ResourceManager.GetString("Dice_TapToRoll", resourceCulture) ?? "Tap to Roll";
        public static string Dice_DetailsTip => ResourceManager.GetString("Dice_DetailsTip", resourceCulture) ?? "Details & History";
        public static string Dice_RecentRolls => ResourceManager.GetString("Dice_RecentRolls", resourceCulture) ?? "Recent Rolls";
        public static string Dice_FullHistoryTip => ResourceManager.GetString("Dice_FullHistoryTip", resourceCulture) ?? "Full History";
        public static string Dice_Count1 => ResourceManager.GetString("Dice_Count1", resourceCulture) ?? "1 die";
        public static string Dice_Count2 => ResourceManager.GetString("Dice_Count2", resourceCulture) ?? "2 dice";
        public static string Dice_Count3 => ResourceManager.GetString("Dice_Count3", resourceCulture) ?? "3 dice";
        public static string Dice_Count4 => ResourceManager.GetString("Dice_Count4", resourceCulture) ?? "4 dice";
        public static string Dice_Count5 => ResourceManager.GetString("Dice_Count5", resourceCulture) ?? "5 dice";
        public static string Dice_Count6 => ResourceManager.GetString("Dice_Count6", resourceCulture) ?? "6 dice";
    }
}
