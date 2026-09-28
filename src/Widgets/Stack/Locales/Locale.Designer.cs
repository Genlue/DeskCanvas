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
        public static string Stack_Widget_Subtitle => ResourceManager.GetString("Stack_Widget_Subtitle", resourceCulture) ?? "在同一卡片内重叠放置多个相同比例的小组件，通过右侧小圆点或滚轮切换";
        public static string Setting_Manage_Stack => ResourceManager.GetString("Setting_Manage_Stack", resourceCulture) ?? "管理重叠小组件";
        public static string Setting_Add_Widget => ResourceManager.GetString("Setting_Add_Widget", resourceCulture) ?? "添加组件";
        public static string Setting_Remove_Widget => ResourceManager.GetString("Setting_Remove_Widget", resourceCulture) ?? "移除";
        public static string Setting_Move_Up => ResourceManager.GetString("Setting_Move_Up", resourceCulture) ?? "上移";
        public static string Setting_Move_Down => ResourceManager.GetString("Setting_Move_Down", resourceCulture) ?? "下移";
        public static string Setting_Allow_Wheel => ResourceManager.GetString("Setting_Allow_Wheel", resourceCulture) ?? "鼠标滚轮切页";
        public static string Setting_Wheel_Only_On_Dots => ResourceManager.GetString("Setting_Wheel_Only_On_Dots", resourceCulture) ?? "仅圆点处可用滚轮切页";
    }
}
