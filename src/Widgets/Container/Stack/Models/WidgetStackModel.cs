using System.Collections.Generic;

namespace StackWidgets.Models;

/// <summary>
/// Description of a single stacked child widget.
/// </summary>
public record StackedWidgetEntry(
    string AssemblyName,
    string ViewTypeName,
    string DisplayTitle,
    string? SettingsJson = null,
    double? ContentScale = null
)
{
    public string GetLocalizedTitle() => StackWidgetTitleHelper.Localize(AssemblyName, ViewTypeName, DisplayTitle);
}

public static class StackWidgetTitleHelper
{
    private static bool IsZh => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", System.StringComparison.OrdinalIgnoreCase);

    private static readonly Dictionary<(string, string), (string Zh, string En)> KnownTitles = new()
    {
        [("Clock", "AnalogI")] = ("时钟 (经典表盘 I)", "Clock (Classic I)"),
        [("Clock", "AnalogIV")] = ("时钟 (表盘-3)", "Clock (Dial-3)"),
        [("Clock", "Digital")] = ("时钟 (数字时钟)", "Clock (Digital)"),
        [("Notes", "Note")] = ("便签", "Sticky Notes"),
        [("Reminders", "List")] = ("待办清单", "Reminders"),
        [("Monitor", "SingleMetric")] = ("系统监控 (轻量仪表)", "System Monitor (Single Metric)"),
        [("Monitor", "MultiDashboard")] = ("系统监控 (全能看板)", "System Monitor (Multi Dashboard)"),
        [("Weather", "Forecast")] = ("天气预报", "Weather Forecast"),
        [("Weather", "FourDayForecast")] = ("天气 (四日预报)", "Weather (4-Day Forecast)"),
        [("Batteries", "BatteriesView")] = ("电池电量", "Batteries"),
        [("Pomodoro", "PomodoroView")] = ("番茄工作法", "Pomodoro"),
        [("Progress", "ProgressView")] = ("进度刻度", "Progress"),
        [("Search", "SearchView")] = ("快捷搜索", "Quick Search"),
        [("Tools", "ClipboardView")] = ("剪贴板", "Clipboard"),
        [("Tools", "TranslatorView")] = ("翻译工具", "Translator"),
        [("Tools", "CalculatorView")] = ("计算器", "Calculator"),
        [("Folders", "Folder")] = ("文件夹 (标准)", "Folder (Standard)"),
        [("Folders", "BigFolder")] = ("大文件夹", "Big Folder"),
        [("Picture", "PictureView")] = ("相册照片", "Photo Frame"),
        [("Map", "MapView")] = ("全景地图", "World Map"),
        [("Calendar", "Date")] = ("日历 (日期)", "Calendar (Date)"),
        [("Calendar", "Month")] = ("日历 (月历)", "Calendar (Month)")
    };

    public static string Localize(string assemblyName, string viewTypeName, string fallbackTitle)
    {
        if (KnownTitles.TryGetValue((assemblyName, viewTypeName), out var pair))
        {
            if (string.IsNullOrEmpty(fallbackTitle) || fallbackTitle == pair.Zh || fallbackTitle == pair.En)
                return IsZh ? pair.Zh : pair.En;
        }
        return fallbackTitle;
    }
}

public class StackedWidgetSettingItem
{
    public StackedWidgetEntry Entry { get; set; } = null!;
    public int Index { get; set; }
    public string DisplayTitle => Entry.GetLocalizedTitle();
    public bool HasSettings { get; set; }
}

/// <summary>
/// Stored configuration for the Widget Stack component.
/// </summary>
public record WidgetStackModel(
    List<StackedWidgetEntry>? Entries = null,
    int SelectedIndex = 0,
    bool AllowWheelSwitch = true,
    bool WheelSwitchOnlyOnDots = true,
    bool AutoHideDots = false
)
{
    public List<StackedWidgetEntry> Entries { get; init; } = Entries ?? [];

    public WidgetStackModel() : this([], 0, true, true, false) {}

    public static List<StackedWidgetEntry> GetDefaultEntries() => [];
}
