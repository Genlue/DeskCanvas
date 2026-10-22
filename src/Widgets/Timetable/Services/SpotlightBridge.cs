using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using Timetable.Locales;
using Timetable.Models;
using TimetableView = Timetable.Views.Timetable;

namespace Timetable.Services;

/// <summary>
/// Exposes the timetable to the host 聚焦搜索 (Spotlight). Registered once per process from the
/// timetable view's load path, so the course group only exists while a Timetable widget does.
/// <para>
/// Queries are in-memory over the store's cached model: a course matches by name, teacher, room
/// or note, and the subtitle spells out where and when it meets. Enter opens the full-schedule
/// secondary panel — the same surface the widget's own corner button opens.
/// </para>
/// </summary>
public sealed class TimetableSpotlightProvider : ISpotlightExternalProvider
{
    public const string ProviderId = "timetable";

    /// <summary>Teacher / room / note matches rank below a name match, but still surface.</summary>
    private const double TeacherWeight = 0.5;
    private const double RoomWeight = 0.4;
    private const double NoteWeight = 0.3;

    public string Id => ProviderId;

    public Task<IReadOnlyList<SpotlightExternalHit>> QueryAsync(string query, int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>([]);

        List<SpotlightExternalHit> result;
        try
        {
            result = Score(TimetableStore.Get(), query.Trim(), limit);
        }
        catch
        {
            // A broken/locked store must never take the search surface down.
            result = [];
        }

        return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>(result);
    }

    private static List<SpotlightExternalHit> Score(TimetableModel model, string query, int limit)
    {
        var scored = new List<(double Score, SpotlightExternalHit Hit)>();
        var courses = model.Courses ?? [];

        for (var index = 0; index < courses.Count; index++)
        {
            var course = courses[index];
            if (course is null || string.IsNullOrWhiteSpace(course.Name)) continue;

            var score = MatchScore(course, query);
            if (score <= 0) continue;

            scored.Add((score, new SpotlightExternalHit(
                $"{ProviderId}:{index}",
                course.Name!,
                Subtitle(model, course),
                Badge: null,
                RunAsync: OpenPanelAsync,
                Kind: SpotlightResultKind.Widget,
                GroupTitle: Locale.Timetable_Spotlight_Group)));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Hit)
            .ToList();
    }

    /// <summary>Name first; teacher / room / note are secondary, discounted matches.</summary>
    private static double MatchScore(TimetableCourse course, string query)
    {
        var name = LauncherScoring.Score(course.Name ?? string.Empty, query);
        if (name > 0) return name;

        var teacher = LauncherScoring.Score(course.Teacher ?? string.Empty, query) * TeacherWeight;
        var room = LauncherScoring.Score(course.Room ?? string.Empty, query) * RoomWeight;
        var note = LauncherScoring.Score(course.Note ?? string.Empty, query) * NoteWeight;
        return Math.Max(teacher, Math.Max(room, note));
    }

    /// <summary>"张老师 · A101 · 周一 第 3-4 节" — only the parts the course actually has.</summary>
    private static string Subtitle(TimetableModel model, TimetableCourse course)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(course.Teacher)) parts.Add(course.Teacher);
        if (!string.IsNullOrWhiteSpace(course.Room)) parts.Add(course.Room);

        var entry = course.Entries?.FirstOrDefault(e => e.Periods is { Count: > 0 });
        if (entry is { Periods: { Count: > 0 } periods })
        {
            var day = TimetableClock.DayShortName(entry.Day);
            var range = periods.Count > 1
                ? $"{periods[0]}-{periods[^1]}"
                : periods[0].ToString(CultureInfo.CurrentCulture);
            var slot = string.Format(Locale.Timetable_Spotlight_Slot, range);
            parts.Add($"{day} {slot}");
        }

        return string.Join(" · ", parts);
    }

    /// <summary>Open the panel anchored on the live widget, or centered when none is loaded.</summary>
    private static Task<bool> OpenPanelAsync()
    {
        try
        {
            if (SpotlightBridge.TryOpenPanel()) return Task.FromResult(true);
        }
        catch
        {
            // Fall through: the store may still have data even if the live view is gone.
        }

        try
        {
            var model = TimetableStore.Get();
            if (!model.HasData) return Task.FromResult(false);
            Views.TimetablePanelWindow.ShowPanel(model, screenCenter: null, owner: null, cornerRadius: null);
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Registration + live-view anchor for the timetable provider. The view attaches on load so a
/// course hit opens the panel where the user's timetable actually is; when every view is gone the
/// provider still answers and the panel opens centered.
/// </summary>
public static class SpotlightBridge
{
    private static int registered;
    private static TimetableView? liveView;

    /// <summary>Called from the view's load path: remember the anchor and register once.</summary>
    public static void Attach(TimetableView view)
    {
        liveView = view;
        EnsureRegistered();
    }

    /// <summary>Called from the view's unload path: drop the anchor if it is still this view.</summary>
    public static void Detach(TimetableView view)
    {
        if (ReferenceEquals(liveView, view)) liveView = null;
    }

    /// <summary>Open the full schedule from the last live widget, when there is one.</summary>
    public static bool TryOpenPanel()
    {
        var view = liveView;
        if (view is null) return false;
        view.OpenPanel();
        return true;
    }

    private static void EnsureRegistered()
    {
        if (Interlocked.CompareExchange(ref registered, 1, 0) != 0) return;

        try
        {
            if (App.Services?.GetService(typeof(SpotlightExternalResults))
                is not SpotlightExternalResults results)
            {
                // Host not ready / contract not registered: allow a later call to retry.
                Interlocked.Exchange(ref registered, 0);
                return;
            }

            results.Register(new TimetableSpotlightProvider());
        }
        catch
        {
            Interlocked.Exchange(ref registered, 0);
        }
    }
}
