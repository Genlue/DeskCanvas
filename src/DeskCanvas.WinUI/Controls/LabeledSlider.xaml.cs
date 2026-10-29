using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskCanvas.WinUI.Controls;

/// <summary>一次“用户完成编辑”后的新值；页面可继续读 <c>sender.Value</c>。</summary>
public sealed class LabeledSliderValueCommittedEventArgs : EventArgs
{
    public LabeledSliderValueCommittedEventArgs(double value) => Value = value;

    public double Value { get; }
}

/// <summary>
/// “滑块 + 当前值”复合编辑器（旧 <c>LabeledSlider</c> 的 WinUI 版，报告 §3 第二步第二行）。
/// <para>
/// 契约（与旧实现一致，报告 §4.2）：
/// </para>
/// <list type="bullet">
/// <item><b>Value</b> 是唯一事实源。Slider / 数值框都从它推值；用户编辑回写 Value
/// （因此页面的预览绑定会跟着变），但只有“完成编辑”才触发 <see cref="ValueCommitted"/>。</item>
/// <item>拖拽结束、轨道点击、键盘方向键/Home/End、数值框 Enter、失焦、微调按钮都只提交一次；
/// 初始化或外部刷新 Value 不触发提交，也不写盘。</item>
/// <item><b>Step</b> 把用户编辑吸附到以 Minimum 为锚的网格并夹在范围内；模型加载的值原样镜像，
/// 不会因为打开设置页改动配置。</item>
/// <item>NaN / Infinity 不会进入部件（显示为范围起点），非法范围回退 0–100。</item>
/// </list>
/// </summary>
public partial class LabeledSlider : UserControl
{
    private const double Epsilon = 1e-9;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.0, OnValuePropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(0.0, OnRangePropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LabeledSlider), new PropertyMetadata(100.0, OnRangePropertyChanged));

    /// <summary>吸附步进（正数，默认 1）；非法值回退 1。</summary>
    public static readonly DependencyProperty StepProperty = DependencyProperty.Register(
        nameof(Step), typeof(double), typeof(LabeledSlider), new PropertyMetadata(1.0, OnRangePropertyChanged));

    /// <summary>文本值格式（当前区域文化），默认 "0"。</summary>
    public static readonly DependencyProperty DisplayFormatProperty = DependencyProperty.Register(
        nameof(DisplayFormat), typeof(string), typeof(LabeledSlider), new PropertyMetadata("0", OnRangePropertyChanged));

    /// <summary>数值文本后缀（例如 "%"）；可编辑模式下不显示。</summary>
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(string), typeof(LabeledSlider), new PropertyMetadata(string.Empty, OnUnitPropertyChanged));

    /// <summary>false → 只读格式化文本；true → 可编辑数值框。</summary>
    public static readonly DependencyProperty ShowEditableValueProperty = DependencyProperty.Register(
        nameof(ShowEditableValue), typeof(bool), typeof(LabeledSlider),
        new PropertyMetadata(false, OnShowEditableValueChanged));

    /// <summary>每次“用户完成编辑”触发一次；加载/外部更新不触发。</summary>
    public event EventHandler<LabeledSliderValueCommittedEventArgs>? ValueCommitted;

    /// <summary>内部写 Value / 写部件时的重入保护（忽略自己造成的回声）。</summary>
    private bool _isSynchronizing;

    /// <summary>外部世界最后一次知道的值（外部加载或上次提交）。</summary>
    private double _lastCommittedValue;

    public LabeledSlider()
    {
        InitializeComponent();

        SliderPart.ValueChanged += OnSliderValueChanged;
        // Slider 会把指针事件标记为已处理，所以监听 handled 事件也要开；
        // 轨道点击 / 拖拽抬手 / 捕获丢失都从这里收口到同一个 TryCommit。

        SliderPart.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSliderPointerReleased), true);
        SliderPart.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSliderPointerCaptureLost), true);
        SliderPart.AddHandler(KeyUpEvent, new KeyEventHandler(OnSliderKeyUp), true);

        NumericPart.ValueChanged += OnNumericValueChanged;
        NumericPart.AddHandler(KeyDownEvent, new KeyEventHandler(OnNumericKeyDown), true);
        NumericPart.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnNumericPointerReleased), true);
        NumericPart.LostFocus += OnNumericLostFocus;

        // 行级无障碍名称要传到真正可交互的部件（行本身是装饰）。

        RegisterPropertyChangedCallback(AutomationProperties.NameProperty,
            (d, dp) => ((LabeledSlider)d).PropagateAutomationName());

        ApplyRangeToParts();
        UpdateEditorVisibility();
        UpdateDisplayText();
        _lastCommittedValue = Value;
    }

    // ---- CLR 包装 ---------------------------------------------------------------------

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Step
    {
        get => (double)GetValue(StepProperty);
        set => SetValue(StepProperty, value);
    }

    public string DisplayFormat
    {
        get => (string)GetValue(DisplayFormatProperty);
        set => SetValue(DisplayFormatProperty, value);
    }

    public string Unit
    {
        get => (string)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public bool ShowEditableValue
    {
        get => (bool)GetValue(ShowEditableValueProperty);
        set => SetValue(ShowEditableValueProperty, value);
    }

    // ---- 属性变化 ---------------------------------------------------------------------

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LabeledSlider)d;
        var value = (double)e.NewValue;

        // 外部写值（模型加载/别的编辑器）：重置提交基线，页面已经知道这个值。

        if (!control._isSynchronizing)
        {
            control._lastCommittedValue = value;
        }

        control.PushValueToParts(value);
        control.UpdateDisplayText();
    }

    private static void OnRangePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (LabeledSlider)d;
        control.ApplyRangeToParts();

        // 运行时改范围/步进后立刻把当前值重新吸附，避免行里留着不在网格上的值。

        var snapped = control.Snap(control.Value);
        if (!double.IsFinite(control.Value) || Math.Abs(snapped - control.Value) > Epsilon)
        {
            control.SetValue(ValueProperty, snapped);
        }
        else
        {
            control.PushValueToParts(snapped);
            control.UpdateDisplayText();
        }
    }

    private static void OnUnitPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LabeledSlider)d).UpdateDisplayText();

    private static void OnShowEditableValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((LabeledSlider)d).UpdateEditorVisibility();

    // ---- 值规范化 ---------------------------------------------------------------------

    /// <summary>非法范围回退 0–100（行里永远显示一个说得通的值）。</summary>
    private (double Min, double Max) EffectiveRange()
    {
        var min = Minimum;
        var max = Maximum;
        if (!double.IsFinite(min)) min = 0;
        if (!double.IsFinite(max)) max = 100;
        if (max <= min)
        {
            min = 0;
            max = 100;
        }

        return (min, max);
    }

    private double EffectiveStep()
    {
        var step = Step;
        return double.IsFinite(step) && step > 0 ? step : 1.0;
    }

    /// <summary>夹到范围并吸附到以 Minimum 为锚的步进网格。</summary>
    private double Snap(double value)
    {
        var (min, max) = EffectiveRange();
        var step = EffectiveStep();

        if (!double.IsFinite(value)) value = min;
        value = Math.Clamp(value, min, max);

        var ticks = Math.Round((value - min) / step, MidpointRounding.AwayFromZero);
        var snapped = Math.Round(min + ticks * step, 10); // 去掉 0.30000000000000004 这类残差
        return Math.Clamp(snapped, min, max);
    }

    // ---- 部件同步 ---------------------------------------------------------------------

    private void PushValueToParts(double value)
    {
        // 非有限值绝不能进 Slider（会污染它自己的强制转换），显示为范围起点。

        if (!double.IsFinite(value)) value = EffectiveRange().Min;

        var previous = _isSynchronizing;
        _isSynchronizing = true;
        try
        {
            if (Math.Abs(SliderPart.Value - value) > Epsilon) SliderPart.Value = value;

            // NumberBox 用 NaN 表示“文本无法解析”，所以只有有限值才回写。

            if (double.IsFinite(NumericPart.Value) && Math.Abs(NumericPart.Value - value) > Epsilon)
            {
                NumericPart.Value = value;
            }
            else if (!double.IsFinite(NumericPart.Value))
            {
                NumericPart.Value = value;
            }
        }
        finally
        {
            _isSynchronizing = previous;
        }
    }

    private void ApplyRangeToParts()
    {
        var (min, max) = EffectiveRange();
        var step = EffectiveStep();

        var previous = _isSynchronizing;
        _isSynchronizing = true;
        try
        {
            SliderPart.Minimum = min;
            SliderPart.Maximum = max;
            SliderPart.StepFrequency = step;
            SliderPart.SnapsTo = SliderSnapsTo.StepValues;

            NumericPart.Minimum = min;
            NumericPart.Maximum = max;
            NumericPart.SmallChange = step;
            NumericPart.LargeChange = step * 10;
        }
        finally
        {
            _isSynchronizing = previous;
        }
    }

    /// <summary>来自部件的用户编辑：吸附 → 通过 Value 发布 → 镜像到兄弟部件。</summary>
    private void SetValueFromParts(double raw)
    {
        var snapped = Snap(raw);
        if (!double.IsFinite(Value) || Math.Abs(snapped - Value) > Epsilon)
        {
            var previous = _isSynchronizing;
            _isSynchronizing = true;
            try
            {
                SetValue(ValueProperty, snapped);
            }
            finally
            {
                _isSynchronizing = previous;
            }
        }
        else
        {
            // 有效值没变，但部件可能停在漂移的原始输入上，归一化它们而不动 Value。

            PushValueToParts(snapped);
            UpdateDisplayText();
        }
    }

    private void PropagateAutomationName()
    {
        var name = AutomationProperties.GetName(this);
        if (string.IsNullOrEmpty(name)) return;

        AutomationProperties.SetName(SliderPart, name);
        AutomationProperties.SetName(NumericPart, name);
        AutomationProperties.SetName(ValueTextPart, name);
    }

    private void UpdateEditorVisibility()
    {
        ValueTextPart.Visibility = ShowEditableValue ? Visibility.Collapsed : Visibility.Visible;
        NumericPart.Visibility = ShowEditableValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateDisplayText()
    {
        var format = string.IsNullOrEmpty(DisplayFormat) ? "0" : DisplayFormat;
        var value = double.IsFinite(Value) ? Value : EffectiveRange().Min;

        string text;
        try
        {
            text = value.ToString(format, CultureInfo.CurrentCulture);
        }
        catch (FormatException)
        {
            text = value.ToString(CultureInfo.CurrentCulture);
        }

        ValueTextPart.Text = text + (Unit ?? string.Empty);
    }

    // ---- 部件事件 ---------------------------------------------------------------------

    private void OnSliderValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSynchronizing) return;
        SetValueFromParts(e.NewValue);
    }

    private void OnSliderPointerReleased(object sender, PointerRoutedEventArgs e) => TryCommit();

    /// <summary>捕获被抢走（窗口失活/弹出层）时抬手永远不到，提交用户已经拖到的值。</summary>
    private void OnSliderPointerCaptureLost(object sender, PointerRoutedEventArgs e) => TryCommit();

    private void OnSliderKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down
            or VirtualKey.Home or VirtualKey.End or VirtualKey.PageUp or VirtualKey.PageDown)
        {
            TryCommit();
        }
    }

    private void OnNumericValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isSynchronizing) return;
        if (double.IsFinite(args.NewValue)) SetValueFromParts(args.NewValue);
    }

    private void OnNumericKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) TryCommit();
    }

    /// <summary>微调按钮的抬手：WinUI NumberBox 没有“Spinned”事件，用指针抬手收口。</summary>
    private void OnNumericPointerReleased(object sender, PointerRoutedEventArgs e) => TryCommit();

    private void OnNumericLostFocus(object sender, RoutedEventArgs e) => TryCommit();

    // ---- 提交 -------------------------------------------------------------------------

    private void TryCommit()
    {
        if (_isSynchronizing) return;

        var value = Value;
        if (!double.IsFinite(value)) return;
        if (Math.Abs(_lastCommittedValue - value) <= Epsilon) return;

        _lastCommittedValue = value;
        ValueCommitted?.Invoke(this, new LabeledSliderValueCommittedEventArgs(value));
    }
}
