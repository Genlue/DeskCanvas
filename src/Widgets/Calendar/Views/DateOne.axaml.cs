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

/// <summary>1x1 日期小组件，日号字号按卡片的物理像素推导。</summary>
public partial class DateOne : UserControl, IWidgetSurfaceHints, IWidgetActivityConsumer
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
    public DateOne(IWidgetSession? session = null)
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
    /// 按物理像素推导日号字号，并用基线补偿让墨迹居中；表头随之同步缩放。
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
        // BuildGeometry has no glyphs to return for some text/typeface pairs (null geometry);
        // treat that as "no measurable ink" so the caller keeps its current size.
        var inkGeometry = formatted.BuildGeometry(default);
        var tightInkWidthPerEm = inkGeometry is null ? 0 : inkGeometry.Bounds.Width / ReferenceFontSize;
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
