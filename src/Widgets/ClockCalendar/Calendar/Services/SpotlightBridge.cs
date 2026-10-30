using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Calendar.Locales;
using Calendar.Models;
using Calendar.Views;
using DeskCanvas;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;

namespace Calendar.Services;

/// <summary>
/// Exposes unified calendar schedule events to the host 聚焦搜索 (Spotlight).
/// Registered once per process from the widget view's load path.
/// </summary>
public sealed class ScheduleSpotlightProvider : ISpotlightExternalProvider
{
    public const string ProviderId = "calendar.schedule";

    public string Id => ProviderId;

    public Task<IReadOnlyList<SpotlightExternalHit>> QueryAsync(string query, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>([]);

        List<SpotlightExternalHit> result;
        try
        {
            result = Score(ScheduleStore.Get(), query.Trim(), limit);
        }
        catch
        {
            result = [];
        }

        return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>(result);
    }

    private static List<SpotlightExternalHit> Score(ScheduleModel model, string query, int limit)
    {
        var scored = new List<(double Score, SpotlightExternalHit Hit)>();
        var events = model.Events ?? [];
        var now = DateTime.Now;

        for (var index = 0; index < events.Count; index++)
        {
            var ev = events[index];
            if (ev is null || string.IsNullOrWhiteSpace(ev.Title)) continue;

            // Score against title (primary weight)
            var titleScore = LauncherScoring.Score(ev.Title, query);
            
            // Score against location or notes or category
            var locationScore = !string.IsNullOrWhiteSpace(ev.Location) ? LauncherScoring.Score(ev.Location, query) * 0.6 : 0;
            var categoryScore = !string.IsNullOrWhiteSpace(ev.Category) ? LauncherScoring.Score(ev.Category, query) * 0.5 : 0;
            var notesScore = !string.IsNullOrWhiteSpace(ev.Notes) ? LauncherScoring.Score(ev.Notes, query) * 0.4 : 0;

            var maxScore = Math.Max(titleScore, Math.Max(locationScore, Math.Max(categoryScore, notesScore)));
            if (maxScore <= 0) continue;

            // Bonus for today's ongoing/upcoming events, slight penalty for completed/past events
            if (ev.IsCompleted)
            {
                maxScore = Math.Max(1, maxScore - 40);
            }
            else if (ev.StartTime.Date == now.Date)
            {
                if (now >= ev.StartTime && now <= ev.EndTime)
                    maxScore += 25; // Ongoing
                else if (ev.StartTime > now)
                    maxScore += 15; // Upcoming today
            }
            else if (ev.EndTime < now)
            {
                maxScore = Math.Max(1, maxScore - 30); // Past event
            }

            var capturedEvent = ev;
            scored.Add((maxScore, new SpotlightExternalHit(
                $"{ProviderId}:{capturedEvent.Id}",
                capturedEvent.Title,
                FormatSubtitle(capturedEvent),
                Badge: capturedEvent.Category,
                RunAsync: () =>
                {
                    SchedulePanelWindow.ShowPanel(null, null, capturedEvent.Id);
                    return Task.FromResult(true);
                },
                Kind: SpotlightResultKind.Widget,
                GroupTitle: Locale.Calendar_Schedule_Title
            )));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Hit)
            .ToList();
    }

    private static string FormatSubtitle(ScheduleEventModel ev)
    {
        var datePrefix = ev.StartTime.Date == DateTime.Today
            ? "今天"
            : ev.StartTime.ToString("MM月dd日");

        var timeStr = ev.IsAllDay
            ? "全天"
            : $"{ev.StartTime:HH:mm} - {ev.EndTime:HH:mm}";

        var locationStr = string.IsNullOrWhiteSpace(ev.Location) ? "" : $" · {ev.Location}";
        return $"{datePrefix} {timeStr}{locationStr}";
    }
}

/// <summary>
/// Idempotent registration glue between the widget and host spotlight service.
/// </summary>
public static class SpotlightBridge
{
    private static int registered;

    public static void EnsureRegistered()
    {
        if (Interlocked.CompareExchange(ref registered, 1, 0) != 0) return;

        try
        {
            if (App.Services?.GetService(typeof(SpotlightExternalResults)) is not SpotlightExternalResults results)
            {
                Interlocked.Exchange(ref registered, 0);
                return;
            }

            results.Register(new ScheduleSpotlightProvider());
        }
        catch
        {
            Interlocked.Exchange(ref registered, 0);
        }
    }
}
