using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace DeskCanvas.Core.Models.Settings;

/// <summary>
/// How 聚焦搜索 picks its light/dark mode.
/// <para>
/// Three of these are not the same question: 跟随全局 asks the <b>application</b> (the same theme the
/// widgets get, including 失焦自动切换 and 壁纸自动主题), while 跟随系统 asks <b>Windows</b> and ignores
/// whatever the app was forced to. A launcher that must stay dark over a light desktop needs the
/// second, so both exist as explicit choices instead of one "follow" that silently means the other.
/// </para>
/// </summary>
public enum LauncherThemeMode
{
    /// <summary>Same light/dark as the app's own surfaces (the default).</summary>
    FollowGlobal = 0,

    /// <summary>Windows' own light/dark, even when the app is forced the other way.</summary>
    FollowSystem = 1,

    /// <summary>Always light.</summary>
    Light = 2,

    /// <summary>Always dark.</summary>
    Dark = 3,
}

/// <summary>
/// The canonical category keys of the launcher's result filter, in the order the category bar
/// shows them. <see cref="LauncherSettings.CategoryHotkeys"/> is keyed by these strings.
/// <para>
/// The keys are stable storage identifiers, not labels: a translated chip label must never become
/// a dictionary key, or switching the UI language would silently drop every custom binding.
/// </para>
/// </summary>
public static class LauncherCategoryKeys
{
    /// <summary>Every result (no filter).</summary>
    public const string All = "all";

    /// <summary>Launcher shortcuts.</summary>
    public const string Apps = "apps";

    /// <summary>Image files only.</summary>
    public const string Images = "images";

    /// <summary>Non-image files (folders and images have their own categories).</summary>
    public const string Files = "files";

    /// <summary>Directories only.</summary>
    public const string Folders = "folders";

    /// <summary>Widget-owned rows and 快捷指令 workflows.</summary>
    public const string Widgets = "widgets";

    /// <summary>Clipboard history.</summary>
    public const string Clipboard = "clipboard";

    /// <summary>The web-search row (在浏览器中搜索).</summary>
    public const string Web = "web";

    /// <summary>
    /// The order the category bar uses when the user has not arranged it: 全部 first, then the
    /// F-key order the chips have always had.
    /// </summary>
    public static IReadOnlyList<string> Order { get; } =
        [All, Apps, Images, Files, Folders, Widgets, Clipboard, Web];

    /// <summary>
    /// The canonical spelling of <paramref name="key"/>, or <c>null</c> when it is not a category.
    /// Case-insensitive so a hand-edited "Apps" still resolves; the returned value is always the
    /// storage spelling, never the caller's.
    /// </summary>
    public static string? Match(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;

        var text = key.Trim();
        foreach (var candidate in Order)
            if (string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
                return candidate;

        return null;
    }

    /// <summary>True when <paramref name="key"/> names a category.</summary>
    public static bool IsKnown(string? key) => Match(key) != null;

    /// <summary>
    /// Default category-lock gestures: F1–F7 select the chip at the same position, F8 the web row.
    /// Function keys are the launcher's own while the overlay is open, so they never collide with
    /// typing. 网页 is appended <b>after</b> 剪贴板 on purpose — the existing keys keep their
    /// positions, so nothing a user already learned moves when the new chip appears.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultHotkeys { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [All] = "F1",
            [Apps] = "F2",
            [Images] = "F3",
            [Files] = "F4",
            [Folders] = "F5",
            [Widgets] = "F6",
            [Clipboard] = "F7",
            [Web] = "F8",
        };
}

/// <summary>
/// How the launcher's app grid orders its tiles. Pinned apps are always floated to the front,
/// whatever the mode — a pin is a placement, not a sort key.
/// </summary>
public enum LauncherSortMode
{
    /// <summary>Display name, culture-aware and case-insensitive. The default (and the catalogue's own order).</summary>
    Name = 0,

    /// <summary>Most recently launched first; never-launched apps keep the name order at the end.</summary>
    LastUsed = 1,

    /// <summary>Most launches first; ties keep the name order.</summary>
    MostUsed = 2,
}

/// <summary>
/// 聚焦搜索 (Spotlight-style launcher) settings.
/// <para>
/// The launcher's catalogue is deliberately <b>user-curated</b>: the user drops <c>.lnk</c> /
/// <c>.url</c> shortcuts into <see cref="EffectiveFolder"/> and the launcher shows exactly those.
/// There is no Start-Menu scan and no UWP AppUserModelID plumbing — see
/// <c>docs/聚焦搜索可行性研究.md</c>.
/// </para>
/// <para>
/// Every value is tolerant of a hand-edited file: <see cref="Normalize"/> clamps the numeric
/// ranges and drops unusable tokens instead of throwing, so a broken settings entry degrades to
/// the default rather than disabling the feature.
/// </para>
/// </summary>
/// <param name="Enabled">Whether the global hotkey and the overlay are active. Off by default — a
/// launcher that hijacks a system-wide gesture must be an explicit choice.</param>
/// <param name="Hotkey">Canonical gesture text ("Alt+Space"). <c>null</c>/blank uses <see cref="DefaultHotkey"/>.</param>
/// <param name="Folder">Shortcut folder. <c>null</c>/blank uses <see cref="DefaultFolder"/>.</param>
/// <param name="Columns">Grid columns at the default card width.</param>
/// <param name="GapPercent">
/// The gutter between two grid cells, as a percentage of a cell (0–45). The <b>icon size is
/// derived</b> from it (<c>icon = cell − gutter</c>), never set directly — see
/// <see cref="LauncherGridMetrics"/>.
/// </param>
/// <param name="RowGapPercent">
/// The vertical gutter between two rows, as a percentage of a cell (0–60). Separate from
/// <paramref name="GapPercent"/> because the two read differently: the horizontal gutter is the air
/// around an icon, while the vertical one is what makes a grid with names look "stretched" — the
/// label block already adds height, and the gutter on top of it was previously derived from the
/// column gap with no way to tighten it.
/// </param>
/// <param name="Width">Card width in DIP.</param>
/// <param name="Height">Card height in DIP.</param>
/// <param name="Blacklist">
/// Processes that suppress the launcher: while one of them owns the foreground window the
/// hotkey does nothing. Stored as bare process names ("chrome", not "chrome.exe" and not a path);
/// <see cref="NormalizeProcessName"/> is the single rule every entry goes through.
/// </param>
/// <param name="CornerRadius">Card corner radius in DIP, 0–100. Ignored while <paramref name="UseWidgetCornerRadius"/> is on.</param>
/// <param name="UseWidgetCornerRadius">Follow the widget card radius (<c>Dimensions.Radius</c>) instead of <paramref name="CornerRadius"/>.</param>
/// <param name="UseGlobalTheme">Follow the application theme. When off, the fields below override it one by one (null = still follow).</param>
/// <param name="Surface">Material override: Acrylic / LiquidGlass / FullColor.</param>
/// <param name="ThemeMode">
/// How the launcher picks light/dark (see <see cref="LauncherThemeMode"/>). Ignored while
/// <paramref name="UseGlobalTheme"/> is on, because that switch means "be the app's theme".
/// </param>
/// <param name="OpacityLevel">Card opacity override, 0–1.</param>
/// <param name="LiquidGlass">
/// 液态玻璃 optics. <b>The launcher's own</b> — it never inherits the widget material's optics
/// (see <see cref="ResolveTheme"/>), so tuning one never moves the other.
/// </param>
/// <param name="ShowNames">Draw the app name under each grid icon. Off = an icon-only grid (the
/// filtered result list always shows names, otherwise it would be unusable).</param>
/// <param name="NameLines">
/// How many lines an app name may occupy under its icon (1–3). Every tile reserves this many lines
/// whether its own name needs them or not, so all icons of a row stay on one baseline; names longer
/// than the limit are trimmed with an ellipsis.
/// </param>
/// <param name="GlassSampleScreen">
/// Liquid glass samples the <b>live screen</b> behind the overlay instead of the wallpaper. This is
/// the launcher's own pipeline: a Spotlight-style panel sits over real window content, and
/// refracting the wallpaper underneath it would show a desktop that is not actually there.
/// </param>
/// <param name="GlassSampleInterval">
/// How often that screen frame is re-captured, in milliseconds. Only meaningful while
/// <paramref name="GlassSampleScreen"/> is on: it is the freshness/cost trade for the glass —
/// a longer interval costs less but freezes motion behind the card.
/// </param>
/// <param name="FileSearch">
/// Also search <b>files</b> (Everything, or the built-in folder index when Everything is not
/// available) as the user types, next to the app matches.
/// </param>
/// <param name="AutoStartEverything">
/// Start Everything (minimised) when a file query needs it and the client is not running. The
/// service alone cannot answer IPC queries — the client process owns the IPC window.
/// </param>
/// <param name="RecentApps">
/// Show the most recently used apps (up to 8) as the grid's first row when the query is empty.
/// </param>
/// <param name="SortMode">
/// How the app grid orders its tiles (name / recently used / most used). Pinned apps stay in
/// front in every mode; see <see cref="LauncherSortMode"/>.
/// </param>
/// <param name="PreviewBar">
/// Show the preview pane on the right while a result is selected. The pane hides itself when the
/// window is too narrow, so this stays a preference, not a layout risk.
/// </param>
/// <param name="AnimateSummon">
/// Play the summon animation (180 ms fade + micro-scale) when the overlay opens. On by default;
/// off shows the card immediately, which is what a user who summons it dozens of times a day wants.
/// </param>
/// <param name="ClipboardHistory">
/// Record copied text and links into a local clipboard history (default <b>off</b>: a machine-wide
/// clipboard listener is a privacy decision, not a default). The history never leaves the machine
/// and its content is never written to logs.
/// </param>
/// <param name="ClipboardRetentionDays">
/// How many days clipboard entries survive (1–30). Entries older than this are dropped on start
/// and on a timer.
/// </param>
/// <param name="ClipboardExcluded">
/// Processes while one of which owns the foreground window clipboard changes are NOT recorded.
/// Same storage rules as <see cref="Blacklist"/>: bare process names, normalized once.
/// </param>
/// <param name="SearchHotkey">
/// The second summon gesture: opens the launcher directly in <b>search</b> mode (no app grid,
/// the field focused, the card compact until something is typed). <c>null</c>/blank uses
/// <see cref="DefaultSearchHotkey"/>; the same text as <paramref name="Hotkey"/> means "no second
/// gesture" and is not registered twice.
/// </param>
/// <param name="FollowGlobalBackground">
/// Take the card background from the global theme's 纯色背景色 pair. Which half of that pair is used
/// is decided by the launcher's <b>own</b> light/dark mode (<see cref="ThemeMode"/>), not by the
/// mode of the widgets behind it — a dark launcher takes the dark global colour even when the app
/// is light. Off = the two colours below paint the card (and its liquid-glass coating) instead.
/// </param>
/// <param name="BackgroundColorLight">
/// Custom card background in light mode, <c>#RRGGBB</c>. Alpha is deliberately not accepted: how
/// translucent the card is stays the material's (and 不透明度's) job, not a second knob.
/// </param>
/// <param name="BackgroundColorDark">Custom card background in dark mode, same rules.</param>
/// <param name="BlockFullscreen">
/// Suppress summoning the launcher while a true full-screen foreground application is active (e.g. exclusive full-screen games or presentation slides). Default is <c>true</c>.
/// </param>
/// <param name="CategoryHotkeys">
/// Category-lock gestures while the overlay is open, keyed by <see cref="LauncherCategoryKeys"/>.
/// <c>null</c> means "use the defaults" (F1–F8); an entry with an empty value means "this category
/// has no key". Values are gesture text ("F3", "Ctrl+Alt+3") parsed by the host; an unparseable
/// value simply never matches. They are local to the overlay, not global hotkeys.
/// </param>
/// <param name="WebSearch">
/// Offer the 在浏览器中搜索 row for every query. It is the same idea as the 搜索 component — pick
/// an engine, type, land on the result page — but it lives in the launcher, so it works even when
/// that widget is not on the desktop.
/// </param>
/// <param name="SearchEngine">
/// Which engine the web row uses: an id from the host's preset table ("google", "bing", "baidu",
/// …) or "custom" for <paramref name="SearchEngineUrl"/>. <c>null</c>/blank uses the default.
/// </param>
/// <param name="SearchEngineUrl">
/// Custom search address, with <c>{q}</c> where the query goes. Only read while
/// <paramref name="SearchEngine"/> is "custom"; a template without the placeholder is ignored so a
/// typo can never send the user to a page that silently drops the query.
/// </param>
/// <param name="SearchBrowser">
/// Executable of the browser the web row opens (a full path, as discovered from the registry).
/// <c>null</c>/blank means "the Windows default browser", which is also what a path that no longer
/// exists degrades to.
/// </param>
/// <param name="WebResultCount">
/// How many real web hits are listed under the 在浏览器中搜索 row while typing (0–10). 0 turns the
/// inline list off and leaves only the action row; the fetch is a network call, so "off" is a
/// legitimate choice and not just a cosmetic one.
/// </param>
/// <param name="CategoryOrder">
/// The user's chip order, by <see cref="LauncherCategoryKeys"/> storage key. 全部 is not stored:
/// it is the "no filter" entry and always stays first, so a saved order only lists the filters.
/// <c>null</c> means <see cref="LauncherCategoryKeys.Order"/>.
/// </param>
/// <param name="SearchPlaceholder">
/// Custom placeholder text for the search box when in All category. null/blank means use default.
/// </param>
/// <param name="SpaceShowsRecentLaunches">
/// Whether entering only a space in search mode displays recently opened items from search results.
/// </param>
/// <param name="SpaceRecentLaunchesCount">
/// How many recently opened items to show when typing space (3–20).
/// </param>
/// <param name="TakeOverSystemHotkeys">
/// When the configured gesture cannot be registered because Windows reserves it or another
/// program owns it (Win+S is the canonical example), take it over with a low-level keyboard hook
/// instead of reporting a conflict. Off by default: a hook sees every keystroke and removes the
/// combination from the system UI, so this must be an explicit choice. Requires the app to run
/// elevated to cover elevated foreground windows; the UI says so separately.
/// </param>
public record LauncherSettings(
    bool Enabled = false,
    string? Hotkey = null,
    string? Folder = null,
    int Columns = LauncherSettings.DefaultColumns,
    int GapPercent = LauncherSettings.DefaultGapPercent,
    int RowGapPercent = LauncherSettings.DefaultRowGapPercent,
    double Width = LauncherSettings.DefaultWidth,
    double Height = LauncherSettings.DefaultHeight,
    List<string>? Blacklist = null,
    double CornerRadius = LauncherSettings.DefaultCornerRadius,
    bool UseWidgetCornerRadius = false,
    bool UseGlobalTheme = true,
    SurfaceStyle? Surface = null,
    LauncherThemeMode ThemeMode = LauncherThemeMode.FollowGlobal,
    double? OpacityLevel = null,
    LiquidGlassSettings? LiquidGlass = null,
    bool GlassSampleScreen = true,
    int GlassSampleInterval = LauncherSettings.DefaultGlassSampleInterval,
    bool ShowNames = true,
    int NameLines = LauncherSettings.DefaultNameLines,
    bool FileSearch = true,
    bool AutoStartEverything = true,
    bool RecentApps = true,
    LauncherSortMode SortMode = LauncherSortMode.Name,
    bool PreviewBar = true,
    bool AnimateSummon = true,
    bool ClipboardHistory = false,
    int ClipboardRetentionDays = LauncherSettings.DefaultClipboardRetentionDays,
    List<string>? ClipboardExcluded = null,
    string? SearchHotkey = null,
    bool FollowGlobalBackground = true,
    string? BackgroundColorLight = null,
    string? BackgroundColorDark = null,
    bool BlockFullscreen = true,
    Dictionary<string, string>? CategoryHotkeys = null,
    bool WebSearch = true,
    string? SearchEngine = null,
    string? SearchEngineUrl = null,
    string? SearchBrowser = null,
    int WebResultCount = LauncherSettings.DefaultWebResultCount,
    List<string>? CategoryOrder = null,
    string? SearchPlaceholder = null,
    bool SpaceShowsRecentLaunches = true,
    int SpaceRecentLaunchesCount = LauncherSettings.DefaultSpaceRecentLaunchesCount,
    bool TakeOverSystemHotkeys = false)
{
    /// <summary>Alt+Space — the closest usable analogue to macOS Cmd+Space (Win+Space is the
    /// Windows input-language switch and cannot be taken).</summary>
    public const string DefaultHotkey = "Alt+Space";

    /// <summary>Alt+Shift+Space — the search-only gesture, next to the list gesture above.</summary>
    public const string DefaultSearchHotkey = "Alt+Shift+Space";

    /// <summary>Default number of columns in the launcher app grid.</summary>
    public const int DefaultColumns = 7;
    /// <summary>Default width of the launcher window in DIPs.</summary>
    public const double DefaultWidth = 820;
    /// <summary>Default height of the launcher window in DIPs.</summary>
    public const double DefaultHeight = 560;

    /// <summary>Minimum allowed number of columns in the grid.</summary>
    public const int MinColumns = 3;
    /// <summary>Maximum allowed number of columns in the grid.</summary>
    public const int MaxColumns = 12;

    /// <summary>
    /// Gutter between cells as a percentage of a cell. 18% reads as "a little air around every
    /// icon" at any column count; 0 packs icons edge to edge, 45 is the point where the icon is
    /// barely larger than its own gutter.
    /// </summary>
    public const int DefaultGapPercent = 18;
    /// <summary>Minimum column gutter percentage.</summary>
    public const int MinGapPercent = 0;
    /// <summary>Maximum column gutter percentage.</summary>
    public const int MaxGapPercent = 45;

    /// <summary>
    /// Vertical gutter between rows, as a percentage of a cell. 10% is the value the grid used to
    /// derive implicitly (<c>columnGap × 0.6</c> at the default 18%), so existing layouts keep their
    /// rhythm; 0 packs the rows tight, which is what a grid with names on usually wants.
    /// </summary>
    public const int DefaultRowGapPercent = 10;
    /// <summary>Minimum row gutter percentage.</summary>
    public const int MinRowGapPercent = 0;
    /// <summary>Maximum row gutter percentage.</summary>
    public const int MaxRowGapPercent = 60;

    /// <summary>Minimum allowed launcher width in DIPs.</summary>
    public const double MinWidth = 480;
    /// <summary>Maximum allowed launcher width in DIPs.</summary>
    public const double MaxWidth = 1600;
    /// <summary>Minimum allowed launcher height in DIPs.</summary>
    public const double MinHeight = 320;
    /// <summary>Maximum allowed launcher height in DIPs.</summary>
    public const double MaxHeight = 1200;
    /// <summary>Default card corner radius in DIPs.</summary>
    public const double DefaultCornerRadius = 18;
    /// <summary>Minimum allowed card corner radius in DIPs.</summary>
    public const double MinCornerRadius = 0;
    /// <summary>Maximum allowed card corner radius in DIPs.</summary>
    public const double MaxCornerRadius = 100;

    /// <summary>
    /// How many lines a tile name may occupy. Two is the macOS-parity default: enough for the
    /// "Microsoft Visual Studio Code" class of names, while a one-line name still leaves the icon
    /// on the same baseline as its neighbours (the label box is reserved, not measured).
    /// </summary>
    public const int DefaultNameLines = 2;
    /// <summary>Minimum number of text lines for tile labels.</summary>
    public const int MinNameLines = 1;
    /// <summary>Maximum number of text lines for tile labels.</summary>
    public const int MaxNameLines = 3;

    /// <summary>
    /// Screen re-capture period for the launcher's glass. 400 ms is the same trade the shared
    /// wallpaper sampler makes: animated content behind the card stays visibly alive without
    /// paying for a full-desktop BitBlt on every frame.
    /// </summary>
    public const int DefaultGlassSampleInterval = 400;

    /// <summary>
    /// Floor of 采样间隔. The one capture stage a frame cannot skip is one virtual-desktop BitBlt
    /// (single-digit milliseconds at 1440p), so 3 ms lets the user ask for "as fresh as the pipeline
    /// allows" instead of freezing motion behind the card at 100 ms. The glass surface still drops
    /// frames it cannot keep up with rather than queueing them, so an interval below the real cost
    /// degrades to "every frame is a new capture", never to a backlog.
    /// </summary>
    public const int MinGlassSampleInterval = 3;
    /// <summary>Ceiling of 采样间隔, in milliseconds.</summary>
    public const int MaxGlassSampleInterval = 2000;

    /// <summary>
    /// Clipboard history retention (days) default and range. 7 days / 100 entries is the doc's
    /// default trade: enough to be useful across a work week, small enough that the file stays a
    /// few KiB and "clear history" is not a long operation.
    /// </summary>
    public const int DefaultClipboardRetentionDays = 7;
    /// <summary>Minimum retention period for clipboard history, in days.</summary>
    public const int MinClipboardRetentionDays = 1;
    /// <summary>Maximum retention period for clipboard history, in days.</summary>
    public const int MaxClipboardRetentionDays = 30;

    /// <summary>Hard cap of clipboard entries kept in memory and on disk.</summary>
    public const int MaxClipboardEntries = 100;

    /// <summary>Hard cap of recently-used apps shown in the empty-query row.</summary>
    public const int MaxRecentApps = 8;

    /// <summary>
    /// The engine the web row starts on. Google is also the 搜索 component's default, so the two
    /// surfaces behave the same out of the box.
    /// </summary>
    public const string DefaultSearchEngine = "google";

    /// <summary><see cref="SearchEngine"/> value that means "use <see cref="SearchEngineUrl"/>".</summary>
    public const string CustomSearchEngine = "custom";

    /// <summary>Placeholder the custom template must contain.</summary>
    public const string SearchQueryPlaceholder = "{q}";

    /// <summary>
    /// How many web hits sit under the 在浏览器中搜索 row by default. Three is enough to answer
    /// "did the engine understand me" without turning the launcher into a browser tab.
    /// </summary>
    public const int DefaultWebResultCount = 3;

    /// <summary>Zero is a valid count: keep the action row, list no hits.</summary>
    public const int MinWebResultCount = 0;

    /// <summary>Hard cap of inline web hits. Each one is a row the keyboard has to walk.</summary>
    public const int MaxWebResultCount = 10;

    /// <summary>Default number of recently opened items to show on space.</summary>
    public const int DefaultSpaceRecentLaunchesCount = 10;
    /// <summary>Minimum number of recently opened items to show on space.</summary>
    public const int MinSpaceRecentLaunchesCount = 3;
    /// <summary>Maximum number of recently opened items to show on space.</summary>
    public const int MaxSpaceRecentLaunchesCount = 20;

    /// <summary>
    /// The default shortcut folder: <c>&lt;data folder&gt;\Launcher</c>.
    /// <para>
    /// Deliberately under <see cref="Const.DataFolder"/> and <b>not</b> under the active profile:
    /// the shortcuts a user curates are machine-level assets, while the on/off switch and the
    /// hotkey are settings and do follow the profile.
    /// </para>
    /// </summary>
    public static string DefaultFolder => Path.Combine(Const.DataFolder, "Launcher");

    /// <summary>The folder the launcher actually reads.</summary>
    public string EffectiveFolder =>
        string.IsNullOrWhiteSpace(Folder) ? DefaultFolder : Folder!.Trim();

    /// <summary>The gesture text the launcher actually registers.</summary>
    public string EffectiveHotkey =>
        string.IsNullOrWhiteSpace(Hotkey) ? DefaultHotkey : Hotkey!.Trim();

    /// <summary>The search-mode gesture. Equal to <see cref="EffectiveHotkey"/> = no second gesture.</summary>
    public string EffectiveSearchHotkey =>
        string.IsNullOrWhiteSpace(SearchHotkey) ? DefaultSearchHotkey : SearchHotkey!.Trim();

    /// <summary>The engine id the web row resolves against (never blank).</summary>
    public string EffectiveSearchEngine =>
        string.IsNullOrWhiteSpace(SearchEngine) ? DefaultSearchEngine : SearchEngine!.Trim().ToLowerInvariant();

    /// <summary>
    /// The custom template, or <c>null</c> when it is unusable. A template without the placeholder
    /// is rejected here rather than at launch time: silently dropping the query turns a typo into
    /// "it opened the wrong page", which is much harder to diagnose than the settings page saying
    /// what is missing.
    /// </summary>
    public string? EffectiveSearchEngineUrl
    {
        get
        {
            var raw = SearchEngineUrl?.Trim();
            if (string.IsNullOrEmpty(raw)) return null;
            if (!raw.Contains(SearchQueryPlaceholder, StringComparison.Ordinal)) return null;
            return Uri.TryCreate(raw.Replace(SearchQueryPlaceholder, "x", StringComparison.Ordinal),
                UriKind.Absolute, out _) ? raw : null;
        }
    }

    /// <summary>The browser executable the web row should use, or <c>null</c> for the default one.</summary>
    public string? EffectiveSearchBrowser =>
        string.IsNullOrWhiteSpace(SearchBrowser) ? null : SearchBrowser!.Trim();

    /// <summary>True when both gestures are the same combination (the second one is then not registered).</summary>
    public bool SearchHotkeyCollidesWithList =>
        string.Equals(EffectiveSearchHotkey, EffectiveHotkey, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The category-lock gestures actually in force: every category has a default (F1–F7), and
    /// <see cref="CategoryHotkeys"/> overrides one by one. An empty value means "this category has
    /// no key" — deliberately different from "not configured", which falls back to the default.
    /// </summary>
    public IReadOnlyDictionary<string, string> EffectiveCategoryHotkeys
    {
        get
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in LauncherCategoryKeys.Order)
                result[key] = LauncherCategoryKeys.DefaultHotkeys[key];

            if (CategoryHotkeys == null) return result;

            foreach (var key in LauncherCategoryKeys.Order)
            {
                foreach (var pair in CategoryHotkeys)
                {
                    if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                    result[key] = pair.Value?.Trim() ?? string.Empty;
                    break;
                }
            }

            return result;
        }
    }

    /// <summary>
    /// Bind one category to <paramref name="gesture"/> (empty = unbind) and persist every category
    /// explicitly, so a later change to the defaults can never re-bind a key the user cleared.
    /// </summary>
    public LauncherSettings WithCategoryHotkey(string categoryKey, string? gesture)
    {
        var key = LauncherCategoryKeys.Order.FirstOrDefault(candidate =>
            string.Equals(candidate, categoryKey, StringComparison.OrdinalIgnoreCase));
        if (key == null) return this;

        var next = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in EffectiveCategoryHotkeys) next[pair.Key] = pair.Value;
        next[key] = gesture?.Trim() ?? string.Empty;
        return this with { CategoryHotkeys = next };
    }

    /// <summary>Drop every override and return to the F1–F7 defaults.</summary>
    public LauncherSettings WithoutCategoryHotkeys() => this with { CategoryHotkeys = null };

    /// <summary>
    /// The inline web-result count actually in force (clamped, never negative).
    /// </summary>
    public int EffectiveWebResultCount => Math.Clamp(WebResultCount, MinWebResultCount, MaxWebResultCount);

    /// <summary>
    /// The chip order actually in force. 全部 is always first — it is the "no filter" entry, not a
    /// category of results — followed by the saved order, then any category the saved list forgot
    /// (a hand-edited file, or a category added by a later release).
    /// </summary>
    public IReadOnlyList<string> EffectiveCategoryOrder
    {
        get
        {
            var result = new List<string>(LauncherCategoryKeys.Order.Count) { LauncherCategoryKeys.All };

            if (CategoryOrder != null)
            {
                foreach (var raw in CategoryOrder)
                {
                    var key = LauncherCategoryKeys.Match(raw);
                    if (key == null || key == LauncherCategoryKeys.All) continue;
                    if (result.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                    result.Add(key);
                }
            }

            foreach (var key in LauncherCategoryKeys.Order)
                if (!result.Contains(key, StringComparer.OrdinalIgnoreCase))
                    result.Add(key);

            return result;
        }
    }

    /// <summary>
    /// Persist <paramref name="keys"/> as the chip order. Unknown keys are dropped and 全部 is not
    /// stored; an order that matches the default is written as <c>null</c>, so the settings file
    /// stays clean and a future default change still reaches users who never reordered anything.
    /// </summary>
    public LauncherSettings WithCategoryOrder(IEnumerable<string> keys)
    {
        var ordered = new List<string>(LauncherCategoryKeys.Order.Count);
        foreach (var raw in keys)
        {
            var key = LauncherCategoryKeys.Match(raw);
            if (key == null || key == LauncherCategoryKeys.All) continue;
            if (ordered.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            ordered.Add(key);
        }

        // Every known category must appear, or the tail would be re-appended in default order.
        foreach (var key in LauncherCategoryKeys.Order)
            if (key != LauncherCategoryKeys.All && !ordered.Contains(key, StringComparer.OrdinalIgnoreCase))
                ordered.Add(key);

        var isDefault = ordered.Count == LauncherCategoryKeys.Order.Count - 1;
        for (var i = 0; isDefault && i < ordered.Count; i++)
            isDefault = string.Equals(ordered[i], LauncherCategoryKeys.Order[i + 1], StringComparison.Ordinal);

        return this with { CategoryOrder = isDefault ? null : ordered };
    }

    /// <summary>Return the chips to the built-in order.</summary>
    public LauncherSettings WithoutCategoryOrder() => this with { CategoryOrder = null };

    /// <summary>
    /// The launcher's own 液态玻璃 optics. Unset means the built-in defaults, <b>not</b> the widget
    /// material's record: 聚焦搜索 and the widgets are tuned separately.
    /// </summary>
    public LiquidGlassSettings EffectiveLiquidGlass => (LiquidGlass ?? new LiquidGlassSettings()).Normalize();

    /// <summary>The custom light-mode card background as a validated <c>#RRGGBB</c>, or null.</summary>
    public string? EffectiveBackgroundColorLight => NormalizeColor(BackgroundColorLight);

    /// <summary>The custom dark-mode card background as a validated <c>#RRGGBB</c>, or null.</summary>
    public string? EffectiveBackgroundColorDark => NormalizeColor(BackgroundColorDark);

    /// <summary>
    /// Clamp every value into its supported range and drop unusable tokens.
    /// </summary>
    public LauncherSettings Normalize() => this with
    {
        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? null : Hotkey.Trim(),
        SearchHotkey = string.IsNullOrWhiteSpace(SearchHotkey) ? null : SearchHotkey.Trim(),
        SearchEngine = string.IsNullOrWhiteSpace(SearchEngine) ? null : SearchEngine.Trim().ToLowerInvariant(),
        SearchEngineUrl = string.IsNullOrWhiteSpace(SearchEngineUrl) ? null : SearchEngineUrl.Trim(),
        SearchBrowser = string.IsNullOrWhiteSpace(SearchBrowser) ? null : SearchBrowser.Trim(),
        BackgroundColorLight = NormalizeColor(BackgroundColorLight),
        BackgroundColorDark = NormalizeColor(BackgroundColorDark),
        Folder = string.IsNullOrWhiteSpace(Folder) ? null : Folder.Trim(),
        Columns = Math.Clamp(Columns, MinColumns, MaxColumns),
        GapPercent = Math.Clamp(GapPercent, MinGapPercent, MaxGapPercent),
        RowGapPercent = Math.Clamp(RowGapPercent, MinRowGapPercent, MaxRowGapPercent),
        NameLines = Math.Clamp(NameLines, MinNameLines, MaxNameLines),
        Width = Clamp(Width, MinWidth, MaxWidth, DefaultWidth),
        Height = Clamp(Height, MinHeight, MaxHeight, DefaultHeight),
        Blacklist = NormalizeBlacklist(Blacklist),
        CornerRadius = Clamp(CornerRadius, MinCornerRadius, MaxCornerRadius, DefaultCornerRadius),
        GlassSampleInterval = Math.Clamp(GlassSampleInterval, MinGlassSampleInterval, MaxGlassSampleInterval),
        ClipboardRetentionDays = Math.Clamp(
            ClipboardRetentionDays, MinClipboardRetentionDays, MaxClipboardRetentionDays),
        ClipboardExcluded = NormalizeBlacklist(ClipboardExcluded),
        // An undefined enum token from a hand-edited file degrades to "follow global".
        Surface = Surface is { } surface && Enum.IsDefined(surface) ? surface : null,
        ThemeMode = Enum.IsDefined(ThemeMode) ? ThemeMode : LauncherThemeMode.FollowGlobal,
        // Same tolerance for the grid's order: an unknown token falls back to name order.
        SortMode = Enum.IsDefined(SortMode) ? SortMode : LauncherSortMode.Name,
        CategoryHotkeys = NormalizeCategoryHotkeys(CategoryHotkeys),
        CategoryOrder = NormalizeCategoryOrder(CategoryOrder),
        SearchPlaceholder = string.IsNullOrWhiteSpace(SearchPlaceholder) ? null : SearchPlaceholder.Trim(),
        SpaceShowsRecentLaunches = SpaceShowsRecentLaunches,
        SpaceRecentLaunchesCount = Math.Clamp(SpaceRecentLaunchesCount, MinSpaceRecentLaunchesCount, MaxSpaceRecentLaunchesCount),
        WebResultCount = Math.Clamp(WebResultCount, MinWebResultCount, MaxWebResultCount),
        OpacityLevel = OpacityLevel is { } opacity && double.IsFinite(opacity)
            ? Math.Clamp(opacity, 0.0, 1.0)
            : null,
        LiquidGlass = LiquidGlass?.Normalize(),
    };

    /// <summary>
    /// The grid this launcher draws into <paramref name="availableWidth"/> DIP of content.
    /// A convenience over <see cref="LauncherGridMetrics.Compute"/> so callers pass the settings, not
    /// six loose numbers.
    /// </summary>
    public LauncherGridMetrics GridFor(double availableWidth)
        => LauncherGridMetrics.Compute(availableWidth, Columns, GapPercent, ShowNames, RowGapPercent, NameLines);

    /// <summary>
    /// The radius the card is actually drawn with: either the launcher's own value or the widget
    /// card radius it was asked to follow.
    /// </summary>
    /// <param name="widgetRadius"><c>Dimensions.Radius</c> from the application settings.</param>
    public double EffectiveCornerRadius(double widgetRadius)
    {
        if (!UseWidgetCornerRadius) return Math.Clamp(CornerRadius, MinCornerRadius, MaxCornerRadius);

        // A hand-edited widget radius still has to stay inside the launcher's supported range.
        return double.IsFinite(widgetRadius)
            ? Math.Clamp(widgetRadius, MinCornerRadius, MaxCornerRadius)
            : DefaultCornerRadius;
    }

    /// <summary>
    /// The launcher's own light/dark decision.
    /// <para>
    /// This is the one place that answers "is 聚焦搜索 dark", and it deliberately does <b>not</b>
    /// read the window's theme variant: the variant is the app's (widgets) mode, while the launcher
    /// may follow Windows, or be pinned. <see cref="LauncherThemeMode.FollowGlobal"/> is the only
    /// branch that borrows the app's answer.
    /// </para>
    /// </summary>
    /// <param name="global">The global (or focus-state) theme the launcher would inherit.</param>
    /// <param name="appIsDark">Whether the app's own surfaces are currently dark.</param>
    /// <param name="systemIsDark">Whether Windows is currently dark.</param>
    public bool ResolveIsDark(Theme global, bool appIsDark, bool systemIsDark)
    {
        // 跟随全局主题 means "be the app's theme", so a mode override stored earlier must not
        // survive the switch being flipped back on.
        var mode = UseGlobalTheme ? LauncherThemeMode.FollowGlobal : ThemeMode;
        return mode switch
        {
            LauncherThemeMode.Light => false,
            LauncherThemeMode.Dark => true,
            LauncherThemeMode.FollowSystem => systemIsDark,
            _ => global.DarkMode ?? appIsDark,
        };
    }

    /// <summary>
    /// The card background this launcher paints in <paramref name="isDark"/>: the custom colour for
    /// that mode, or — when following, or when the slot is empty — the global 纯色背景色 for the
    /// same mode. A half-filled custom pair therefore never paints a transparent card.
    /// </summary>
    public string ResolveBackgroundColor(Theme global, bool isDark)
    {
        if (!FollowGlobalBackground)
        {
            var custom = isDark ? EffectiveBackgroundColorDark : EffectiveBackgroundColorLight;
            if (custom != null) return custom;
        }

        return isDark ? global.EffectiveSolidBackgroundDark : global.EffectiveSolidBackgroundLight;
    }

    /// <summary>
    /// The theme the launcher renders with: the global one, or the global one with this launcher's
    /// overrides applied field by field (a null override keeps following the global value), resolved
    /// for the launcher's <b>own</b> light/dark mode.
    /// </summary>
    /// <param name="global">The global (or focus-state) theme the launcher would inherit.</param>
    /// <param name="isDark">The launcher's own mode — see <see cref="ResolveIsDark"/>.</param>
    public Theme ResolveTheme(Theme global, bool isDark)
    {
        var theme = UseGlobalTheme
            ? global
            : global with
            {
                Surface = Surface ?? global.Surface,
                OpacityLevel = OpacityLevel ?? global.OpacityLevel,
            };

        return theme with
        {
            // The mode is resolved, not deferred: a bool? here would re-open the question inside
            // every consumer that reads the variant instead (the glass coating does exactly that).
            DarkMode = isDark,

            // 液态玻璃 optics belong to the launcher: inheriting the widget material's optics made
            // every slider here a second control over the widgets' glass. Unset = the launcher's
            // own defaults, never the global record.
            LiquidGlass = EffectiveLiquidGlass,

            // The background pair is resolved for the same mode, so the card fill and the glass
            // coating read the same colour as the card itself.
            SolidBackgroundLight = ResolveBackgroundColor(global, isDark: false),
            SolidBackgroundDark = ResolveBackgroundColor(global, isDark: true),
        };
    }

    /// <summary>Trim a <c>#RRGGBB</c> value into canonical form; anything else → null.</summary>
    private static string? NormalizeColor(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text.Length > 0 && text[0] != '#') text = "#" + text;
        if (text.Length != 7) return null;
        for (var index = 1; index < text.Length; index++)
            if (!Uri.IsHexDigit(text[index])) return null;
        return text.ToUpperInvariant();
    }

    /// <summary>The blacklist as the user sees it (cleaned, de-duplicated, order preserved).</summary>
    public IReadOnlyList<string> EffectiveBlacklist => NormalizeBlacklist(Blacklist) ?? [];

    /// <summary>The clipboard exclusion list as the user sees it (same rules as the blacklist).</summary>
    public IReadOnlyList<string> EffectiveClipboardExcluded => NormalizeBlacklist(ClipboardExcluded) ?? [];

    /// <summary>
    /// True when clipboard changes must NOT be recorded because <paramref name="processName"/> owns
    /// the foreground window. An unreadable process counts as "not excluded" — suppressing the
    /// recorder on a guess would silently drop entries.
    /// </summary>
    public bool IsClipboardExcluded(string? processName)
    {
        var candidate = NormalizeProcessName(processName);
        if (candidate == null) return false;

        return EffectiveClipboardExcluded.Any(entry =>
            string.Equals(NormalizeProcessName(entry), candidate, StringComparison.Ordinal));
    }

    /// <summary>
    /// The one rule for turning whatever was typed / captured into a comparable entry: take the
    /// file name, drop a trailing <c>.exe</c>, trim, and lower-case. Returns <c>null</c> for an
    /// unusable value (blank, or a bare path with no file name).
    /// </summary>
    public static string? NormalizeProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var name = raw.Trim().Trim('"');
        if (name.Length == 0) return null;

        // A full path is accepted (the settings page offers "add current process", which yields a
        // bare name, but a hand-edited file may carry a path).
        var slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        name = name.Trim();
        return name.Length == 0 ? null : name.ToLowerInvariant();
    }

    /// <summary>True when <paramref name="processName"/> (with or without <c>.exe</c>) is listed.</summary>
    public bool IsBlacklisted(string? processName)
    {
        var candidate = NormalizeProcessName(processName);
        if (candidate == null) return false;

        return EffectiveBlacklist.Any(entry =>
            string.Equals(NormalizeProcessName(entry), candidate, StringComparison.Ordinal));
    }

    /// <summary>Add a process to the blacklist (no-op when it is already there or unusable).</summary>
    public LauncherSettings WithProcessAdded(string? processName)
    {
        var name = NormalizeProcessName(processName);
        if (name == null) return this;

        var current = EffectiveBlacklist;
        if (current.Any(entry => string.Equals(NormalizeProcessName(entry), name, StringComparison.Ordinal)))
            return this;

        return this with { Blacklist = [.. current, name] };
    }

    /// <summary>Remove a process from the blacklist (no-op when it is not listed).</summary>
    public LauncherSettings WithProcessRemoved(string? processName)
    {
        var name = NormalizeProcessName(processName);
        if (name == null) return this;

        var remaining = EffectiveBlacklist
            .Where(entry => !string.Equals(NormalizeProcessName(entry), name, StringComparison.Ordinal))
            .ToList();

        return remaining.Count == EffectiveBlacklist.Count ? this : this with { Blacklist = remaining };
    }

    /// <summary>
    /// Keep only known category keys, trim every gesture, and resolve duplicates by bar order
    /// (first wins, the later binding becomes "no key"). Unknown keys are dropped; <c>null</c>
    /// stays <c>null</c> so "never configured" keeps following the defaults.
    /// </summary>
    private static Dictionary<string, string>? NormalizeCategoryHotkeys(Dictionary<string, string>? map)
    {
        if (map == null) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in LauncherCategoryKeys.Order)
        {
            string? raw = null;
            var found = false;
            foreach (var pair in map)
            {
                if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)) continue;
                raw = pair.Value;
                found = true;
                break;
            }

            if (!found) continue;

            var text = raw?.Trim() ?? string.Empty;
            if (text.Length > 0 && !seen.Add(text)) text = string.Empty;
            result[key] = text;
        }

        return result;
    }

    /// <summary>
    /// Keep only known category keys, drop 全部 (it is pinned first) and duplicates, and collapse
    /// an order that is already the default back to <c>null</c>.
    /// </summary>
    private static List<string>? NormalizeCategoryOrder(List<string>? keys)
    {
        if (keys == null) return null;

        var ordered = new List<string>(keys.Count);
        foreach (var raw in keys)
        {
            var key = LauncherCategoryKeys.Match(raw);
            if (key == null || key == LauncherCategoryKeys.All) continue;
            if (ordered.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
            ordered.Add(key);
        }

        if (ordered.Count == 0) return null;

        var isDefault = ordered.Count == LauncherCategoryKeys.Order.Count - 1;
        for (var i = 0; isDefault && i < ordered.Count; i++)
            isDefault = string.Equals(ordered[i], LauncherCategoryKeys.Order[i + 1], StringComparison.Ordinal);

        return isDefault ? null : ordered;
    }

    /// <summary>Trim, drop blanks, de-duplicate case-insensitively; <c>null</c> stays <c>null</c>.</summary>
    private static List<string>? NormalizeBlacklist(List<string>? entries)
    {
        if (entries == null) return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(entries.Count);

        foreach (var entry in entries)
        {
            var name = NormalizeProcessName(entry);
            if (name == null || !seen.Add(name)) continue;
            result.Add(name);
        }

        return result;
    }

    /// <summary>NaN/Infinity-safe clamp (a hand-edited <c>1e999</c> must not produce an unopenable card).</summary>
    private static double Clamp(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
