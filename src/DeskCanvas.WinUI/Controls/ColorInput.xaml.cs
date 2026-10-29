using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI;
using DeskCanvas.Locales;

namespace DeskCanvas.WinUI.Controls;

/// <summary>提交颜色时如何合成 alpha 通道。</summary>
public enum ColorInputAlphaPolicy
{
    /// <summary>保留上一个值的 alpha，除非用户显式编辑了 alpha（默认）。</summary>
    Preserve = 0,

    /// <summary>alpha 不归这个字段管：每次提交都写成不透明（FF）。</summary>
    ForceOpaque = 1,
}

/// <summary>写回模型值的字符串格式。</summary>
public enum ColorInputFormat
{
    /// <summary>"#RRGGBB"（alpha 由单独的业务字段/透明度滑块承担）。</summary>
    Rgb = 0,

    /// <summary>"#AARRGGBB"（Avalonia 旧约定，不是 CSS 的 RRGGBBAA）。</summary>
    Argb = 1,
}

/// <summary>一次被接受的用户编辑后的新值；仅 AllowEmpty 字段被清空时是 null。</summary>
public sealed class ColorInputValueCommittedEventArgs : EventArgs
{
    public ColorInputValueCommittedEventArgs(string? value) => Value = value;

    public string? Value { get; }
}

/// <summary>
/// 颜色编辑器（旧 <c>ColorInput</c> 的 WinUI 版，报告 §3 第二步第三行）：色块 + ColorPicker Flyout +
/// Hex 草稿框 + 内联错误。
/// <para>
/// 契约（与旧实现一致，报告 §4.2）：
/// </para>
/// <list type="bullet">
/// <item>Hex 接受 #RGB / #ARGB / #RRGGBB / #AARRGGBB；半成品（"#"、"#FF"）永远不会写回模型。</item>
/// <item>Enter / 失焦提交一次有效草稿；非法草稿留在输入框里显示错误并保留上一个有效预览。</item>
/// <item>拾色器在 Flyout 打开期间只更新预览；<b>关闭 Flyout 才提交一次</b>——不是每次 ColorChanged 都写盘。</item>
/// <item>外部加载/刷新 Value 不触发 <see cref="ValueCommitted"/>，也不会引起额外保存。</item>
/// <item><see cref="InvalidFormatText"/> 是用户可见文案，默认取 Locale.Settings_ColorInput_InvalidFormat，
/// 由嵌入方本地化。</item>
/// </list>
/// </summary>
public partial class ColorInput : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorInput), new PropertyMetadata(null, OnValuePropertyChanged));

    /// <summary>显示拾色器的 alpha 编辑；并不代表本控件会丢弃 alpha。</summary>
    public static readonly DependencyProperty IsAlphaVisibleProperty = DependencyProperty.Register(
        nameof(IsAlphaVisible), typeof(bool), typeof(ColorInput),
        new PropertyMetadata(false, OnAlphaVisibleChanged));

    public static readonly DependencyProperty AlphaPolicyProperty = DependencyProperty.Register(
        nameof(AlphaPolicy), typeof(ColorInputAlphaPolicy), typeof(ColorInput), new PropertyMetadata(ColorInputAlphaPolicy.Preserve));

    public static readonly DependencyProperty ColorFormatProperty = DependencyProperty.Register(
        nameof(ColorFormat), typeof(ColorInputFormat), typeof(ColorInput),
        new PropertyMetadata(ColorInputFormat.Argb, OnColorFormatChanged));

    /// <summary>true → 空值是一个合法的模型状态（自动/跟随系统）。</summary>
    public static readonly DependencyProperty AllowEmptyProperty = DependencyProperty.Register(
        nameof(AllowEmpty), typeof(bool), typeof(ColorInput), new PropertyMetadata(false));

    /// <summary>只读仍可看见、选中并复制颜色，但不能编辑。</summary>
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(ColorInput),
        new PropertyMetadata(false, OnReadOnlyChanged));

    /// <summary>非法草稿的提示文案；由嵌入方本地化。</summary>
    public static readonly DependencyProperty InvalidFormatTextProperty = DependencyProperty.Register(
        nameof(InvalidFormatText), typeof(string), typeof(ColorInput), new PropertyMetadata(string.Empty));

    /// <summary>一次被接受的用户编辑（有效 Hex、拾色器完成、清空）触发一次。</summary>
    public event EventHandler<ColorInputValueCommittedEventArgs>? ValueCommitted;

    /// <summary>最后一个解析成功的颜色（外部加载或提交）；Preserve alpha 的来源。</summary>
    private Color? _lastValidColor;

    /// <summary>内部写值/写部件的重入保护。</summary>
    private bool _isSynchronizing;

    /// <summary>程序化写文本不算用户输入。</summary>
    private bool _updatingText;

    /// <summary>用户自上次成功应用后改过 Hex 草稿。</summary>
    private bool _draftEdited;

    /// <summary>Flyout 打开期间的待提交颜色（只做预览）。</summary>
    private Color? _pendingPickerColor;

    public ColorInput()
    {
        InitializeComponent();

        PickerPart.ColorChanged += OnPickerColorChanged;
        HexBoxPart.TextChanged += OnHexTextChanged;
        HexBoxPart.KeyDown += OnHexKeyDown;
        HexBoxPart.LostFocus += OnHexLostFocus;

        InvalidFormatText = Locale.Settings_ColorInput_InvalidFormat ?? string.Empty;

        ApplyFormatToParts();
        ApplyAlphaToPicker();
        UpdateReadOnlyState();
        PushSwatch(_lastValidColor);
    }

    // ---- CLR 包装 ---------------------------------------------------------------------

    public string? Value
    {
        get => (string?)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsAlphaVisible
    {
        get => (bool)GetValue(IsAlphaVisibleProperty);
        set => SetValue(IsAlphaVisibleProperty, value);
    }

    public ColorInputAlphaPolicy AlphaPolicy
    {
        get => (ColorInputAlphaPolicy)GetValue(AlphaPolicyProperty);
        set => SetValue(AlphaPolicyProperty, value);
    }

    public ColorInputFormat ColorFormat
    {
        get => (ColorInputFormat)GetValue(ColorFormatProperty);
        set => SetValue(ColorFormatProperty, value);
    }

    public bool AllowEmpty
    {
        get => (bool)GetValue(AllowEmptyProperty);
        set => SetValue(AllowEmptyProperty, value);
    }

    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    public string InvalidFormatText
    {
        get => (string)GetValue(InvalidFormatTextProperty);
        set => SetValue(InvalidFormatTextProperty, value);
    }

    /// <summary>当前是否在显示格式错误（页面可用于显示额外的错误行）。</summary>
    public bool HasValidationError { get; private set; }

    /// <summary>当前错误文案（无错误时 null）。</summary>
    public string? ValidationMessage { get; private set; }

    // ---- 属性变化 ---------------------------------------------------------------------

    private static void OnValuePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (ColorInput)d;
        // 外部模型加载/更新：只镜像，不提交、不保存。

        control.UpdateFromValue((string?)e.NewValue);
    }

    private static void OnAlphaVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorInput)d).ApplyAlphaToPicker();

    private static void OnColorFormatChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorInput)d).ApplyFormatToParts();

    private static void OnReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ColorInput)d).UpdateReadOnlyState();

    // ---- 外部加载 ---------------------------------------------------------------------

    private void UpdateFromValue(string? value)
    {
        if (_isSynchronizing) return;

        if (string.IsNullOrWhiteSpace(value))
        {
            _lastValidColor = null;
            PushDraftText(string.Empty);
            PushSwatch(_lastValidColor);
            SetValidationError(false);
            return;
        }

        if (TryParseHex(value, out var color))
        {
            _lastValidColor = color;
            PushPickerColor(color);
            PushDraftText(Format(color));
            PushSwatch(color);
            SetValidationError(false);
        }
        else
        {
            // 显示坏的存档数据而不是悄悄藏起来：保留原文供用户修，模型不被改动。

            PushDraftText(value);
            PushSwatch(_lastValidColor);
            SetValidationError(true);
        }
    }

    // ---- 用户输入 ---------------------------------------------------------------------

    private void OnHexTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingText || _isSynchronizing) return;

        // 一开始输入就清掉上次的判定；"#" / "#FF" 这类半成品在提交时才报错。

        _draftEdited = true;
        SetValidationError(false);
    }

    private void OnHexKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            CommitDraft();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            RevertDraft();
            e.Handled = true;
        }
    }

    private void OnHexLostFocus(object sender, RoutedEventArgs e) => CommitDraft();

    /// <summary>Enter / 失焦提交草稿；用户没改过就没有可提交的东西。</summary>
    private void CommitDraft()
    {
        if (_isSynchronizing || !_draftEdited) return;

        var text = (HexBoxPart.Text ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            if (!AllowEmpty)
            {
                SetValidationError(true);
                return;
            }

            _draftEdited = false;
            _lastValidColor = null;
            SetValidationError(false);
            PushSwatch(null);

            if (!string.IsNullOrEmpty(Value))
            {
                PublishValue(null);
                ValueCommitted?.Invoke(this, new ColorInputValueCommittedEventArgs(null));
            }

            return;
        }

        if (!TryParseHex(text, out var parsed))
        {
            // 非法输入留在草稿框里；模型和上一个有效预览保持不变。

            SetValidationError(true);
            return;
        }

        _draftEdited = false;
        ApplyColor(parsed, fromHex: true, hexDigits: CountHexDigits(text), publish: true);
    }

    /// <summary>Escape：恢复本行草稿；已经提交的模型改动不回滚。</summary>
    private void RevertDraft()
    {
        _draftEdited = false;
        SetValidationError(false);
        PushDraftText(_lastValidColor is { } color ? Format(color) : (Value ?? string.Empty));
    }

    /// <summary>拾色器拖动期间只更新预览，不写模型。</summary>
    private void OnPickerColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_isSynchronizing) return;

        _pendingPickerColor = args.NewColor;
        PushSwatch(args.NewColor);
        PushDraftText(Format(args.NewColor));
        SetValidationError(false);
    }

    private void OnPickerFlyoutOpened(object? sender, object e)
    {
        _pendingPickerColor = _lastValidColor;
        if (_lastValidColor is { } color) PushPickerColor(color);
    }

    /// <summary>Flyout 关闭才算“用户完成选色”：这时提交一次，避免每帧写盘。</summary>
    private void OnPickerFlyoutClosed(object? sender, object e)
    {
        var pending = _pendingPickerColor;
        _pendingPickerColor = null;
        if (pending is not { } color) return;

        _draftEdited = false;
        ApplyColor(color, fromHex: false, hexDigits: 0, publish: true);
    }

    // ---- 归一化 ---------------------------------------------------------------

    /// <summary>归一化有效颜色、镜像到各部件，并只在值变化时发布一次。</summary>
    private void ApplyColor(Color incoming, bool fromHex, int hexDigits, bool publish)
    {
        var alpha = ResolveAlpha(incoming, fromHex, hexDigits);
        var composed = Color.FromArgb(alpha, incoming.R, incoming.G, incoming.B);
        _lastValidColor = composed;

        var formatted = Format(composed);
        var changed = !string.Equals(Value, formatted, StringComparison.Ordinal);

        _isSynchronizing = true;
        try
        {
            _draftEdited = false;
            PushPickerColor(composed);
            PushDraftText(formatted);
            PushSwatch(composed);
            SetValidationError(false);
        }
        finally
        {
            _isSynchronizing = false;
        }

        if (!publish || !changed) return;

        PublishValue(formatted);
        ValueCommitted?.Invoke(this, new ColorInputValueCommittedEventArgs(formatted));
    }

    private void PublishValue(string? formatted)
    {
        var previous = _isSynchronizing;
        _isSynchronizing = true;
        try
        {
            SetValue(ValueProperty, formatted);
        }
        finally
        {
            _isSynchronizing = previous;
        }
    }

    private byte ResolveAlpha(Color incoming, bool fromHex, int hexDigits)
    {
        // ForceOpaque 无条件成立：这一行声明 alpha 不归它管。

        if (AlphaPolicy == ColorInputAlphaPolicy.ForceOpaque) return 255;

        // 显式 #ARGB / #AARRGGBB 就是用户在直接编辑 alpha。

        if (fromHex && (hexDigits == 4 || hexDigits == 8)) return incoming.A;

        // 拾色器带 alpha 也是显式编辑。

        if (IsAlphaVisible) return incoming.A;

        // 其余情况绝不丢弃上一个值的 alpha。

        return _lastValidColor?.A ?? incoming.A;
    }

    // ---- 解析 / 格式化 ---------------------------------------------------------------

    /// <summary>接受 #RGB / #ARGB / #RRGGBB / #AARRGGBB（'#' 可省略）。</summary>
    private static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var value = text.Trim();
        if (value.StartsWith('#')) value = value.Substring(1);

        if (value.Length is not (3 or 4 or 6 or 8)) return false;

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch)) return false;
        }

        static byte Nibble(string s, int index) => (byte)Convert.ToInt32(s.Substring(index, 1), 16);
        static byte Pair(string s, int index) => (byte)Convert.ToInt32(s.Substring(index, 2), 16);

        switch (value.Length)
        {
            case 3:
                color = Color.FromArgb(255, (byte)(Nibble(value, 0) * 17), (byte)(Nibble(value, 1) * 17), (byte)(Nibble(value, 2) * 17));
                return true;
            case 4:
                color = Color.FromArgb((byte)(Nibble(value, 0) * 17), (byte)(Nibble(value, 1) * 17),
                    (byte)(Nibble(value, 2) * 17), (byte)(Nibble(value, 3) * 17));
                return true;
            case 6:
                color = Color.FromArgb(255, Pair(value, 0), Pair(value, 2), Pair(value, 4));
                return true;
            default:
                color = Color.FromArgb(Pair(value, 0), Pair(value, 2), Pair(value, 4), Pair(value, 6));
                return true;
        }
    }

    /// <summary>草稿文本的纯 hex 位数；不是纯 hex 形式时返回 -1。</summary>
    private static int CountHexDigits(string text)
    {
        var value = text.Trim();
        if (value.StartsWith('#')) value = value.Substring(1);
        if (value.Length == 0) return -1;

        foreach (var ch in value)
        {
            if (!Uri.IsHexDigit(ch)) return -1;
        }

        return value.Length;
    }

    private string Format(Color color) => ColorFormat == ColorInputFormat.Rgb
        ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        : $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    // ---- 部件同步 ---------------------------------------------------------------------

    private void PushPickerColor(Color color)
    {
        var previous = _isSynchronizing;
        _isSynchronizing = true;
        try
        {
            if (PickerPart.Color != color) PickerPart.Color = color;
        }
        finally
        {
            _isSynchronizing = previous;
        }
    }

    private void PushDraftText(string text)
    {
        if (string.Equals(HexBoxPart.Text, text, StringComparison.Ordinal)) return;

        _updatingText = true;
        try
        {
            HexBoxPart.Text = text;
        }
        finally
        {
            _updatingText = false;
        }
    }

    private void PushSwatch(Color? color)
    {
        if (color is { } value)
        {
            SwatchBorder.Background = new SolidColorBrush(value);
            EmptyMark.Visibility = Visibility.Collapsed;
            // 色块是纯视觉元素，无障碍名称至少要说出当前颜色。

            AutomationProperties.SetName(SwatchButton, Format(value));
        }
        else
        {
            SwatchBorder.Background = null;
            EmptyMark.Visibility = Visibility.Visible;
            AutomationProperties.SetName(SwatchButton, string.Empty);
        }
    }

    private void SetValidationError(bool hasError)
    {
        HasValidationError = hasError;
        ValidationMessage = hasError ? InvalidFormatText : null;

        ErrorTextPart.Visibility = hasError ? Visibility.Visible : Visibility.Collapsed;
        ErrorTextPart.Text = hasError ? InvalidFormatText : string.Empty;
    }

    private void ApplyFormatToParts()
    {
        HexBoxPart.PlaceholderText = ColorFormat == ColorInputFormat.Rgb ? "#RRGGBB" : "#AARRGGBB";
        PushDraftText(_lastValidColor is { } color ? Format(color) : (Value ?? string.Empty));
    }

    private void ApplyAlphaToPicker()
    {
        PickerPart.IsAlphaEnabled = IsAlphaVisible;
        PickerPart.IsAlphaSliderVisible = IsAlphaVisible;
        PickerPart.IsAlphaTextInputVisible = IsAlphaVisible;
    }

    private void UpdateReadOnlyState()
    {
        SwatchButton.IsEnabled = !IsReadOnly;
        HexBoxPart.IsReadOnly = IsReadOnly;
    }
}
