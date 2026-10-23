#nullable enable
namespace AudioVisualizer.Locales
{
    using System;

    public class Locale
    {
        private static global::System.Resources.ResourceManager? resourceMan;
        private static global::System.Globalization.CultureInfo? resourceCulture;

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
                    global::System.Resources.ResourceManager temp = new global::System.Resources.ResourceManager("AudioVisualizer.Locales.Locale", typeof(Locale).Assembly);
                    resourceMan = temp;
                }
                return resourceMan;
            }
        }

        [global::System.ComponentModel.EditorBrowsableAttribute(global::System.ComponentModel.EditorBrowsableState.Advanced)]
        public static global::System.Globalization.CultureInfo? Culture
        {
            get => resourceCulture;
            set => resourceCulture = value;
        }

        public static string AudioVisualizer => ResourceManager.GetString("AudioVisualizer", resourceCulture) ?? "音频律动";
        public static string AudioVisualizer_Title => ResourceManager.GetString("AudioVisualizer_Title", resourceCulture) ?? "音频律动";
        public static string AudioVisualizer_Subtitle => ResourceManager.GetString("AudioVisualizer_Subtitle", resourceCulture) ?? "任意尺寸自适应音频律动组件，支持创作画板自由绘制曲线直线闭合图形、柱状或线条律动、单向双向及内外音频源";
        
        public static string Visualizer_SectionAppearance => ResourceManager.GetString("Visualizer_SectionAppearance", resourceCulture) ?? "律动表现与外观样式";
        public static string Visualizer_SectionCreation => ResourceManager.GetString("Visualizer_SectionCreation", resourceCulture) ?? "创作面板 (自由绘制线条与形状)";
        public static string Visualizer_SectionAudio => ResourceManager.GetString("Visualizer_SectionAudio", resourceCulture) ?? "音频识别源与响应设置";

        public static string Visualizer_Style => ResourceManager.GetString("Visualizer_Style", resourceCulture) ?? "律动表现形式";
        public static string Visualizer_Style_Subtitle => ResourceManager.GetString("Visualizer_Style_Subtitle", resourceCulture) ?? "支持柱状图律动、线条波动或渐变波浪填充";

        public static string Visualizer_Direction => ResourceManager.GetString("Visualizer_Direction", resourceCulture) ?? "律动震荡方向";
        public static string Visualizer_Direction_Subtitle => ResourceManager.GetString("Visualizer_Direction_Subtitle", resourceCulture) ?? "单向（向上/向外）或双向（两侧对称/内外起伏震荡）";

        public static string Visualizer_Frameless => ResourceManager.GetString("Visualizer_Frameless", resourceCulture) ?? "隐藏背景 (无边框桌面悬浮模式)";
        public static string Visualizer_Frameless_Subtitle => ResourceManager.GetString("Visualizer_Frameless_Subtitle", resourceCulture) ?? "隐藏卡片边框与背景底板，律动线条直接在桌面壁纸上空灵跳跃";

        public static string Visualizer_AudioSource => ResourceManager.GetString("Visualizer_AudioSource", resourceCulture) ?? "音频识别源";
        public static string Visualizer_AudioSource_Subtitle => ResourceManager.GetString("Visualizer_AudioSource_Subtitle", resourceCulture) ?? "切换声音采集来源：电脑内音频（系统播放）或录制/麦克风（外界声音）";

        public static string Visualizer_BarCount => ResourceManager.GetString("Visualizer_BarCount", resourceCulture) ?? "采样频段数 / 柱子数量";
        public static string Visualizer_BarCount_Subtitle => ResourceManager.GetString("Visualizer_BarCount_Subtitle", resourceCulture) ?? "沿绘制路径采集分布的频谱频段数量";

        public static string Visualizer_StrokeWidth => ResourceManager.GetString("Visualizer_StrokeWidth", resourceCulture) ?? "柱体 / 线条粗细";
        public static string Visualizer_StrokeWidth_Subtitle => ResourceManager.GetString("Visualizer_StrokeWidth_Subtitle", resourceCulture) ?? "律动柱子宽度或波浪线条的笔触粗细 (px)";

        public static string Visualizer_MaxAmplitude => ResourceManager.GetString("Visualizer_MaxAmplitude", resourceCulture) ?? "最大律动振幅";
        public static string Visualizer_MaxAmplitude_Subtitle => ResourceManager.GetString("Visualizer_MaxAmplitude_Subtitle", resourceCulture) ?? "音频节奏爆发时柱子或线条的最大延展高度 (px)";

        public static string Visualizer_Sensitivity => ResourceManager.GetString("Visualizer_Sensitivity", resourceCulture) ?? "音频灵敏度";
        public static string Visualizer_Sensitivity_Subtitle => ResourceManager.GetString("Visualizer_Sensitivity_Subtitle", resourceCulture) ?? "控制对微弱或轻柔音乐节奏的放大响应倍率";

        public static string Visualizer_Smoothing => ResourceManager.GetString("Visualizer_Smoothing", resourceCulture) ?? "平滑衰减度";
        public static string Visualizer_Smoothing_Subtitle => ResourceManager.GetString("Visualizer_Smoothing_Subtitle", resourceCulture) ?? "节奏回落时的阻尼衰减速度，值越大越丝滑";

        public static string Visualizer_ColorTheme => ResourceManager.GetString("Visualizer_ColorTheme", resourceCulture) ?? "色彩主题与渐变";
        public static string Visualizer_ColorTheme_Subtitle => ResourceManager.GetString("Visualizer_ColorTheme_Subtitle", resourceCulture) ?? "赛博霓虹、极光幻境、烈焰暖阳等酷炫色彩预设";

        public static string Visualizer_ShowGlow => ResourceManager.GetString("Visualizer_ShowGlow", resourceCulture) ?? "霓虹外发光效果";
        public static string Visualizer_ShowGlow_Subtitle => ResourceManager.GetString("Visualizer_ShowGlow_Subtitle", resourceCulture) ?? "在律动线条和柱体外侧渲染半透明发光晕影";

        public static string Visualizer_ShowBaseline => ResourceManager.GetString("Visualizer_ShowBaseline", resourceCulture) ?? "显示绘制基准线";
        public static string Visualizer_ShowBaseline_Subtitle => ResourceManager.GetString("Visualizer_ShowBaseline_Subtitle", resourceCulture) ?? "在桌面组件中保留绘制的原始基准轮廓线";

        public static string Visualizer_DrawMode => ResourceManager.GetString("Visualizer_DrawMode", resourceCulture) ?? "绘制工具模式";
        public static string Visualizer_ClosedShape => ResourceManager.GetString("Visualizer_ClosedShape", resourceCulture) ?? "闭合图形";
        public static string Visualizer_ClosedShape_Subtitle => ResourceManager.GetString("Visualizer_ClosedShape_Subtitle", resourceCulture) ?? "首尾相连形成闭合轮廓（圆形、多边形或自定义闭合图案）";

        public static string Visualizer_Undo => ResourceManager.GetString("Visualizer_Undo", resourceCulture) ?? "撤销";
        public static string Visualizer_Clear => ResourceManager.GetString("Visualizer_Clear", resourceCulture) ?? "清空重画";
        public static string Visualizer_Presets => ResourceManager.GetString("Visualizer_Presets", resourceCulture) ?? "经典形状预设";
        public static string Visualizer_LivePreview => ResourceManager.GetString("Visualizer_LivePreview", resourceCulture) ?? "画板内实时律动预览";
    }
}
