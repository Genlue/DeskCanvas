using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DeskCanvas.Locales;

namespace DeskCanvas.WinUI.Controls;

/// <summary>
/// 二级面板的共用外壳（旧 <c>SecondaryPanelShell</c> 的 WinUI 版，报告 §3 第二步第五行）。
/// <list type="bullet">
/// <item>槽位：<see cref="Title"/> / <see cref="Subtitle"/> / <see cref="Body"/> / <see cref="Footer"/>，
/// 以及标题右侧的业务操作 <see cref="HeaderActions"/>（收藏、刷新、分类…）。</item>
/// <item>关闭是“请求”而不是动作：<see cref="CloseRequested"/> 由面板窗口决定何时做退场动画并关闭。</item>
/// <item><see cref="HeaderRoot"/> 暴露给面板窗口做拖动命中区——外壳不管窗口几何。</item>
/// <item>面板正文一律 WinUI 控件；外壳不做玻璃/材质采样，材质由面板窗口背景负责（报告 §4.1）。</item>
/// </list>
/// </summary>
public partial class SecondaryPanelShell : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SecondaryPanelShell),
        new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle), typeof(string), typeof(SecondaryPanelShell),
        new PropertyMetadata(string.Empty, OnSubtitleChanged));

    /// <summary>标题右侧的业务按钮（放在关闭按钮之前）。</summary>
    public static readonly DependencyProperty HeaderActionsProperty = DependencyProperty.Register(
        nameof(HeaderActions), typeof(object), typeof(SecondaryPanelShell),
        new PropertyMetadata(null, OnHeaderActionsChanged));

    /// <summary>正文槽位。</summary>
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(object), typeof(SecondaryPanelShell),
        new PropertyMetadata(null, OnBodyChanged));

    /// <summary>可选底部/状态行槽位。</summary>
    public static readonly DependencyProperty FooterProperty = DependencyProperty.Register(
        nameof(Footer), typeof(object), typeof(SecondaryPanelShell),
        new PropertyMetadata(null, OnFooterChanged));

    /// <summary>关闭按钮的无障碍名称/工具提示；默认取 Locale.SecondaryPanel_Close。</summary>
    public static readonly DependencyProperty CloseButtonLabelProperty = DependencyProperty.Register(
        nameof(CloseButtonLabel), typeof(string), typeof(SecondaryPanelShell),
        new PropertyMetadata(string.Empty, OnCloseButtonLabelChanged));

    /// <summary>面板请求关闭（Esc/外点/关闭按钮由面板窗口统一收口）。</summary>
    public event EventHandler? CloseRequested;

    public SecondaryPanelShell()
    {
        InitializeComponent();

        CloseButtonLabel = Locale.SecondaryPanel_Close ?? string.Empty;
        ApplyCloseButtonLabel();
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

    public object? HeaderActions
    {
        get => GetValue(HeaderActionsProperty);
        set => SetValue(HeaderActionsProperty, value);
    }

    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public object? Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }

    public string CloseButtonLabel
    {
        get => (string)GetValue(CloseButtonLabelProperty);
        set => SetValue(CloseButtonLabelProperty, value);
    }

    /// <summary>面板窗口拖动命中区（标题栏条带，不含正文）。</summary>
    public FrameworkElement HeaderDragArea => HeaderRoot;

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shell = (SecondaryPanelShell)d;
        shell.TitleText.Text = (string)e.NewValue ?? string.Empty;
        shell.TitleText.Visibility = string.IsNullOrEmpty(shell.TitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shell = (SecondaryPanelShell)d;
        shell.SubtitleText.Text = (string)e.NewValue ?? string.Empty;
        shell.SubtitleText.Visibility = string.IsNullOrEmpty(shell.SubtitleText.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnHeaderActionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shell = (SecondaryPanelShell)d;
        shell.ActionsHost.Content = e.NewValue;
        shell.ActionsHost.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnBodyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shell = (SecondaryPanelShell)d;
        shell.BodyHost.Content = e.NewValue;
    }

    private static void OnFooterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var shell = (SecondaryPanelShell)d;
        shell.FooterHost.Content = e.NewValue;
        shell.FooterRoot.Visibility = e.NewValue is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void OnCloseButtonLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SecondaryPanelShell)d).ApplyCloseButtonLabel();

    private void ApplyCloseButtonLabel()
    {
        var label = string.IsNullOrEmpty(CloseButtonLabel) ? "Close" : CloseButtonLabel;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(CloseButton, label);
        ToolTipService.SetToolTip(CloseButton, label);
    }

    private void OnCloseButtonClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
