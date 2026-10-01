using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;

namespace DeskCanvas.Views.Controls;

public class ClickThroughTextBox : TextBox
{
    protected override Type StyleKeyOverride => typeof(TextBox);

    /// <summary>
    /// 卡片内联编辑开关（附加属性，默认 true）。
    /// <para>
    /// 必须是<b>附加属性</b>而不是实例 StyledProperty：本控件覆写了
    /// <c>StyleKeyOverride = typeof(TextBox)</c>，Avalonia 样式系统的类型选择器
    /// 按"样式键"匹配——<c>Selector="controls|ClickThroughTextBox"</c> 这类写法
    /// <b>永远匹配不到</b>本控件的实例。改成附加属性后，样式经由 TextBox 选择器
    /// （按样式键命中）设置 <c>controls:ClickThroughTextBox.AllowEdit</c>，
    /// 与 <c>ScrollViewer.HorizontalScrollBarVisibility</c> 设在 TextBox 上的既有
    /// 先例完全同构。视图代码也可直接 <c>SetAllowEdit(box, bool)</c>。
    /// </para>
    /// </summary>
    public static readonly AttachedProperty<bool> AllowEditProperty =
        AvaloniaProperty.RegisterAttached<ClickThroughTextBox, Control, bool>(
            "AllowEdit", defaultValue: true);

    public static bool GetAllowEdit(Control element) => element.GetValue(AllowEditProperty);

    public static void SetAllowEdit(Control element, bool value) => element.SetValue(AllowEditProperty, value);

    /// <summary>Whether double-click may activate the editor (see <see cref="AllowEditProperty"/>).</summary>
    private bool AllowEdit => GetValue(AllowEditProperty);

    public FlyoutBase? DefaultContextFlyout { get; set; }

    /// <summary>
    /// True between the activating double-click and its word-selection collapse
    /// (see <see cref="OnDoubleTapped"/>).
    /// </summary>
    private bool pendingActivationCollapse;

    /// <summary>
    /// Re-entrancy guard for the caret/selection index normalization.
    /// </summary>
    private bool adjustingIndices;
    
    public ClickThroughTextBox()
    {
        Focusable = false;
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DoubleTappedEvent, OnDoubleTapped);
        KeyDown += OnKeyDown;
        LostFocus += OnLostFocus;
        Initialized += OnInitialized;
        Unloaded += OnUnloaded;
        PropertyChanged += OnPropertyChanged;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (VisualRoot is Widget widget &&
            ((e.Key == Key.Enter && !AcceptsReturn) || 
             (e.Key == Key.Tab && !AcceptsTab) ||
             (e.Key == Key.Escape)))
        {
            widget.FocusManager?.ClearFocus();
        }
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        RemoveHandler(PointerPressedEvent, OnPointerPressed);
        RemoveHandler(DoubleTappedEvent, OnDoubleTapped);
        KeyDown -= OnKeyDown;
        LostFocus -= OnLostFocus;
        Initialized -= OnInitialized;
        Unloaded -= OnUnloaded;
        PropertyChanged -= OnPropertyChanged;
    }

    private void OnInitialized(object? sender, EventArgs e)
    {
        DefaultContextFlyout = ContextFlyout;
        ContextFlyout = null;
    }

    private void OnLostFocus(object? sender, RoutedEventArgs e)
    {
        pendingActivationCollapse = false;
        if (ContextFlyout is { IsOpen: true })
        {
            e.Handled = true;
        }
        else
        {
            ContextFlyout = null;
            Focusable = false;
            ClearSelection();
        }
    }

    /// <summary>
    /// Avalonia's TextPresenter throws "Index and length must refer to a location
    /// within the string" (GetCombinedText → Substring(caretIndex)) when the caret
    /// index exceeds the text length while an IME preedit is active. With mixed
    /// Inter + CJK-fallback text the caret index can drift one position past the
    /// text end, so keep all text indices inside the valid range.
    /// </summary>
    private void OnPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (adjustingIndices) return;
        if (e.Property != TextProperty && e.Property != CaretIndexProperty &&
            e.Property != SelectionStartProperty && e.Property != SelectionEndProperty)
        {
            return;
        }

        var length = Text?.Length ?? 0;
        if (CaretIndex >= 0 && CaretIndex <= length &&
            SelectionStart >= 0 && SelectionStart <= length &&
            SelectionEnd >= 0 && SelectionEnd <= length)
        {
            return;
        }

        adjustingIndices = true;
        try
        {
            if (CaretIndex < 0) CaretIndex = 0;
            if (CaretIndex > length) CaretIndex = length;
            if (SelectionStart < 0) SelectionStart = 0;
            if (SelectionStart > length) SelectionStart = length;
            if (SelectionEnd < 0) SelectionEnd = 0;
            if (SelectionEnd > length) SelectionEnd = length;
        }
        finally
        {
            adjustingIndices = false;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!AllowEdit)
        {
            // Inline editing is disabled (widget setting): behave as pure card
            // surface — forward the press so the widget can be dragged, never
            // activate the caret, the flyout or the word selection.
            if (!IsFocused && VisualRoot is Widget card)
            {
                card.OnPointerPressed(sender, e);
                e.Handled = true;
            }
            return;
        }

        if (!IsFocused && e.ClickCount == 1 && VisualRoot is Widget widget)
        {
            // First single click: pass through so the widget can be dragged;
            // text boxes act like part of the desktop surface until activated.
            widget.OnPointerPressed(sender, e);
            e.Handled = true;
        }
        else if (!IsFocused && e.ClickCount >= 2)
        {
            // Activating double-click: the single click already passed through
            // for dragging, so this press must only activate the box. The default
            // double-click word selection is collapsed to its end by
            // OnDoubleTapped afterwards.
            Focusable = true;
            ContextFlyout = DefaultContextFlyout;
            Focus();

            // CJK/fallback hit-testing places an end-of-text click one character
            // early (caret shows at the end but Backspace deletes the second-to-
            // last char). When the click is beyond the last glyph, force the
            // caret to the text END.
            if (e.GetPosition(this).X >= Padding.Left + MeasureTextWidth())
                CaretIndex = Text?.Length ?? 0;

            pendingActivationCollapse = true;
        }
        else
        {
            Focusable = true;
            ContextFlyout = DefaultContextFlyout;
        }
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!pendingActivationCollapse) return;
        pendingActivationCollapse = false;

        // Collapse the activation word selection to its END. Collapsing to the
        // start (ClearSelection) would leave the caret one character to the left
        // of the click with CJK text, so the first Backspace deletes the
        // second-to-last character instead of the last one.
        if (SelectionStart != SelectionEnd)
        {
            var length = Text?.Length ?? 0;
            var end = Math.Min(SelectionEnd, length);
            CaretIndex = end;
            SelectionStart = end;
            SelectionEnd = end;
        }
    }

    /// <summary>
    /// Measure the text width the way the TextBox lays it out (DIPs).
    /// </summary>
    private double MeasureTextWidth()
    {
        var text = Text ?? string.Empty;
        if (text.Length == 0) return 0;

        var typeface = new Typeface(FontFamily, FontStyle, FontWeight);
        var layout = new TextLayout(
            text,
            typeface,
            FontSize,
            foreground: null,
            textAlignment: TextAlignment,
            textWrapping: TextWrapping,
            textTrimming: TextTrimming.None,
            textDecorations: null,
            flowDirection: FlowDirection);
        return layout.Width;
    }
}
