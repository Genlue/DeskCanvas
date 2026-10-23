using System;
using System.Collections.Generic;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskCanvas.Core.Interfaces;
using AudioVisualizer.Models;
using AudioVisualizer.Services;

namespace AudioVisualizer.Views.Settings;

public partial class AudioVisualizerSettings : UserControl
{
    private readonly IWidgetLayoutProvider widgetLayoutProvider;
    private AudioVisualizerModel model;
    private bool initializing = true;
    private readonly double actualBoardWidth;
    private readonly double actualBoardHeight;

    private static readonly (VisualizerStyle Style, string Label)[] StyleOptions =
    [
        (VisualizerStyle.Bars, "柱状图律动 (Spectrum Bars)"),
        (VisualizerStyle.LineWave, "线条波浪律动 (Line Wave)"),
        (VisualizerStyle.AreaWave, "渐变波浪填充 (Area Wave)"),
        (VisualizerStyle.Dots, "粒子光点律动 (Dancing Dots)")
    ];

    private static readonly (RhythmDirection Direction, string Label)[] DirectionOptions =
    [
        (RhythmDirection.Unidirectional, "单向 (向上/向外单侧延展)"),
        (RhythmDirection.Bidirectional, "双向 (双侧对称/内外起伏震荡)")
    ];

    private static readonly (AudioSourceType Source, string Label)[] AudioSourceOptions =
    [
        (AudioSourceType.SystemAudio, "电脑内音频 (系统播放/扬声器回放)"),
        (AudioSourceType.Microphone, "录制 / 麦克风 (环境声音/麦克风采集)")
    ];

    private static readonly (DrawToolMode Mode, string Label)[] DrawModeOptions =
    [
        (DrawToolMode.Curve, "平滑曲线 (Spline Curve)"),
        (DrawToolMode.Line, "直线/折线 (Straight Lines)"),
        (DrawToolMode.Freehand, "随心手绘 (Freehand)")
    ];

    private static readonly (int Count, string Label)[] BarCountOptions =
    [
        (16, "16 频段 (轻量极简)"),
        (24, "24 频段 (标准清爽)"),
        (32, "32 频段 (经典推荐)"),
        (48, "48 频段 (高密度细腻)"),
        (64, "64 频段 (全频段震撼)")
    ];

    private static readonly (ColorThemePreset Theme, string Label)[] ColorThemeOptions =
    [
        (ColorThemePreset.Cyberpunk, "赛博霓虹 (Cyberpunk: 青与粉)"),
        (ColorThemePreset.Aurora, "极光幻境 (Aurora: 绿与天蓝)"),
        (ColorThemePreset.Sunset, "烈焰暖阳 (Sunset: 橙与红)"),
        (ColorThemePreset.Glacier, "冰川极光 (Glacier: 冰蓝与白)"),
        (ColorThemePreset.Violet, "紫罗兰之夜 (Violet: 电紫与橙)"),
        (ColorThemePreset.MinimalWhite, "纯白极简 (Pure White)")
    ];

    public AudioVisualizerSettings(IWidgetLayoutProvider widgetLayoutProvider)
        : this(widgetLayoutProvider, widgetLayoutProvider.Get().GetModel<AudioVisualizerModel>() ?? new AudioVisualizerModel())
    {
    }

    public AudioVisualizerSettings(IWidgetLayoutProvider widgetLayoutProvider, AudioVisualizerModel model)
    {
        this.widgetLayoutProvider = widgetLayoutProvider;
        this.model = model.Normalize();

        var layout = widgetLayoutProvider.Get();
        // 创作画板尺寸严格根据当前桌面组件实际尺寸 (layout.Width × layout.Height)
        actualBoardWidth = layout.Width > 0 ? layout.Width : 380;
        actualBoardHeight = layout.Height > 0 ? layout.Height : 190;

        InitializeComponent();
        SetupDrawingBoard();
        PopulateControls();
        initializing = false;
    }

    private void SetupDrawingBoard()
    {
        CanvasSizeTitleText.Text = $"画板比例与映射: {(int)Math.Round(actualBoardWidth)} × {(int)Math.Round(actualBoardHeight)} px (自适应等比显示)";
        DrawingBoard.SetWidgetProportions(actualBoardWidth, actualBoardHeight);
        DrawingBoard.Model = model;
        DrawingBoard.PathUpdated += OnDrawingBoardPathUpdated;
    }

    private void OnCenterClicked(object? sender, RoutedEventArgs e)
    {
        DrawingBoard.CenterShape();
    }

    private void OnSmoothClicked(object? sender, RoutedEventArgs e)
    {
        DrawingBoard.SmoothPath();
    }

    private void PopulateControls()
    {
        // 1. 绘制工具模式
        DrawModeCombo.ItemsSource = Array.ConvertAll(DrawModeOptions, x => x.Label);
        int drawIdx = Array.FindIndex(DrawModeOptions, x => x.Mode == model.DrawMode);
        DrawModeCombo.SelectedIndex = drawIdx >= 0 ? drawIdx : 0;

        // 2. 闭合图形
        ClosedShapeToggle.IsChecked = model.IsClosedShape;

        // 3. 预设形状
        PresetsCombo.ItemsSource = new List<string>
        {
            "水平直线 (单向底线)",
            "居中基线 (双向推荐)",
            "正弦波浪 (优雅海浪)",
            "动感圆环 (闭合圆环)",
            "浪漫爱心 (心形闭合)",
            "科技八边形 (多边形)",
            "优雅拱形 (曲线弧形)"
        };

        // 4. 表现形式
        StyleCombo.ItemsSource = Array.ConvertAll(StyleOptions, x => x.Label);
        int styleIdx = Array.FindIndex(StyleOptions, x => x.Style == model.VisualizerStyle);
        StyleCombo.SelectedIndex = styleIdx >= 0 ? styleIdx : 0;

        // 5. 律动方向
        DirectionCombo.ItemsSource = Array.ConvertAll(DirectionOptions, x => x.Label);
        int dirIdx = Array.FindIndex(DirectionOptions, x => x.Direction == model.RhythmDirection);
        DirectionCombo.SelectedIndex = dirIdx >= 0 ? dirIdx : 0;

        // 6. 隐藏背景
        FramelessToggle.IsChecked = model.IsFrameless;

        // 7. 粗细与振幅
        StrokeWidthBox.Value = (decimal)model.StrokeWidth;
        MaxAmplitudeBox.Value = (decimal)model.MaxAmplitude;

        // 8. 频段数
        BarCountCombo.ItemsSource = Array.ConvertAll(BarCountOptions, x => x.Label);
        int barIdx = Array.FindIndex(BarCountOptions, x => x.Count == model.BarCount);
        BarCountCombo.SelectedIndex = barIdx >= 0 ? barIdx : 2;

        // 9. 音频源
        AudioSourceCombo.ItemsSource = Array.ConvertAll(AudioSourceOptions, x => x.Label);
        int srcIdx = Array.FindIndex(AudioSourceOptions, x => x.Source == model.AudioSource);
        AudioSourceCombo.SelectedIndex = srcIdx >= 0 ? srcIdx : 0;

        // 10. 灵敏度与平滑度
        SensitivityBox.Value = (decimal)model.Sensitivity;
        SmoothingBox.Value = (decimal)model.Smoothing;

        // 11. 色彩主题
        ColorThemeCombo.ItemsSource = Array.ConvertAll(ColorThemeOptions, x => x.Label);
        int themeIdx = Array.FindIndex(ColorThemeOptions, x => x.Theme == model.ColorTheme);
        ColorThemeCombo.SelectedIndex = themeIdx >= 0 ? themeIdx : 0;

        // 12. 开关
        ShowGlowToggle.IsChecked = model.ShowGlow;
        ShowBaselineToggle.IsChecked = model.ShowBaseline;
    }

    private void OnDrawingBoardPathUpdated(List<PathPoint> points, bool isClosed, DrawToolMode mode)
    {
        if (initializing) return;
        SaveModel(model with
        {
            Points = points,
            IsClosedShape = isClosed,
            DrawMode = mode,
            DesignWidth = actualBoardWidth,
            DesignHeight = actualBoardHeight
        });

        ClosedShapeToggle.IsChecked = isClosed;
    }

    private void OnDrawModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = DrawModeCombo.SelectedIndex;
        if (idx >= 0 && idx < DrawModeOptions.Length)
        {
            var mode = DrawModeOptions[idx].Mode;
            DrawingBoard.SetDrawMode(mode);
            SaveModel(model with { DrawMode = mode });
        }
    }

    private void OnClosedShapeChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        bool closed = ClosedShapeToggle.IsChecked == true;
        DrawingBoard.SetClosedShape(closed);
        SaveModel(model with { IsClosedShape = closed });
    }

    private void OnPresetSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing || PresetsCombo.SelectedIndex < 0) return;

        (List<PathPoint> points, bool isClosed, DrawToolMode mode) preset = PresetsCombo.SelectedIndex switch
        {
            0 => PresetShapes.CreateHorizontalLine(),
            1 => PresetShapes.CreateCenterLine(),
            2 => PresetShapes.CreateSineWave(),
            3 => PresetShapes.CreateCircle(),
            4 => PresetShapes.CreateHeart(),
            5 => PresetShapes.CreateOctagon(),
            6 => PresetShapes.CreateArch(),
            _ => PresetShapes.CreateHorizontalLine()
        };

        DrawingBoard.ApplyPreset(preset.points, preset.isClosed, preset.mode);
        ClosedShapeToggle.IsChecked = preset.isClosed;

        SaveModel(model with
        {
            Points = preset.points,
            IsClosedShape = preset.isClosed,
            DrawMode = preset.mode,
            DesignWidth = actualBoardWidth,
            DesignHeight = actualBoardHeight
        });
    }

    private void OnUndoClicked(object? sender, RoutedEventArgs e)
    {
        DrawingBoard.Undo();
    }

    private void OnClearClicked(object? sender, RoutedEventArgs e)
    {
        DrawingBoard.Clear();
    }

    private void OnLivePreviewToggleChanged(object? sender, RoutedEventArgs e)
    {
        DrawingBoard.LivePreviewEnabled = LivePreviewToggle.IsChecked == true;
    }

    private void OnStyleChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = StyleCombo.SelectedIndex;
        if (idx >= 0 && idx < StyleOptions.Length)
        {
            SaveModel(model with { VisualizerStyle = StyleOptions[idx].Style });
        }
    }

    private void OnDirectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = DirectionCombo.SelectedIndex;
        if (idx >= 0 && idx < DirectionOptions.Length)
        {
            SaveModel(model with { RhythmDirection = DirectionOptions[idx].Direction });
        }
    }

    private void OnFramelessChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        SaveModel(model with { IsFrameless = FramelessToggle.IsChecked == true });
    }

    private void OnStrokeWidthChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        double val = (double)(StrokeWidthBox.Value ?? (decimal)model.StrokeWidth);
        SaveModel(model with { StrokeWidth = val });
    }

    private void OnMaxAmplitudeChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        double val = (double)(MaxAmplitudeBox.Value ?? (decimal)model.MaxAmplitude);
        SaveModel(model with { MaxAmplitude = val });
    }

    private void OnBarCountChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = BarCountCombo.SelectedIndex;
        if (idx >= 0 && idx < BarCountOptions.Length)
        {
            SaveModel(model with { BarCount = BarCountOptions[idx].Count });
        }
    }

    private void OnAudioSourceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = AudioSourceCombo.SelectedIndex;
        if (idx >= 0 && idx < AudioSourceOptions.Length)
        {
            var src = AudioSourceOptions[idx].Source;
            AudioCaptureService.Instance.SwitchSource(src);
            SaveModel(model with { AudioSource = src });
        }
    }

    private void OnSensitivityChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        double val = (double)(SensitivityBox.Value ?? (decimal)model.Sensitivity);
        SaveModel(model with { Sensitivity = val });
    }

    private void OnSmoothingChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (initializing) return;
        double val = (double)(SmoothingBox.Value ?? (decimal)model.Smoothing);
        SaveModel(model with { Smoothing = val });
    }

    private void OnColorThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (initializing) return;
        int idx = ColorThemeCombo.SelectedIndex;
        if (idx >= 0 && idx < ColorThemeOptions.Length)
        {
            SaveModel(model with { ColorTheme = ColorThemeOptions[idx].Theme });
        }
    }

    private void OnGlowChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        SaveModel(model with { ShowGlow = ShowGlowToggle.IsChecked == true });
    }

    private void OnBaselineChanged(object? sender, RoutedEventArgs e)
    {
        if (initializing) return;
        SaveModel(model with { ShowBaseline = ShowBaselineToggle.IsChecked == true });
    }

    private void SaveModel(AudioVisualizerModel updated)
    {
        model = updated.Normalize();
        DrawingBoard.Model = model;

        var currentLayout = widgetLayoutProvider.Get();
        var serialized = JsonSerializer.SerializeToElement(model);
        widgetLayoutProvider.Save(currentLayout with { Settings = serialized });
    }
}
