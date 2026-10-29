using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskCanvas.WinUI.Controls;

/// <summary>
/// 设置页里“默认收起、点标题才展开”的分组（旧 <c>CollapsibleSection</c> 的 WinUI 版）。
/// <para>
/// 与旧控件一样，<see cref="IsExpanded"/> 是“这一次访问”的视图状态而不是设置项：页面可以把它
/// 绑到自己的属性上（TwoWay），控件负责与原生 Expander 双向同步。
/// </para>
/// <para>
/// 行内容放 <see cref="SectionContent"/>（不是 UserControl 的 Content）：本控件的 XAML 自己就是
/// 内容树，页面的子元素放 Content 会把整棵折叠外壳替换掉。
/// </para>
/// </summary>
public partial class CollapsibleSection : UserControl
{
    /// <summary>分组标题（折叠头的可点击文本）。</summary>
    public static readonly DependencyProperty HeaderProperty = DependencyProperty.Register(
        nameof(Header), typeof(string), typeof(CollapsibleSection),
        new PropertyMetadata(string.Empty, OnHeaderChanged));

    /// <summary>是否展开；默认收起（稳定默认值，页面通常绑到自己的视图状态）。</summary>
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(CollapsibleSection),
        new PropertyMetadata(false, OnIsExpandedChanged));

    /// <summary>折叠起来的那批行。</summary>
    public static readonly DependencyProperty SectionContentProperty = DependencyProperty.Register(
        nameof(SectionContent), typeof(object), typeof(CollapsibleSection),
        new PropertyMetadata(null, OnSectionContentChanged));

    public CollapsibleSection()
    {
        InitializeComponent();

        // 用户点折叠头时 Expander 自己改 IsExpanded；这里把它写回本控件的 DP，
        // 页面的 TwoWay 绑定才会收到通知。

        RootExpander.RegisterPropertyChangedCallback(Expander.IsExpandedProperty, OnExpanderIsExpandedChanged);

        SyncHeader();
        SyncContent();
        RootExpander.IsExpanded = IsExpanded;
    }

    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public object? SectionContent
    {
        get => GetValue(SectionContentProperty);
        set => SetValue(SectionContentProperty, value);
    }

    private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CollapsibleSection)d).SyncHeader();

    private static void OnSectionContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CollapsibleSection)d).SyncContent();

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var section = (CollapsibleSection)d;
        var value = (bool)e.NewValue;
        if (section.RootExpander.IsExpanded != value)
        {
            section.RootExpander.IsExpanded = value;
        }
    }

    private void OnExpanderIsExpandedChanged(DependencyObject sender, DependencyProperty dp)
    {
        var value = RootExpander.IsExpanded;
        if (IsExpanded != value)
        {
            IsExpanded = value;
        }
    }

    private void SyncHeader() => HeaderText.Text = Header ?? string.Empty;

    private void SyncContent()
    {
        if (ReferenceEquals(BodyHost.Content, SectionContent)) return;
        BodyHost.Content = SectionContent;
    }
}
