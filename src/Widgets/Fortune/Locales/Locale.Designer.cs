namespace Fortune.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Fortune.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Fortune => ResourceManager.GetString("Fortune", resourceCulture) ?? "Fortune";
        public static string Fortune_Title => ResourceManager.GetString("Fortune_Title", resourceCulture) ?? "Daily Fortune & Luck";
        public static string Fortune_Subtitle => ResourceManager.GetString("Fortune_Subtitle", resourceCulture) ?? "Daily Luck · Fortune Poetry · Mindful Guidance";
        public static string Fortune_Draw => ResourceManager.GetString("Fortune_Draw", resourceCulture) ?? "Draw Fortune";
        public static string Fortune_Drawing => ResourceManager.GetString("Fortune_Drawing", resourceCulture) ?? "Drawing Fortune...";
        public static string Fortune_DrawnToday => ResourceManager.GetString("Fortune_DrawnToday", resourceCulture) ?? "Drawn for Today";
        public static string Fortune_FreeDraw => ResourceManager.GetString("Fortune_FreeDraw", resourceCulture) ?? "Free Divination";
        public static string Fortune_Redraw => ResourceManager.GetString("Fortune_Redraw", resourceCulture) ?? "Draw Again";
        public static string Fortune_Suitable => ResourceManager.GetString("Fortune_Suitable", resourceCulture) ?? "DO";
        public static string Fortune_Taboo => ResourceManager.GetString("Fortune_Taboo", resourceCulture) ?? "DON'T";
        public static string Fortune_LuckyColor => ResourceManager.GetString("Fortune_LuckyColor", resourceCulture) ?? "Lucky Color";
        public static string Fortune_LuckyNumber => ResourceManager.GetString("Fortune_LuckyNumber", resourceCulture) ?? "Lucky Number";
        public static string Fortune_LuckyThing => ResourceManager.GetString("Fortune_LuckyThing", resourceCulture) ?? "Lucky Item";
        public static string Fortune_History => ResourceManager.GetString("Fortune_History", resourceCulture) ?? "Fortune Journal";
        public static string Fortune_Copy => ResourceManager.GetString("Fortune_Copy", resourceCulture) ?? "Copy Fortune";
        public static string Fortune_Copied => ResourceManager.GetString("Fortune_Copied", resourceCulture) ?? "Copied to clipboard!";
        public static string Fortune_PoolType => ResourceManager.GetString("Fortune_PoolType", resourceCulture) ?? "Poem Pool Theme";
        public static string Fortune_PoolTraditional => ResourceManager.GetString("Fortune_PoolTraditional", resourceCulture) ?? "Classical Spiritual Poetry";
        public static string Fortune_PoolCyberWorker => ResourceManager.GetString("Fortune_PoolCyberWorker", resourceCulture) ?? "Cyber Healing & Work Lore";
        public static string Fortune_PoolInspiration => ResourceManager.GetString("Fortune_PoolInspiration", resourceCulture) ?? "Life Inspiration & Wisdom";
        public static string Fortune_FreeDrawMode => ResourceManager.GetString("Fortune_FreeDrawMode", resourceCulture) ?? "Unlimited Free Redraw";
        public static string Fortune_PopupHeader => ResourceManager.GetString("Fortune_PopupHeader", resourceCulture) ?? "🎋 Daily Fortune & Luck";
        public static string Fortune_SampleDate => ResourceManager.GetString("Fortune_SampleDate", resourceCulture) ?? "Oct 16";
        public static string Fortune_SampleGrade => ResourceManager.GetString("Fortune_SampleGrade", resourceCulture) ?? "Great Blessing";
        public static string Fortune_SamplePoemTitle => ResourceManager.GetString("Fortune_SamplePoemTitle", resourceCulture) ?? "Smooth Sailing · Lot No. 8";
        public static string Fortune_SamplePoemName => ResourceManager.GetString("Fortune_SamplePoemName", resourceCulture) ?? "Smooth Sailing";
        public static string Fortune_SampleScore => ResourceManager.GetString("Fortune_SampleScore", resourceCulture) ?? "Luck Index: 98 ★★★★★";
        public static string Fortune_SampleScoreShort => ResourceManager.GetString("Fortune_SampleScoreShort", resourceCulture) ?? "Luck 98";
        public static string Fortune_SamplePoemFull => ResourceManager.GetString("Fortune_SamplePoemFull", resourceCulture) ?? "With favoring winds we meet at last; ten thousand miles need no toil.\nThe sky stands clear as a painting; drift free toward the azure void.";
        public static string Fortune_SamplePoemShort => ResourceManager.GetString("Fortune_SamplePoemShort", resourceCulture) ?? "With favoring winds we meet at last,\nten thousand miles need no toil.";
        public static string Fortune_SampleExplanationLong => ResourceManager.GetString("Fortune_SampleExplanationLong", resourceCulture) ?? "[Reading] Fortune flows smoothly; heaven and earth align. Every concern will resolve itself — press boldly ahead.";
        public static string Fortune_SampleExplanation => ResourceManager.GetString("Fortune_SampleExplanation", resourceCulture) ?? "Fortune flows smoothly; heaven and earth align. Every concern will resolve itself.";
        public static string Fortune_SampleSuitableLong => ResourceManager.GetString("Fortune_SampleSuitableLong", resourceCulture) ?? "DO: Press the advantage, make good friends";
        public static string Fortune_SampleTabooLong => ResourceManager.GetString("Fortune_SampleTabooLong", resourceCulture) ?? "DON'T: Overthink, hesitate";
        public static string Fortune_SampleSuitable => ResourceManager.GetString("Fortune_SampleSuitable", resourceCulture) ?? "DO: Deep focus";
        public static string Fortune_SampleTaboo => ResourceManager.GetString("Fortune_SampleTaboo", resourceCulture) ?? "DON'T: Indecision";
        public static string Fortune_SampleAdvice => ResourceManager.GetString("Fortune_SampleAdvice", resourceCulture) ?? "DO Seize the momentum · DON'T Look back";
        public static string Fortune_SampleLuckyColor => ResourceManager.GetString("Fortune_SampleLuckyColor", resourceCulture) ?? "Sky Blue";
        public static string Fortune_SampleLuckyThing => ResourceManager.GetString("Fortune_SampleLuckyThing", resourceCulture) ?? "Desk Plant";
        public static string Fortune_LuckyColorLabel => ResourceManager.GetString("Fortune_LuckyColorLabel", resourceCulture) ?? "Lucky Color:";
        public static string Fortune_LuckyNumberLabel => ResourceManager.GetString("Fortune_LuckyNumberLabel", resourceCulture) ?? "Lucky Number:";
        public static string Fortune_LuckyThingLabel => ResourceManager.GetString("Fortune_LuckyThingLabel", resourceCulture) ?? "Lucky Item:";
        public static string Fortune_LuckyItem => ResourceManager.GetString("Fortune_LuckyItem", resourceCulture) ?? "Lucky Item";
        public static string Fortune_DailyTitle => ResourceManager.GetString("Fortune_DailyTitle", resourceCulture) ?? "Today's Fortune";
        public static string Fortune_ShakeDraw => ResourceManager.GetString("Fortune_ShakeDraw", resourceCulture) ?? "Shake for Fortune";
        public static string Fortune_TooltipJournal => ResourceManager.GetString("Fortune_TooltipJournal", resourceCulture) ?? "Poem & Journal";
        public static string Fortune_DrawHint => ResourceManager.GetString("Fortune_DrawHint", resourceCulture) ?? "Clear your mind · Draw today's fortune";
        public static string Fortune_DailyTip => ResourceManager.GetString("Fortune_DailyTip", resourceCulture) ?? "Set each morning · Tap for the reading";
        public static string Fortune_GuidanceTitle => ResourceManager.GetString("Fortune_GuidanceTitle", resourceCulture) ?? "Fortune Guidance";
        public static string Fortune_OpenJournal => ResourceManager.GetString("Fortune_OpenJournal", resourceCulture) ?? "Open Fortune Journal →";
        public static string Fortune_FreeDrawButton => ResourceManager.GetString("Fortune_FreeDrawButton", resourceCulture) ?? "Draw Again Freely (Any Question)";
        public static string Fortune_ResetNote => ResourceManager.GetString("Fortune_ResetNote", resourceCulture) ?? "First draw resets at midnight daily";
        public static string Fortune_HistoryHeader => ResourceManager.GetString("Fortune_HistoryHeader", resourceCulture) ?? "📜 Fortune Journal (Recent Draws)";
    }
}
