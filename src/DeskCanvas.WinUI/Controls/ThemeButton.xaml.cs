using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.WinUI.Controls;

/// <summary>
/// 主题（材质）样片（旧 <c>ThemeButton</c> 的 WinUI 版，报告 §3 第二步第六行）。
/// <para>
/// 本轮只做样片外观与选中态：模拟壁纸上的半透明/实色卡片，按 <see cref="Surface"/> 与
/// <see cref="IsDarkPreview"/> 画出材质与明暗的差异；“Aa” 表示卡片文字色。
/// 真实组件画面（旧实现里的液态玻璃/高光）需要 Avalonia 嵌入，留给后续页面按需接入。
/// </para>
/// <para>
/// 控件不读 IAppSettingsProvider、也不写设置：点击只触发 <see cref="Click"/>，
/// 由页面（应用主题页）决定切换语义与保存时机。
/// </para>
/// </summary>
public partial class ThemeButton : UserControl
{
    /// <summary>样片展示的材质。</summary>
    public static readonly DependencyProperty SurfaceProperty = DependencyProperty.Register(
        nameof(Surface), typeof(SurfaceStyle), typeof(ThemeButton),
        new PropertyMetadata(SurfaceStyle.Acrylic, OnVisualChanged));

    /// <summary>样片下方的名称（由页面从 Locale/SurfaceNames 注入）。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ThemeButton),
        new PropertyMetadata(string.Empty, OnVisualChanged));

    /// <summary>是否当前生效的材质（显示强调色选中环）。</summary>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(ThemeButton),
        new PropertyMetadata(false, OnVisualChanged));

    /// <summary>样片是否按深色预览（壁纸压暗 + 卡片用深色配色）。</summary>
    public static readonly DependencyProperty IsDarkPreviewProperty = DependencyProperty.Register(
        nameof(IsDarkPreview), typeof(bool), typeof(ThemeButton),
        new PropertyMetadata(false, OnVisualChanged));

    /// <summary>点击样片（页面在此切换并保存；控件自己不写设置）。</summary>
    public event RoutedEventHandler? Click;

    public ThemeButton()
    {
        InitializeComponent();
        SyncSample();
    }

    public SurfaceStyle Surface
    {
        get => (SurfaceStyle)GetValue(SurfaceProperty);
        set => SetValue(SurfaceProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    public bool IsDarkPreview
    {
        get => (bool)GetValue(IsDarkPreviewProperty);
        set => SetValue(IsDarkPreviewProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ThemeButton)d).SyncSample();

    private void SyncSample()
    {
        var dark = IsDarkPreview;
        DimOverlay.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;

        switch (Surface)
        {
            case SurfaceStyle.LiquidGlass:
                SampleCard.Background = ResolveBrush("HostGlassSampleBrush");
                SampleCard.BorderBrush = ResolveBrush("HostSampleRimBrush");
                SampleText.Foreground = ResolveBrush("HostSampleTextLightBrush");
                break;

            case SurfaceStyle.FullColor:
                SampleCard.Background = ResolveBrush(dark ? "HostFullColorDarkSampleBrush" : "HostFullColorLightSampleBrush");
                SampleCard.BorderBrush = null;
                SampleText.Foreground = ResolveBrush("HostSampleAccentTextBrush");
                break;

            default: // Acrylic
                SampleCard.Background = ResolveBrush("HostAcrylicSampleBrush");
                SampleCard.BorderBrush = ResolveBrush("HostSampleRimBrush");
                SampleText.Foreground = ResolveBrush("HostSampleTextLightBrush");
                break;
        }

        SelectionRing.BorderBrush = IsSelected ? ResolveBrush("HostSelectionRingBrush") : null;

        var label = Label ?? string.Empty;
        LabelText.Text = label;
        LabelText.Visibility = string.IsNullOrEmpty(label) ? Visibility.Collapsed : Visibility.Visible;
        AutomationProperties.SetName(SampleButton, label);
    }

    private static Brush? ResolveBrush(string key)
        => Application.Current?.Resources is { } resources
           && resources.TryGetValue(key, out var value) && value is Brush brush
            ? brush
            : null;

    private void OnSampleClick(object sender, RoutedEventArgs e) => Click?.Invoke(this, e);
}
