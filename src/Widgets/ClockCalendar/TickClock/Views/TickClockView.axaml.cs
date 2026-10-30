using System;
using System.ComponentModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Services;
using TickClock.Models;
using TickClock.ViewModels;

namespace TickClock.Views;

/// <summary>
/// 刻度时钟：白色方卡 + 一圈刻度 + 红色星期 + 超大时间 + 底部日期。
/// </summary>
public partial class TickClockView : UserControl, IWidgetSurfaceHints, IWidgetSelfRefreshing
{
    /// <summary>参考图卡片铺的是纯白底，多彩主题下沿用它。</summary>
    public string? ColorfulBackgroundResourceKey => "TickClockCardBackground";

    private readonly TickClockViewModel viewModel;
    private DeskCanvas.Views.Widget? hostWidget;

    public TickClockView() : this(new TickClockModel()) { }

    public TickClockView(TickClockModel model)
    {
        viewModel = new TickClockViewModel(model);
        DataContext = viewModel;

        InitializeComponent();

        Loaded += (_, _) =>
        {
            viewModel.Start();
            ApplyScale(Bounds.Size);
            UpdateWeekdayBrush();
            BindHostCorner();
        };
        ActualThemeVariantChanged += (_, _) => UpdateWeekdayBrush();
        Unloaded += (_, _) =>
        {
            viewModel.Stop();
            UnbindHostCorner();
        };
        SizeChanged += (_, e) => ApplyScale(e.NewSize);
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TickClockViewModel.ShowWeekday)
                or nameof(TickClockViewModel.ShowDate))
            {
                ApplyScale(Bounds.Size);
            }
        };

        ApplyScale(new Size(220, 220));
    }

    /// <summary>
    /// 按卡片的短边缩放整套字面：参考稿是 430×430，1:1 抄下比例后
    /// 任何网格间距/缩放下都保持同样的观感。
    /// </summary>
    private void ApplyScale(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0 || double.IsInfinity(size.Width) || double.IsInfinity(size.Height))
            return;

        double shortSide = Math.Min(size.Width, size.Height);
        double u = shortSide / 430.0;

        // 单格（≈100px 以下）塞不下三行，只留时间并以整卡为中心。
        bool compact = shortSide < 118;

        WeekdayLabel.FontSize = Math.Max(7, 54 * u);
        DateLabel.FontSize = Math.Max(7, 54 * u);

        // 时间盒：宽度是卡片宽度的 83%，高度是卡片高度的 35%（与参考图测量值一致）。
        // 盒子始终锁在参考图的宽高比 2.37 上——非方形卡片（4x2 长条、竖卡）下
        // 只取两个方向里更小的那个，绝不把数字拉成扁片或瘦条。
        const double timeAspect = 2.55;
        double availableWidth = size.Width * (compact ? 0.86 : 0.83);
        double availableHeight = size.Height * (compact ? 0.80 : 0.38);
        double boxWidth = Math.Min(availableWidth, availableHeight * timeAspect);
        TimeLabel.Width = Math.Max(0, boxWidth);
        TimeLabel.Height = Math.Max(0, boxWidth / timeAspect);

        WeekdayLabel.IsVisible = !compact && viewModel.ShowWeekday;
        DateLabel.IsVisible = !compact && viewModel.ShowDate;

        var rows = TextGrid.RowDefinitions;
        if (compact)
        {
            SetRow(rows, 0, 0);
            SetRow(rows, 1, 0);
            SetRow(rows, 2, 0);
            SetRow(rows, 3, 1);
            SetRow(rows, 4, 0);
            SetRow(rows, 5, 0);
            SetRow(rows, 6, 0);
            return;
        }

        SetRow(rows, 0, 11);
        SetRow(rows, 1, 13);
        SetRow(rows, 2, 6);
        SetRow(rows, 3, 38);
        SetRow(rows, 4, 7);
        SetRow(rows, 5, 11);
        SetRow(rows, 6, 14);
    }

    /// <summary>
    /// 找到承载本组件的桌面卡片并订阅它的圆角：用户改圆角/切主题时刻度环跟着重算，
    /// 始终与卡片边缘保持同心内缩。找不到宿主（无头预览）时控件退回比例值。
    /// </summary>
    private void BindHostCorner()
    {
        if (hostWidget != null) return;

        for (var node = this.GetVisualParent(); node != null; node = node.GetVisualParent())
        {
            if (node is not DeskCanvas.Views.Widget widget) continue;
            hostWidget = widget;
            hostWidget.PropertyChanged += OnHostWidgetPropertyChanged;
            ApplyCardCorner();
            return;
        }
    }

    private void UnbindHostCorner()
    {
        if (hostWidget == null) return;
        hostWidget.PropertyChanged -= OnHostWidgetPropertyChanged;
        hostWidget = null;
    }

    private void OnHostWidgetPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(DeskCanvas.Views.Widget.Radius))
            ApplyCardCorner();
    }

    private void ApplyCardCorner()
    {
        if (hostWidget == null) return;
        DialTicks.CardCornerRadius = hostWidget.Radius.TopLeft;
    }

    /// <summary>
    /// 星期标签取色：多彩（全彩）主题用参考图的朱红；其余主题跟随系统强调色
    /// （单色主题的强调色由主题字典锁成黑白/背景色，所以这里不需要再分支）。
    /// 资源键用 <c>SystemControlForegroundAccentBrush</c>——与 FlipClock / Calendar 的今日点同源。
    /// </summary>
    private void UpdateWeekdayBrush()
    {
        try
        {
            var provider = DeskCanvas.App.Services?.GetService(typeof(IAppSettingsProvider)) as IAppSettingsProvider;
            var theme = provider?.Get()?.Theme;

            // 全彩：参考图的朱红。
            if (theme?.IsColorful == true)
            {
                WeekdayLabel.Foreground = viewModel.AccentBrush;
                return;
            }

            // 单色：跟主题的前景色（主题字典已把它锁成黑白或背景反色）；
            // 其余材质：跟系统强调色。
            string key = theme?.Monochrome == true
                ? "SystemControlForegroundBaseHighBrush"
                : "SystemControlForegroundAccentBrush";

            if (ThemeScopeResolver.TryFindResource<IBrush>(this, key, ActualThemeVariant, out var brush)
                && brush != null)
            {
                WeekdayLabel.Foreground = brush;
                return;
            }

            // 无宿主资源（无头预览）：退回参考图的朱红，保证预览不丢强调色。
            WeekdayLabel.Foreground = viewModel.AccentBrush;
        }
        catch
        {
            WeekdayLabel.Foreground = viewModel.AccentBrush;
        }
    }

    private static void SetRow(RowDefinitions rows, int index, double stars)
    {
        if (index < 0 || index >= rows.Count) return;
        rows[index].Height = new GridLength(stars, GridUnitType.Star);
    }

    /// <inheritdoc />
    public void Refresh(WidgetLayout layout)
    {
        if (layout.Settings is not { } settings || settings.ValueKind != JsonValueKind.Object)
            return;

        try
        {
            var updated = settings.Deserialize<TickClockModel>();
            if (updated != null)
            {
                viewModel.Apply(updated);
                ApplyScale(Bounds.Size);
            }
        }
        catch
        {
            // 设置内容损坏时保持当前显示，不要让异常打断宿主的重建流程。
        }
    }
}
