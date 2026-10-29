using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using DeskCanvas.Locales;
using DeskCanvas.ViewModels;

namespace DeskCanvas.WinUI.Controls;

/// <summary>
/// 开发用目检页：把公共控件与典型状态排在一页里，供协调者截图验收（无参构造）。
/// <para>
/// 业务文案一律从 Locale 取；页面只写“开发者标题”（控件名/状态名）。这里的提交日志用于
/// 目视确认报告 §4.2 的提交语义：初始化、外部刷新不会出现在日志里，只有完成编辑才会。
/// </para>
/// </summary>
public sealed partial class DevelopmentControlGallery : UserControl
{
    private int _statusCycle;

    public DevelopmentControlGallery()
    {
        InitializeComponent();

        ApplyLocalizedText();
        ApplyDemoValues();
        WireStatusDemos();
    }

    /// <summary>把 Locale 文案灌进各个目检行（页面实际用法同此）。</summary>
    private void ApplyLocalizedText()
    {
        CommitLog.Text = "Last commit: —  (initialization and external reload never log here)";

        BlurRow.Title = Locale.Settings_Appearance_Glass_Blur ?? string.Empty;
        BlurRow.Subtitle = Locale.Settings_Appearance_Glass_BlurHint ?? string.Empty;

        ToggleRow.Title = Locale.Settings_Appearance_Glass_LiveSampling ?? string.Empty;
        ToggleRow.Subtitle = Locale.Settings_Appearance_Glass_LiveSamplingHint ?? string.Empty;

        AdminRow.Title = Locale.Settings_Appearance_AccentColor_Title ?? string.Empty;
        AdminActionButton.Content = Locale.Settings_Appearance_ApplyTheme ?? string.Empty;

        RestartRow.Title = Locale.Settings_Appearance_Glass_Highlight ?? string.Empty;

        NarrowRow.Title = Locale.Settings_Appearance_AccentShift_Title ?? string.Empty;
        NarrowRow.Subtitle = Locale.Settings_Appearance_AccentShift_Subtitle ?? string.Empty;
        NarrowRow.EditorMinWidth = 220;

        OpacityRow.Title = Locale.Settings_Appearance_OpacityLevel_Title ?? string.Empty;
        OpacityRow.Subtitle = Locale.Settings_Appearance_OpacityLevel_Subtitle ?? string.Empty;

        ReadOnlySliderRow.Title = Locale.Settings_Appearance_Glass_HighlightAngle ?? string.Empty;

        AccentRow.Title = Locale.Settings_Appearance_AccentColor_Title ?? string.Empty;
        BackgroundRow.Title = Locale.Settings_Appearance_Colors ?? string.Empty;
        InvalidColorRow.Title = Locale.Settings_Appearance_AccentColor_Manual ?? string.Empty;

        OpticsSection.Header = Locale.Settings_Appearance_Glass_Blur ?? string.Empty;
        OpticsRowA.Title = Locale.Settings_Appearance_Glass_LiveSampling ?? string.Empty;
        OpticsRowB.Title = Locale.Settings_Appearance_Glass_Blur ?? string.Empty;

        AcrylicSample.Label = SurfaceNames.Of(DeskCanvas.Core.Models.Settings.SurfaceStyle.Acrylic);
        GlassSample.Label = SurfaceNames.Of(DeskCanvas.Core.Models.Settings.SurfaceStyle.LiquidGlass);
        ColorfulSample.Label = SurfaceNames.Of(DeskCanvas.Core.Models.Settings.SurfaceStyle.FullColor);
        DarkSample.Label = SurfaceNames.Of(DeskCanvas.Core.Models.Settings.SurfaceStyle.Acrylic);

        PanelDemo.Title = Locale.Settings_Appearance_Glass_Blur ?? string.Empty;
        PanelDemo.Subtitle = Locale.Settings_Appearance_Glass_BlurHint ?? string.Empty;
        PanelRow.Title = Locale.Settings_Appearance_Glass_LiveSampling ?? string.Empty;
        PanelFooterText.Text = Locale.Settings_Spotlight_Files_Status_Connected ?? string.Empty;
    }

    /// <summary>目检用的典型值（不是用户提交，因此不会出现在提交日志里）。</summary>
    private void ApplyDemoValues()
    {
        BlurSlider.Value = 60;
        OpacitySlider.Value = 0.85;
        PlainSlider.Value = 24;
        HighlightSlider.Value = 40;
        LiveSamplingSwitch.IsOn = true;
        NarrowEditor.Text = "Consolas";

        AccentColorInput.Value = "#FF005FB8";
        BackgroundColorInput.Value = "#F2FFFFFF";
        PanelColorInput.Value = "#66FFFFFF";
        InvalidColorInput.Value = "#12"; // 坏存档值：应显示内联错误而不是悄悄清空

        AcrylicSample.IsSelected = true;
        GlassSample.Surface = DeskCanvas.Core.Models.Settings.SurfaceStyle.LiquidGlass;
        ColorfulSample.Surface = DeskCanvas.Core.Models.Settings.SurfaceStyle.FullColor;
        DarkSample.Surface = DeskCanvas.Core.Models.Settings.SurfaceStyle.FullColor;
    }

    private void WireStatusDemos()
    {
        var retry = Locale.Settings_Appearance_SaveRetry ?? string.Empty;

        StatusLoading.ShowLoading(Locale.Settings_General_Update_Checking ?? string.Empty);
        StatusEmpty.ShowEmpty(Locale.Settings_Appearance_FocusLink_Whitelist_Empty ?? string.Empty);

        StatusStale.RetryText = retry;
        StatusStale.ShowStale(Locale.Settings_Spotlight_Files_Status_Connected ?? string.Empty);
        StatusStale.RetryRequested += (_, _) => LogEvent("Stale retry requested");

        StatusError.RetryText = retry;
        StatusError.ShowError(Locale.Settings_Appearance_SaveFailed ?? string.Empty);
        StatusError.RetryRequested += (_, _) => LogEvent("Error retry requested");

        CycleStatusButton.Content = Locale.Settings_Appearance_SaveReload ?? string.Empty;
    }

    // ---- 提交日志：只记录“用户完成编辑” ------------------------------------------------

    private void OnSliderCommitted(object sender, LabeledSliderValueCommittedEventArgs e)
        => LogEvent($"LabeledSlider committed {e.Value:0.####}");

    private void OnColorCommitted(object sender, ColorInputValueCommittedEventArgs e)
        => LogEvent($"ColorInput committed {e.Value ?? "(empty)"}");

    private void OnAdminActionClick(object sender, RoutedEventArgs e) => LogEvent("Admin row action");

    private void OnThemeSampleClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not ThemeButton clicked) return;

        AcrylicSample.IsSelected = ReferenceEquals(clicked, AcrylicSample);
        GlassSample.IsSelected = ReferenceEquals(clicked, GlassSample);
        ColorfulSample.IsSelected = ReferenceEquals(clicked, ColorfulSample);
        DarkSample.IsSelected = ReferenceEquals(clicked, DarkSample);

        LogEvent($"Theme sample clicked: {clicked.Label}");
    }

    private void OnCycleStatusClick(object sender, RoutedEventArgs e)
    {
        _statusCycle = (_statusCycle + 1) % 3;
        switch (_statusCycle)
        {
            case 0:
                StatusLoading.ShowLoading(Locale.Settings_General_Update_Checking ?? string.Empty);
                break;
            case 1:
                StatusEmpty.ShowEmpty(Locale.Settings_Appearance_FocusLink_Whitelist_Empty ?? string.Empty);
                break;
            default:
                StatusError.ShowError(Locale.Settings_Appearance_SaveFailed ?? string.Empty);
                break;
        }

        LogEvent($"Status demo -> {_statusCycle}");
    }

    private void LogEvent(string message) => CommitLog.Text = "Last commit: " + message;
}
