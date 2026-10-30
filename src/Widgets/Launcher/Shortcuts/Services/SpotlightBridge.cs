using DeskCanvas;
using DeskCanvas.Core.Services;
using DeskCanvas.Services;
using Shortcuts.Models;

namespace Shortcuts.Services;

/// <summary>
/// Exposes the shortcut library to the host 聚焦搜索 (Spotlight). Registered once per process
/// (from <c>ShortcutsView.OnLoaded</c>, i.e. only when a Shortcuts widget actually exists on the
/// desktop — no widget, no provider, which is the accepted degradation).
/// <para>
/// Queries are pure in-memory filtering over the store's cached document: the workflow count is
/// small (tens), so scoring runs per keystroke without any extra IO. Execution goes through
/// <see cref="ShortcutHotkeyService.ExecuteShortcutAsync"/>, the same single run entry point the
/// global hotkeys use, so confirmation, the owner-window resolution and the input dialog behave
/// exactly like a hotkey press.
/// </para>
/// </summary>
public sealed class ShortcutsSpotlightProvider : ISpotlightExternalProvider
{
    public const string ProviderId = "shortcuts";

    public string Id => ProviderId;

    public Task<IReadOnlyList<SpotlightExternalHit>> QueryAsync(string query, int limit,
        CancellationToken ct)
    {
        // Workflow rows are only meaningful while the user is typing.
        if (string.IsNullOrWhiteSpace(query) || limit <= 0)
            return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>([]);

        List<SpotlightExternalHit> result;
        try
        {
            result = Score(ShortcutsStore.Get(), query.Trim(), limit);
        }
        catch
        {
            // A broken/locked store must never take the search surface down.
            result = [];
        }

        return Task.FromResult<IReadOnlyList<SpotlightExternalHit>>(result);
    }

    private static List<SpotlightExternalHit> Score(ShortcutsDocument document, string query, int limit)
    {
        var scored = new List<(int Score, SpotlightExternalHit Hit)>();

        foreach (var shortcut in document.Shortcuts)
        {
            if (shortcut is null || string.IsNullOrWhiteSpace(shortcut.Name)) continue;

            var score = LauncherScoring.Score(shortcut.Name, query);
            if (score <= 0) continue;

            var id = shortcut.Id;
            var hotkey = string.IsNullOrWhiteSpace(shortcut.Hotkey) ? null : shortcut.Hotkey;
            // Workflows that need user interaction get the existing localized askInput action
            // name as the hint (no new string): askInput steps pop their own prompt UI when run,
            // and required declared inputs make a run fail loudly instead of running silently.
            var subtitle = NeedsInput(shortcut) ? Loc.Get("Shortcuts_Action_askInput") : null;

            scored.Add((score, new SpotlightExternalHit(
                id,
                shortcut.Name,
                subtitle,
                hotkey,
                () => ShortcutHotkeyService.ExecuteShortcutAsync(id))));
        }

        return scored
            .OrderByDescending(item => item.Score)
            .Take(limit)
            .Select(item => item.Hit)
            .ToList();
    }

    /// <summary>True when running this workflow will involve the user: an askInput step or a required declared input.</summary>
    private static bool NeedsInput(ShortcutDefinition shortcut)
    {
        try
        {
            return shortcut.EffectiveGraph.Nodes.Any(n => n is { Type: "askInput" })
                || shortcut.InputFields?.Any(f => f.Required) == true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>One-time registration glue between the plugin and the host's spotlight singleton.</summary>
public static class SpotlightBridge
{
    private static int registered;

    /// <summary>
    /// Register the provider exactly once per process. Safe to call from every view load; the
    /// host's <see cref="SpotlightExternalResults.Register"/> also replaces by id, so even a
    /// theoretical double registration converges on one live provider. Requires the host DI
    /// container to be up (it always is by the time a widget view can load).
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

            results.Register(new ShortcutsSpotlightProvider());
        }
        catch
        {
            Interlocked.Exchange(ref registered, 0);
        }
    }
}
