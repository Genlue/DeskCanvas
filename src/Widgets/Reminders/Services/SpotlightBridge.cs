using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using Reminders.Locales;
using Reminders.Models;

namespace Reminders.Services;

/// <summary>
/// Exposes the checklist to the host 聚焦搜索 (Spotlight). Registered once per process from the
/// widget view's load path, i.e. only when a Reminders widget exists on the desktop — the same
/// accepted degradation the Shortcuts plugin uses (no widget, no results).
/// <para>
/// Queries are pure in-memory filtering over the store's cached document, so a keystroke never
/// pays for file IO. Enter <b>toggles the item</b> through the same store every widget instance
/// observes, which makes "find a to-do, tick it off" a one-keystroke operation from anywhere.
/// </para>
/// </summary>
public sealed class RemindersSpotlightProvider : ISpotlightExternalProvider
{
    public const string ProviderId = "reminders";

    /// <summary>Completed items still match, but rank below pending ones with the same text score.</summary>
    private const int CompletedPenalty = 60;

    public string Id => ProviderId;

    public Task<IReadOnlyList<SpotlightExternalHit>> QueryAsync(string query, int limit,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>([]);

        List<SpotlightExternalHit> result;
        try
        {
            result = Score(RemindersStore.Get(), query.Trim(), limit);
        }
        catch
        {
            // A broken/locked store must never take the search surface down.
            result = [];
        }

        return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>(result);
    }

    private static List<SpotlightExternalHit> Score(RemindersListModel model, string query, int limit)
    {
        var scored = new List<(int Score, SpotlightExternalHit Hit)>();
        var reminders = model.Reminders ?? [];

        for (var index = 0; index < reminders.Count; index++)
        {
            var reminder = reminders[index];
            if (reminder is null || string.IsNullOrWhiteSpace(reminder.Title)) continue;

            var score = LauncherScoring.Score(reminder.Title, query);
            if (score <= 0) continue;
            if (reminder.Completed) score = Math.Max(1, score - CompletedPenalty);

            // The closure captures the row's identity (index + title). A save between the query
            // and Enter can move the row; ToggleAsync re-resolves by title before touching it.
            var capturedIndex = index;
            var capturedTitle = reminder.Title;

            scored.Add((score, new SpotlightExternalHit(
                $"{ProviderId}:{capturedIndex}",
                capturedTitle,
                Subtitle(reminder, model.ListName),
                Badge: null,
                RunAsync: () => ToggleAsync(capturedIndex, capturedTitle),
                Kind: SpotlightResultKind.Widget,
                GroupTitle: Locale.Reminders_Spotlight_Group)));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Hit)
            .ToList();
    }

    /// <summary>"待办清单 · 未完成" — the list it belongs to and its current state.</summary>
    private static string Subtitle(ReminderModel reminder, string? listName)
    {
        var state = reminder.Completed ? Locale.Reminders_Spotlight_Done : Locale.Reminders_Spotlight_Pending;
        var name = DisplayListName(listName);
        return string.IsNullOrWhiteSpace(name) ? state : $"{name} · {state}";
    }

    /// <summary>
    /// The stored default list name is Chinese ("待办清单"); an English UI must not surface it.
    /// Same rule the widget's own view model applies to the card header.
    /// </summary>
    private static string? DisplayListName(string? listName)
    {
        var isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);
        if (!isZh && (listName == "待办清单" || listName == "清单"))
            return Locale.Reminders_DefaultListName;
        return listName;
    }

    /// <summary>
    /// Toggle the captured item. DeleteOnCheck is honoured exactly like the widget's own check
    /// box: a completed item in such a list is removed instead of being kept struck through.
    /// </summary>
    private static Task<bool> ToggleAsync(int capturedIndex, string capturedTitle)
    {
        try
        {
            var model = RemindersStore.Get();
            var reminders = model.Reminders ?? [];
            var index = capturedIndex >= 0 && capturedIndex < reminders.Count
                        && string.Equals(reminders[capturedIndex].Title, capturedTitle, StringComparison.Ordinal)
                ? capturedIndex
                : reminders.FindIndex(item => string.Equals(item.Title, capturedTitle, StringComparison.Ordinal));
            if (index < 0) return Task.FromResult(false);

            var current = reminders[index];
            var next = new List<ReminderModel>(reminders);
            if (!current.Completed && model.DeleteOnCheck)
            {
                next.RemoveAt(index);
            }
            else
            {
                next[index] = current with { Completed = !current.Completed };
            }

            RemindersStore.Save(model with { Reminders = next });
            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>One-time registration glue between the widget and the host's spotlight singleton.</summary>
public static class SpotlightBridge
{
    private static int registered;

    /// <summary>
    /// Register the provider exactly once per process. Safe to call from every view load; the
    /// host's <see cref="SpotlightExternalResults.Register"/> replaces by id, so a double
    /// registration converges on one live provider.
    /// </summary>
    public static void EnsureRegistered()
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

            results.Register(new RemindersSpotlightProvider());
        }
        catch
        {
            Interlocked.Exchange(ref registered, 0);
        }
    }
}
