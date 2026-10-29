using System;
using System.Runtime.CompilerServices;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using ClockLocale = global::Clock.Locales.Locale;
using AnalogClockSettingsViewModel = global::Clock.ViewModels.AnalogClockSettingsViewModel;
using AvColor = Avalonia.Media.Color;

namespace DeskCanvas.WinUI.WidgetEditors.ClockEditors;

/// <summary>
/// Clock · 指针时钟。c>Clock.Views.Settings.AnalogClockSettings</c>）的 WinUI 版。/// <para>
/// 逐项对照旧页面：显示秒数。4 小时制（。AnalogIV 可见）、显示日期（。AnalogIV 可见）。/// 表盘不透明（仅 AnalogI 可见）、表盘颜色折叠区（跟随背。/ 黑白 / 自定。+ 明暗两个颜色输入。/// 自定义时才显示）、本机时区开。+ 时区下拉。AnalogI/III/IV 三个组件共用这一页。/// </para>
/// <para>
/// 颜色保存语义与旧页面一致：<c>ColorInput</c> 只在完成编辑（Enter/失焦/拾色器关闭）后提交一次，
/// 提交后转。<c>#RRGGBB</c> 写入组件模型；打开页面不写盘。/// </para>
/// </summary>
internal sealed class AnalogClockEditorPage : ClockEditorPageBase
{
    private readonly AnalogClockSettingsViewModel viewModel;

    private readonly SettingRow use24HoursRow;
    private readonly SettingRow showDateRow;
    private readonly SettingRow dialOpaqueRow;
    private readonly SettingRow timeZoneRow;
    private readonly StackPanel dialColorSection;
    private readonly SettingRow dialColorDarkRow;
    private readonly SettingRow dialColorLightRow;

    public AnalogClockEditorPage(WidgetEditorContext context)
        : base(context)
    {
        viewModel = new AnalogClockSettingsViewModel(context.LayoutProvider);

        Add(ToggleRow(ClockLocale.Clock_ShowSeconds, viewModel.ShowSeconds,
            value => Save(() => viewModel.ShowSeconds = value)));

        use24HoursRow = ToggleRow(ClockLocale.Clock_Use24Hours, viewModel.Use24Hours,
            value => Save(() => viewModel.Use24Hours = value), viewModel.Show24HoursSetting);
        Add(use24HoursRow);

        showDateRow = ToggleRow(ClockLocale.Clock_ShowDate, viewModel.ShowDate,
            value => Save(() => viewModel.ShowDate = value), viewModel.ShowDateSetting);
        Add(showDateRow);

        dialOpaqueRow = ToggleRow(ClockLocale.Clock_DialOpaque, viewModel.IsDialOpaque, OnDialOpaqueChanged,
            viewModel.ShowDialOpaqueSetting);
        Add(dialOpaqueRow);

        // 表盘颜色折叠区：模式 + 明暗两个颜色输入（RGB，alpha 不参与表盘着色，恒写不透明）。
        var modeCombo = EditorUi.Combo(
            viewModel.DialColorModes,
            option => option.Name,
            viewModel.DialColorMode,
            option =>
            {
                Save(() => viewModel.DialColorMode = option);
                UpdateDialColorVisibility();
            },
            minWidth: 130);

        var darkInput = CreateColorInput(ClockLocale.Clock_DialColor_Dark, ToHex(viewModel.DialColorDark),
            color => Save(() => viewModel.DialColorDark = color));
        var lightInput = CreateColorInput(ClockLocale.Clock_DialColor_Light, ToHex(viewModel.DialColorLight),
            color => Save(() => viewModel.DialColorLight = color));

        dialColorDarkRow = EditorUi.Row(ClockLocale.Clock_DialColor_Dark, null, darkInput,
            viewModel.ShowDialColorPickers, editorMinWidth: 180);
        dialColorLightRow = EditorUi.Row(ClockLocale.Clock_DialColor_Light, null, lightInput,
            viewModel.ShowDialColorPickers, editorMinWidth: 180);

        dialColorSection = new StackPanel
        {
            Spacing = 2,
            Visibility = viewModel.ShowDialColorSetting ? Visibility.Visible : Visibility.Collapsed,
        };
        dialColorSection.Children.Add(EditorUi.Row(ClockLocale.Clock_DialColor_Title, null, modeCombo,
            editorMinWidth: 130));
        dialColorSection.Children.Add(dialColorDarkRow);
        dialColorSection.Children.Add(dialColorLightRow);
        Add(dialColorSection);

        Add(ToggleRow(ClockLocale.Clock_LocalTimeZone, viewModel.UseLocalTimeZone, OnLocalTimeZoneChanged));

        timeZoneRow = TimeZoneRow(
            DistinctZones(viewModel.TimeZones),
            viewModel.TimeZone,
            zone => viewModel.TimeZone = zone,
            viewModel.ShowTimeZones);
        Add(timeZoneRow);
    }

    private static ColorInput CreateColorInput(string title, string initialHex, Action<AvColor> apply)
    {
        var input = new ColorInput
        {
            ColorFormat = ColorInputFormat.Rgb,
            AlphaPolicy = ColorInputAlphaPolicy.ForceOpaque,
            Value = initialHex,
            MinWidth = 180,
        };

        AutomationProperties.SetName(input, title);
        input.ValueCommitted += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Value)) return;
            if (!AvColor.TryParse(args.Value, out var color)) return;
            apply(color);
        };

        return input;
    }

    private void OnDialOpaqueChanged(bool isOpaque)
    {
        Save(() => viewModel.IsDialOpaque = isOpaque);
        UpdateDialColorVisibility();
    }

    private void OnLocalTimeZoneChanged(bool useLocal)
    {
        Save(() => viewModel.UseLocalTimeZone = useLocal);
        EditorUi.SetVisible(timeZoneRow, viewModel.ShowTimeZones);
    }

    private void UpdateDialColorVisibility()
    {
        EditorUi.SetVisible(dialColorSection, viewModel.ShowDialColorSetting);
        EditorUi.SetVisible(dialColorDarkRow, viewModel.ShowDialColorPickers);
        EditorUi.SetVisible(dialColorLightRow, viewModel.ShowDialColorPickers);
    }

    private static string ToHex(AvColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>自注册：Clock 的三个指针钟共用这一页（报告 §3 第三步：多个视图可共用工厂）。/summary>
internal static class AnalogClockEditorRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => WidgetEditorRegistry.Register(
            "Clock",
            "Clock.Views.Settings.AnalogClockSettings",
            static context => new AnalogClockEditorPage(context));
}
