using System.Runtime.CompilerServices;
using DeskCanvas.WinUI.Controls;
using DeskCanvas.WinUI.WidgetEditors;
using Microsoft.UI.Xaml.Controls;
using ClockLocale = global::Clock.Locales.Locale;
using FlipClockSettingsViewModel = global::Clock.ViewModels.FlipClockSettingsViewModel;

namespace DeskCanvas.WinUI.WidgetEditors.ClockEditors;

/// <summary>
/// Clock · 翻页时钟（<c>Clock.Views.Settings.FlipClockSettings</c>）的 WinUI 版。
/// <para>
/// 逐项对照旧页面：24 小时制、显示秒数 + 秒数样式（翻转卡 / 徽标，显示秒数时才出现）。
/// 显示日期 + 日期位置（顶部条 / 底部条 / 卡片覆盖，显示日期时才出现）。
/// 本机时区开关 + 时区下拉。
/// </para>
/// </summary>
internal sealed class FlipClockEditorPage : ClockEditorPageBase
{
    private readonly FlipClockSettingsViewModel viewModel;
    private readonly SettingRow secondsStyleRow;
    private readonly SettingRow datePositionRow;
    private readonly SettingRow timeZoneRow;

    public FlipClockEditorPage(WidgetEditorContext context)
        : base(context)
    {
        viewModel = new FlipClockSettingsViewModel(context.LayoutProvider);

        Add(ToggleRow(ClockLocale.Clock_Use24Hours, viewModel.Use24Hours,
            value => Save(() => viewModel.Use24Hours = value)));

        Add(ToggleRow(ClockLocale.Clock_ShowSeconds, viewModel.ShowSeconds, OnShowSecondsChanged));

        var secondsStyleCombo = EditorUi.Combo(
            viewModel.SecondsStyleOptions,
            option => option.DisplayName,
            viewModel.SelectedSecondsStyleOption,
            option => Save(() => viewModel.SelectedSecondsStyleOption = option),
            minWidth: 195);
        secondsStyleRow = EditorUi.Row(ClockLocale.Clock_SecondsStyle, null, secondsStyleCombo,
            viewModel.ShowSeconds, editorMinWidth: 195);
        Add(secondsStyleRow);

        Add(ToggleRow(ClockLocale.Clock_ShowDate, viewModel.ShowDate, OnShowDateChanged));

        var datePositionCombo = EditorUi.Combo(
            viewModel.DatePositionOptions,
            option => option.DisplayName,
            viewModel.SelectedDatePositionOption,
            option => Save(() => viewModel.SelectedDatePositionOption = option),
            minWidth: 195);
        datePositionRow = EditorUi.Row(ClockLocale.Clock_DatePosition, null, datePositionCombo,
            viewModel.ShowDate, editorMinWidth: 195);
        Add(datePositionRow);

        Add(ToggleRow(ClockLocale.Clock_LocalTimeZone, viewModel.UseLocalTimeZone, OnLocalTimeZoneChanged));

        timeZoneRow = TimeZoneRow(
            DistinctZones(viewModel.TimeZones),
            viewModel.TimeZone,
            zone => viewModel.TimeZone = zone,
            viewModel.ShowTimeZones);
        Add(timeZoneRow);
    }

    private void OnShowSecondsChanged(bool showSeconds)
    {
        Save(() => viewModel.ShowSeconds = showSeconds);
        EditorUi.SetVisible(secondsStyleRow, viewModel.ShowSeconds);
    }

    private void OnShowDateChanged(bool showDate)
    {
        Save(() => viewModel.ShowDate = showDate);
        EditorUi.SetVisible(datePositionRow, viewModel.ShowDate);
    }

    private void OnLocalTimeZoneChanged(bool useLocal)
    {
        Save(() => viewModel.UseLocalTimeZone = useLocal);
        EditorUi.SetVisible(timeZoneRow, viewModel.ShowTimeZones);
    }
}

/// <summary>自注册：翻页时钟设置页。/summary>
internal static class FlipClockEditorRegistration
{
    [ModuleInitializer]
    internal static void Register()
        => WidgetEditorRegistry.Register(
            "Clock",
            "Clock.Views.Settings.FlipClockSettings",
            static context => new FlipClockEditorPage(context));
}
