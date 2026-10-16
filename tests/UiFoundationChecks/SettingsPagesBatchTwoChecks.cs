using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using DeskCanvas.Views.Controls;
using ColorInputFormat = DeskCanvas.UI.Controls.ColorInputFormat;
using ColorInputAlphaPolicy = DeskCanvas.UI.Controls.ColorInputAlphaPolicy;

namespace UiFoundationChecks;

/// <summary>
/// 设置页迁移 第二批(W9) 的契约检查：Batteries / Calendar / ControlCenter×2 / Fixed /
/// Folders×3 / Monitor×2 / Stack。每一页断言六件事：
/// 1) 声明 ScrollOwnership=Host，且页面自身没有整体 ScrollViewer（唯一滚动所有者是
///    EditWidget 内容视口）；
/// 2) 分组标题全部解析到共享 dc-section-header，且不再残留 group-header / section-header
///    过渡别名；
/// 3) 不再定义本地通用样式块（icon-btn / icon-button / group-header / section-header /
///    setting-label / setting-combo / setting-num / small-button / action-btn）；
/// 4) 设置行全部收敛为共享 Setting（按页钉住精确数量——统一不等于删行）；
/// 5) 滑块+数值行走 LabeledSlider、颜色行走 ColorInput（数量写进断言消息），
///    裸 Slider / 裸 ColorPicker 为 0；
/// 6) 关键业务控件仍在（Batteries 的设备刷新与类型记忆、ControlCenter 的拼装画布、
///    Folders 的条目列表管理、Stack 的子组件管理等不被"统一"抹掉）。
/// 实例化统一使用 Program.cs 里现成的 StubWidgetLayoutProvider。
/// 注意：助手命名带 BatchTwo 前缀，避免与第一批检查文件(W9a)的同名私有助手冲突——
/// 两个文件最终会合入同一个 partial Program。
/// </summary>
internal static partial class Program
{
    private static void CheckSettingsPagesBatchTwo()
    {
        Console.WriteLine("--- 设置页迁移 第二批(W9) ---");

        CheckBatteriesBatchTwo();
        CheckCalendarBatchTwo();
        CheckControlCenterBatchTwo();
        CheckAggregateBatchTwo();
        CheckFoldersBatchTwo();
        CheckMonitorBatchTwo();
        CheckWidgetStackBatchTwo();
    }

    // ---- 公共页面契约（与第一批同构，但独立命名以防 partial 冲突） --------------------------

    private static void CheckBatchTwoPageContract(
        string name,
        UserControl page,
        int expectedHeaders,
        int expectedSettings,
        Action? extra = null)
    {
        // 1) 滚动契约：Host 声明 + 无页面级 ScrollViewer（唯一滚动所有者是 EditWidget 视口）。
        Check(SettingsPage.GetScrollOwnership(page) == SettingsPageScrollOwnership.Host,
            $"{name}: declares ScrollOwnership=Host");
        var scrollViewers = page.GetLogicalDescendants().OfType<ScrollViewer>().ToList();
        Check(scrollViewers.Count == 0,
            $"{name}: keeps no page-level ScrollViewer (unique scroll owner is the host viewport, found {scrollViewers.Count})");

        // 2) 分组标题统一 dc-section-header，过渡别名清零。
        var headers = page.GetLogicalDescendants().OfType<TextBlock>()
            .Count(t => t.Classes.Contains("dc-section-header"));
        Check(headers == expectedHeaders,
            $"{name}: {expectedHeaders} group headers resolve dc-section-header (found {headers})");
        Check(!page.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Classes.Contains("group-header") || t.Classes.Contains("section-header")),
            $"{name}: no group-header / section-header transition alias remains");

        // 3) 本地通用样式块清零（tier-tab 等业务专属样式不受此限）。
        foreach (var fragment in new[]
                 {
                     "icon-btn", "icon-button", "group-header", "section-header",
                     "setting-label", "setting-desc", "setting-combo", "setting-num",
                     "setting-input", "small-button", "action-btn",
                 })
        {
            Check(!HasLocalSelector(page.Styles, fragment),
                $"{name}: keeps no local '{fragment}' base style");
        }

        // 4) 设置行收敛为共享 Setting（精确数量：统一不得吞掉业务行）。
        var settings = page.GetLogicalDescendants().OfType<Setting>().Count();
        Check(settings == expectedSettings,
            $"{name}: {expectedSettings} rows on the shared Setting (found {settings})");

        // 5) 滑块/颜色行走共享复合控件，裸控件为 0。
        var labeled = page.GetLogicalDescendants().OfType<LabeledSlider>().Count();
        var colorInputs = page.GetLogicalDescendants().OfType<ColorInput>().Count();
        var straySliders = CountStray<Slider, LabeledSlider>(page);
        var strayPickers = CountStray<ColorPicker, ColorInput>(page);
        Check(straySliders == 0,
            $"{name}: 0 bare Slider rows, {labeled} on LabeledSlider (stray {straySliders})");
        Check(strayPickers == 0,
            $"{name}: 0 bare ColorPicker rows, {colorInputs} on ColorInput (stray {strayPickers})");

        extra?.Invoke();
    }

    private static Setting? BatchTwoSetting(Control page, string title)
        => page.GetLogicalDescendants().OfType<Setting>()
            .FirstOrDefault(s => s.Title == title);

    // ---- Batteries -------------------------------------------------------------------------

    private static void CheckBatteriesBatchTwo()
    {
        var page = new Batteries.Views.Settings.BatteriesSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("Batteries", page, expectedHeaders: 3, expectedSettings: 3, extra: () =>
        {
            // 设备详情专属区域：刷新按钮、主机设备卡与外设/配对面板都在。
            Check(page.FindControl<Button>("RefreshDevicesBtn") is not null,
                "Batteries: the device refresh button survives");
            Check(page.FindControl<StackPanel>("PeripheralsPanel") is not null
                  && page.FindControl<StackPanel>("PairedDevicesPanel") is not null,
                "Batteries: the peripheral + paired-device panels survive");
            Check(page.FindControl<Border>("MainDeviceBadge") is not null,
                "Batteries: the battery badge (level data colours) survives");
            // 静态文案已资源化：三组标题直接读 Locale。
            Check(BatchTwoSetting(page, Batteries.Locales.Locale.Batteries_ShowPeripherals) is not null,
                "Batteries: the show-peripherals row survives");
        });
    }

    // ---- Calendar --------------------------------------------------------------------------

    private static void CheckCalendarBatchTwo()
    {
        var page = new Calendar.Views.Settings.MonthCalendarSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("MonthCalendar", page, expectedHeaders: 0, expectedSettings: 5, extra: () =>
        {
            // 今天颜色明暗两行走共享 ColorInput（模型持久化 "#RRGGBB"）。
            var colorInputs = page.GetLogicalDescendants().OfType<ColorInput>().Count();
            Check(colorInputs == 2,
                $"MonthCalendar: the light/dark today-colour rows are ColorInputs (found {colorInputs})");
            Check(page.GetLogicalDescendants().OfType<ColorInput>()
                    .All(c => c.ColorFormat == ColorInputFormat.Rgb && c.AlphaPolicy == ColorInputAlphaPolicy.ForceOpaque),
                "MonthCalendar: colour rows keep the opaque-RGB persisted format");
            Check(BatchTwoSetting(page, Calendar.Locales.Locale.Calendar_TodayColor) is not null
                  && BatchTwoSetting(page, Calendar.Locales.Locale.Calendar_FirstDayOfWeek) is not null,
                "MonthCalendar: the today-colour mode / first-day rows survive");
        });
    }

    // ---- ControlCenter ×2 ------------------------------------------------------------------

    private static void CheckControlCenterBatchTwo()
    {
        var composer = new ControlCenter.Views.Settings.ControlCenterSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("ControlCenter", composer, expectedHeaders: 1, expectedSettings: 1, extra: () =>
        {
            // 拼装画布业务整体保留：档位切换、交互画布、待选池与跨度按钮。
            Check(composer.FindControl<Canvas>("ComposerCanvas") is not null
                  && composer.FindControl<WrapPanel>("DockWrapPanel") is not null,
                "ControlCenter: the composer canvas + dock pool survive");
            Check(composer.FindControl<Button>("Tab2x2Btn") is not null
                  && composer.FindControl<Button>("Tab4x4Btn") is not null,
                "ControlCenter: the tier switcher tabs survive");
            Check(composer.FindControl<Button>("Span1x1Btn") is not null
                  && composer.FindControl<Button>("Span2x2Btn") is not null,
                "ControlCenter: the span/orientation buttons survive");
            Check(composer.GetLogicalDescendants().OfType<Button>()
                    .Count(b => b.Classes.Contains("tier-tab")) == 3,
                "ControlCenter: all three tabs keep the business tier-tab style");
            Check(BatchTwoSetting(composer, ControlCenter.Locales.Locale.ControlCenter_WheelStepTitle) is not null,
                "ControlCenter: the wheel-step row survives as a Setting");
        });

        var single = new ControlCenter.Views.Settings.SingleControlSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("SingleControl", single, expectedHeaders: 1, expectedSettings: 1, extra: () =>
        {
            // 单控件绑定卡片网格在 code-behind 构建，必须仍被填充。
            var grid = single.FindControl<UniformGrid>("ItemsGrid");
            Check(grid is not null && grid.Children.Count > 0,
                $"SingleControl: the tile-binding card grid is populated ({grid?.Children.Count ?? 0} cards)");
            Check(single.FindControl<ComboBox>("WheelStepCombo") is not null,
                "SingleControl: the wheel-step combo survives");
        });
    }

    // ---- Fixed (Aggregate) -----------------------------------------------------------------

    private static void CheckAggregateBatchTwo()
    {
        var page = new FixedWidgets.Views.Settings.AggregateSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("Aggregate", page, expectedHeaders: 0, expectedSettings: 10, extra: () =>
        {
            // 城市搜索识别与今天颜色明暗两行是本页业务。
            Check(page.FindControl<AutoCompleteBox>("SearchBox") is not null
                  && page.FindControl<TextBlock>("CityStatusText") is not null,
                "Aggregate: the city search + status business survives");
            Check(page.FindControl<ColorInput>("TodayColorLightInput") is not null
                  && page.FindControl<ColorInput>("TodayColorDarkInput") is not null,
                "Aggregate: the light/dark today-colour rows are named ColorInputs");
            Check(page.GetLogicalDescendants().OfType<ColorInput>()
                    .All(c => c.ColorFormat == ColorInputFormat.Rgb && c.AlphaPolicy == ColorInputAlphaPolicy.ForceOpaque),
                "Aggregate: colour rows keep the opaque-RGB persisted format");
            Check(page.FindControl<ComboBox>("TodayColorModeCombo") is not null,
                "Aggregate: the today-colour mode combo survives");
        });
    }

    // ---- Folders ×3 ------------------------------------------------------------------------

    private static void CheckFoldersBatchTwo()
    {
        var folder = new Folders.Views.Settings.FolderSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("Folder", folder, expectedHeaders: 5, expectedSettings: 24, extra: () =>
        {
            // 条目列表管理（路径输入、拾取按钮、拖拽排序行）是本页专属区域。
            Check(folder.FindControl<StackPanel>("ItemsEditor") is not null
                  && folder.FindControl<ItemsControl>("ItemList") is not null,
                "Folder: the hand-editable item list region survives");
            Check(folder.FindControl<TextBox>("PathBox") is not null
                  && folder.FindControl<TextBlock>("WatchFolderText") is not null,
                "Folder: the path entry + watch-folder display survive");
            // 监视文件夹的五条规则行（排序/目录优先/隐藏文件/扩展名/降序/隐藏子文件夹）
            // 现在是 Setting 包装行，未监视时必须整行隐藏而不是只剩标题的空行。
            foreach (var rowName in new[] { "WatchSortSetting", "DirsFirstSetting", "ShowHiddenSetting", "HideExtSetting", "SortDescSetting", "HideSubfoldersSetting" })
            {
                var row = folder.FindControl<Setting>(rowName);
                Check(row is not null && !row.IsVisible,
                    $"Folder: {rowName} exists and is hidden while not watching");
            }
            Check(folder.GetLogicalDescendants().OfType<LabeledSlider>().Count() == 1,
                "Folder: the title-offset row is a shared LabeledSlider");
        });

        var bigFolder = new Folders.Views.Settings.BigFolderSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("BigFolder", bigFolder, expectedHeaders: 3, expectedSettings: 12, extra: () =>
        {
            Check(bigFolder.FindControl<ItemsControl>("ItemList") is not null
                  && bigFolder.FindControl<StackPanel>("ItemsEditor") is not null,
                "BigFolder: the drag-reorderable item list region survives");
            Check(bigFolder.FindControl<ComboBox>("DensityBox") is not null
                  && bigFolder.FindControl<StackPanel>("CustomDimensionsPanel") is not null,
                "BigFolder: the density modes with custom columns/rows survive");
        });

        var singleFile = new Folders.Views.Settings.SingleFileSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("SingleFile", singleFile, expectedHeaders: 2, expectedSettings: 2, extra: () =>
        {
            var slider = singleFile.FindControl<LabeledSlider>("IconPercentSlider");
            Check(slider is { Minimum: 20, Maximum: 100 } && slider.Unit == "%",
                $"SingleFile: the icon-percent row keeps 20–100% on LabeledSlider (got {slider?.Minimum}–{slider?.Maximum})");
            Check(singleFile.FindControl<TextBox>("PathBox") is not null,
                "SingleFile: the read-only target path box survives");
        });
    }

    // ---- Monitor ×2 ------------------------------------------------------------------------

    private static void CheckMonitorBatchTwo()
    {
        var multi = new Monitor.Views.Settings.MultiDashboardSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("MultiDashboard", multi, expectedHeaders: 0, expectedSettings: 4, extra: () =>
        {
            // 多仪表盘的四个槽位选择器一个都不能少。
            var titles = new[]
            {
                Monitor.Locales.Locale.Monitor_MultiDashboard_Item1,
                Monitor.Locales.Locale.Monitor_MultiDashboard_Item2,
                Monitor.Locales.Locale.Monitor_MultiDashboard_Item3,
                Monitor.Locales.Locale.Monitor_MultiDashboard_Item4,
            };
            Check(titles.All(t => BatchTwoSetting(multi, t) is not null),
                "MultiDashboard: all four metric-slot rows survive");
            Check(multi.GetLogicalDescendants().OfType<ComboBox>().Count() == 4,
                $"MultiDashboard: four slot combos remain (found {multi.GetLogicalDescendants().OfType<ComboBox>().Count()})");
        });

        var single = new Monitor.Views.Settings.SingleMetricSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("SingleMetric", single, expectedHeaders: 0, expectedSettings: 1, extra: () =>
        {
            Check(BatchTwoSetting(single, Monitor.Locales.Locale.Monitor_Metric) is not null,
                "SingleMetric: the metric chooser row survives");
        });
    }

    // ---- Stack -----------------------------------------------------------------------------

    private static void CheckWidgetStackBatchTwo()
    {
        var page = new StackWidgets.Views.Settings.WidgetStackSettings(new StubWidgetLayoutProvider());
        CheckBatchTwoPageContract("WidgetStack", page, expectedHeaders: 1, expectedSettings: 4, extra: () =>
        {
            // 子组件管理专属区域：拖放落地提示、已堆叠列表与添加控件行。
            Check(page.FindControl<Border>("DropZoneBorder") is not null,
                "WidgetStack: the drag-drop landing zone survives");
            Check(page.FindControl<ItemsControl>("ItemsList") is not null,
                "WidgetStack: the stacked-children list survives");
            Check(page.FindControl<ComboBox>("AvailableWidgetsCombo") is not null
                  && BatchTwoSetting(page, StackWidgets.Locales.Locale.Setting_Add_Widget) is not null,
                "WidgetStack: the add-widget row survives");
            Check(page.GetLogicalDescendants().OfType<ToggleSwitch>().Count() == 3,
                "WidgetStack: the three wheel/dots toggles survive");
        });
    }
}
