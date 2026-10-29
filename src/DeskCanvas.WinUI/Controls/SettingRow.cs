using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using DeskCanvas.Locales;

namespace DeskCanvas.WinUI.Controls;

/// <summary>行的排布请求。Auto 由行自身宽度决定是否换行（对照旧 SettingLayoutMode）。</summary>
public enum SettingRowLayoutMode
{
    /// <summary>放得下 label + editor 就单行，否则编辑器换到下一行。</summary>
    Auto = 0,

    /// <summary>始终单行（短标题 + 小开关）；标题仍然换行不截断。</summary>
    Inline = 1,

    /// <summary>始终两行：第一行图标 + 标题组，第二行编辑器。</summary>
    Stacked = 2,
}

/// <summary>
/// 设置行（旧 <c>Setting</c> + <c>SettingLayoutPanel</c> 的 WinUI 版，报告 §3 第二步第一行）。
/// <para>
/// 四个槽位：图标、标题/说明/重启·权限提示、编辑器。页面把自己要放的编辑控件直接写成子元素
/// （Grid 的隐式内容就是 Children），例如：
/// <code>
/// &lt;controls:SettingRow Title="..." Subtitle="..."&gt;
///     &lt;ToggleSwitch /&gt;
/// &lt;/controls:SettingRow&gt;
/// </code>
/// 行的测量/排布完全由本类接管：内容宽度不足 “图标 + 标题最小宽 + 间隙 + 编辑器需求宽” 时，
/// 编辑器整体换到下一行。阈值带回差（hysteresis），窗口边缘来回拖动时不会在两种排布间抖动。
/// </para>
/// <para>
/// 提示文案来自 host 的 Locale（<see cref="AdminRequiredText"/> / <see cref="RestartRequiredText"/>），
/// 控件自身不写死中文；调用方也可以覆盖成本页面的说法。
/// </para>
/// </summary>
public class SettingRow : Grid
{
    private const double DefaultLabelMinWidth = 160.0;
    private const double DefaultRowGap = 12.0;
    private const double DefaultIconGap = 14.0;
    private const double EditorGap = 8.0;
    private const double Hysteresis = 16.0;

    // ---- 依赖属性 ---------------------------------------------------------------------

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(object), typeof(SettingRow), new PropertyMetadata(null, OnVisualPartChanged));

    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnVisualPartChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnVisualPartChanged));

    /// <summary>需要管理员权限才能修改该设置（显示提示，不负责提权）。</summary>
    public static readonly DependencyProperty AdminRequiredProperty = DependencyProperty.Register(
        nameof(AdminRequired), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, OnVisualPartChanged));

    /// <summary>修改后需要重启应用才生效。</summary>
    public static readonly DependencyProperty RestartRequiredProperty = DependencyProperty.Register(
        nameof(RestartRequired), typeof(bool), typeof(SettingRow), new PropertyMetadata(false, OnVisualPartChanged));

    /// <summary>权限提示文案；默认取 Locale.Settings_AdminRequired，页面可覆盖。</summary>
    public static readonly DependencyProperty AdminRequiredTextProperty = DependencyProperty.Register(
        nameof(AdminRequiredText), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnVisualPartChanged));

    /// <summary>重启提示文案；默认取 Locale.Settings_RestartRequired，页面可覆盖。</summary>
    public static readonly DependencyProperty RestartRequiredTextProperty = DependencyProperty.Register(
        nameof(RestartRequiredText), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty, OnVisualPartChanged));

    public static readonly DependencyProperty LayoutModeProperty = DependencyProperty.Register(
        nameof(LayoutMode), typeof(SettingRowLayoutMode), typeof(SettingRow),
        new PropertyMetadata(SettingRowLayoutMode.Auto, OnLayoutPropertyChanged));

    /// <summary>单行排布时留给标题组的最小宽度。</summary>
    public static readonly DependencyProperty LabelMinWidthProperty = DependencyProperty.Register(
        nameof(LabelMinWidth), typeof(double), typeof(SettingRow),
        new PropertyMetadata(DefaultLabelMinWidth, OnLayoutPropertyChanged));

    /// <summary>页面声明的编辑器合理最小宽度（0 = 只按编辑器自然宽度判断）。</summary>
    public static readonly DependencyProperty EditorMinWidthProperty = DependencyProperty.Register(
        nameof(EditorMinWidth), typeof(double), typeof(SettingRow),
        new PropertyMetadata(0.0, OnLayoutPropertyChanged));

    /// <summary>编辑器对齐；换行后宽编辑器一般用 Stretch，开关类保持 Right。</summary>
    public static readonly DependencyProperty EditorHorizontalAlignmentProperty = DependencyProperty.Register(
        nameof(EditorHorizontalAlignment), typeof(HorizontalAlignment), typeof(SettingRow),
        new PropertyMetadata(HorizontalAlignment.Right, OnLayoutPropertyChanged));

    /// <summary>换行时标题组与编辑器之间的垂直间距。</summary>
    public static readonly DependencyProperty RowGapProperty = DependencyProperty.Register(
        nameof(RowGap), typeof(double), typeof(SettingRow), new PropertyMetadata(DefaultRowGap, OnLayoutPropertyChanged));

    /// <summary>图标与标题组之间的水平间距。</summary>
    public static readonly DependencyProperty IconGapProperty = DependencyProperty.Register(
        nameof(IconGap), typeof(double), typeof(SettingRow), new PropertyMetadata(DefaultIconGap, OnLayoutPropertyChanged));

    // ---- 部件 -------------------------------------------------------------------------

    private readonly ContentControl _iconHost;
    private readonly StackPanel _labelGroup;
    private readonly TextBlock _title;
    private readonly TextBlock _subtitle;
    private readonly TextBlock _adminHint;
    private readonly TextBlock _restartHint;

    /// <summary>当前是否处于两行排布（上一次测量结果；回差用）。</summary>
    private bool _stacked;

    public SettingRow()
    {
        // 行本身不做成模板控件：旧实现的模板只负责三个槽位，这里直接接管 Grid 的测量/排布，
        // 页面把编辑控件写成子元素即可，不需要额外 ControlTemplate 或主题字典。

        Padding = new Thickness(16, 12, 16, 12);

        _title = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        _subtitle = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _adminHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        _restartHint = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        ApplyTextStyle(_title, "HostSettingTitleStyle", 14, secondary: false);
        ApplyTextStyle(_subtitle, "HostSettingSubtitleStyle", 12, secondary: true);
        ApplyTextStyle(_adminHint, "HostSettingHintStyle", 12, secondary: true);
        ApplyTextStyle(_restartHint, "HostSettingHintStyle", 12, secondary: true);

        _labelGroup = new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _title, _subtitle, _adminHint, _restartHint },
        };

        _iconHost = new ContentControl
        {
            Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            IsHitTestVisible = false,
            Padding = new Thickness(0),
        };

        Children.Add(_iconHost);
        Children.Add(_labelGroup);

        // 提示文案由 host Locale 提供（与旧 Setting 门面一致），控件不内联中文。

        AdminRequiredText = Locale.Settings_AdminRequired ?? string.Empty;
        RestartRequiredText = Locale.Settings_RestartRequired ?? string.Empty;

        SyncVisualParts();
    }

    // ---- CLR 包装 ---------------------------------------------------------------------

    /// <summary>图标槽位：任意 UIElement（PathIcon / FontIcon / Image…）。</summary>
    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool AdminRequired
    {
        get => (bool)GetValue(AdminRequiredProperty);
        set => SetValue(AdminRequiredProperty, value);
    }

    public bool RestartRequired
    {
        get => (bool)GetValue(RestartRequiredProperty);
        set => SetValue(RestartRequiredProperty, value);
    }

    public string AdminRequiredText
    {
        get => (string)GetValue(AdminRequiredTextProperty);
        set => SetValue(AdminRequiredTextProperty, value);
    }

    public string RestartRequiredText
    {
        get => (string)GetValue(RestartRequiredTextProperty);
        set => SetValue(RestartRequiredTextProperty, value);
    }

    public SettingRowLayoutMode LayoutMode
    {
        get => (SettingRowLayoutMode)GetValue(LayoutModeProperty);
        set => SetValue(LayoutModeProperty, value);
    }

    public double LabelMinWidth
    {
        get => (double)GetValue(LabelMinWidthProperty);
        set => SetValue(LabelMinWidthProperty, value);
    }

    public double EditorMinWidth
    {
        get => (double)GetValue(EditorMinWidthProperty);
        set => SetValue(EditorMinWidthProperty, value);
    }

    public HorizontalAlignment EditorHorizontalAlignment
    {
        get => (HorizontalAlignment)GetValue(EditorHorizontalAlignmentProperty);
        set => SetValue(EditorHorizontalAlignmentProperty, value);
    }

    public double RowGap
    {
        get => (double)GetValue(RowGapProperty);
        set => SetValue(RowGapProperty, value);
    }

    public double IconGap
    {
        get => (double)GetValue(IconGapProperty);
        set => SetValue(IconGapProperty, value);
    }

    /// <summary>上一次测量的单行阈值；诊断/自检用。</summary>
    public double LastHorizontalThreshold { get; private set; }

    /// <summary>上一次测量得到的排布结果。</summary>
    public bool IsStacked => _stacked;

    // ---- 属性变化 ---------------------------------------------------------------------

    private static void OnVisualPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingRow)d).SyncVisualParts();

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SettingRow)d).InvalidateMeasure();

    /// <summary>把属性值写进三个槽位的部件（唯一写入点，避免渲染出属性里没有的状态）。</summary>
    private void SyncVisualParts()
    {
        _title.Text = Title ?? string.Empty;
        _title.Visibility = string.IsNullOrEmpty(Title) ? Visibility.Collapsed : Visibility.Visible;

        _subtitle.Text = Subtitle ?? string.Empty;
        _subtitle.Visibility = string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        _adminHint.Text = AdminRequiredText ?? string.Empty;
        _adminHint.Visibility = AdminRequired ? Visibility.Visible : Visibility.Collapsed;

        _restartHint.Text = RestartRequiredText ?? string.Empty;
        _restartHint.Visibility = RestartRequired ? Visibility.Visible : Visibility.Collapsed;

        var icon = Icon;
        if (!ReferenceEquals(_iconHost.Content, icon))
        {
            _iconHost.Content = icon;
        }

        _iconHost.Visibility = icon is null ? Visibility.Collapsed : Visibility.Visible;
        InvalidateMeasure();
    }

    private static void ApplyTextStyle(TextBlock target, string key, double fallbackFontSize, bool secondary)
    {
        if (Application.Current?.Resources is { } resources
            && resources.TryGetValue(key, out var value) && value is Style style)
        {
            target.Style = style;
            return;
        }

        // 资源字典没加载（设计器/单测）时的兜底：只用中性的字号/透明度，不写死颜色。

        target.FontSize = fallbackFontSize;
        if (secondary)
        {
            target.Opacity = 0.65;
        }
    }

    // ---- 排布 -------------------------------------------------------------------------

    private List<FrameworkElement> GetEditorChildren()
    {
        var editors = new List<FrameworkElement>();
        foreach (var child in Children)
        {
            if (ReferenceEquals(child, _iconHost) || ReferenceEquals(child, _labelGroup)) continue;
            if (child is FrameworkElement element && element.Visibility == Visibility.Visible) editors.Add(element);
        }

        return editors;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var padding = Padding;
        var contentWidth = double.IsFinite(availableSize.Width)
            ? Math.Max(0, availableSize.Width - padding.Left - padding.Right)
            : double.PositiveInfinity;

        _iconHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var iconVisible = _iconHost.Visibility == Visibility.Visible;
        var iconWidth = iconVisible ? _iconHost.DesiredSize.Width + IconGap : 0;
        var iconHeight = iconVisible ? _iconHost.DesiredSize.Height : 0;

        var editors = GetEditorChildren();

        // 编辑器需求宽度：先按无限宽量一次，得到与“当前是否已换行”无关的稳定需求；
        // 否则窄排布下的自然宽度会把阈值一起缩小，导致再也回不到单行。

        var editorNaturalWidth = 0.0;
        foreach (var editor in editors)
        {
            editor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (double.IsFinite(editor.DesiredSize.Width)) editorNaturalWidth += editor.DesiredSize.Width;
        }

        if (editors.Count > 1) editorNaturalWidth += (editors.Count - 1) * EditorGap;

        var editorRequired = Math.Max(0, EditorMinWidth);
        foreach (var editor in editors)
        {
            if (editor.Width > 0) editorRequired = Math.Max(editorRequired, editor.Width);
            if (editor.MinWidth > 0) editorRequired = Math.Max(editorRequired, editor.MinWidth);
        }

        if (double.IsFinite(editorNaturalWidth)) editorRequired = Math.Max(editorRequired, editorNaturalWidth);

        var labelMin = Math.Max(0, LabelMinWidth);
        var rowGap = Math.Max(0, RowGap);
        var threshold = iconWidth + labelMin + rowGap + editorRequired;
        LastHorizontalThreshold = threshold;

        _stacked = LayoutMode switch
        {
            SettingRowLayoutMode.Stacked => true,
            SettingRowLayoutMode.Inline => false,
            _ => double.IsFinite(contentWidth) && (_stacked ? contentWidth < threshold + Hysteresis : contentWidth < threshold),
        };

        // 编辑器的最终测量：单行时留给它的宽度是“整行 - 图标 - 标题最小宽 - 间隙”。

        var editorAvailable = _stacked
            ? contentWidth
            : (double.IsFinite(contentWidth)
                ? Math.Max(0, contentWidth - iconWidth - labelMin - rowGap)
                : double.PositiveInfinity);

        var editorWidth = 0.0;
        var editorHeight = 0.0;
        foreach (var editor in editors)
        {
            editor.Measure(new Size(editorAvailable, double.PositiveInfinity));
            if (double.IsFinite(editor.DesiredSize.Width)) editorWidth += editor.DesiredSize.Width;
            editorHeight = Math.Max(editorHeight, editor.DesiredSize.Height);
        }

        if (editors.Count > 1) editorWidth += (editors.Count - 1) * EditorGap;

        var labelWidth = _stacked
            ? Math.Max(0, contentWidth - iconWidth)
            : Math.Max(0, contentWidth - iconWidth - editorWidth - rowGap);

        _labelGroup.Measure(new Size(double.IsFinite(labelWidth) ? labelWidth : double.PositiveInfinity, double.PositiveInfinity));

        var labelHeight = _labelGroup.DesiredSize.Height;

        if (_stacked)
        {
            var firstRow = Math.Max(iconHeight, labelHeight);
            var height = firstRow + (editors.Count > 0 ? rowGap + editorHeight : 0);
            var width = double.IsFinite(contentWidth)
                ? contentWidth
                : Math.Max(iconWidth + _labelGroup.DesiredSize.Width, editorWidth);
            return new Size(width + padding.Left + padding.Right, height + padding.Top + padding.Bottom);
        }
        else
        {
            var height = Math.Max(Math.Max(iconHeight, labelHeight), editorHeight);
            var width = double.IsFinite(contentWidth)
                ? contentWidth
                : iconWidth + _labelGroup.DesiredSize.Width + rowGap + editorWidth;
            return new Size(width + padding.Left + padding.Right, height + padding.Top + padding.Bottom);
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var padding = Padding;
        var left = padding.Left;
        var top = padding.Top;
        var width = Math.Max(0, finalSize.Width - padding.Left - padding.Right);

        var iconVisible = _iconHost.Visibility == Visibility.Visible;
        var iconWidth = iconVisible ? _iconHost.DesiredSize.Width + IconGap : 0;
        var iconHeight = iconVisible ? _iconHost.DesiredSize.Height : 0;

        var editors = GetEditorChildren();
        var rowGap = Math.Max(0, RowGap);

        var editorWidth = 0.0;
        var editorHeight = 0.0;
        foreach (var editor in editors)
        {
            editorWidth += editor.DesiredSize.Width;
            editorHeight = Math.Max(editorHeight, editor.DesiredSize.Height);
        }

        if (editors.Count > 1) editorWidth += (editors.Count - 1) * EditorGap;

        var labelHeight = _labelGroup.DesiredSize.Height;

        if (_stacked)
        {
            var firstRow = Math.Max(iconHeight, labelHeight);

            if (iconVisible)
            {
                _iconHost.Arrange(new Rect(left, top + Math.Max(0, (firstRow - iconHeight) / 2),
                    _iconHost.DesiredSize.Width, iconHeight));
            }

            _labelGroup.Arrange(new Rect(left + iconWidth, top, Math.Max(0, width - iconWidth), labelHeight));

            if (editors.Count > 0)
            {
                ArrangeEditorRow(editors, left, width, top + firstRow + rowGap, editorWidth, editorHeight);
            }
        }
        else
        {
            var editorLeft = left + Math.Max(0, width - editorWidth);

            if (iconVisible)
            {
                _iconHost.Arrange(new Rect(left, top + Math.Max(0, (firstRowHeight(iconHeight, labelHeight, editorHeight) - iconHeight) / 2),
                    _iconHost.DesiredSize.Width, iconHeight));
            }

            _labelGroup.Arrange(new Rect(
                left + iconWidth,
                top + Math.Max(0, (firstRowHeight(iconHeight, labelHeight, editorHeight) - labelHeight) / 2),
                Math.Max(0, editorLeft - left - iconWidth - rowGap),
                labelHeight));

            if (editors.Count > 0)
            {
                ArrangeEditorRow(editors, editorLeft, Math.Max(0, width - (editorLeft - left)),
                    top + Math.Max(0, (firstRowHeight(iconHeight, labelHeight, editorHeight) - editorHeight) / 2),
                    editorWidth, editorHeight);
            }
        }

        return finalSize;

        static double firstRowHeight(double icon, double label, double editor) => Math.Max(Math.Max(icon, label), editor);
    }

    /// <summary>
    /// 把编辑器组放到 <paramref name="rowLeft"/> 起、宽 <paramref name="rowWidth"/> 的行里。

    /// 单个编辑器（常态）按 <see cref="EditorHorizontalAlignment"/> 对齐；多个编辑器按顺序横排。

    /// </summary>
    private void ArrangeEditorRow(IReadOnlyList<FrameworkElement> editors, double rowLeft, double rowWidth,
        double y, double editorWidth, double editorHeight)
    {
        var alignment = EditorHorizontalAlignment;
        var groupWidth = alignment == HorizontalAlignment.Stretch ? Math.Max(editorWidth, rowWidth) : editorWidth;

        var groupLeft = alignment switch
        {
            HorizontalAlignment.Left => rowLeft,
            HorizontalAlignment.Center => rowLeft + Math.Max(0, (rowWidth - groupWidth) / 2),
            HorizontalAlignment.Stretch => rowLeft,
            _ => rowLeft + Math.Max(0, rowWidth - groupWidth),
        };

        if (editors.Count == 1)
        {
            var child = editors[0];
            var childWidth = alignment == HorizontalAlignment.Stretch
                ? groupWidth
                : Math.Min(child.DesiredSize.Width, groupWidth);
            child.Arrange(new Rect(groupLeft, y, childWidth, child.DesiredSize.Height));
            return;
        }

        var x = groupLeft;
        foreach (var child in editors)
        {
            child.Arrange(new Rect(x, y, child.DesiredSize.Width, child.DesiredSize.Height));
            x += child.DesiredSize.Width + EditorGap;
        }
    }
}
