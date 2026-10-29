using System;
using System.Runtime.CompilerServices;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ClockLocale = global::Clock.Locales.Locale;
using FramelessClockSettingsViewModel = global::Clock.ViewModels.FramelessClockSettingsViewModel;
// 只别名取需要的模型类型：直接 using DeskCanvas.Core.Models.Settings 会让 Grid 与
// Microsoft.UI.Xaml.Controls.Grid 冲突（CS0104，见报告 §0.1 第 1 条）。
using SurfaceStyle = DeskCanvas.Core.Models.Settings.SurfaceStyle;
using AppSettings = DeskCanvas.Core.Models.Settings.AppSettings;

namespace DeskCanvas.WinUI.WidgetEditors.ClockEditors;

/// <summary>
/// Clock · 无框数字钟（<c>Clock.Views.Settings.FramelessClockSettings</c>）的 WinUI 版。/// <para>
/// 逐项对照旧页面：24 小时制、显示秒数、独立材质主题（跟随全局 + 全部材质预设）。/// 液态玻璃不透明度（仅液态玻璃材质时出现）、字体来源（精。系统。 两个字体下拉（含说明）。/// 字体粗细。00。00，两端“细/粗”）、固定数字宽度、拉伸模式、覆盖颜色遮。+ 覆盖层选项
/// （跟随强调色 / 自定义颜。/ 不透明度）、本机时区开。+ 时区下拉。/// </para>
/// <para>
/// 滑块与颜色都在完成编辑时写入一次（。LabeledSlider/ColorInput 的提交语义，报告 §4.2）；
/// 其余开。下拉与旧页面一样选中即保存。页面打开只读模型。/// </para>
/// </summary>
internal sealed class FramelessClockEditorPage : ClockEditorPageBase
{
    private readonly FramelessClockSettingsViewModel viewModel;
    private readonly IAppSettingsProvider? appSettingsProvider;

    private readonly SettingRow liquidGlassRow;
    private readonly SettingRow artisticFontRow;
    private readonly SettingRow systemFontRow;
    private readonly StackPanel overlaySection;
    private readonly SettingRow overlayColorRow;
    private readonly SettingRow timeZoneRow;

    public FramelessClockEditorPage(WidgetEditorContext context)
        : base(context)
    {
        appSettingsProvider = context.GetService<IAppSettingsProvider>();
        viewModel = new FramelessClockSettingsViewModel(context.LayoutProvider);

        Add(ToggleRow(ClockLocale.Clock_Use24Hours, viewModel.Use24Hours,
            value => Save(() => viewModel.Use24Hours = value)));

        Add(ToggleRow(ClockLocale.Clock_ShowSeconds, viewModel.ShowSeconds,
            value => Save(() => viewModel.ShowSeconds = value)));

        // 独立材质主题：选项来自组件 VM（与外观页共享同一份材质预设目录）。
        var materialCombo = EditorUi.Combo(
            viewModel.MaterialOptions,
            option => option.DisplayName,
            viewModel.SelectedMaterialOption,
            option =>
            {
                Save(() => viewModel.SelectedMaterialOption = option);
                UpdateLiquidGlassVisibility();
            },
            minWidth: 210);
        Add(EditorUi.Row(ClockLocale.Clock_MaterialTheme, ClockLocale.Clock_MaterialTheme_Subtitle,
            materialCombo, editorMinWidth: 210));

        var liquidSlider = new LabeledSlider
        {
            Minimum = 0,
            Maximum = 100,
            Step = 1,
            Unit = "%",
            Width = 180,
            Value = viewModel.LiquidGlassOpacityPercent,
        };
        liquidSlider.ValueCommitted += (_, args) => Save(() => viewModel.LiquidGlassOpacityPercent = args.Value);
        AutomationProperties.SetName(liquidSlider, ClockLocale.Clock_LiquidGlassOpacity);
        liquidGlassRow = EditorUi.Row(ClockLocale.Clock_LiquidGlassOpacity, null, liquidSlider,
            ShowLiquidGlassOpacity(), editorMinWidth: 180);
        Add(liquidGlassRow);

        // 字体分类：精选锁屏艺术字。/ 系统自带字体。
        var fontSourceCombo = EditorUi.Combo(
            viewModel.FontSourceOptions,
            option => option.DisplayName,
            viewModel.SelectedFontSourceOption,
            option =>
            {
                Save(() => viewModel.SelectedFontSourceOption = option);
                UpdateFontVisibility();
            },
            minWidth: 210);
        Add(EditorUi.Row(ClockLocale.Clock_FontSource, null, fontSourceCombo, editorMinWidth: 210));

        var artisticCombo = EditorUi.Combo(
            viewModel.ArtisticFontOptions,
            option => option.DisplayName,
            viewModel.SelectedArtisticFont,
            option => Save(() => viewModel.SelectedArtisticFont = option),
            description: option => option.Description,
            minWidth: 210);
        artisticFontRow = EditorUi.Row(ClockLocale.Clock_ArtisticFont, null, artisticCombo,
            viewModel.ShowArtisticFontSelector, editorMinWidth: 210);
        Add(artisticFontRow);

        var systemCombo = EditorUi.Combo(
            viewModel.SystemFontOptions,
            option => option.DisplayName,
            viewModel.SelectedSystemFont,
            option => Save(() => viewModel.SelectedSystemFont = option),
            description: option => option.Description,
            minWidth: 210);
        systemFontRow = EditorUi.Row(ClockLocale.Clock_SystemFont, null, systemCombo,
            viewModel.ShowSystemFontSelector, editorMinWidth: 210);
        Add(systemFontRow);

        Add(EditorUi.Row(ClockLocale.Clock_FontWeight, ClockLocale.Clock_FontWeight_Subtitle,
            CreateFontWeightEditor(), editorMinWidth: 300));

        Add(ToggleRow(ClockLocale.Clock_FixedWidthDigits, viewModel.FixedWidthDigits,
            value => Save(() => viewModel.FixedWidthDigits = value),
            subtitle: ClockLocale.Clock_FixedWidthDigits_Subtitle));

        Add(ToggleRow(ClockLocale.Clock_StretchFill, viewModel.StretchFill,
            value => Save(() => viewModel.StretchFill = value),
            subtitle: ClockLocale.Clock_StretchFill_Subtitle));

        Add(ToggleRow(ClockLocale.Clock_EnableOverlay, viewModel.EnableOverlay, OnEnableOverlayChanged,
            subtitle: ClockLocale.Clock_EnableOverlay_Subtitle));

        // 覆盖层选项：跟随强调色 + 自定义颜色（RGB，alpha 由不透明度单独决定）+ 不透明度。
        var overlayColorInput = new ColorInput
        {
            ColorFormat = ColorInputFormat.Rgb,
            AlphaPolicy = ColorInputAlphaPolicy.ForceOpaque,
            Value = viewModel.OverlayColor,
            MinWidth = 180,
        };
        AutomationProperties.SetName(overlayColorInput, ClockLocale.Clock_OverlayColor);
        overlayColorInput.ValueCommitted += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Value)) return;
            Save(() => viewModel.OverlayColor = args.Value!);
        };

        overlayColorRow = EditorUi.Row(ClockLocale.Clock_OverlayColor, null, overlayColorInput,
            viewModel.ShowCustomColor, editorMinWidth: 180);

        var overlayOpacitySlider = new LabeledSlider
        {
            Minimum = 5,
            Maximum = 100,
            Step = 1,
            Unit = "%",
            Width = 180,
            Value = viewModel.OverlayOpacityPercent,
        };
        overlayOpacitySlider.ValueCommitted +=
            (_, args) => Save(() => viewModel.OverlayOpacityPercent = args.Value);
        AutomationProperties.SetName(overlayOpacitySlider, ClockLocale.Clock_OverlayOpacity);

        overlaySection = new StackPanel
        {
            Spacing = 2,
            Visibility = viewModel.ShowOverlayOptions ? Visibility.Visible : Visibility.Collapsed,
        };
        overlaySection.Children.Add(ToggleRow(ClockLocale.Clock_FollowAccentColor, viewModel.FollowAccentColor,
            OnFollowAccentChanged));
        overlaySection.Children.Add(overlayColorRow);
        overlaySection.Children.Add(EditorUi.Row(ClockLocale.Clock_OverlayOpacity, null, overlayOpacitySlider,
            editorMinWidth: 180));
        Add(overlaySection);

        Add(ToggleRow(ClockLocale.Clock_LocalTimeZone, viewModel.UseLocalTimeZone, OnLocalTimeZoneChanged));

        timeZoneRow = TimeZoneRow(
            DistinctZones(viewModel.TimeZones),
            viewModel.TimeZone,
            zone => viewModel.TimeZone = zone,
            viewModel.ShowTimeZones);
        Add(timeZoneRow);
    }

    public override void Attach()
    {
        if (appSettingsProvider is null) return;
        appSettingsProvider.DataChanged += OnAppSettingsChanged;
        UpdateLiquidGlassVisibility();
    }

    public override void Detach()
    {
        if (appSettingsProvider is null) return;
        appSettingsProvider.DataChanged -= OnAppSettingsChanged;
    }

    private Grid CreateFontWeightEditor()
    {
        var grid = new Grid
        {
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var thin = new TextBlock
        {
            Text = ClockLocale.Clock_FontWeight_Thin,
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(thin, 0);

        var slider = new LabeledSlider
        {
            Minimum = 100,
            Maximum = 900,
            Step = 1,
            Width = 200,
            VerticalAlignment = VerticalAlignment.Center,
            // 初值必须在挂提交事件之前给：初始化不写盘。
            Value = viewModel.FontWeightSlider,
        };
        slider.ValueCommitted += (_, args) => Save(() => viewModel.FontWeightSlider = args.Value);
        Grid.SetColumn(slider, 1);

        var thick = new TextBlock
        {
            Text = ClockLocale.Clock_FontWeight_Thick,
            FontSize = 11,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(thick, 2);

        grid.Children.Add(thin);
        grid.Children.Add(slider);
        grid.Children.Add(thick);
        return grid;
    }

    private void OnEnableOverlayChanged(bool enabled)
    {
        Save(() => viewModel.EnableOverlay = enabled);
        EditorUi.SetVisible(overlaySection, viewModel.ShowOverlayOptions);
    }

    private void OnFollowAccentChanged(bool followAccent)
    {
        Save(() => viewModel.FollowAccentColor = followAccent);
        EditorUi.SetVisible(overlayColorRow, viewModel.ShowCustomColor);
    }

    private void OnLocalTimeZoneChanged(bool useLocal)
    {
        Save(() => viewModel.UseLocalTimeZone = useLocal);
        EditorUi.SetVisible(timeZoneRow, viewModel.ShowTimeZones);
    }

    private void OnAppSettingsChanged(object sender, AppSettings? oldSettings, AppSettings newSettings)
        => UpdateLiquidGlassVisibility();

    private void UpdateLiquidGlassVisibility()
        => EditorUi.SetVisible(liquidGlassRow, ShowLiquidGlassOpacity());

    private void UpdateFontVisibility()
    {
        EditorUi.SetVisible(artisticFontRow, viewModel.ShowArtisticFontSelector);
        EditorUi.SetVisible(systemFontRow, viewModel.ShowSystemFontSelector);
    }

    /// <summary>
    /// 液态玻璃不透明度只在“组件自己的材质 = 液态玻。V2”或“跟随全局且全局 = 液态玻。V2”时可用
    /// （与组件 VM 。<c>ShowLiquidGlassOpacity</c> 同一判断；这里自己算是为了在页面生命周期。
    /// 跟随全局设置变化，而不。VM 再加一个长期订阅）。
    /// </summary>
    private bool ShowLiquidGlassOpacity()
    {
        if (viewModel.SurfaceOverride is { } surface) return surface == SurfaceStyle.LiquidGlassV2;

        try
        {
            return appSettingsProvider?.Get()?.Theme.EffectiveSurface == SurfaceStyle.LiquidGlassV2;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>自注册：无框数字钟设置页。/summary>
internal static class FramelessClockEditorRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => WidgetEditorRegistry.Register(
            "Clock",
            "Clock.Views.Settings.FramelessClockSettings",
            static context => new FramelessClockEditorPage(context));
}