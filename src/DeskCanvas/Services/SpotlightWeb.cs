using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;
using DeskCanvas.Locales;
using Microsoft.Win32;

namespace DeskCanvas.Services;

/// <summary>
/// One search engine the 网页 row can send a query to: a name the row shows and a URL template
/// with <c>{q}</c> where the escaped query goes.
/// <para>
/// The templates are the same ones the 搜索 component ships (<c>PresetEngines</c>) — that widget is
/// a separate assembly loaded at runtime, so they cannot be referenced, but the addresses are part
/// of the product's behaviour: a query typed here and a query typed there must land on the same
/// result page.
/// </para>
/// </summary>
/// <param name="Id">Stable storage key ("google", "bing", …); never localized.</param>
/// <param name="Name">Display name, a brand — deliberately not translated.</param>
/// <param name="UrlTemplate">Absolute URL containing <see cref="LauncherSettings.SearchQueryPlaceholder"/>.</param>
public sealed record WebSearchEngine(string Id, string Name, string UrlTemplate)
{
    /// <summary>The address for <paramref name="query"/>, or empty when the template is unusable.</summary>
    public string UrlFor(string query)
    {
        if (string.IsNullOrWhiteSpace(UrlTemplate)) return string.Empty;

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return HostUrl;
        return UrlTemplate.Replace(
            LauncherSettings.SearchQueryPlaceholder, Uri.EscapeDataString(trimmed), StringComparison.Ordinal);
    }

    /// <summary>The engine's own home (the template minus its query), for the engine-less case.</summary>
    public string HostUrl
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UrlTemplate)) return string.Empty;
            return Uri.TryCreate(UrlTemplate.Replace(LauncherSettings.SearchQueryPlaceholder, "x", StringComparison.Ordinal),
                UriKind.Absolute, out var probe)
                ? $"{probe.Scheme}://{probe.Authority}"
                : string.Empty;
        }
    }

    /// <summary>"bing.com" — the row's subtitle says where the query is about to go.</summary>
    public string Host
    {
        get
        {
            if (string.IsNullOrWhiteSpace(UrlTemplate)) return string.Empty;
            return Uri.TryCreate(UrlTemplate.Replace(LauncherSettings.SearchQueryPlaceholder, "x", StringComparison.Ordinal),
                UriKind.Absolute, out var probe)
                ? probe.Host
                : string.Empty;
        }
    }
}

/// <summary>
/// The engine table the 网页 row resolves against, plus the resolution rules for a hand-edited
/// settings file: an unknown id falls back to the default engine, and a custom template is only
/// used when it is a real absolute URL carrying the placeholder.
/// </summary>
public static class WebSearchEngines
{
    /// <summary>Default engine when nothing is configured — the 搜索 component's default too.</summary>
    public static WebSearchEngine Default => All[0];

    /// <summary>The presets, in the order the settings page lists them.</summary>
    public static IReadOnlyList<WebSearchEngine> All { get; } =
    [
        new("google", "Google", "https://www.google.com/search?q={q}"),
        new("bing", "Bing", "https://www.bing.com/search?q={q}"),
        new("baidu", "百度", "https://www.baidu.com/s?wd={q}"),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={q}"),
        new("github", "GitHub", "https://github.com/search?q={q}"),
        new("bilibili", "哔哩哔哩", "https://search.bilibili.com/all?keyword={q}"),
        new("zhihu", "知乎", "https://www.zhihu.com/search?type=content&q={q}"),
        new("youtube", "YouTube", "https://www.youtube.com/results?search_query={q}"),
    ];

    /// <summary>The preset with this id, or <c>null</c>.</summary>
    public static WebSearchEngine? Find(string? id)
        => string.IsNullOrWhiteSpace(id)
            ? null
            : All.FirstOrDefault(engine => string.Equals(engine.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The engine a launcher's settings actually produce. A custom entry that does not resolve
    /// (no URL, no placeholder, not absolute) falls back to <see cref="Default"/> instead of
    /// producing a row that would open nothing.
    /// </summary>
    public static WebSearchEngine Resolve(LauncherSettings launcher)
    {
        if (string.Equals(launcher.EffectiveSearchEngine, LauncherSettings.CustomSearchEngine, StringComparison.Ordinal)
            && launcher.EffectiveSearchEngineUrl is { } template)
        {
            var custom = new WebSearchEngine(
                LauncherSettings.CustomSearchEngine, Locale.Settings_Spotlight_Web_Engine_CustomName, template);

            // "用 自定义 搜索…" says nothing; the host does. The localized fallback is only for a
            // template the URI parser cannot read a host from.
            return custom.Host.Length > 0 ? custom with { Name = custom.Host } : custom;
        }

        return Find(launcher.EffectiveSearchEngine) ?? Default;
    }
}

/// <summary>One browser the 网页 row can launch. <see cref="Path"/> null = the Windows default.</summary>
public sealed record WebBrowser(string Name, string? Path)
{
    /// <summary>True for "whatever Windows has as the default browser".</summary>
    public bool IsDefault => string.IsNullOrEmpty(Path);
}

/// <summary>
/// Discovers the browsers installed on this machine, so the user can pick one instead of always
/// being pushed at the default.
/// <para>
/// The source is the Windows per-user / machine registration that every browser writes for the
/// "default apps" page (<c>SOFTWARE\Clients\StartMenuInternet</c>): it is the only list that is
/// both machine-wide and free of hard-coded install paths. Browsers that do not register there
/// simply do not appear, and the default-browser choice still works for them.
/// </para>
/// </summary>
public static class WebBrowsers
{
    /// <summary>The Windows default browser (follows 默认应用).</summary>
    public static WebBrowser Default => new(Locale.Settings_Spotlight_Web_Browser_Default, null);

    private static IReadOnlyList<WebBrowser>? detected;

    /// <summary>
    /// Installed browsers, de-duplicated by executable, discovered once per process. The list is
    /// cheap but touches the registry, and the settings page asks for it on every build.
    /// </summary>
    public static IReadOnlyList<WebBrowser> Detect()
    {
        if (detected != null) return detected;

        // The whole discovery is Windows-only (registry). Off Windows there is nothing to
        // enumerate and 默认浏览器 is the only honest answer.
        if (!OperatingSystem.IsWindows())
        {
            detected = [];
            return detected;
        }

        var found = new List<WebBrowser>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var clients = baseKey.OpenSubKey(@"SOFTWARE\Clients\StartMenuInternet");
                    if (clients == null) continue;

                    foreach (var id in clients.GetSubKeyNames())
                    {
                        using var client = clients.OpenSubKey(id);
                        if (client == null) continue;

                        var name = (client.GetValue(null) as string)?.Trim();
                        using var command = client.OpenSubKey(@"shell\open\command");
                        var exe = ExecutableFrom(command?.GetValue(null) as string);
                        if (exe == null || !seen.Add(exe)) continue;

                        found.Add(new WebBrowser(
                            string.IsNullOrWhiteSpace(name) ? System.IO.Path.GetFileNameWithoutExtension(exe) : name!,
                            exe));
                    }
                }
                catch
                {
                    // A registry view that cannot be opened (32-bit on ARM, policy) contributes
                    // nothing; the other views and the default-browser choice still stand.
                }
            }
        }

        detected = found.Count == 0 ? [] : found;
        return detected;
    }

    /// <summary>
    /// The stored path resolved to a usable browser: the executable when it still exists, the
    /// default browser otherwise. A browser that was uninstalled must not turn the row into a
    /// dead click.
    /// </summary>
    public static WebBrowser Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Default;

        var trimmed = path.Trim().Trim('"');
        if (!File.Exists(trimmed)) return Default;

        var known = Detect().FirstOrDefault(browser =>
            string.Equals(browser.Path, trimmed, StringComparison.OrdinalIgnoreCase));
        return known ?? new WebBrowser(System.IO.Path.GetFileNameWithoutExtension(trimmed), trimmed);
    }

    /// <summary>
    /// The executable in a shell open command. The value is <c>"C:\…\browser.exe" --single-argument
    /// %1</c>; a quoted path wins, and an unquoted one is cut at <c>.exe</c>.
    /// </summary>
    private static string? ExecutableFrom(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var text = command.Trim();
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end > 1) return Normalize(text[1..end]);
        }

        var marker = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return Normalize(marker > 0 ? text[..(marker + 4)] : text);
    }

    private static string? Normalize(string candidate)
    {
        var path = candidate.Trim().Trim('"');
        return path.Length == 0 || !File.Exists(path) ? null : path;
    }
}

/// <summary>
/// A prepared web search: everything the row, the preview pane and the launch need, resolved once
/// per query. It is a value, so the result row can carry it without touching the settings again
/// when the user presses Enter ten seconds later.
/// </summary>
/// <param name="Query">The text the user typed (trimmed).</param>
/// <param name="EngineName">Engine display name, for the row title ("Google").</param>
/// <param name="EngineHost">Engine host, for the row subtitle ("google.com").</param>
/// <param name="Url">The absolute address the browser will open.</param>
/// <param name="BrowserName">Browser display name ("默认浏览器", "Google Chrome").</param>
/// <param name="BrowserPath">Browser executable, or <c>null</c> for the Windows default.</param>
public sealed record SpotlightWebQuery(
    string Query, string EngineName, string EngineHost, string Url, string BrowserName, string? BrowserPath);

/// <summary>
/// The 网页 result source: turns the typed query into "open this engine in this browser".
/// <para>
/// It produces exactly <b>one</b> row per query — the configured engine. A list of engines under
/// the results would be a second settings page inside the launcher; the choice belongs in
/// 设置 › 聚焦搜索 › 网页搜索, and the 网页 category gives the row a place of its own.
/// </para>
/// </summary>
public sealed class SpotlightWebProvider
{
    /// <summary>Score of the web row. It has its own group, so this only orders it against itself.</summary>
    private const double WebScore = 300;

    private readonly IAppSettingsProvider settings;

    /// <summary>Builds the provider over the settings that decide engine / browser / on-off.</summary>
    public SpotlightWebProvider(IAppSettingsProvider settings)
        => this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// Resolve the query into a launchable target, or <c>null</c> when the feature is off or the
    /// query is empty. Never throws: a broken settings file degrades to "no web row".
    /// </summary>
    public SpotlightWebQuery? Prepare(string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return null;

        try
        {
            var launcher = settings.Get().EffectiveLauncher;
            if (!launcher.WebSearch) return null;

            var engine = WebSearchEngines.Resolve(launcher);
            var url = engine.UrlFor(text);
            if (url.Length == 0) return null;

            var browser = WebBrowsers.Resolve(launcher.EffectiveSearchBrowser);
            return new SpotlightWebQuery(text, engine.Name, engine.Host, url, browser.Name, browser.Path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The result row for a prepared target (Kind = Web, one per query).</summary>
    public static SpotlightResult CreateResult(SpotlightWebQuery target)
        => new(
            SpotlightResultKind.Web,
            "web:" + target.Url, // stable per (engine, query): re-typing the same query keeps the id
            Title: target.Query,
            Subtitle: null,
            Score: WebScore,
            RunAsync: () => System.Threading.Tasks.Task.FromResult(Open(target)),
            WebQuery: target);

    /// <summary>
    /// Open <paramref name="target"/>: the chosen browser when one was configured and still
    /// exists, the Windows default otherwise. Returns false only when both paths failed, so the
    /// overlay stays open on a real failure instead of closing over nothing.
    /// </summary>
    public static bool Open(SpotlightWebQuery target)
    {
        if (target == null || string.IsNullOrWhiteSpace(target.Url)) return false;

        if (!string.IsNullOrWhiteSpace(target.BrowserPath) && File.Exists(target.BrowserPath))
        {
            try
            {
                var info = new ProcessStartInfo { FileName = target.BrowserPath, UseShellExecute = false };
                // ArgumentList quotes the URL itself: a query may contain '&', '?', spaces.
                info.ArgumentList.Add(target.Url);
                Process.Start(info);
                return true;
            }
            catch
            {
                // Fall through to the shell: a browser that refuses this URL is still better
                // served by the default one than by nothing at all.
            }
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target.Url, UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
