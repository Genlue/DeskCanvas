using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Calendar.ViewModels;
using DeskCanvas.Contracts;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Runtime;

namespace Calendar.Views;

/// <summary>
/// 1x1 日期小组件：通过 IWidgetSurfaceHints 声明 Colorful 模式下的专属卡片画刷。
/// <para>
/// 日号与表头的字号在 <see cref="ApplyFontSizes"/> 里按卡片实际渲染的物理像素数
/// （DIP × DPI × 宿主 ContentScale）推导：数字墨迹高 = 卡片高 × 0.66，并经
/// FormattedText.Baseline 做基线锚定补偿使墨迹居中于日号格——墨迹严格落在行 1 格内，
/// 不会截断。旧版内层 Viewbox + 负 Margin 收行盒空白（超过实际空白时墨迹伸出布局盒
/// 被裁掉）正是顶部截断的根源，已随外层 Viewbox 一起移除。
/// </para>
/// </summary>
public partial class Date : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
{
    /// <inheritdoc />
    public string? ColorfulBackgroundResourceKey => "CalendarCardBackground";

    /// <summary>字号推导的参考字号：墨迹占比与字号无关，在 100 号基准下量取。</summary>
    private const double ReferenceFontSize = 100;

    /// <summary>日号目标墨迹高，占卡片高的比例（0.66；墨迹完整落在行 1 格内，不截断）。</summary>
    public const double DayInkCardHeightRatio = 0.66;

    /// <summary>日号紧墨迹宽的上限，占卡片宽的比例（按 BuildGeometry 的 tight 墨迹宽计，非排版推进宽）。</summary>
    public const double DayInkCardWidthRatio = 0.94;

    /// <summary>表头 em 字号，占卡高的比例（CJK 墨迹 ≈ 0.74 em → 墨迹 ≈ 0.14 卡高，同参考图）。</summary>
    private const double HeaderEmRatio = 0.19;

    private readonly DateCalendarViewModel viewModel;

    /// <summary>
    /// <paramref name="session"/> is injected by WidgetFactory when the host runs the session
    /// pipeline (a stable instance identity): the view model then gates its timer subscription
    /// on the session's activity. Null keeps the legacy always-on behaviour (preview, legacy
    /// layout entries) — those hosts never push activity either.
    /// </summary>
    public Date(IWidgetSession? session = null)
    {
        viewModel = new DateCalendarViewModel(session);
        DataContext = viewModel;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => ApplyFontSizes();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DateCalendarViewModel.Day))
                ApplyFontSizes();
        };
        InitializeComponent();
    }

    /// <inheritdoc />
    public void OnSessionActivity(ActivitySnapshot activity) => viewModel.OnSessionActivity(activity);

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        viewModel.Start();
        if (TopLevel.GetTopLevel(this) is { } topLevel)
            topLevel.ScalingChanged += OnScalingChanged;
        ApplyFontSizes();
    }

    private void OnUnloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        viewModel.Stop();
        if (TopLevel.GetTopLevel(this) is { } topLevel)
            topLevel.ScalingChanged -= OnScalingChanged;
    }

    private void OnScalingChanged(object? sender, EventArgs e) => ApplyFontSizes();

    /// <summary>
    /// 按卡片实际渲染的物理像素数确定字号。日号：目标墨迹高 = 卡片高 ×
    /// <see cref="DayInkCardHeightRatio"/>，并受紧墨迹宽 ≤ 卡宽 ×
    /// <see cref="DayInkCardWidthRatio"/> 约束，取小者；墨迹占比在
    /// <see cref="ReferenceFontSize"/> 基准下用 FormattedText 量出（Extent = 实际墨迹高、
    /// BuildGeometry.Bounds = 紧墨迹宽），与字号无关，换任何天数（"1"…"31"）都成立。
    /// 目标先以物理像素表达（DIP × RenderScaling × 宿主 ContentScale），再除回每 DIP 的
    /// 物理像素数得到 DIP 字号——对任何 DPI、显示器缩放与组件缩放（ContentScale），
    /// 数字都占同样的真实像素比例。
    /// <para>
    /// 基线锚定补偿：TextBlock 的墨迹挂在基线上，行盒被格高钳制时字号越大墨迹越随
    /// 基线下坠（0.44 卡高时已有 +6.5px 下偏，0.66 会直接坠出卡底）。因此日号改为
    /// 顶端对齐 + 按 FormattedText.Baseline 计算的上边距，使墨迹中心恒等于日号格
    /// 中心——墨迹（非行盒）严格落在行 1 格内，不会截断；行盒的透明空白越界无害。
    /// </para>
    /// 表头字号按卡高 × <see cref="HeaderEmRatio"/> 的 em 值推导，两块标签同步缩放。
    /// </summary>
    private void ApplyFontSizes()
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var pixelPerDip = (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) * ResolveHostContentScale();

        var headerEm = height * HeaderEmRatio;
        WeekdayLabel.FontSize = headerEm;
        MonthLabel.FontSize = headerEm;

        var dayText = DayLabel.Text;
        if (string.IsNullOrEmpty(dayText)) return;

        var typeface = new Typeface(DayLabel.FontFamily, FontStyle.Normal, DayLabel.FontWeight);
        var formatted = new FormattedText(
            dayText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            ReferenceFontSize,
            Brushes.Black);

        var inkHeightPerEm = formatted.Extent / ReferenceFontSize;
        var tightInkWidthPerEm = formatted.BuildGeometry(default).Bounds.Width / ReferenceFontSize;
        if (inkHeightPerEm <= 0 || tightInkWidthPerEm <= 0) return;

        // 目标物理像素 ÷ 每 DIP 物理像素数 = DIP 字号；高度与宽度约束取小。
        var byHeight = height * DayInkCardHeightRatio / inkHeightPerEm;
        var byWidth = width * DayInkCardWidthRatio / tightInkWidthPerEm;
        DayLabel.FontSize = Math.Min(byHeight, byWidth);

        // 基线锚定补偿（见 XML doc）：margin = (格高 + 墨迹高)/2 − 基线距行盒顶距离，
        // 使 块顶 + 基线 − 墨迹高（墨迹顶）= 格顶 + (格高 − 墨迹高)/2（墨迹居中于格）。
        var fontSize = DayLabel.FontSize;
        var inkHeight = fontSize * inkHeightPerEm;
        var baseline = formatted.Baseline / ReferenceFontSize * fontSize;
        var cellHeight = height * 0.8;
        DayLabel.Margin = new Thickness(0, (cellHeight + inkHeight) / 2 - baseline, 0, 0);
    }

    /// <summary>
    /// 宿主 ContentPresenter 的等比 RenderTransform（Widget.ContentScale），无边框时钟
    /// （FramelessDigital.ResolveHostContentScale）同款算法：渲染变换不参与布局，视图的
    /// Bounds 是缩放前的 DIP 尺寸，换算物理像素时必须把它计入。
    /// </summary>
    private double ResolveHostContentScale() =>
        Parent is Visual parent && parent.RenderTransform is ScaleTransform scaleTransform
            ? scaleTransform.ScaleX
            : 1.0;
}
