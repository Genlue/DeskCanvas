using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using DeskCanvas.Views.Controls;
using ColorInputFormat = DeskCanvas.UI.Controls.ColorInputFormat;
using ColorInputAlphaPolicy = DeskCanvas.UI.Controls.ColorInputAlphaPolicy;

namespace UiFoundationChecks;

/// <summary>
/// 设置页迁移 第一批(W9) 的契约检查。每一页断言六件事：
/// 1) 声明 ScrollOwnership=Host，且页面自身没有整体 ScrollViewer（唯一滚动所有者是
///    EditWidget 内容视口；仅允许带 MaxHeight 的有界列表区域保留内部滚动器）；
/// 2) 分组标题全部解析到共享 dc-section-header，且不再残留 group-header / section-header
///    过渡别名；
/// 3) 不再定义本地通用样式块（icon-btn / icon-button / group-header / section-header /
///    setting-label / setting-desc / setting-combo / setting-input / small-button /
///    action-btn）；
/// 4) 设置行全部收敛为共享 Setting（按页钉住精确数量——统一不等于删行）；
/// 5) 滑块+数值行走 LabeledSlider、颜色行走 ColorInput（数量写进断言消息），
///    裸 Slider / 裸 ColorPicker 为 0；
/// 6) 关键业务控件仍在（列表管理、裁切面板、凭据输入等专属区域不被"统一"抹掉）。
/// 实例化统一使用 Program.cs 里现成的 StubWidgetLayoutProvider。
/// </summary>
internal static partial class Program
{
    private static void CheckSettingsPagesBatchOne()
    {
        Console.WriteLine("--- 设置页迁移 第一批(W9) ---");

        CheckClockBatchOne();
        CheckSearchBatchOne();
        CheckNotesBatchOne();
        CheckPictureBatchOne();
        CheckPomodoroRemindersBatchOne();
        CheckTimetableBatchOne();
        CheckToolsBatchOne();
        CheckWeatherBatchOne();
    }

    // ---- 公共页面契约 ---------------------------------------------------------------------

    private static void CheckBatchPageContract(
        string name,
        UserControl page,
        int expectedHeaders,
        int expectedSettings,
        Action? extra = null)
    {
        // 1) 滚动契约：Host 声明 + 无页面级 ScrollViewer。
        Check(SettingsPage.GetScrollOwnership(page) == SettingsPageScrollOwnership.Host,
            $"{name}: declares ScrollOwnership=Host");
        var scrollViewers = page.GetLogicalDescendants().OfType<ScrollViewer>().ToList();
        Check(scrollViewers.All(s => double.IsFinite(s.MaxHeight) && s.MaxHeight > 0),
            $"{name}: keeps no page-level ScrollViewer (only bounded list areas with MaxHeight remain: {scrollViewers.Count})");

        // 2) 分组标题统一 dc-section-header，过渡别名清零。
        var headers = page.GetLogicalDescendants().OfType<TextBlock>()
            .Count(t => t.Classes.Contains("dc-section-header"));
        Check(headers == expectedHeaders,
            $"{name}: {expectedHeaders} group headers resolve dc-section-header (found {headers})");
        Check(!page.GetLogicalDescendants().OfType<TextBlock>()
                .Any(t => t.Classes.Contains("group-header") || t.Classes.Contains("section-header")),
            $"{name}: no group-header / section-header transition alias remains");

        // 3) 本地通用样式块清零（页面级 Styles 只允许业务专属样式）。
        foreach (var fragment in new[]
                 {
                     "icon-btn", "icon-button", "group-header", "section-header",
                     "setting-label", "setting-desc", "setting-combo", "setting-input",
                     "small-button", "action-btn",
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

    private static Setting? BatchSetting(Control page, string title)
        => page.GetLogicalDescendants().OfType<Setting>()
            .FirstOrDefault(s => s.Title == title);

    // ---- Clock ×3 -------------------------------------------------------------------------

    private static void CheckClockBatchOne()
    {
        // W6 已迁移 FramelessClockSettings，本批只覆盖 Analog / Digital / Flip。
        var provider = new StubWidgetLayoutProvider();
        var analog = new Clock.Views.Settings.AnalogClockSettings(provider);
        CheckBatchPageContract("AnalogClock", analog, expectedHeaders: 0, expectedSettings: 9, extra: () =>
        {
            // 表盘明暗双色行（自定义模式业务）仍在，走共享 ColorInput。
            Check(analog.FindControl<ColorInput>("DialColorDarkInput") is not null
                  && analog.FindControl<ColorInput>("DialColorLightInput") is not null,
                "AnalogClock: the dark/light dial colour rows survive on ColorInput");
            Check(BatchSetting(analog, Clock.Locales.Locale.Clock_DialColor_Title) is not null,
                "AnalogClock: the dial colour mode row survives");
            Check(BatchSetting(analog, Clock.Locales.Locale.Clock_DialOpaque) is not null,
                "AnalogClock: the dial opaque toggle row survives");
        });

        var digital = new Clock.Views.Settings.DigitalClockSettings(provider);
        CheckBatchPageContract("DigitalClock", digital, expectedHeaders: 0, expectedSettings: 5, extra: () =>
        {
            Check(BatchSetting(digital, Clock.Locales.Locale.Clock_ShowDate) is not null
                  && BatchSetting(digital, Clock.Locales.Locale.Clock_Use24Hours) is not null,
                "DigitalClock: date / 24-hours business rows survive");
        });

        var flip = new Clock.Views.Settings.FlipClockSettings(provider);
        CheckBatchPageContract("FlipClock", flip, expectedHeaders: 0, expectedSettings: 7, extra: () =>
        {
            // 翻页钟专属：秒数样式与日期位置两个条件行。
            Check(BatchSetting(flip, Clock.Locales.Locale.Clock_SecondsStyle) is not null
                  && BatchSetting(flip, Clock.Locales.Locale.Clock_DatePosition) is not null,
                "FlipClock: seconds-style / date-position rows survive");
        });
    }

    // ---- Search ---------------------------------------------------------------------------

    private static void CheckSearchBatchOne()
    {
        var page = new Search.Views.Settings.SearchSettings();
        CheckBatchPageContract("Search", page, expectedHeaders: 2, expectedSettings: 4, extra: () =>
        {
            // 列表管理专属区域整体保留：工具栏、内联编辑面板、引擎列表与四个行内操作。
            Check(page.FindControl<ItemsControl>("EnginesList") is not null,
                "Search: the engine management list survives");
            Check(page.FindControl<Border>("EditPanel") is not null,
                "Search: the inline add/edit panel survives");
            Check(page.FindControl<ColorInput>("EditColorInput") is not null,
                "Search: the engine icon colour row edits on ColorInput");
            var listButtons = page.FindControl<ItemsControl>("EnginesList")!
                .GetLogicalDescendants().OfType<Button>().ToList();
            Check(!listButtons.Any() || listButtons.All(b => b.Classes.Contains("icon-button")),
                $"Search: engine row actions stay on the shared compact alias ({listButtons.Count} buttons)");
        });
    }

    // ---- Notes ----------------------------------------------------------------------------

    private static void CheckNotesBatchOne()
    {
        var page = new Notes.Views.Settings.NoteSettings(new StubWidgetLayoutProvider());
        CheckBatchPageContract("Notes", page, expectedHeaders: 0, expectedSettings: 24, extra: () =>
        {
            // 八个 markdown 颜色行 + 页头颜色行全部在共享 ColorInput 上（含防抖业务仍在）。
            var colorInputs = page.GetLogicalDescendants().OfType<ColorInput>().Count();
            Check(colorInputs == 9,
                $"Notes: 9 colour rows (8 markdown + header) on ColorInput (found {colorInputs})");
            Check(page.FindControl<ColorInput>("HeaderColorInput") is not null,
                "Notes: the header colour row survives on ColorInput");
            Check(page.FindControl<ComboBox>("HeaderModeBox") is not null
                  && page.FindControl<StackPanel>("StyleSection") is not null
                  && page.FindControl<StackPanel>("FilesPanel") is not null,
                "Notes: header mode / style section / folder file list business areas survive");
            Check(page.FindControl<TextBox>("HeaderTextBar") is not null
                  && page.FindControl<NumericUpDown>("HeaderHeightBox") is not null,
                "Notes: the header text / header height rows survive");
        });
    }

    // ---- Picture --------------------------------------------------------------------------

    private static void CheckPictureBatchOne()
    {
        var page = new Picture.Views.Settings.PictureSettings(new StubWidgetLayoutProvider());
        CheckBatchPageContract("Picture", page, expectedHeaders: 1, expectedSettings: 11, extra: () =>
        {
            // 裁切专属区域整体保留：预览器、图库列表、三个取景滑块与九宫格快捷对齐。
            Check(page.FindControl<Picture.Controls.CropPreviewControl>("CropPreview") is not null,
                "Picture: the crop preview control survives");
            Check(page.FindControl<ListBox>("PictureListBox") is not null,
                "Picture: the picture library list survives");
            var cropX = page.FindControl<LabeledSlider>("CropXSlider");
            var zoom = page.FindControl<LabeledSlider>("ZoomSlider");
            Check(cropX is { Minimum: 0, Maximum: 1 } && Math.Abs(cropX.Step - 0.01) < 1e-9,
                "Picture: CropX is a 0–1 percent LabeledSlider");
            Check(zoom is { Minimum: 1, Maximum: 3 } && Math.Abs(zoom.Step - 0.01) < 1e-9
                  && zoom.Unit == "x" && zoom.DisplayFormat == "0.00",
                "Picture: Zoom keeps the 1.00x–3.00x business formatting on LabeledSlider");
            var alignButtons = page.GetLogicalDescendants().OfType<Button>()
                .Count(b => b.Tag is string tag && tag.Contains(','));
            Check(alignButtons == 9,
                $"Picture: the 9-grid quick-align buttons survive (found {alignButtons})");
        });
    }

    // ---- Pomodoro / Reminders -------------------------------------------------------------

    private static void CheckPomodoroRemindersBatchOne()
    {
        var pomodoro = new Pomodoro.Views.Settings.PomodoroSettings(new StubWidgetLayoutProvider());
        CheckBatchPageContract("Pomodoro", pomodoro, expectedHeaders: 0, expectedSettings: 6, extra: () =>
        {
            Check(pomodoro.FindControl<NumericUpDown>("FocusMinutesInput") is { Minimum: 1, Maximum: 120 },
                "Pomodoro: the focus-minutes numeric row survives (1–120)");
            Check(pomodoro.FindControl<ToggleSwitch>("AutoStartBreaksSwitch") is not null
                  && pomodoro.FindControl<ToggleSwitch>("AutoStartFocusSwitch") is not null,
                "Pomodoro: both auto-start toggles survive");
        });

        var list = new Reminders.Views.Settings.ListSettings(new StubWidgetLayoutProvider());
        CheckBatchPageContract("Reminders", list, expectedHeaders: 0, expectedSettings: 4, extra: () =>
        {
            Check(list.FindControl<ToggleSwitch>("AllowInlineEditToggle") is not null
                  && list.FindControl<ToggleSwitch>("DeleteOnCheckToggle") is not null,
                "Reminders: inline-edit / delete-on-check toggles survive");
            Check(BatchSetting(list, Reminders.Locales.Locale.Reminders_DeleteAll) is not null,
                "Reminders: the delete-all action row survives");
        });
    }

    // ---- Timetable ------------------------------------------------------------------------

    private static void CheckTimetableBatchOne()
    {
        var page = new Timetable.Views.Settings.TimetableSettings();
        CheckBatchPageContract("Timetable", page, expectedHeaders: 4, expectedSettings: 9, extra: () =>
        {
            // 课表专属：学期起始日期、周数行与节次编辑面板（动态构建）。
            Check(page.FindControl<DatePicker>("StartDatePicker") is not null,
                "Timetable: the term start date picker survives");
            Check(page.FindControl<NumericUpDown>("TotalWeeksBox") is not null
                  && page.FindControl<NumericUpDown>("CurrentWeekBox") is not null,
                "Timetable: the week counters survive");
            Check(page.FindControl<StackPanel>("PeriodsPanel") is not null,
                "Timetable: the dynamic period editor panel survives");
        });
    }

    // ---- Tools ×3 -------------------------------------------------------------------------

    private static void CheckToolsBatchOne()
    {
        var calculator = new Tools.Views.Settings.CalculatorSettings();
        CheckBatchPageContract("Calculator", calculator, expectedHeaders: 3, expectedSettings: 7, extra: () =>
        {
            var maxHistory = calculator.FindControl<LabeledSlider>("MaxHistorySlider");
            Check(maxHistory is { Minimum: 10, Maximum: 100 } && Math.Abs(maxHistory.Step - 5) < 1e-9,
                "Calculator: max-history is a 10–100 step-5 LabeledSlider");
            Check(calculator.FindControl<ComboBox>("DecimalPlacesCombo") is not null
                  && calculator.FindControl<ComboBox>("AngleModeCombo") is not null
                  && calculator.FindControl<ComboBox>("DefaultTabCombo") is not null,
                "Calculator: the three mode combos survive");
        });

        var clipboard = new Tools.Views.Settings.ClipboardSettings();
        CheckBatchPageContract("Clipboard", clipboard, expectedHeaders: 3, expectedSettings: 6, extra: () =>
        {
            var maxCount = clipboard.FindControl<LabeledSlider>("MaxCountSlider");
            Check(maxCount is { Minimum: 5, Maximum: 100 } && Math.Abs(maxCount.Step - 5) < 1e-9,
                "Clipboard: max-count is a 5–100 step-5 LabeledSlider");
            Check(clipboard.FindControl<ToggleSwitch>("CaptureTextToggle") is not null
                  && clipboard.FindControl<ToggleSwitch>("CaptureImagesToggle") is not null
                  && clipboard.FindControl<ToggleSwitch>("CaptureFilesToggle") is not null,
                "Clipboard: the three capture-format toggles survive");
        });

        var translator = new Tools.Views.Settings.TranslatorSettings();
        CheckBatchPageContract("Translator", translator, expectedHeaders: 3, expectedSettings: 5, extra: () =>
        {
            Check(translator.FindControl<TextBox>("BaiduSecretBox") is { PasswordChar: '•' },
                "Translator: the masked Baidu secret input survives");
            Check(translator.FindControl<TextBox>("BaiduAppIdBox") is not null
                  && translator.FindControl<TextBox>("CustomUrlBox") is not null
                  && translator.FindControl<ComboBox>("DefaultEngineCombo") is not null,
                "Translator: credential / endpoint / engine rows survive");
        });
    }

    // ---- Weather --------------------------------------------------------------------------

    private static void CheckWeatherBatchOne()
    {
        var page = new Weather.Views.Settings.ForecastSettings(new StubWidgetLayoutProvider());
        CheckBatchPageContract("Forecast", page, expectedHeaders: 0, expectedSettings: 2, extra: () =>
        {
            Check(page.FindControl<AutoCompleteBox>("Search") is not null,
                "Forecast: the location search box survives");
            Check(BatchSetting(page, Weather.Locales.Locale.Weather_Units) is not null,
                "Forecast: the units row survives");
        });
    }
}
