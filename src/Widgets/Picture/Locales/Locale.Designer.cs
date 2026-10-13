namespace Picture.Locales {
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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("Picture.Locales.Locale", typeof(Locale).Assembly);
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
        
        public static string Picture => ResourceManager.GetString("Picture", resourceCulture) ?? "画框相册";
        public static string Picture_Title => ResourceManager.GetString("Picture_Title", resourceCulture) ?? "画框相册";
        public static string Picture_Subtitle => ResourceManager.GetString("Picture_Subtitle", resourceCulture) ?? "桌面精选照片相册，支持多图轮播与自定义裁切取景";
        public static string Picture_AddFiles => ResourceManager.GetString("Picture_AddFiles", resourceCulture) ?? "添加图片...";
        public static string Picture_AddFolder => ResourceManager.GetString("Picture_AddFolder", resourceCulture) ?? "添加文件夹...";
        public static string Picture_Delete => ResourceManager.GetString("Picture_Delete", resourceCulture) ?? "删除";
        public static string Picture_Clear => ResourceManager.GetString("Picture_Clear", resourceCulture) ?? "清空全部";
        public static string Picture_MoveUp => ResourceManager.GetString("Picture_MoveUp", resourceCulture) ?? "上移";
        public static string Picture_MoveDown => ResourceManager.GetString("Picture_MoveDown", resourceCulture) ?? "下移";
        public static string Picture_Interval => ResourceManager.GetString("Picture_Interval", resourceCulture) ?? "轮播时间间隔";
        public static string Picture_Interval_5s => ResourceManager.GetString("Picture_Interval_5s", resourceCulture) ?? "5 秒";
        public static string Picture_Interval_10s => ResourceManager.GetString("Picture_Interval_10s", resourceCulture) ?? "10 秒";
        public static string Picture_Interval_30s => ResourceManager.GetString("Picture_Interval_30s", resourceCulture) ?? "30 秒";
        public static string Picture_Interval_1m => ResourceManager.GetString("Picture_Interval_1m", resourceCulture) ?? "1 分钟";
        public static string Picture_Interval_5m => ResourceManager.GetString("Picture_Interval_5m", resourceCulture) ?? "5 分钟";
        public static string Picture_Interval_15m => ResourceManager.GetString("Picture_Interval_15m", resourceCulture) ?? "15 分钟";
        public static string Picture_Interval_30m => ResourceManager.GetString("Picture_Interval_30m", resourceCulture) ?? "30 分钟";
        public static string Picture_Interval_1h => ResourceManager.GetString("Picture_Interval_1h", resourceCulture) ?? "1 小时";
        public static string Picture_Interval_1d => ResourceManager.GetString("Picture_Interval_1d", resourceCulture) ?? "1 天";
        public static string Picture_Interval_Manual => ResourceManager.GetString("Picture_Interval_Manual", resourceCulture) ?? "手动切换 (不自动轮播)";
        public static string Picture_Order => ResourceManager.GetString("Picture_Order", resourceCulture) ?? "播放顺序";
        public static string Picture_Order_Seq => ResourceManager.GetString("Picture_Order_Seq", resourceCulture) ?? "顺序循环";
        public static string Picture_Order_Shuffle => ResourceManager.GetString("Picture_Order_Shuffle", resourceCulture) ?? "随机播放";
        public static string Picture_Order_Fixed => ResourceManager.GetString("Picture_Order_Fixed", resourceCulture) ?? "固定单张";
        public static string Picture_FitMode => ResourceManager.GetString("Picture_FitMode", resourceCulture) ?? "画面适应模式";
        public static string Picture_FitMode_CustomCrop => ResourceManager.GetString("Picture_FitMode_CustomCrop", resourceCulture) ?? "自定义手动裁切 (推荐)";
        public static string Picture_FitMode_Fill => ResourceManager.GetString("Picture_FitMode_Fill", resourceCulture) ?? "等比充满 (居中自适应裁切)";
        public static string Picture_FitMode_Fit => ResourceManager.GetString("Picture_FitMode_Fit", resourceCulture) ?? "完整显示 (四周留黑/留白)";
        public static string Picture_IsFrameless => ResourceManager.GetString("Picture_IsFrameless", resourceCulture) ?? "无边框铺满桌面";
        public static string Picture_ShowCaption => ResourceManager.GetString("Picture_ShowCaption", resourceCulture) ?? "显示图片名称标签";
        public static string Picture_ClickToNext => ResourceManager.GetString("Picture_ClickToNext", resourceCulture) ?? "单击小组件切换下一张";
        public static string Picture_DoubleClickToOpen => ResourceManager.GetString("Picture_DoubleClickToOpen", resourceCulture) ?? "双击在看图软件中打开原图";
        public static string Picture_CropSettings => ResourceManager.GetString("Picture_CropSettings", resourceCulture) ?? "当前所选图片裁切与取景";
        public static string Picture_CropX => ResourceManager.GetString("Picture_CropX", resourceCulture) ?? "水平取景偏移 (左 ~ 右)";
        public static string Picture_CropY => ResourceManager.GetString("Picture_CropY", resourceCulture) ?? "垂直取景偏移 (上 ~ 下)";
        public static string Picture_Zoom => ResourceManager.GetString("Picture_Zoom", resourceCulture) ?? "Zoom Level";
        public static string Picture_EmptyHint => ResourceManager.GetString("Picture_EmptyHint", resourceCulture) ?? "Right-click widget to add photos";
        public static string Picture_SelectImageHint => ResourceManager.GetString("Picture_SelectImageHint", resourceCulture) ?? "Select or add an image";
        public static string Picture_LibraryTitle => ResourceManager.GetString("Picture_LibraryTitle", resourceCulture) ?? "Photo Library";
        public static string Picture_LibrarySubtitle => ResourceManager.GetString("Picture_LibrarySubtitle", resourceCulture) ?? "Add photos or folders to display and rotate";
        public static string Picture_CropTip => ResourceManager.GetString("Picture_CropTip", resourceCulture) ?? "💡 Tip: Drag inside the preview to pan, scroll wheel to zoom";
        public static string Picture_QuickAlign => ResourceManager.GetString("Picture_QuickAlign", resourceCulture) ?? "Quick Align:";
        public static string Picture_Filter_Images => ResourceManager.GetString("Picture_Filter_Images", resourceCulture) ?? "Images & Animations (*.jpg, *.png, *.webp, *.gif, *.bmp, *.ico, *.tiff)";
        public static string Picture_Filter_Animations => ResourceManager.GetString("Picture_Filter_Animations", resourceCulture) ?? "Animations (*.gif, *.webp)";
        public static string Picture_Filter_AllFiles => ResourceManager.GetString("Picture_Filter_AllFiles", resourceCulture) ?? "All Files (*.*)";
    }
}
