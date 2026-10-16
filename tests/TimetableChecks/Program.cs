using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models;
using DeskCanvas.Core.Models.Attributes;
using Timetable.Models;
using Timetable.Services;
using Grid = Avalonia.Controls.Grid;

namespace TimetableChecks;

/// <summary>
/// Pins the 课程表 (Timetable) widget's data contracts:
/// <list type="number">
/// <item><b>Week expressions</b> — every documented "weeks" syntax parses, odd/even
/// disjointness holds, and malformed input is rejected instead of silently widened.</item>
/// <item><b>The importer</b> — the JSON format contract (merge, conflict warnings, field
/// tolerance, hard limits) behaves exactly as the review screen promises.</item>
/// <item><b>Term maths &amp; palette</b> — the current-week calculation and colour parsing
/// are deterministic.</item>
/// <item><b>The embedded guide</b> — the copyable AI prompt is present and marker-stripped.</item>
/// </list>
/// </summary>
internal static class Program
{
    private static int passed;
    private static int failed;

    private static void Check(bool condition, string name)
    {
        if (condition)
        {
            passed++;
            Console.WriteLine($"  [ok]   {name}");
        }
        else
        {
            failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }

    [STAThread]
    private static int Main()
    {
        // Isolation FIRST, before anything touches Const: this harness constructs real widget
        // views and the settings page, whose store interactions save to disk. Without the
        // redirect they would resolve DataFolder to the packaged app's %LocalAppData%\DeskCanvas
        // and stomp the user's live timetable.json (this happened — twice).
        Environment.SetEnvironmentVariable("DESCANVAS_DATA_FOLDER",
            Path.Combine(Path.GetTempPath(), "DeskCanvas-Checks-Data"));

        // Widget views and the review windows are constructed below, so the Avalonia
        // services must be up (platform detected, no lifetime started).
        AppBuilder.Configure<Application>().UsePlatformDetect().SetupWithoutStarting();

        Console.WriteLine("TimetableChecks — 课程表组件逻辑验证");
        Console.WriteLine();

        Console.WriteLine("1. 周次表达式 (WeekExpression)");
        WeekExpressionChecks();

        Console.WriteLine();
        Console.WriteLine("2. 导入与校验 (TimetableImporter)");
        ImporterChecks();

        Console.WriteLine();
        Console.WriteLine("3. 学期推算与运行时 (TimetableClock / TimetableRuntime)");
        ClockChecks();

        Console.WriteLine();
        Console.WriteLine("4. 调色板 (CoursePalette)");
        PaletteChecks();

        Console.WriteLine();
        Console.WriteLine("5. 内置文档与提示词 (TimetableGuide)");
        GuideChecks();

        Console.WriteLine();
        Console.WriteLine("6. 注册契约与视图构造 (WidgetInfo / XAML)");
        RegistrationChecks();

        Console.WriteLine();
        Console.WriteLine($"{passed}/{passed + failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ---------- 1. Week expressions ----------

    private static void WeekExpressionChecks()
    {
        Check(WeekExpression.TryParse(null, out var all, out _) && all.All && all.IsActive(9),
            "缺省 = 每周");
        Check(WeekExpression.TryParse("", out var empty, out _) && empty.All,
            "空串 = 每周");
        Check(WeekExpression.TryParse("1-16", out var r1, out _) &&
              r1.IsActive(1) && r1.IsActive(16) && !r1.IsActive(17) && !r1.IsActive(0),
            "1-16 区间边界");
        Check(WeekExpression.TryParse("1-16单", out var odd, out _) &&
              odd.IsActive(1) && !odd.IsActive(2) && odd.IsActive(15) && !odd.IsActive(16),
            "单周");
        Check(WeekExpression.TryParse("2-16双", out var even, out _) &&
              even.IsActive(2) && !even.IsActive(3) && even.IsActive(16),
            "双周");
        Check(WeekExpression.TryParse("1,3,5", out var list, out _) &&
              list.IsActive(3) && !list.IsActive(4),
            "周列表");
        Check(WeekExpression.TryParse("第1-8周", out var cn, out _) && cn.IsActive(5),
            "中文「第N周」写法");
        Check(WeekExpression.TryParse("１－１６", out var fullWidth, out _) && fullWidth.IsActive(16),
            "全角数字与破折号");
        Check(WeekExpression.TryParse("1-8,10-16", out var combo, out _) &&
              combo.IsActive(10) && !combo.IsActive(9),
            "组合区间");
        Check(!WeekExpression.TryParse("abc", out _, out _), "非法输入被拒绝");
        Check(!WeekExpression.TryParse("0-5", out _, out _), "越界周次被拒绝");
        Check(!WeekExpression.TryParse("5-1", out _, out _), "倒序区间被拒绝");
        Check(WeekExpression.TryParse("2-2单", out var emptySet, out _) && emptySet.IsEmpty,
            "空集可检测（2-2单）");
        Check(!odd.Intersects(even), "单周与双周不相交（冲突检测前提）");
        Check(odd.Intersects(WeekSet.AllWeeks), "与每周规则必有交集");
    }

    // ---------- 2. Importer ----------

    private const string ValidJson = """
    {
      "version": 1,
      "name": "测试学期",
      "startDate": "2026-09-07",
      "totalWeeks": 20,
      "periods": [
        { "label": "第1节", "start": "08:00", "end": "08:45" },
        { "label": "第2节", "start": "08:55", "end": "09:40" },
        { "label": "第3节", "start": "10:00", "end": "10:45" },
        { "label": "第4节", "start": "10:55", "end": "11:40" }
      ],
      "courses": [
        { "name": "高等数学", "teacher": "张三", "room": "A101", "note": "实验课在 B 区实验室",
          "entries": [ { "day": 1, "periods": [1, 2], "weeks": "1-16" } ] },
        { "name": "大学英语", "teacher": "李四", "room": "B203",
          "entries": [ { "day": 2, "periods": [3, 4], "weeks": "2-16双" } ] },
        { "name": "物理实验", "room": "实验楼305",
          "entries": [ { "day": 5, "periods": [3, 4] } ] }
      ]
    }
    """;

    private static void ImporterChecks()
    {
        // --- Valid document ---
        var valid = TimetableImporter.ImportJson(ValidJson);
        Check(valid.Model != null && !valid.HasErrors, "合法文件无错误");
        Check(valid.CourseCount == 3 && valid.SlotCount == 3, "课程/时段统计正确");
        Check(valid.Model!.StartDate == "2026-09-07", "startDate 规范化");
        Check(valid.Model.Periods is { Count: 4 } && valid.Model.Periods[0].Start == "08:00",
            "时间表解析");

        var runtime = TimetableRuntime.Build(valid.Model);
        Check(runtime.MaxPeriod == 4 && runtime.ByDay[1].Count == 1 && runtime.ByDay[5].Count == 1,
            "运行时索引（按天、最高节次）");
        Check(valid.Model.Courses![1].Entries![0].Weeks == "2-16双", "周次原样保留");
        Check(valid.Model.Courses![0].Note == "实验课在 B 区实验室", "课程备注（note）解析保留");
        Check(valid.Model.Courses[1].Note == null, "无备注课程保持为空");

        // --- Merge of repeated courses ---
        var merge = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "语文", "teacher": "王老师", "room": "101", "note": "理论课",
            "entries": [ { "day": 1, "periods": [1] } ] },
          { "name": "语文", "teacher": "王老师", "room": "101", "note": "实验课",
            "entries": [ { "day": 3, "periods": [2] } ] }
        ] }
        """);
        Check(merge.Model != null && merge.CourseCount == 1 && merge.SlotCount == 2, "重复课程自动合并");
        Check(merge.HasWarnings && merge.Issues.Exists(i => i.Message.Contains("合并")), "合并产生警告");
        Check(merge.Model!.Courses![0].Note == "理论课；实验课", "合并时备注拼接保留");

        // --- Conflict detection ---
        var conflict = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "A", "entries": [ { "day": 1, "periods": [1] } ] },
          { "name": "B", "entries": [ { "day": 1, "periods": [1] } ] }
        ] }
        """);
        Check(conflict.HasWarnings && conflict.Issues.Exists(i => i.Message.Contains("重叠")),
            "同格同周冲突被警告");

        var noConflict = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "A", "entries": [ { "day": 1, "periods": [1], "weeks": "1-15单" } ] },
          { "name": "B", "entries": [ { "day": 1, "periods": [1], "weeks": "2-16双" } ] }
        ] }
        """);
        Check(!noConflict.Issues.Exists(i => i.Message.Contains("重叠")),
            "单/双周同格不误报冲突");

        // --- Field-level repairs ---
        var badDay = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "A", "entries": [ { "day": 8, "periods": [1] } ] },
          { "name": "B", "entries": [ { "day": 2, "periods": [1] } ] }
        ] }
        """);
        Check(badDay.Model != null && badDay.CourseCount == 1 &&
              badDay.Issues.Exists(i => i.Message.Contains("day=8")),
            "非法 day 被跳过并警告");

        var badWeeks = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "A", "entries": [ { "day": 1, "periods": [1], "weeks": "banana" } ] }
        ] }
        """);
        Check(badWeeks.Model != null &&
              badWeeks.Model.Courses![0].Entries![0].Weeks == null &&
              badWeeks.Issues.Exists(i => i.Message.Contains("无法解析")),
            "非法周次回退为每周并警告");

        var noName = TimetableImporter.ImportJson("""
        { "courses": [
          { "entries": [ { "day": 1, "periods": [1] } ] },
          { "name": "B", "entries": [ { "day": 2, "periods": [1] } ] }
        ] }
        """);
        Check(noName.Model != null && noName.CourseCount == 1 &&
              noName.Issues.Exists(i => i.Message.Contains("缺少 name")),
            "缺少课程名被跳过并警告");

        var bigPeriod = TimetableImporter.ImportJson("""
        { "courses": [
          { "name": "A", "entries": [ { "day": 1, "periods": [31] } ] },
          { "name": "B", "entries": [ { "day": 2, "periods": [1] } ] }
        ] }
        """);
        Check(bigPeriod.Issues.Exists(i => i.Message.Contains("超出上限")),
            "节次超上限被忽略并警告");

        var shortTable = TimetableImporter.ImportJson("""
        { "periods": [ { "label": "第1节", "start": "08:00", "end": "08:45" } ],
          "courses": [ { "name": "A", "entries": [ { "day": 1, "periods": [6] } ] } ] }
        """);
        Check(shortTable.Issues.Exists(i => i.Message.Contains("时间表只定义了 1 节")),
            "节次超出时间表定义被警告");

        var beyondTerm = TimetableImporter.ImportJson("""
        { "totalWeeks": 20,
          "courses": [ { "name": "A", "entries": [ { "day": 1, "periods": [1], "weeks": "1-25" } ] } ] }
        """);
        Check(beyondTerm.Issues.Exists(i => i.Message.Contains("超过总周数")),
            "周次超出总周数被警告");

        var badStart = TimetableImporter.ImportJson("""
        { "startDate": "九月",
          "courses": [ { "name": "A", "entries": [ { "day": 1, "periods": [1] } ] } ] }
        """);
        Check(badStart.Model != null && badStart.Model.StartDate == null &&
              badStart.Issues.Exists(i => i.Message.Contains("无法识别")),
            "非法 startDate 被忽略并警告");

        // --- Tolerant JSON shapes ---
        var trailing = TimetableImporter.ImportJson("""
        { "version": 1, /* 注释 */ "courses": [ { "name": "A", "entries": [ { "day": 1, "periods": [1] } ], }, ], }
        """);
        Check(trailing.Model != null && !trailing.HasErrors, "注释与尾逗号容错");

        var bareArray = TimetableImporter.ImportJson("""
        [ { "name": "A", "entries": [ { "day": 1, "periods": [1] } ] } ]
        """);
        Check(bareArray.Model != null && bareArray.CourseCount == 1, "裸数组容错");

        var stringNumber = TimetableImporter.ImportJson("""
        { "courses": [ { "name": "A", "entries": [ { "day": "1", "periods": [1] } ] } ] }
        """);
        Check(stringNumber.Model != null &&
              stringNumber.Model.Courses![0].Entries![0].Day == 1,
            "字符串数字（\"day\":\"1\"）容错");

        // --- Hard failures ---
        var syntax = TimetableImporter.ImportJson("{ \"courses\": [");
        Check(syntax.HasErrors && syntax.Model == null, "语法错误 → 阻止导入");

        var noCourses = TimetableImporter.ImportJson("{ \"courses\": [] }");
        Check(noCourses.HasErrors, "空课程 → 阻止导入");

        var tooMany = new System.Text.StringBuilder("{ \"courses\": [");
        for (var i = 0; i < 501; i++)
        {
            tooMany.Append(i > 0 ? "," : "")
                   .Append("{ \"name\": \"C").Append(i)
                   .Append("\", \"entries\": [ { \"day\": 1, \"periods\": [1] } ] }");
        }
        tooMany.Append("] }");
        var overLimit = TimetableImporter.ImportJson(tooMany.ToString());
        Check(overLimit.HasErrors && overLimit.Issues.Exists(i => i.Message.Contains("上限")),
            "课程数超 500 → 阻止导入");

        // --- File-level guard ---
        var bigPath = Path.Combine(Path.GetTempPath(), $"timetable-checks-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(bigPath, new string('a', 3 * 1024 * 1024));
            var fileResult = TimetableImporter.ImportFileAsync(bigPath).GetAwaiter().GetResult();
            Check(fileResult.HasErrors && fileResult.Issues[0].Message.Contains("2 MB"),
                "超过 2 MB 的文件被拒绝");
        }
        finally
        {
            try { File.Delete(bigPath); } catch { /* best effort */ }
        }

        // --- The repo sample must always import cleanly (it is the quick-start test file) ---
        var samplePath = Path.Combine(Directory.GetCurrentDirectory(), "sample-timetable.json");
        if (File.Exists(samplePath))
        {
            var sample = TimetableImporter.ImportFileAsync(samplePath).GetAwaiter().GetResult();
            Check(sample.Model != null && !sample.HasErrors && sample.CourseCount == 7,
                "仓库示例课表 sample-timetable.json 导入无错误（7 门课）");
            Check(sample.Model!.Courses!.Exists(c => c.Note == "每周提交实验报告"),
                "示例课表备注字段解析");
        }
        else
        {
            Console.WriteLine("  [skip] sample-timetable.json 不在当前目录（跳过）");
        }
    }

    // ---------- 3. Clock / runtime ----------

    private static void ClockChecks()
    {
        var model = new TimetableModel(1, null, "2026-09-07", 20, 0);
        Check(TimetableClock.ComputeWeek(model, new DateTime(2026, 9, 7)) == 1, "开学当天 = 第 1 周");
        Check(TimetableClock.ComputeWeek(model, new DateTime(2026, 9, 21)) == 3, "两周后 = 第 3 周");
        Check(TimetableClock.ComputeWeek(model, new DateTime(2026, 9, 1)) == 0, "开学前 = 未确定");
        Check(TimetableClock.ComputeWeek(model with { CurrentWeekOverride = 5 },
            new DateTime(2026, 9, 7)) == 5, "手动周次覆盖生效");
        Check(TimetableClock.ComputeWeek(new TimetableModel(), new DateTime(2026, 9, 7)) == 0,
            "未设置学期日期 = 未确定");
    }

    // ---------- 4. Palette ----------

    private static void PaletteChecks()
    {
        Check(CoursePalette.TryParseHex("#5B8FF9", out var rgb) &&
              rgb.R == 0x5B && rgb.G == 0x8F && rgb.B == 0xF9,
            "#RRGGBB 解析");
        Check(CoursePalette.TryParseHex("#abc", out var shortHex) && shortHex.R == 0xAA,
            "#RGB 展开");
        Check(!CoursePalette.TryParseHex("red", out _), "非法颜色被拒绝");
        Check(CoursePalette.DefaultColorFor("高等数学") == CoursePalette.DefaultColorFor("高等数学") &&
              CoursePalette.DefaultColorFor("高等数学") != CoursePalette.DefaultColorFor("大学英语"),
            "默认配色按课程名稳定分配");

        var onWhite = CoursePalette.ForegroundFor(Colors.White);
        var onBlack = CoursePalette.ForegroundFor(Colors.Black);
        Check(onWhite is ISolidColorBrush { Color.R: < 0x80 } &&
              onBlack is ISolidColorBrush { Color.R: > 0x80 },
            "前景色按亮度自动取反");
    }

    // ---------- 5. Guide ----------

    private static void GuideChecks()
    {
        Check(TimetableGuide.FullText.Contains("AI-PROMPT:START"),
            "内置写法文档资源可加载");
        Check(TimetableGuide.Prompt.Length > 200, "AI 提示词非空");
        Check(!TimetableGuide.Prompt.Contains("AI-PROMPT"), "提示词已剥离标记注释");
        Check(TimetableGuide.Prompt.Contains("自查"), "提示词包含「输出前自查」要求");
        Check(TimetableGuide.Prompt.Contains("note") && TimetableGuide.Prompt.Contains("备注"),
            "提示词包含 note 备注字段说明");
        Check(TimetableGuide.Prompt.Contains("边界情况"), "提示词包含边界情况处理说明");
    }

    // ---------- 6. Registration & view construction ----------

    private static void RegistrationChecks()
    {
        var assembly = typeof(Timetable.Views.Timetable).Assembly;

        var infos = assembly.GetCustomAttributes<WidgetInfoAttribute>().ToList();
        Check(infos.Count == 1, "恰好注册 1 个组件");
        var info = infos.FirstOrDefault();
        Check(info != null &&
              info.ViewType == typeof(Timetable.Views.Timetable) &&
              info.ModelType == typeof(TimetableModel) &&
              info.EditModelViewType != null,
            "WidgetInfo 的 View / Model / 设置页类型正确");
        Check(info != null && info.PresetSpans.Count == 4 &&
              info.PresetSpans[0] == (1, 1) && info.PresetSpans[3] == (4, 4),
            "presetSpans = 1x1,2x2,4x2,4x4");

        var localeAttrs = assembly.GetCustomAttributes<LocaleAttribute>().ToList();
        Check(localeAttrs.Count == 1 && !string.IsNullOrWhiteSpace(Timetable.Locales.Locale.Timetable_Title),
            "Locale 注册且标题资源可读");

        // Construct the views with real data — this executes the XAML loader and the whole
        // build path (grid assembly, lanes, today list, panel) outside a live window.
        // The unified store (timetable.json) is the single model source: both the widget and
        // the settings page read it, so seed it instead of relying on the (legacy) model arg.
        var valid = TimetableImporter.ImportJson(ValidJson);
        var validModel = valid.Model!;
        var constructed = false;
        Timetable.Views.Settings.TimetableSettings? settings = null;
        Timetable.Views.Timetable? view = null;
        try
        {
            TimetableStore.ResetForTesting(validModel);
            view = new Timetable.Views.Timetable(validModel);
            TimetableStore.ResetForTesting(new TimetableModel());
            settings = new Timetable.Views.Settings.TimetableSettings();
            var review = new Timetable.Views.ImportReviewWindow(valid);
            var guide = new Timetable.Views.GuideWindow();
            var panel = new Timetable.Views.TimetablePanelWindow(validModel);
            constructed = view != null && settings != null && review != null && guide != null && panel != null;

            // Release the panel's active-panel registration (it was never shown).
            panel.Close();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        构造异常: {ex.Message}");
        }
        Check(constructed, "主视图 / 设置页 / 审核窗口 / 文档窗口 / 二级面板均可构造");

        var openButton = view?.FindControl<Button>("OpenPanelButton");
        Check(openButton != null && openButton.Classes.Contains("panel-open"),
            "二级面板入口按钮已就位（点击触发，不再依赖双击）");

        // --- Settings period-time editor: collapsible rows (the 2026-10 adaptation fix).
        // Nine always-open pickers clipped at every width; rows now fold, and editors that
        // were narrower than their own face (TimePicker 126 / DatePicker 150) got real
        // minimum widths. The widget card's scroll policy pins live on the views below. ---
        var emptyRows = settings?.FindControl<StackPanel>("PeriodsPanel");
        Check(emptyRows is { Children.Count: 8 }, "未导入课表时设置页提供 8 行默认节次");

        // Row anatomy checks run against the stub-seeded store: the settings page mirrors the
        // unified store, never the per-widget Settings copy.
        TimetableStore.ResetForTesting(validModel);
        var stub = new StubLayoutProvider(new WidgetLayout(
            "Timetable", "Timetable", 0, 0, 200, 200, JsonSerializer.SerializeToElement(validModel)));
        var filledSettings = new Timetable.Views.Settings.TimetableSettings(stub);
        var rows = filledSettings.FindControl<StackPanel>("PeriodsPanel");
        Check(rows is { Children.Count: 4 }, "节次行数 = 课表用到/JSON 定义的节次数（4）");

        // Row anatomy: Border → StackPanel [ header Grid [titles, chevron], editor WrapPanel ].
        var firstRow = rows?.Children.OfType<Border>().FirstOrDefault();
        var rowContent = firstRow?.Child as StackPanel;
        var headerGrid = rowContent?.Children.OfType<Grid>().FirstOrDefault();
        var editor = rowContent?.Children.OfType<WrapPanel>().FirstOrDefault();
        var titleStack = headerGrid?.Children.OfType<StackPanel>().FirstOrDefault();
        var summary = titleStack?.Children.OfType<TextBlock>().LastOrDefault();
        var chevron = headerGrid?.Children.OfType<PathIcon>().FirstOrDefault();

        Check(rowContent != null && headerGrid != null && editor != null && chevron != null && summary != null,
            "节次行结构：头部（标题 + 摘要 + 展开箭头）+ 可折叠编辑器");
        Check(editor is { IsVisible: false }, "节次编辑器默认收起");

        TimePicker? startPicker = editor?.Children.OfType<StackPanel>().ElementAtOrDefault(0)
            ?.Children.OfType<TimePicker>().FirstOrDefault();
        TimePicker? endPicker = editor?.Children.OfType<StackPanel>().ElementAtOrDefault(1)
            ?.Children.OfType<TimePicker>().FirstOrDefault();

        Check(startPicker?.SelectedTime == new TimeSpan(8, 0, 0) && endPicker?.SelectedTime == new TimeSpan(8, 45, 0),
            "JSON 中的节次时间自动填入（第 1 节 08:00–08:45）");
        Check(summary?.Text == "08:00 – 08:45", "收起态摘要显示已设置的时间（08:00 – 08:45）");
        Check(startPicker is { MinWidth: >= 200 } && endPicker is { MinWidth: >= 200 },
            "TimePicker 最小宽度 ≥ 200（完整面孔需 ≈242；旧 126 会把 hour/数字裁成碎片）");

        // Fold/unfold through the production toggle path.
        var handlesField = filledSettings.GetType().GetField("periodRowHandles",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var toggleMethod = filledSettings.GetType().GetMethod("TogglePeriodRow",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var handles = handlesField?.GetValue(filledSettings) as System.Collections.IList;
        Check(handles is { Count: 4 } && toggleMethod != null, "节次行句柄已登记（4）");

        if (handles is { Count: > 0 } && toggleMethod != null)
        {
            toggleMethod.Invoke(filledSettings, [handles[0]]);
            Check(editor is { IsVisible: true }, "点击后编辑器展开");
            toggleMethod.Invoke(filledSettings, [handles[0]]);
            Check(editor is { IsVisible: false }, "再次点击后编辑器收起");
        }

        // The date face lays out at ≈296 DIP; 150 clipped it to a bare month cell.
        var startDatePicker = filledSettings.FindControl<DatePicker>("StartDatePicker");
        Check(startDatePicker is { MinWidth: >= 290 }, "DatePicker 最小宽度 ≥ 290（旧 150 只显示单个月份格）");

        if (startPicker != null)
        {
            startPicker.SelectedTime = new TimeSpan(9, 5, 0);
            // Single source of truth: the edit lands in the unified store, not in the
            // per-widget Settings copy (that double-write was the divergence/corruption vector).
            var saved = TimetableStore.Get();
            Check(saved.Periods is { Count: >= 1 } savedPeriods && savedPeriods[0].Start == "09:05",
                "修改节次时间立即写入统一存储（09:05）");
            Check(summary?.Text == "09:05 – 08:45", "修改后摘要即时刷新（09:05 – 08:45）");
        }
        else
        {
            Check(false, "修改节次时间立即写入统一存储（09:05）");
        }

        // The 2×2 report (widened colour cards clipped square at the right edge): the
        // widget host's Hidden horizontal policy measures ScrollViewer content unconstrained,
        // so the widget must keep Horizontal=Disabled locally to constrain card text.
        var todayScroll = view?.FindControl<ScrollViewer>("TodayScroll");
        var weekScroll = view?.FindControl<ScrollViewer>("WeekScroll");
        Check(todayScroll?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "今天列表的 ScrollViewer 本地为横向 Disabled（长文本被裁成直角的根因修复）");
        Check(weekScroll?.HorizontalScrollBarVisibility == ScrollBarVisibility.Disabled,
            "周视图的 ScrollViewer 本地为横向 Disabled");
    }

    /// <summary>Minimal layout provider so the settings page can be exercised without a live widget.</summary>
    private sealed class StubLayoutProvider(WidgetLayout initial) : IWidgetLayoutProvider
    {
        public WidgetLayout Current { get; private set; } = initial;
        public string ScreenId { get; set; } = "TimetableChecks";

        public WidgetLayout Get() => Current;
        public void Save(WidgetLayout data) => Current = data;
        public void Remove() { }

        public event DataChangedEvent<WidgetLayout>? DataChanging { add { } remove { } }
        public event DataChangedEvent<WidgetLayout>? DataChanged { add { } remove { } }
    }
}
