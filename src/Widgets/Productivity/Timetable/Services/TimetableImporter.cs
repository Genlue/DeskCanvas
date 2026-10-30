using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Timetable.Models;

namespace Timetable.Services;

public enum ImportSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>One problem found while validating an imported file.</summary>
public sealed record ImportIssue(ImportSeverity Severity, string Message);

/// <summary>
/// Outcome of an import: the normalized model (applied only after the user's review)
/// plus the issue list shown on the review screen. Errors block the import;
/// warnings ask the user to double-check against the original image.
/// </summary>
public sealed class ImportResult
{
    public TimetableModel? Model { get; init; }
    public List<ImportIssue> Issues { get; init; } = [];
    public int CourseCount { get; init; }
    public int SlotCount { get; init; }

    public bool HasErrors
    {
        get
        {
            foreach (var issue in Issues)
                if (issue.Severity == ImportSeverity.Error) return true;
            return false;
        }
    }

    public bool HasWarnings
    {
        get
        {
            foreach (var issue in Issues)
                if (issue.Severity == ImportSeverity.Warning) return true;
            return false;
        }
    }

    public static ImportResult Fail(string message) =>
        new() { Issues = { new ImportIssue(ImportSeverity.Error, message) } };
}

/// <summary>
/// Parses and validates a timetable JSON file. All limits from
/// <see cref="TimetableLimits"/> are enforced here — file size, course/slot/period
/// counts — so nothing downstream has to defend against oversized input.
/// </summary>
public static class TimetableImporter
{
    private const int MaxIssues = 200;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<ImportResult> ImportFileAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return ImportResult.Fail($"找不到文件：{path}");
            if (info.Length > TimetableLimits.MaxImportFileBytes)
                return ImportResult.Fail(
                    $"文件大小 {info.Length / 1024.0 / 1024.0:F1} MB，超过 2 MB 上限；请确认这是课表 JSON 文件");

            var json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return ImportJson(json);
        }
        catch (Exception ex)
        {
            return ImportResult.Fail($"无法读取文件：{ex.Message}");
        }
    }

    public static ImportResult ImportJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ImportResult.Fail("文件内容为空");

        TimetableDocument? document;
        try
        {
            document = ParseDocument(json);
        }
        catch (JsonException ex)
        {
            return ImportResult.Fail($"JSON 解析失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            return ImportResult.Fail($"无法解析文件：{ex.Message}");
        }

        if (document == null) return ImportResult.Fail("未能从文件中读取到课表数据");
        return Validate(document);
    }

    private static TimetableDocument? ParseDocument(string json)
    {
        // Tolerate a bare course array (a common AI output shape): wrap it into a document.
        if (json.TrimStart().StartsWith('['))
        {
            var courses = JsonSerializer.Deserialize<List<DocumentCourse>>(json, Options);
            return new TimetableDocument { Courses = courses };
        }
        return JsonSerializer.Deserialize<TimetableDocument>(json, Options);
    }

    private static ImportResult Validate(TimetableDocument doc)
    {
        var issues = new List<ImportIssue>();

        void Add(ImportSeverity severity, string message)
        {
            if (issues.Count < MaxIssues) issues.Add(new ImportIssue(severity, message));
            else if (issues.Count == MaxIssues)
                issues.Add(new ImportIssue(ImportSeverity.Info, $"问题过多，仅显示前 {MaxIssues} 条"));
        }

        ImportResult FailWith(string message)
        {
            Add(ImportSeverity.Error, message);
            return new ImportResult { Issues = issues };
        }

        // ---- Bell periods ----
        var periods = new List<TimetablePeriod>();
        var rawPeriods = doc.Periods ?? [];
        if (rawPeriods.Count > TimetableLimits.MaxPeriods)
            Add(ImportSeverity.Warning,
                $"时间表包含 {rawPeriods.Count} 节，超过上限 {TimetableLimits.MaxPeriods} 节，超出部分已忽略");

        for (var i = 0; i < rawPeriods.Count && periods.Count < TimetableLimits.MaxPeriods; i++)
        {
            var p = rawPeriods[i];
            var start = TimetableModel.NormalizeTime(p.Start);
            var end = TimetableModel.NormalizeTime(p.End);
            if (!string.IsNullOrWhiteSpace(p.Start) && start == null)
                Add(ImportSeverity.Warning, $"第 {i + 1} 节的上课时间“{p.Start}”无法识别，已忽略");
            if (!string.IsNullOrWhiteSpace(p.End) && end == null)
                Add(ImportSeverity.Warning, $"第 {i + 1} 节的下课时间“{p.End}”无法识别，已忽略");
            periods.Add(new TimetablePeriod(
                string.IsNullOrWhiteSpace(p.Label) ? null : p.Label.Trim(),
                start, end));
        }

        // ---- Meta ----
        var name = Clean(doc.Name, TimetableLimits.MaxNameLength);

        string? startDate = null;
        if (!string.IsNullOrWhiteSpace(doc.StartDate))
        {
            startDate = TimetableModel.NormalizeDate(doc.StartDate);
            if (startDate == null)
                Add(ImportSeverity.Warning, $"学期开始日期“{doc.StartDate}”无法识别（建议 yyyy-MM-dd），已忽略");
        }

        var totalWeeks = Math.Clamp(doc.TotalWeeks, 0, TimetableLimits.MaxWeeks);
        if (doc.TotalWeeks > TimetableLimits.MaxWeeks)
            Add(ImportSeverity.Warning, $"总周数 {doc.TotalWeeks} 超出上限，已按 {TimetableLimits.MaxWeeks} 周处理");

        // ---- Courses ----
        var rawCourses = doc.Courses ?? [];
        if (rawCourses.Count == 0) return FailWith("没有找到任何课程（courses 为空）");
        if (rawCourses.Count > TimetableLimits.MaxCourses)
            return FailWith($"课程数量 {rawCourses.Count} 超过上限 {TimetableLimits.MaxCourses}，请确认文件是否为课表数据");

        var builders = new List<CourseBuilder>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var mergedNames = new List<string>();
        var slotTotal = 0;
        var slotOverflow = false;

        for (var i = 0; i < rawCourses.Count; i++)
        {
            var dc = rawCourses[i];
            var courseName = Clean(dc.Name, TimetableLimits.MaxNameLength);
            if (courseName == null)
            {
                Add(ImportSeverity.Warning, $"第 {i + 1} 门课程缺少 name（课程名称），已跳过");
                continue;
            }

            var teacher = Clean(dc.Teacher, TimetableLimits.MaxShortTextLength);
            var room = Clean(dc.Room, TimetableLimits.MaxShortTextLength);
            var note = Clean(dc.Note, TimetableLimits.MaxNoteLength);

            string? color = null;
            if (!string.IsNullOrWhiteSpace(dc.Color))
            {
                if (CoursePalette.TryParseHex(dc.Color, out _)) color = dc.Color.Trim();
                else Add(ImportSeverity.Warning, $"《{courseName}》的颜色“{dc.Color}”不是有效的 #RRGGBB，已改用自动配色");
            }

            var entries = new List<TimetableEntry>();
            foreach (var de in dc.Entries ?? [])
            {
                if (de.Day is < 1 or > 7)
                {
                    Add(ImportSeverity.Warning,
                        $"《{courseName}》：day={de.Day} 无效（应为 1–7，周一 = 1），已跳过该时段");
                    continue;
                }

                var ps = new List<int>();
                foreach (var p in de.Periods ?? [])
                {
                    if (p < 1) continue;
                    if (p > TimetableLimits.MaxPeriods)
                    {
                        Add(ImportSeverity.Warning, $"《{courseName}》：节次 {p} 超出上限 {TimetableLimits.MaxPeriods}，已忽略");
                        continue;
                    }
                    if (!ps.Contains(p)) ps.Add(p);
                }
                if (ps.Count == 0)
                {
                    Add(ImportSeverity.Warning, $"《{courseName}》：某个时段缺少有效的 periods（节次），已跳过");
                    continue;
                }
                ps.Sort();

                string? weeks = null;
                if (!string.IsNullOrWhiteSpace(de.Weeks))
                {
                    var rawWeeks = de.Weeks.Trim();
                    if (WeekExpression.TryParse(rawWeeks, out var set, out var weekError))
                    {
                        if (set.IsEmpty)
                            Add(ImportSeverity.Warning,
                                $"《{courseName}》：周次“{rawWeeks}”解析后为空集，该时段不会显示，请核对");
                        weeks = rawWeeks;
                    }
                    else
                    {
                        Add(ImportSeverity.Warning,
                            $"《{courseName}》：周次“{rawWeeks}”无法解析（{weekError}），该时段已按每周显示，请核对");
                    }
                }

                if (slotTotal >= TimetableLimits.MaxSlots)
                {
                    slotOverflow = true;
                    continue;
                }

                entries.Add(new TimetableEntry(de.Day, ps, weeks));
                slotTotal++;
            }

            if (entries.Count == 0)
            {
                Add(ImportSeverity.Warning, $"《{courseName}》没有任何有效时段，已跳过");
                continue;
            }

            // Courses repeated with identical identity (name/teacher/room/colour) are merged —
            // AI output frequently repeats a course once per weekday it appears on. Notes from
            // both sides are kept (joined when they differ).
            var key = $"{courseName}\u0001{teacher}\u0001{room}\u0001{color}";
            if (index.TryGetValue(key, out var existing))
            {
                var target = builders[existing];
                target.Entries.AddRange(entries);
                if (!string.IsNullOrEmpty(note))
                {
                    target.Note = string.IsNullOrEmpty(target.Note) ? note
                        : target.Note == note ? target.Note
                        : $"{target.Note}；{note}";
                }
                if (!mergedNames.Contains(courseName)) mergedNames.Add(courseName);
            }
            else
            {
                index[key] = builders.Count;
                builders.Add(new CourseBuilder(courseName, teacher, room, color, note, entries));
            }

            if (slotOverflow) break;
        }

        if (slotOverflow)
            Add(ImportSeverity.Warning, $"时段总数超过上限 {TimetableLimits.MaxSlots}，超出部分已忽略");

        foreach (var merged in mergedNames)
            Add(ImportSeverity.Warning, $"《{merged}》在 JSON 中出现了多次，已自动合并为同一门课");

        if (builders.Count == 0) return FailWith("没有解析出任何可用的课程");

        // ---- Cross checks ----
        var maxUsed = 0;
        foreach (var b in builders)
            foreach (var e in b.Entries)
                if (e.Periods![^1] > maxUsed) maxUsed = e.Periods[^1];

        if (periods.Count > 0 && maxUsed > periods.Count)
            Add(ImportSeverity.Warning,
                $"课表使用了第 {maxUsed} 节，但时间表只定义了 {periods.Count} 节；未定义的节次没有上下课时间");

        if (totalWeeks > 0)
        {
            var beyond = new List<string>();
            foreach (var b in builders)
            {
                foreach (var e in b.Entries)
                {
                    if (e.Weeks == null) continue;
                    if (!WeekExpression.TryParse(e.Weeks, out var set, out _) || set.All) continue;
                    var highest = 64 - BitOperations.LeadingZeroCount(set.Mask);
                    if (highest > totalWeeks)
                    {
                        beyond.Add($"《{b.Name}》");
                        break;
                    }
                }
            }
            if (beyond.Count > 0)
                Add(ImportSeverity.Warning,
                    $"以下课程的周次超过总周数（{totalWeeks} 周），请核对：{string.Join("、", beyond)}");
        }

        AddConflicts(builders, issues, Add);

        // ---- Build the normalized model ----
        var courses = new List<TimetableCourse>(builders.Count);
        foreach (var b in builders)
            courses.Add(new TimetableCourse(b.Name, b.Teacher, b.Room, b.Color, b.Entries, b.Note));

        var model = new TimetableModel(
            TimetableModel.CurrentVersion, name, startDate, totalWeeks, 0, periods, courses,
            ShowWeekend: true, ShowRoom: true, ShowTeacher: false).Normalize();

        return new ImportResult
        {
            Model = model,
            Issues = issues,
            CourseCount = model.CourseCount,
            SlotCount = model.SlotCount,
        };
    }

    /// <summary>Same day, same period, overlapping weeks → almost certainly a data error worth a warning.</summary>
    private static void AddConflicts(List<CourseBuilder> builders, List<ImportIssue> issues,
        Action<ImportSeverity, string> add)
    {
        if (issues.Count >= MaxIssues) return;

        var dayPeriodLists = new Dictionary<(int Day, int Period), List<(int Index, WeekSet Weeks)>>();
        for (var bi = 0; bi < builders.Count; bi++)
        {
            foreach (var e in builders[bi].Entries)
            {
                var weeks = e.Weeks != null && WeekExpression.TryParse(e.Weeks, out var set, out _)
                    ? set
                    : WeekSet.AllWeeks;
                foreach (var p in e.Periods!)
                {
                    var key = (e.Day, p);
                    if (!dayPeriodLists.TryGetValue(key, out var list)) dayPeriodLists[key] = list = [];
                    list.Add((bi, weeks));
                }
            }
        }

        var pairDayPeriods = new Dictionary<(int A, int B, int Day), SortedSet<int>>();
        foreach (var ((day, period), list) in dayPeriodLists)
        {
            for (var i = 0; i < list.Count; i++)
            {
                for (var j = i + 1; j < list.Count; j++)
                {
                    if (!list[i].Weeks.Intersects(list[j].Weeks)) continue;
                    var a = Math.Min(list[i].Index, list[j].Index);
                    var b = Math.Max(list[i].Index, list[j].Index);
                    var key = (a, b, day);
                    if (!pairDayPeriods.TryGetValue(key, out var periods)) pairDayPeriods[key] = periods = [];
                    periods.Add(period);
                }
            }
        }

        foreach (var ((a, b, day), periods) in pairDayPeriods
                     .OrderBy(kv => kv.Key.Day).ThenBy(kv => kv.Key.A).ThenBy(kv => kv.Key.B))
        {
            if (issues.Count >= MaxIssues) return;
            add(ImportSeverity.Warning,
                $"{WeekDays.Short(day)}第 {FormatRanges(periods)} 节：《{builders[a].Name}》与《{builders[b].Name}》的时间相互重叠，请核对周次（如单/双周）");
        }
    }

    private static string FormatRanges(IEnumerable<int> sortedPeriods)
    {
        var list = sortedPeriods.Distinct().OrderBy(p => p).ToList();
        var parts = new List<string>();
        var start = list[0];
        var previous = start;

        for (var i = 1; i <= list.Count; i++)
        {
            if (i < list.Count && list[i] == previous + 1)
            {
                previous = list[i];
                continue;
            }
            parts.Add(start == previous ? start.ToString(CultureInfo.InvariantCulture) : $"{start}-{previous}");
            if (i < list.Count)
            {
                start = list[i];
                previous = start;
            }
        }

        return string.Join("、", parts);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private sealed class CourseBuilder(string name, string? teacher, string? room, string? color, string? note, List<TimetableEntry> entries)
    {
        public string Name { get; } = name;
        public string? Teacher { get; } = teacher;
        public string? Room { get; } = room;
        public string? Color { get; } = color;
        public string? Note { get; set; } = note;
        public List<TimetableEntry> Entries { get; } = entries;
    }
}

/// <summary>Short weekday names for issue messages (culture-aware).</summary>
internal static class WeekDays
{
    public static string Short(int day) =>
        CultureInfo.CurrentUICulture.DateTimeFormat.GetShortestDayName(
            (System.DayOfWeek)(((int)System.DayOfWeek.Monday + ((day - 1) % 7)) % 7));
}
