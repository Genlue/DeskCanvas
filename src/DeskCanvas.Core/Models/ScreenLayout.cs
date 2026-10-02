using System.Linq;
using System.Text.Json.Serialization;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Core.Models;

/// <summary>
/// Per-screen widget configuration, stored in <c>layout.json</c> under the
/// <see cref="ScreensLayout.Screens"/> collection.
/// </summary>
/// <param name="Id">Stable screen id (GUID, generated once when the screen first appears). Used for manual rebinding and screen alias.</param>
/// <param name="Key">Auto-match identity: <c>"FriendlyName|WidthxHeight"</c> (e.g. <c>"DELL U2723QE|2560x1600"</c>). <c>null</c> = "legacy primary" entry (matches the current primary screen).</param>
/// <param name="Alias">User alias shown in the UI (e.g. <c>"主屏"</c>, <c>"观影屏"</c>); <c>null</c> falls back to the friendly name.</param>
/// <param name="DeviceName">Windows device name (e.g. <c>"\\.\DISPLAY1"</c>). Set by a manual screen rebinding; <c>null</c> = auto-match by <see cref="Key"/>.</param>
/// <param name="Grid">This screen's manual grid (percent-based). <c>null</c> falls back to <see cref="AppSettings.Grid"/>, then <see cref="Grid.Default"/>.</param>
/// <param name="ContentScale">This screen's content scale (per-widget scale wins over it).
/// <c>null</c> falls back to 1.0.</param>
/// <param name="Layout">Widgets placed on this screen (positions relative to the screen's working area).</param>
/// <param name="Margin">This screen's widget margin/padding. <c>null</c> falls back to global AppSettings.Dimensions.Margin.</param>
/// <param name="Radius">This screen's widget corner radius. <c>null</c> falls back to global AppSettings.Dimensions.Radius.</param>
/// <param name="HardwareId">EDID hardware id of the monitor this entry belongs to (backfilled
/// when the screen is seen). Pins the entry to that physical monitor, so a remote tool's
/// virtual screen — or a same-resolution stand-in — can never adopt it by name.
/// <c>null</c> = not yet seen with a hardware id (matched by key only).</param>
/// <param name="Sidebar">This screen's右侧小组件侧栏 configuration (width + widget list).
/// <c>null</c> = an empty sidebar (the default for configurations written before sidebars existed).
/// Kept independent from <see cref="Layout"/>: desktop widgets never migrate into the sidebar.</param>
public record ScreenLayout(
    string Id,
    string? Key,
    string? Alias,
    string? DeviceName,
    Grid? Grid,
    double? ContentScale,
    List<WidgetLayout> Layout,
    double? Margin = null,
    double? Radius = null,
    string? HardwareId = null,
    SidebarLayout? Sidebar = null)
{
    /// <summary>
    /// Display name for the UI: the user alias when set, otherwise the friendly name part of the <see cref="Key"/>.
    /// </summary>
    [JsonIgnore]
    public string DisplayName => Alias ?? Key?.Split('|')[0] ?? "Primary";

    /// <summary>
    /// This screen's sidebar configuration, materialized to an empty one when the stored
    /// configuration predates sidebars (so callers never need a null check).
    /// </summary>
    [JsonIgnore]
    public SidebarLayout EffectiveSidebar => Sidebar ?? new SidebarLayout();
}

/// <summary>
/// Multi-screen layout file (format v2): a collection of per-screen configurations.
/// <para>
/// v1 files (a plain <see cref="WidgetLayout"/> array) are still readable — they are
/// wrapped as a single "legacy primary" entry (<see cref="ScreenLayout.Key"/> = null,
/// which matches whichever screen is primary at runtime). Saving always writes v2.
/// </para>
/// </summary>
public record ScreensLayout(List<ScreenLayout> Screens, int Version = 2)
{
    /// <summary>Id of the legacy v1 primary entry.</summary>
    public const string LegacyPrimaryId = "primary";

    /// <summary>
    /// Wrap a legacy v1 layout list as a single primary-screen entry.
    /// </summary>
    public static ScreensLayout FromLegacy(List<WidgetLayout> layout) =>
        new([new ScreenLayout(LegacyPrimaryId, null, null, null, null, null, layout)], Version: 1);

    /// <summary>
    /// Find a screen configuration by its <see cref="ScreenLayout.Id"/>.
    /// </summary>
    /// <returns>The matching entry, or <c>null</c> when no entry has that id.</returns>
    public ScreenLayout? FindById(string id) =>
        Screens.FirstOrDefault(screen => screen.Id == id);

    /// <summary>
    /// Replace the entry with the same <see cref="ScreenLayout.Id"/> as <paramref name="screen"/>,
    /// or add it when no such entry exists.
    /// </summary>
    /// <param name="screen">The screen configuration to store.</param>
    /// <returns>The updated layout; the receiver is left unchanged.</returns>
    public ScreensLayout WithScreen(ScreenLayout screen) => this with
    {
        Screens = Screens.Select(item => item.Id == screen.Id ? screen : item).ToList()
    };

    /// <summary>
    /// Append a screen configuration to the collection.
    /// </summary>
    /// <param name="screen">The screen configuration to append.</param>
    /// <returns>The updated layout; the receiver is left unchanged.</returns>
    public ScreensLayout AddScreen(ScreenLayout screen) => this with { Screens = [..Screens, screen] };

    /// <summary>
    /// Insert or replace a screen configuration, depending on whether its
    /// <see cref="ScreenLayout.Id"/> is already present.
    /// </summary>
    /// <param name="screen">The screen configuration to store.</param>
    /// <returns>The updated layout; the receiver is left unchanged.</returns>
    public ScreensLayout UpsertScreen(ScreenLayout screen) =>
        Screens.Any(item => item.Id == screen.Id) ? WithScreen(screen) : AddScreen(screen);

    /// <summary>
    /// Remove redundant duplicate entries: two entries sharing the same identity
    /// <see cref="ScreenLayout.Key"/> are normally twin screens (each keeps its own
    /// layout), but a duplicated empty entry can appear when a screen is matched /
    /// created twice (stale in-memory list). Entries with identical keys are kept
    /// only when BOTH carry widgets; an empty duplicate is dropped in favor of the
    /// entry that owns the widgets (or is aliased) — never merges two layouts.
    /// </summary>
    public ScreensLayout Deduplicate()
    {
        var kept = new List<ScreenLayout>();
        var changed = false;
        foreach (var screen in Screens)
        {
            // The legacy primary entry (Key = null) is always unique.
            if (screen.Key == null)
            {
                kept.Add(screen);
                continue;
            }

            var existing = kept.FirstOrDefault(item => item.Key == screen.Key);
            if (existing == null)
            {
                kept.Add(screen);
                continue;
            }

            // Identical key: prefer the entry with widgets; an empty duplicate is
            // dropped unless it is the only one with an alias.
            if (screen.Layout.Count == 0 && existing.Layout.Count > 0) { changed = true; continue; }
            if (existing.Layout.Count == 0 && screen.Layout.Count > 0)
            {
                kept[kept.IndexOf(existing)] = screen;
                changed = true;
                continue;
            }
            if (existing.Layout.Count == 0 && screen.Layout.Count == 0)
            {
                if (screen.Alias != null && existing.Alias == null)
                {
                    // Keep the aliased twin (the user's named screen), drop the bare one.
                    kept[kept.IndexOf(existing)] = screen;
                }
                // Either way an empty duplicate is dropped → the set changed.
                changed = true;
                continue;
            }

            // Both carry widgets → genuine twin screens; keep both.
            kept.Add(screen);
        }

        // Return the SAME instance when nothing was dropped so callers can test
        // for an actual change with ReferenceEquals (avoiding needless re-saves).
        return changed ? this with { Screens = kept } : this;
    }

    /// <summary>
    /// Drop stale legacy/anonymous screen entries: entries identified only by a GPU adapter
    /// name or the anonymous fallback ("Screen|…") that are <b>not</b> currently attached and
    /// whose every widget is a <see cref="WidgetLayout.SameWidgetAs"/> copy of a widget on a
    /// currently attached screen. Such entries are leftovers of pre-hardware-id matching and of
    /// virtual screens (remote-control tools) that briefly adopted widgets during a session;
    /// after replugging they would never match again — only resurface as phantom duplicates on
    /// the next identity-less screen. Entries whose widgets exist nowhere else are kept: they
    /// may be a real monitor that reported no name, and their content is not recoverable.
    /// </summary>
    /// <param name="attachedEntries">The entries currently matched to attached screens.</param>
    public ScreensLayout PruneStaleLegacyEntries(IReadOnlyList<ScreenLayout> attachedEntries)
    {
        var attachedIds = attachedEntries.Select(entry => entry.Id).ToHashSet();
        var attachedWidgets = attachedEntries.SelectMany(entry => entry.Layout).ToList();

        var kept = new List<ScreenLayout>();
        var changed = false;
        foreach (var screen in Screens)
        {
            if (attachedIds.Contains(screen.Id) || !IsLegacyOrAnonymousKey(screen.Key))
            {
                kept.Add(screen);
                continue;
            }

            // Every widget must live on an attached screen to call this entry a stale copy
            // (an empty legacy entry is stale by definition).
            var allCopies = screen.Layout.All(widget => attachedWidgets.Any(widget.SameWidgetAs));
            if (!allCopies)
            {
                kept.Add(screen);
                continue;
            }

            changed = true;
        }

        return changed ? this with { Screens = kept } : this;
    }

    /// <summary>Whether <paramref name="key"/> comes from a legacy GPU-adapter key or the
    /// anonymous fallback — the two key shapes produced before hardware-id matching existed.</summary>
    private static bool IsLegacyOrAnonymousKey(string? key)
    {
        if (key == null) return false;
        if (key.StartsWith(ScreenIdentity.FallbackName + "|", StringComparison.Ordinal)) return true;
        return key.Contains("GeForce", StringComparison.OrdinalIgnoreCase)
               || key.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
               || key.Contains("Intel", StringComparison.OrdinalIgnoreCase)
               || key.Contains("Graphics", StringComparison.OrdinalIgnoreCase)
               || key.Contains("Virtual Display Adapter", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// All widgets across every screen (flat, for gallery / unload checks / legacy consumers).
    /// </summary>
    [JsonIgnore]
    public List<WidgetLayout> AllWidgets => Screens.SelectMany(screen => screen.Layout).ToList();
}