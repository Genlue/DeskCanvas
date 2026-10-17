using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskCanvas.Core.Interfaces;

namespace StackWidgets.Views.Controls;

/// <summary>
/// Stack 分页圆点条（W10 §9.2 装饰下沉）：从宿主 Widget 的 StackIndicatorsHost
/// 迁移到 Stack 插件的装饰控件，绑定既有 <see cref="IStackWidget"/> 数据
/// （IndicatorItems / IsActive / Opacity / AutoHideIndicators / SwitchToIndex）。
/// <para>
/// 每个宿主实例只创建一次：由 <c>WidgetStackView</c> 在自身构造时创建并缓存，
/// 通过 IWidgetOverlayProvider.Overlay 交给宿主——不在 getter 里重复 new，
/// 也不允许被多个窗口共享为 visual child。
/// </para>
/// 中键＋滚轮的全窗口切页手势仍由宿主隧道层承担；本控件只负责圆点条自身的
/// 点击切页与圆点上的滚轮切页（与迁移前语义一致）。
/// </summary>
public partial class StackIndicatorStrip : Border
{
    private const double HideLingerMs = 600;

    private IStackWidget? stack;
    private DispatcherTimer? hideTimer;
    private IDisposable? rootPointerSubscription;
    private bool rootPointerOver;

    public StackIndicatorStrip()
    {
        InitializeComponent();
    }

    /// <summary>构造并绑定到给定的重叠组件协议实例。</summary>
    public StackIndicatorStrip(IStackWidget stack) : this()
    {
        Bind(stack);
    }

    /// <summary>绑定（或重新绑定）圆点数据来源；随后的 IndicatorItemsChanged 事件会驱动刷新。</summary>
    public void Bind(IStackWidget? newStack)
    {
        if (ReferenceEquals(stack, newStack))
        {
            Refresh();
            return;
        }

        if (stack != null)
            stack.IndicatorItemsChanged -= OnStackItemsChanged;

        stack = newStack;

        if (stack != null)
            stack.IndicatorItemsChanged += OnStackItemsChanged;

        Refresh();
    }

    /// <summary>当前绑定的协议实例（检查与调试用）。</summary>
    public IStackWidget? BoundStack => stack;

    private void OnStackItemsChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>重建圆点与可见性（等价于旧宿主 Notify(StackIndicators) + HasStackIndicators）。</summary>
    public void Refresh()
    {
        var items = stack?.IndicatorItems ?? [];
        DotsHost.ItemsSource = items;
        IsVisible = items.Count > 1;
        UpdateReveal();
    }

    // ---- 点击切页（旧宿主 OnStackIndicatorClicked） ------------------------------------------

    private void OnDotClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button { Tag: int index } && stack != null)
            {
                stack.SwitchToIndex(index);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StackIndicatorStrip] click error: {ex.Message}");
        }
    }

    // ---- 圆点上的滚轮切页（旧宿主 OnStackIndicatorsWheelChanged） -----------------------------

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);

        try
        {
            if (stack is not { } s || s.IndicatorItems is not { Count: > 1 } list)
                return;

            if (s.IsTransitionActive)
            {
                e.Handled = true;
                return;
            }

            var currentIndex = list.FirstOrDefault(i => i.IsActive)?.Index ?? 0;
            if (e.Delta.Y > 0)
            {
                var nextIndex = (currentIndex - 1 + list.Count) % list.Count;
                s.SwitchToIndex(nextIndex);
                e.Handled = true;
            }
            else if (e.Delta.Y < 0)
            {
                var nextIndex = (currentIndex + 1) % list.Count;
                s.SwitchToIndex(nextIndex);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StackIndicatorStrip] wheel error: {ex.Message}");
        }
    }

    // ---- 常态隐藏（旧宿主 UpdateStackIndicatorsReveal / OnStackDotsHideTimerTick） -------------

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 旧实现用宿主窗口的 IsPointerOver：指针在组件任意处都算“正在查看”。
        if (TopLevel.GetTopLevel(this) is Control root)
        {
            rootPointerSubscription?.Dispose();
            rootPointerSubscription = root.GetObservable(InputElement.IsPointerOverProperty)
                .Subscribe(value =>
                {
                    rootPointerOver = value;
                    UpdateReveal();
                });
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        rootPointerSubscription?.Dispose();
        rootPointerSubscription = null;
        rootPointerOver = false;
        hideTimer?.Stop();
    }

    private bool Revealed => stack is { } s && (s.IsTransitionActive || rootPointerOver || IsPointerOver);

    /// <summary>常态隐藏：仅切换过渡期间或指针悬停时显示，空闲后短暂停留再隐藏（Opacity 保持可命中）。</summary>
    public void UpdateReveal()
    {
        if (stack is not { } s || !s.AutoHideIndicators)
        {
            hideTimer?.Stop();
            Opacity = 1;
            return;
        }

        if (s.IsTransitionActive || rootPointerOver || IsPointerOver)
        {
            hideTimer?.Stop();
            Opacity = 1;
            return;
        }

        // 空闲：先短暂停留再隐藏，避免刚结束的交互闪烁。
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
        Opacity = Revealed ? 1 : 0;
    }
}
