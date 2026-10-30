namespace AudioVisualizer.Models;

/// <summary>
/// 律动表现形式 (Visualizer Style)
/// </summary>
public enum VisualizerStyle
{
    /// <summary>
    /// 柱状频谱律动 (Spectrum Bars)
    /// </summary>
    Bars = 0,

    /// <summary>
    /// 线条波浪律动 (Smooth Wave Line)
    /// </summary>
    LineWave = 1,

    /// <summary>
    /// 渐变波浪填充 (Gradient Area Wave)
    /// </summary>
    AreaWave = 2,

    /// <summary>
    /// 粒子光点律动 (Dancing Dots)
    /// </summary>
    Dots = 3
}

/// <summary>
/// 律动震荡方向 (Rhythm Direction)
/// </summary>
public enum RhythmDirection
{
    /// <summary>
    /// 单向：向外或向上单侧延展
    /// </summary>
    Unidirectional = 0,

    /// <summary>
    /// 双向：双侧对称或内外起伏震荡
    /// </summary>
    Bidirectional = 1
}

/// <summary>
/// 音频识别源 (Audio Source)
/// </summary>
public enum AudioSourceType
{
    /// <summary>
    /// 电脑内音频 (WASAPI Loopback 系统声音/播放音乐)
    /// </summary>
    SystemAudio = 0,

    /// <summary>
    /// 录制 / 麦克风 (WASAPI Capture 环境录音/说话)
    /// </summary>
    Microphone = 1
}

/// <summary>
/// 创作面板画笔绘制工具模式
/// </summary>
public enum DrawToolMode
{
    /// <summary>
    /// 平滑曲线绘制 (Spline / Bézier)
    /// </summary>
    Curve = 0,

    /// <summary>
    /// 直线与折线点绘制 (Polyline)
    /// </summary>
    Line = 1,

    /// <summary>
    /// 自由手绘随心画 (Freehand)
    /// </summary>
    Freehand = 2
}

/// <summary>
/// 色彩主题预设
/// </summary>
public enum ColorThemePreset
{
    Cyberpunk = 0,   // 赛博霓虹 (青色 -> 荧光粉)
    Aurora = 1,      // 极光幻境 (荧光绿 -> 湖水蓝)
    Sunset = 2,      // 烈焰暖阳 (日落橙 -> 熔岩红)
    Glacier = 3,     // 冰川极光 (科技天蓝 -> 纯白)
    Violet = 4,      // 紫罗兰之夜 (电紫 -> 霓虹品红)
    MinimalWhite = 5 // 纯白极简
}
