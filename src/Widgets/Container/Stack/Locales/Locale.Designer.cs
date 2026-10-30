namespace StackWidgets.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("StackWidgets.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Stack_Widget_Title => ResourceManager.GetString("Stack_Widget_Title", resourceCulture) ?? "组件重叠";
        public static string Stack_Widget_Subtitle => ResourceManager.GetString("Stack_Widget_Subtitle", resourceCulture) ?? "在同一卡片内重叠放置多个相同比例的小组件，通过右侧小圆点或滚轮切换，也可按住鼠标中键滚动切换";
        public static string Setting_Manage_Stack => ResourceManager.GetString("Setting_Manage_Stack", resourceCulture) ?? "管理重叠小组件";
        public static string Setting_Add_Widget => ResourceManager.GetString("Setting_Add_Widget", resourceCulture) ?? "添加组件";
        public static string Setting_Remove_Widget => ResourceManager.GetString("Setting_Remove_Widget", resourceCulture) ?? "移除";
        public static string Setting_Move_Up => ResourceManager.GetString("Setting_Move_Up", resourceCulture) ?? "上移";
        public static string Setting_Move_Down => ResourceManager.GetString("Setting_Move_Down", resourceCulture) ?? "下移";
        public static string Setting_Allow_Wheel => ResourceManager.GetString("Setting_Allow_Wheel", resourceCulture) ?? "鼠标滚轮切页";
        public static string Setting_Wheel_Only_On_Dots => ResourceManager.GetString("Setting_Wheel_Only_On_Dots", resourceCulture) ?? "滚轮优先交给子组件（空白处切页）";
        public static string Setting_Auto_Hide_Dots => ResourceManager.GetString("Setting_Auto_Hide_Dots", resourceCulture) ?? "Hide Dots While Idle (Shown When Switching Or Hovering)";
        public static string StackWidgets => ResourceManager.GetString("StackWidgets", resourceCulture) ?? "Widget Stack";
        public static string EmptyCard_Title => ResourceManager.GetString("EmptyCard_Title", resourceCulture) ?? "Empty Stack Card";
        public static string EmptyCard_Hint => ResourceManager.GetString("EmptyCard_Hint", resourceCulture) ?? "Right-click & Edit to add widgets";
        public static string Setting_DropZone_Title => ResourceManager.GetString("Setting_DropZone_Title", resourceCulture) ?? "Drag Desktop Widgets Here";
        public static string Setting_DropZone_Desc => ResourceManager.GetString("Setting_DropZone_Desc", resourceCulture) ?? "Drag any widget on the desktop and drop it into this window to add it to the stack";
        public static string Setting_Stacked_List_Title => ResourceManager.GetString("Setting_Stacked_List_Title", resourceCulture) ?? "Stacked Widgets (configure, reorder, or move back to desktop)";
        public static string Setting_Stacked_Empty_Hint => ResourceManager.GetString("Setting_Stacked_Empty_Hint", resourceCulture) ?? "No widgets stacked yet (shown as an empty card on the desktop).\nDrag widgets from the desktop into the drop zone above, or select and add one below.";
        public static string Setting_Extract_To_Desktop => ResourceManager.GetString("Setting_Extract_To_Desktop", resourceCulture) ?? "↖ To Desktop";
        public static string Setting_Extract_To_Desktop_Tip => ResourceManager.GetString("Setting_Extract_To_Desktop_Tip", resourceCulture) ?? "Remove this widget from the stack and restore it to the desktop";
        public static string Setting_Edit_Child => ResourceManager.GetString("Setting_Edit_Child", resourceCulture) ?? "Settings";
        public static string Setting_Edit_Child_Tip => ResourceManager.GetString("Setting_Edit_Child_Tip", resourceCulture) ?? "Configure this widget";
    }
}
