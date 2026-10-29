using System;
using System.Collections.Generic;
using DeskCanvas.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskCanvas.WinUI.WidgetEditors;

/// <summary>
/// 组件编辑页共用的小部件工厂（行、开关、下拉、说明文字、分隔线）。
/// <para>
/// 只做“把 WinUI 控件摆好”，不读写模型；提交语义由页面决定（开关立即保存、滑块颜色完成时保存）。
/// 样式从 <c>Styles/ThemeResources.xaml</c> 的语义 Token 取，取不到时退化为中性字/透明度，
/// 不在页面里散落颜色字面量。
/// </para>
/// </summary>
internal static class EditorUi
{
    /// <summary>标准设置行：标题/说明 + 编辑器（编辑器作为子元素放进 SettingRow 的第三槽位）。</summary>
    public static SettingRow Row(string title, string? subtitle, FrameworkElement editor,
        bool visible = true, double editorMinWidth = 0)
    {
        var row = new SettingRow
        {
            Title = title,
            Subtitle = subtitle ?? string.Empty,
            Visibility = visible ? Visibility.Visible : Visibility.Collapsed,
        };

        if (editorMinWidth > 0) row.EditorMinWidth = editorMinWidth;
        row.Children.Add(editor);
        return row;
    }

    /// <summary>
    /// 开关：先设初值再挂事件，因此初始化不会触发保存；之后每次用户切换只回调一次。
    /// </summary>
    public static ToggleSwitch Toggle(bool value, Action<bool> onChanged)
    {
        ArgumentNullException.ThrowIfNull(onChanged);

        var toggle = new ToggleSwitch
        {
            IsOn = value,
            OnContent = string.Empty,
            OffContent = string.Empty,
            MinWidth = 0,
            Margin = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        toggle.Toggled += (_, _) => onChanged(toggle.IsOn);
        return toggle;
    }

    /// <summary>
    /// 下拉框：选项包装成 <see cref="ComboBoxItem"/>（内容 = 名称 [+ 说明]），
    /// 初值在挂事件之前选中，所以初始化不会触发保存。
    /// </summary>
    /// <param name="items">选项列表（一次性快照；页面自己负责在外部数据变化时重建）。</param>
    /// <param name="label">选项主文案。</param>
    /// <param name="selected">当前值；为 null 时不预选。</param>
    /// <param name="onSelected">用户选中回调（只在真的变化时触发一次）。</param>
    /// <param name="matches">“同一个选项”的判断；默认用 <see cref="object.Equals(object?)"/>。</param>
    /// <param name="description">可选的第二行说明（字体选项用）。</param>
    /// <param name="minWidth">行内最小宽度。</param>
    public static ComboBox Combo<T>(
        IReadOnlyList<T> items,
        Func<T, string> label,
        T? selected,
        Action<T> onSelected,
        Func<T, T, bool>? matches = null,
        Func<T, string>? description = null,
        double minWidth = 130)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(onSelected);

        matches ??= static (a, b) => Equals(a, b);

        var combo = new ComboBox
        {
            MinWidth = minWidth,
            MaxDropDownHeight = 360,
            VerticalAlignment = VerticalAlignment.Center,
        };

        T? current = null;
        ComboBoxItem? initial = null;

        foreach (var item in items)
        {
            var content = new StackPanel { Spacing = 1, Margin = new Thickness(0, 3, 0, 3) };
            content.Children.Add(new TextBlock { Text = label(item), TextWrapping = TextWrapping.Wrap });

            if (description is not null)
            {
                var detail = description(item);
                if (!string.IsNullOrEmpty(detail))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = detail,
                        FontSize = 11,
                        Opacity = 0.65,
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }

            var container = new ComboBoxItem { Content = content, Tag = item };
            combo.Items.Add(container);

            if (selected is not null && initial is null && matches(item, selected))
            {
                initial = container;
            }
        }

        if (initial is not null)
        {
            combo.SelectedItem = initial;
            current = selected;
        }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: T chosen }) return;
            if (current is not null && matches(current, chosen)) return;

            current = chosen;
            onSelected(chosen);
        };

        return combo;
    }

    /// <summary>页面分组标题。</summary>
    public static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 4, 0, 0),
        Style = LookupStyle("HostSectionHeaderStyle"),
    };

    /// <summary>次要说明文字。</summary>
    public static TextBlock Caption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        Style = LookupStyle("HostCaptionTextStyle"),
    };

    /// <summary>分隔线（语义 Token，不写颜色）。</summary>
    public static Border Divider() => new()
    {
        Style = LookupStyle("HostDividerStyle"),
    };

    /// <summary>按需显示/隐藏一个元素。</summary>
    public static void SetVisible(UIElement element, bool visible)
        => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 从应用资源里找样式（含合并字典）。找不到返回 null，由控件保持 Fluent 默认外观。
    /// </summary>
    public static Style? LookupStyle(string key)
        => LookupResource(Application.Current?.Resources, key) as Style;

    /// <summary>从应用资源里找画刷（主题资源请优先用 XAML ThemeResource；代码里取不到时返回 null）。</summary>
    public static Brush? LookupBrush(string key)
        => LookupResource(Application.Current?.Resources, key) as Brush;

    private static object? LookupResource(ResourceDictionary? dictionary, string key)
    {
        if (dictionary is null) return null;

        try
        {
            if (dictionary.TryGetValue(key, out var value)) return value;
        }
        catch (Exception)
        {
            // 某些资源键在主题字典里，直接索引会抛；继续找合并字典。
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            if (LookupResource(merged, key) is { } found) return found;
        }

        return null;
    }
}
