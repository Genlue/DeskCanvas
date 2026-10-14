using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Controls.Shapes;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;

namespace DeskCanvas.Views.Controls;

/// <summary>
/// 旧版组件 DLL 的装饰兼容适配器（W10 §9.2 / 报告 §13）。
/// <para>
/// <b>使用者</b>：已部署且尚未随包重新解包的旧版组件 DLL。它们的视图只实现
/// <see cref="IStackWidget"/>（重叠组件协议），不认识新的
/// <see cref="IWidgetOverlayProvider"/>——分页点是这些旧组件唯一的显示路径，
/// 删掉本适配器等于直接让旧组件失去分页点，因此宿主对
/// “实现了 IStackWidget 但没有实现 IWidgetOverlayProvider”的内容一律经本适配器
/// 走既有视觉路径（<see cref="LegacyStackIndicatorStrip"/> 完整复刻迁移前的
/// StackIndicatorsHost 行为：圆点、点击切页、圆点上滚轮切页、常态隐藏）。
/// </para>
/// <para>
/// <b>移除条件</b>：所有受支持的插件版本都已随包提供实现
/// <see cref="IWidgetOverlayProvider"/> 的新装饰（StackIndicatorStrip）并完成旧 DLL
/// 基线回归验证后，才可删除本适配器与 <see cref="LegacyStackIndicatorStrip"/>。
/// 在此之前不能声称“旧 DLL 完全兼容”的同时删除它。
/// </para>
/// </summary>
public sealed class LegacyStackOverlayAdapter : IWidgetOverlayProvider
{
    private readonly LegacyStackIndicatorStrip strip;

    public LegacyStackOverlayAdapter(IStackWidget stack)
    {
        Stack = stack;
        strip = new LegacyStackIndicatorStrip(stack);
    }

    /// <summary>被适配的旧协议实例（行为合同不变，宿主的手势与编辑路径继续直接使用它）。</summary>
    public IStackWidget Stack { get; }

    /// <inheritdoc />
    public Control? Overlay => strip;

    /// <inheritdoc />
    public WidgetOverlayHints OverlayHints { get; } = new()
    {
        // 与迁移前的 StackIndicatorsMargin 语义等价：常态在卡片外右缘，
        // 原生 Acrylic（UsesNativeBlur 且无原生窗框）时内移进卡片。
        Placement = WidgetOverlayPlacement.OutsideCardRight,
        RightReservedWidth = 13,
        InsetOnNativeBlur = true,
        // 历史方案就是内移而非把装饰矩形 OR 进单一 GDI 区域。
        MergeBoundsIntoWindowRegion = false,
        RelayoutOnBoundsChange = true,
        HitTestableWhenHidden = true,
    };

    /// <inheritdoc />
    public void OnOverlayLayoutChanged(in WidgetOverlayLayoutContext context)
    {
        // 条带的放置完全由提示驱动，无需额外重排。
    }
}

/// <summary>
/// 旧路径的分页圆点条（宿主侧）：逐项复刻迁移前 Widget.axaml 的 StackIndicatorsHost
/// XAML 与对应 code-behind（OnStackIndicatorClicked / OnStackIndicatorsWheelChanged /
/// UpdateStackIndicatorsReveal / stackDotsHideTimer）。仅供
/// <see cref="LegacyStackOverlayAdapter"/> 使用。
/// </summary>
public sealed class LegacyStackIndicatorStrip : Border
{
    private const double HideLingerMs = 600;

    private readonly IStackWidget stack;
    private readonly StackPanel dots;
    private DispatcherTimer? hideTimer;
    private IDisposable? rootPointerSubscription;
    private bool rootPointerOver;

    public LegacyStackIndicatorStrip(IStackWidget stack)
    {
        this.stack = stack;

        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Opacity = 0; // 首次 Refresh 立即按 AutoHide 语义定值
        PointerWheelChanged += OnStripWheel;
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;

        dots = new StackPanel { Orientation = Avalonia.Layout.Orientation.Vertical, Spacing = 2 };
        Child = dots;

        stack.IndicatorItemsChanged += OnStackItemsChanged;
        Refresh();
    }

    private Control BuildDot(StackWidgetIndicatorItem item)
    {
        var button = new Button
        {
            Width = 12,
            Height = 12,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(0),
            Margin = new Thickness(0, 1),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = item.Index,
        };
        button.Styles.Add(new Style(x => x.OfType<Button>().Class(":pointerover"))
        {
            Setters = { new Setter(Button.RenderTransformProperty, new ScaleTransform(1.25, 1.25)) }
        });
        button.Click += OnDotClicked;
        if (!string.IsNullOrEmpty(item.Title))
            ToolTip.SetTip(button, item.Title);

        var panel = new Panel { Width = 12, Height = 12 };
        // 亮暗背景下都够对比的细微暗色描边 + 纯白圆点（激活/非激活透明度）。
        panel.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Stroke = new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)),
            StrokeThickness = 0.8,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        panel.Children.Add(new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = Brushes.White,
            Opacity = item.Opacity,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });
        button.Content = panel;
        return button;
    }

    private void OnStackItemsChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>重建圆点与可见性（等价于宿主旧的 Notify(StackIndicators) + HasStackIndicators）。</summary>
    public void Refresh()
    {
        var items = stack.IndicatorItems ?? [];
        dots.Children.Clear();
        foreach (var item in items)
            dots.Children.Add(BuildDot(item));
        IsVisible = items.Count > 1;
        UpdateReveal();
    }

    // ---- 点击切页（旧 OnStackIndicatorClicked） --------------------------------------------

    private void OnDotClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: int index })
                stack.SwitchToIndex(index);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LegacyStackIndicatorStrip] click error: {ex.Message}");
        }
    }

    // ---- 圆点上的滚轮切页（旧 OnStackIndicatorsWheelChanged；中键+滚轮手势仍归宿主隧道层） ---

    private void OnStripWheel(object? sender, PointerWheelEventArgs e)
    {
        try
        {
            var list = stack.IndicatorItems;
            if (list is not { Count: > 1 })
                return;

            if (stack.IsTransitionActive)
            {
                e.Handled = true;
                return;
            }

            var currentIndex = list.FirstOrDefault(i => i.IsActive)?.Index ?? 0;
            var nextIndex = e.Delta.Y > 0
                ? (currentIndex - 1 + list.Count) % list.Count
                : (currentIndex + 1) % list.Count;
            stack.SwitchToIndex(nextIndex);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[LegacyStackIndicatorStrip] wheel error: {ex.Message}");
        }
    }

    // ---- 常态隐藏（旧 UpdateStackIndicatorsReveal / OnStackDotsHideTimerTick） ---------------

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // 旧实现用的是宿主窗口的 IsPointerOver：指针在组件任意处都算“正在查看”。
        if (this.GetVisualRoot() is Control root)
        {
            rootPointerSubscription = root.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(value =>
                {
                    rootPointerOver = value;
                    UpdateReveal();
                });
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        rootPointerSubscription?.Dispose();
        rootPointerSubscription = null;
        rootPointerOver = false;
        hideTimer?.Stop();
    }

    private bool Revealed => stack.IsTransitionActive || rootPointerOver || IsPointerOver;

    /// <summary>常态隐藏：仅切换过渡期间或指针悬停时显示，空闲后短暂停留再隐藏。</summary>
    public void UpdateReveal()
    {
        if (!stack.AutoHideIndicators)
        {
            hideTimer?.Stop();
            Opacity = 1;
            return;
        }

        if (Revealed)
        {
            hideTimer?.Stop();
            Opacity = 1;
            return;
        }

        // 空闲：先短暂停留再隐藏，避免刚结束的交互闪烁。隐藏用 Opacity 保持可命中。
        if (Opacity > 0 && hideTimer is not { IsEnabled: true })
        {
            hideTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HideLingerMs) };
            hideTimer.Tick -= OnHideTimerTick;
            hideTimer.Tick += OnHideTimerTick;
            hideTimer.Start();
        }
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        hideTimer?.Stop();
        Opacity = stack.AutoHideIndicators && Revealed ? 1 : 0;
    }
}
