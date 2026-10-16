using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Media;
using Clock.Models;
using Clock.ViewModels;
using DeskCanvas.Core.Interfaces;

namespace Clock.Views;

/// <summary>
/// 全新表盘-3：外圈 60 格动态刻度环 + 中部大字号圆角时间 + 顶部公历月日星期 + 底部农历换算日期。
/// 严格保持 1:1 正方形比例（实现 <see cref="IFixedSizeWidget"/>）。
/// 通过 <see cref="IWidgetSurfaceHints"/> 声明 Colorful 模式下的专属卡片画刷
/// （ClockAnalogIVCardBackground —— 亮色白卡、暗色黑卡，表盘全域即卡片，避免亮色模式下因卡片恒黑导致黑字黑线"一片黑"）。
/// </summary>
public partial class AnalogIV : UserControl, IFixedSizeWidget, IWidgetSurfaceHints
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "ClockAnalogIVCardBackground";

    private readonly TickDialClockViewModel viewModel;
    private readonly ClockModel clockModel;

    public IReadOnlyList<(int Columns, int Rows)> AllowedBaseSpans => AnalogI.AllowedBaseSpansOf;
    public IReadOnlyList<(int Columns, int Rows)> PresetSpans => AnalogI.PresetSpansOf;

    public bool IsAllowedSpan(int columns, int rows) => AnalogI.IsAllowedSpanOf(columns, rows);
    public (int Columns, int Rows) SnapSpan(int columns, int rows) => AnalogI.SnapSpanOf(columns, rows);

    public AnalogIV() : this(new ClockModel()) {}

    public AnalogIV(ClockModel clockModel)
    {
        this.clockModel = clockModel;
        viewModel = new TickDialClockViewModel(clockModel);
        DataContext = viewModel;
        Loaded += (_, _) => viewModel.Start();
        Unloaded += (_, _) => viewModel.Stop();
        SizeChanged += OnSizeChanged;
        InitializeComponent();
    }

    /// <summary>
    /// 表盘颜色解析纯函数，委托给 <see cref="AnalogI.ResolveDialColor"/>，保持测试与迁移契约向下兼容。
    /// </summary>
    public static Color? ResolveDialColor(ClockModel model, bool isDarkVariant) =>
        AnalogI.ResolveDialColor(model, isDarkVariant);

    // 1x1 tier: 隐藏日期与农历，保证时间大字与刻度在大致 80~120 DIP 尺寸下清晰可读
    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        var minDimension = Math.Min(e.NewSize.Width, e.NewSize.Height);
        bool isCompact = minDimension <= 120;
        DateBlock.IsVisible = !isCompact && viewModel.ShowDate;
        LunarBlock.IsVisible = !isCompact && viewModel.ShowDate && !string.IsNullOrEmpty(viewModel.LunarText);
    }
}
