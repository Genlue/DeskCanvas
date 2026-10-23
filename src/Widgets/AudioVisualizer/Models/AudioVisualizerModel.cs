using System;
using System.Collections.Generic;

namespace AudioVisualizer.Models;

/// <summary>
/// 音频律动组件持久化配置模型
/// </summary>
public record AudioVisualizerModel
{
    /// <summary>
    /// 是否隐藏背景（无边框模式，与一言组件一致）
    /// </summary>
    public bool IsFrameless { get; init; } = false;

    /// <summary>
    /// 音频识别源：电脑内音频 vs 录制/麦克风
    /// </summary>
    public AudioSourceType AudioSource { get; init; } = AudioSourceType.SystemAudio;

    /// <summary>
    /// 律动表现形式：柱状图、线条律动、波浪填充、光点
    /// </summary>
    public VisualizerStyle VisualizerStyle { get; init; } = VisualizerStyle.Bars;

    /// <summary>
    /// 律动方向：单向（向外/向上） vs 双向（两侧对称/内外起伏）
    /// </summary>
    public RhythmDirection RhythmDirection { get; init; } = RhythmDirection.Unidirectional;

    /// <summary>
    /// 创作画板绘制模式：平滑曲线、直线/折线、自由手绘
    /// </summary>
    public DrawToolMode DrawMode { get; init; } = DrawToolMode.Curve;

    /// <summary>
    /// 是否闭合图形（圆、心形、多边形等）
    /// </summary>
    public bool IsClosedShape { get; init; } = false;

    /// <summary>
    /// 归一化路径节点列表 (取值范围 0.0 ~ 1.0)
    /// </summary>
    public List<PathPoint> Points { get; init; } = [];

    /// <summary>
    /// 创作画板记录时的实际宽度与高度 (用于 1:1 画板与桌面尺寸对应)
    /// </summary>
    public double DesignWidth { get; init; } = 380;
    public double DesignHeight { get; init; } = 190;

    /// <summary>
    /// 采样频段数 / 柱子数量 (16 ~ 64)
    /// </summary>
    public int BarCount { get; init; } = 32;

    /// <summary>
    /// 柱体 / 线条宽度 (px)
    /// </summary>
    public double StrokeWidth { get; init; } = 5.0;

    /// <summary>
    /// 最大律动振幅 (px)
    /// </summary>
    public double MaxAmplitude { get; init; } = 55.0;

    /// <summary>
    /// 灵敏度倍率 (0.5 ~ 3.0)
    /// </summary>
    public double Sensitivity { get; init; } = 1.2;

    /// <summary>
    /// 平滑衰减率 (0.5 ~ 0.98)
    /// </summary>
    public double Smoothing { get; init; } = 0.82;

    /// <summary>
    /// 色彩主题预设
    /// </summary>
    public ColorThemePreset ColorTheme { get; init; } = ColorThemePreset.Cyberpunk;

    /// <summary>
    /// 自定义渐变起始色与终止色 (十六进制 Hex)
    /// </summary>
    public string StartColorHex { get; init; } = "#00E5FF";
    public string EndColorHex { get; init; } = "#FF007F";

    /// <summary>
    /// 是否开启外发光晕影
    /// </summary>
    public bool ShowGlow { get; init; } = true;

    /// <summary>
    /// 是否在桌面组件中显示绘制基准线
    /// </summary>
    public bool ShowBaseline { get; init; } = false;

    /// <summary>
    /// 规范化与边界防护
    /// </summary>
    public AudioVisualizerModel Normalize()
    {
        var pts = Points != null && Points.Count >= 2
            ? new List<PathPoint>(Points)
            : PresetShapes.CreateHorizontalLine().Points;

        int bars = Math.Clamp(BarCount, 12, 128);
        double stroke = Math.Clamp(StrokeWidth, 1.0, 20.0);
        double amp = Math.Clamp(MaxAmplitude, 10.0, 250.0);
        double sens = Math.Clamp(Sensitivity, 0.2, 5.0);
        double smooth = Math.Clamp(Smoothing, 0.3, 0.98);

        return this with
        {
            Points = pts,
            BarCount = bars,
            StrokeWidth = stroke,
            MaxAmplitude = amp,
            Sensitivity = sens,
            Smoothing = smooth,
            DesignWidth = DesignWidth > 0 ? DesignWidth : 380,
            DesignHeight = DesignHeight > 0 ? DesignHeight : 190
        };
    }
}
