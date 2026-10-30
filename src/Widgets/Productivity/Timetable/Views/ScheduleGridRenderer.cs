using Timetable.Services;

namespace Timetable.Views;

/// <summary>
/// Shared placement maths for course blocks on a week grid — used by the live widget view
/// and the import-review preview. Keeps the tricky part (row mapping, transitive overlap
/// grouping, side-by-side lanes for parallel courses) in one place.
/// </summary>
internal static class ScheduleGridRenderer
{
    /// <summary>
    /// A block to place on a day column. Rows index into the caller's displayed period list;
    /// items sharing a <see cref="GroupId"/> overlap and must be placed side by side
    /// (<see cref="Lane"/> of <see cref="LaneCount"/>) inside the group's full row region.
    /// </summary>
    public sealed record Placement(
        TimetableRuntime.Slot Slot,
        int RowIndex,
        int RowSpan,
        int Lane,
        int LaneCount,
        int GroupId);

    /// <summary>
    /// Lays out one day column. <paramref name="week"/> &lt; 1 means "no week context"
    /// (show everything, used by the review preview).
    /// </summary>
    public static List<Placement> LayoutDay(TimetableRuntime runtime, int day, int week, IReadOnlyList<int> usedPeriods)
    {
        var result = new List<Placement>();
        if (day is < 1 or > 7) return result;

        // Map each slot onto displayed row indexes (a slot whose periods are all hidden is skipped).
        var mapped = new List<(TimetableRuntime.Slot Slot, int Fi, int Li)>();
        foreach (var slot in runtime.ByDay[day])
        {
            if (week >= 1 && !slot.Weeks.IsActive(week)) continue;

            var first = -1;
            var last = -1;
            foreach (var p in slot.Entry.Periods!)
            {
                var index = IndexOf(usedPeriods, p);
                if (index < 0) continue;
                if (first < 0) first = index;
                last = index;
            }
            if (first < 0) continue;
            mapped.Add((slot, first, last));
        }

        if (mapped.Count == 0) return result;

        mapped.Sort((a, b) => a.Fi != b.Fi ? a.Fi.CompareTo(b.Fi) : a.Li.CompareTo(b.Li));

        // Transitive overlap groups (intervals sorted by start → only the last group can absorb).
        var groups = new List<(List<(TimetableRuntime.Slot Slot, int Fi, int Li)> Items, int Min, int Max)>();
        foreach (var item in mapped)
        {
            if (groups.Count > 0 && item.Fi <= groups[^1].Max)
            {
                var last = groups[^1];
                last.Items.Add(item);
                if (item.Li > last.Max) last.Max = item.Li;
                groups[^1] = last;
                continue;
            }
            groups.Add(([item], item.Fi, item.Li));
        }

        var groupId = 0;
        foreach (var (items, min, max) in groups)
        {
            var span = max - min + 1;

            if (items.Count == 1)
            {
                result.Add(new Placement(items[0].Slot, min, span, 0, 1, groupId++));
                continue;
            }

            // Parallel courses: greedy lanes so each block gets its own column inside the group.
            var laneEnds = new List<int>();
            var lanes = new int[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var lane = -1;
                for (var l = 0; l < laneEnds.Count; l++)
                {
                    if (laneEnds[l] < items[i].Fi)
                    {
                        lane = l;
                        break;
                    }
                }
                if (lane < 0)
                {
                    lane = laneEnds.Count;
                    laneEnds.Add(-1);
                }
                laneEnds[lane] = items[i].Li;
                lanes[i] = lane;
            }

            for (var i = 0; i < items.Count; i++)
                result.Add(new Placement(items[i].Slot, min, span, lanes[i], laneEnds.Count, groupId));

            groupId++;
        }

        return result;
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }
}
