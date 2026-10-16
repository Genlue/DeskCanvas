namespace Countdown.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Countdown.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Countdown => ResourceManager.GetString("Countdown", resourceCulture) ?? "Countdown";
        public static string Countdown_Title => ResourceManager.GetString("Countdown_Title", resourceCulture) ?? "Countdown & Anniversaries";
        public static string Countdown_Subtitle => ResourceManager.GetString("Countdown_Subtitle", resourceCulture) ?? "Count down to important events & anniversaries";
        public static string Countdown_DaysRemaining => ResourceManager.GetString("Countdown_DaysRemaining", resourceCulture) ?? "DAYS LEFT";
        public static string Countdown_DaysPassed => ResourceManager.GetString("Countdown_DaysPassed", resourceCulture) ?? "DAYS AGO";
        public static string Countdown_Today => ResourceManager.GetString("Countdown_Today", resourceCulture) ?? "TODAY";
        public static string Countdown_HoursMinutesSeconds => ResourceManager.GetString("Countdown_HoursMinutesSeconds", resourceCulture) ?? "REMAINING";
        public static string Countdown_CountupSubtitle => ResourceManager.GetString("Countdown_CountupSubtitle", resourceCulture) ?? "Since {0}";
        public static string Countdown_EventArrived => ResourceManager.GetString("Countdown_EventArrived", resourceCulture) ?? "Target day has arrived!";
        public static string Countdown_Default_NewYear => ResourceManager.GetString("Countdown_Default_NewYear", resourceCulture) ?? "New Year's Day";
        public static string Countdown_Default_SpringFestival => ResourceManager.GetString("Countdown_Default_SpringFestival", resourceCulture) ?? "Spring Festival";
        public static string Countdown_Default_Weekend => ResourceManager.GetString("Countdown_Default_Weekend", resourceCulture) ?? "Weekend";
        public static string Countdown_AddEvent => ResourceManager.GetString("Countdown_AddEvent", resourceCulture) ?? "Add Event";
        public static string Countdown_EditEvent => ResourceManager.GetString("Countdown_EditEvent", resourceCulture) ?? "Edit Event";
        public static string Countdown_EventTitle => ResourceManager.GetString("Countdown_EventTitle", resourceCulture) ?? "Event Title";
        public static string Countdown_TargetDate => ResourceManager.GetString("Countdown_TargetDate", resourceCulture) ?? "Target Date";
        public static string Countdown_IsLunar => ResourceManager.GetString("Countdown_IsLunar", resourceCulture) ?? "Lunar Calendar";
        public static string Countdown_Repeat => ResourceManager.GetString("Countdown_Repeat", resourceCulture) ?? "Repeat";
        public static string Countdown_Repeat_None => ResourceManager.GetString("Countdown_Repeat_None", resourceCulture) ?? "None";
        public static string Countdown_Repeat_Yearly => ResourceManager.GetString("Countdown_Repeat_Yearly", resourceCulture) ?? "Yearly";
        public static string Countdown_Repeat_Monthly => ResourceManager.GetString("Countdown_Repeat_Monthly", resourceCulture) ?? "Monthly";
        public static string Countdown_Repeat_Weekly => ResourceManager.GetString("Countdown_Repeat_Weekly", resourceCulture) ?? "Weekly";
        public static string Countdown_Direction => ResourceManager.GetString("Countdown_Direction", resourceCulture) ?? "Count Mode";
        public static string Countdown_Direction_Auto => ResourceManager.GetString("Countdown_Direction_Auto", resourceCulture) ?? "Auto";
        public static string Countdown_Direction_Countdown => ResourceManager.GetString("Countdown_Direction_Countdown", resourceCulture) ?? "Countdown";
        public static string Countdown_Direction_Countup => ResourceManager.GetString("Countdown_Direction_Countup", resourceCulture) ?? "Anniversary";
        public static string Countdown_Category => ResourceManager.GetString("Countdown_Category", resourceCulture) ?? "Category";
        public static string Countdown_Category_General => ResourceManager.GetString("Countdown_Category_General", resourceCulture) ?? "General";
        public static string Countdown_Category_Exam => ResourceManager.GetString("Countdown_Category_Exam", resourceCulture) ?? "Exam";
        public static string Countdown_Category_Anniversary => ResourceManager.GetString("Countdown_Category_Anniversary", resourceCulture) ?? "Anniversary";
        public static string Countdown_Category_Life => ResourceManager.GetString("Countdown_Category_Life", resourceCulture) ?? "Life";
        public static string Countdown_Category_Holiday => ResourceManager.GetString("Countdown_Category_Holiday", resourceCulture) ?? "Holiday";
        public static string Countdown_Category_Work => ResourceManager.GetString("Countdown_Category_Work", resourceCulture) ?? "Work";
        public static string Countdown_Pin => ResourceManager.GetString("Countdown_Pin", resourceCulture) ?? "Pin";
        public static string Countdown_Delete => ResourceManager.GetString("Countdown_Delete", resourceCulture) ?? "Delete";
        public static string Countdown_Save => ResourceManager.GetString("Countdown_Save", resourceCulture) ?? "Save";
        public static string Countdown_Cancel => ResourceManager.GetString("Countdown_Cancel", resourceCulture) ?? "Cancel";
        public static string Countdown_Style => ResourceManager.GetString("Countdown_Style", resourceCulture) ?? "Card Style";
        public static string Countdown_Style_MinimalNumber => ResourceManager.GetString("Countdown_Style_MinimalNumber", resourceCulture) ?? "Big Number";
        public static string Countdown_Style_RingProgress => ResourceManager.GetString("Countdown_Style_RingProgress", resourceCulture) ?? "Progress Ring";
        public static string Countdown_Style_EventList => ResourceManager.GetString("Countdown_Style_EventList", resourceCulture) ?? "Event List";
        public static string Countdown_Granularity => ResourceManager.GetString("Countdown_Granularity", resourceCulture) ?? "Time Granularity";
        public static string Countdown_Granularity_DaysOnly => ResourceManager.GetString("Countdown_Granularity_DaysOnly", resourceCulture) ?? "Days Only";
        public static string Countdown_Granularity_Auto24Hours => ResourceManager.GetString("Countdown_Granularity_Auto24Hours", resourceCulture) ?? "Dynamic 24h Countdown";
        public static string Countdown_Granularity_AlwaysDetailed => ResourceManager.GetString("Countdown_Granularity_AlwaysDetailed", resourceCulture) ?? "Always Detailed";
        public static string Countdown_TargetEvent => ResourceManager.GetString("Countdown_TargetEvent", resourceCulture) ?? "Display Event";
        public static string Countdown_TargetEvent_Auto => ResourceManager.GetString("Countdown_TargetEvent_Auto", resourceCulture) ?? "Auto (Pinned / Nearest)";
        public static string Countdown_EmptyEvents => ResourceManager.GetString("Countdown_EmptyEvents", resourceCulture) ?? "No events yet, click + to add";
        public static string Countdown_Close => ResourceManager.GetString("Countdown_Close", resourceCulture) ?? "Close panel";
        public static string Countdown_TotalEvents => ResourceManager.GetString("Countdown_TotalEvents", resourceCulture) ?? "{0} Events";
        public static string Countdown_Progress => ResourceManager.GetString("Countdown_Progress", resourceCulture) ?? "Progress";
        public static string Countdown_Style_Subtitle => ResourceManager.GetString("Countdown_Style_Subtitle", resourceCulture) ?? "Visual layout and focus across card sizes";
    }
}
