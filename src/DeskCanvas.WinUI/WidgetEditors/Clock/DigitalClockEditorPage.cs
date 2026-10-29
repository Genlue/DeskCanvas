using System.Runtime.CompilerServices;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ClockLocale = global::Clock.Locales.Locale;
using AnalogClockSettingsViewModel = global::Clock.ViewModels.AnalogClockSettingsViewModel;

namespace DeskCanvas.WinUI.WidgetEditors.ClockEditors;

/// <summary>
/// Clock · 数字时钟（<c>Clock.Views.Settings.DigitalClockSettings</c>）的 WinUI 版。
/// <para>
/// 逐项对照旧页面：显示秒数、显示日期、24 小时制、本机时区开关 + 时区下拉（旧页面没有折叠区）。
/// ViewModel 直接用组件包里的 <see cref="AnalogClockSettingsViewModel"/>（数字钟与指针钟共用它）。
/// 因此模型校验/存档格式与旧页面完全一致。
/// </para>
/// </summary>
internal sealed class DigitalClockEditorPage : ClockEditorPageBase
{
    private readonly AnalogClockSettingsViewModel viewModel;
    private readonly SettingRow timeZoneRow;

    public DigitalClockEditorPage(WidgetEditorContext context)
        : base(context)
    {
        viewModel = new AnalogClockSettingsViewModel(context.LayoutProvider);

        Add(ToggleRow(ClockLocale.Clock_ShowSeconds, viewModel.ShowSeconds,
            value => Save(() => viewModel.ShowSeconds = value)));

        Add(ToggleRow(ClockLocale.Clock_ShowDate, viewModel.ShowDate,
            value => Save(() => viewModel.ShowDate = value)));

        Add(ToggleRow(ClockLocale.Clock_Use24Hours, viewModel.Use24Hours,
            value => Save(() => viewModel.Use24Hours = value)));

        Add(ToggleRow(ClockLocale.Clock_LocalTimeZone, viewModel.UseLocalTimeZone, OnLocalTimeZoneChanged));

        timeZoneRow = TimeZoneRow(
            DistinctZones(viewModel.TimeZones),
            viewModel.TimeZone,
            zone => viewModel.TimeZone = zone,
            viewModel.ShowTimeZones);
        Add(timeZoneRow);
    }

    private void OnLocalTimeZoneChanged(bool useLocal)
    {
        Save(() => viewModel.UseLocalTimeZone = useLocal);
        EditorUi.SetVisible(timeZoneRow, viewModel.ShowTimeZones);
    }
}

/// <summary>
/// 自注册：模块加载时把本页登记到编辑页注册表。键 = (程序集名, 旧设置页完整类型名)。
/// 后续页面执行者各写自己的文件，不需要改注册表或窗口。
/// </summary>
internal static class DigitalClockEditorRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => WidgetEditorRegistry.Register(
            "Clock",
            "Clock.Views.Settings.DigitalClockSettings",
            static context => new DigitalClockEditorPage(context));
}
