using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using DeskCanvas.Core.Interfaces;
using DeskCanvas.Core.Models.Settings;

namespace DeskCanvas.Services;

/// <summary>
/// One real web hit: what a search engine answered for the query, already flattened to the three
/// things a launcher row can show.
/// </summary>
/// <param name="Title">The result's own title (the page's, not the engine's).</param>
/// <param name="Url">The absolute address the row opens.</param>
/// <param name="Snippet">The engine's excerpt, plain text, already trimmed.</param>
public sealed record SpotlightWebHit(string Title, string Url, string Snippet);

/// <summary>
/// Fetches the first few <b>real</b> results for a query and turns them into launcher rows, so the
/// 网页 group shows what the engine found instead of only offering to open a browser.
/// <para>
/// <b>Source:</b> the RSS rendering of the engine's own result page
/// (<c>…/search?q=…&amp;format=rss</c>). It is the only machine-readable endpoint the presets expose
/// without an API key, a developer account or an HTML scrape that breaks on every redesign — and it
/// is the same ranking the browser would show.
/// </para>
/// <para>
/// <b>Never blocks the search:</b> the local sources are rendered first and these rows are merged
/// afterwards (see <see cref="SpotlightQueryRouter.WithWebResults"/>). A slow or offline network
/// therefore costs the inline list, never the app/file/clipboard results.
/// </para>
/// <para>
/// The service never throws: every failure (DNS, timeout, a page that is not XML, a settings file
/// mid-write) degrades to "no hits".
/// </para>
/// </summary>
public sealed class SpotlightWebResults
{
    /// <summary>
    /// Quiet period before the request leaves the machine. The local query already ran by then,
    /// so fast typing cancels this task before it costs a round-trip — and only the query the user
    /// stopped on is actually searched.
    /// </summary>
    private const int SettleDelayMs = 260;

    /// <summary>How long a query's hits stay usable. Typing back to a recent query is free.</summary>
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(3);

    private const int CacheLimit = 16;

    /// <summary>Longest title/snippet a 640 DIP row can show; longer text is trimmed here, not by layout.</summary>
    private const int MaxTitleLength = 140;
    private const int MaxSnippetLength = 160;

    private static readonly Regex TagPattern = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly HttpClient Http = CreateClient();

    private readonly IAppSettingsProvider settings;
    private readonly object gate = new();
    private readonly Dictionary<string, CacheEntry> cache = new(StringComparer.Ordinal);

    /// <summary>Builds the source over the settings that decide engine, browser, on/off and count.</summary>
    public SpotlightWebResults(IAppSettingsProvider settings)
        => this.settings = settings ?? throw new ArgumentNullException(nameof(settings));

    /// <summary>
    /// The hits for <paramref name="query"/>, or an empty list when the feature is off, the query
    /// is blank, or the network refused. Rows keep the engine's order; each carries a runnable
    /// action that opens it in the configured browser.
    /// </summary>
    public async Task<IReadOnlyList<SpotlightResult>> QueryAsync(string? query, CancellationToken ct)
    {
        var text = query?.Trim() ?? string.Empty;
        if (text.Length == 0) return [];

        LauncherSettings launcher;
        try
        {
            launcher = settings.Get().EffectiveLauncher;
        }
        catch
        {
            return [];
        }

        // The inline list is part of 网页搜索: turning the feature off must remove it too.
        if (!launcher.WebSearch) return [];

        var limit = launcher.EffectiveWebResultCount;
        if (limit <= 0) return [];

        // One Latin letter is not a search yet (it is the first keystroke of one), so it does not
        // cost a request. A single CJK character already is a word, so it does.
        if (text.Length < 2 && text[0] < '\u2E80') return [];

        // Two different engines on purpose: the row opens what the user chose, the preview reads
        // the one engine that publishes a feed (see FeedEngineId).
        var engine = WebSearchEngines.Resolve(launcher);
        var browser = WebBrowsers.Resolve(launcher.EffectiveSearchBrowser);

        // The browser is part of the key: cached rows carry the launcher that opens them, and a
        // browser change must not hand back a row that still points at the previous one.
        var cacheKey = string.Join(
            '\u0001', limit.ToString(), engine.Id, browser.Path ?? string.Empty, text.ToLowerInvariant());
        if (TryGetCached(cacheKey, out var cached)) return cached;

        try
        {
            await Task.Delay(SettleDelayMs, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return [];
        }

        var hits = await FetchAsync(FeedEngine, text, limit, ct).ConfigureAwait(false);
        if (hits.Count == 0) return [];

        var rows = new List<SpotlightResult>(hits.Count);
        for (var index = 0; index < hits.Count; index++)
        {
            var hit = hits[index];
            var target = new SpotlightWebQuery(text, engine.Name, engine.Host, hit.Url, browser.Name, browser.Path);

            rows.Add(new SpotlightResult(
                SpotlightResultKind.Web,
                SpotlightResultIds.WebHit(hit.Url),
                hit.Title,
                Subtitle: hit.Snippet,
                // Below the action row (300) and descending, so merging into the same group keeps
                // "open the search" on top and the engine's own order underneath.
                Score: WebHitScore - index,
                RunAsync: () => Task.FromResult(SpotlightWebProvider.Open(target)),
                WebUrl: hit.Url,
                WebSnippet: hit.Snippet));
        }

        Store(cacheKey, rows);
        return rows;
    }

    /// <summary>
    /// The engine whose result page is machine-readable. The 网页 row keeps the user's own engine
    /// choice (it decides where "search for …" goes), but only Bing publishes its ranking as a
    /// feed — the other presets answer with an HTML page that would have to be scraped, which
    /// breaks on every redesign. So the inline preview is always one engine, and that engine is
    /// named in 设置 › 网页搜索 rather than silently varying with the choice above it.
    /// </summary>
    private const string FeedEngineId = "bing";

    /// <summary>Score of the first inline hit; the action row above it is 300 (SpotlightWebProvider).</summary>
    private const double WebHitScore = 250;

    private static WebSearchEngine FeedEngine =>
        WebSearchEngines.Find(FeedEngineId) ?? WebSearchEngines.Default;

    private static async Task<IReadOnlyList<SpotlightWebHit>> FetchAsync(
        WebSearchEngine engine, string query, int limit, CancellationToken ct)
    {
        var url = FeedUrl(engine, query);
        if (url == null) return [];

        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            // Bytes, not a string: the XML declaration is authoritative for the encoding, and the
            // HTTP header of these endpoints is not always the same answer.
            var payload = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            if (payload.Length == 0) return [];

            using var stream = new System.IO.MemoryStream(payload);
            var document = XDocument.Load(stream);
            var items = document.Root?.Element("channel")?.Elements("item");
            if (items == null) return [];

            var hits = new List<SpotlightWebHit>(limit);
            foreach (var item in items)
            {
                if (hits.Count >= limit) break;

                var title = Clean(item.Element("title")?.Value, MaxTitleLength);
                var link = item.Element("link")?.Value?.Trim();
                if (title.Length == 0 || link == null) continue;
                if (!Uri.TryCreate(link, UriKind.Absolute, out var parsed)) continue;
                if (parsed.Scheme is not ("http" or "https")) continue;

                hits.Add(new SpotlightWebHit(title, link, Clean(item.Element("description")?.Value, MaxSnippetLength)));
            }

            return hits;
        }
        catch (OperationCanceledException)
        {
            // The user kept typing, or the request timed out — both mean "no rows this time".
            return [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// The RSS rendering of <paramref name="engine"/>'s result page, or <c>null</c> when the engine
    /// has no such endpoint. Only the engine's own <c>/search</c> address is rewritten; a custom
    /// template is left alone (guessing a feed URL for an unknown site is worse than showing none).
    /// </summary>
    private static string? FeedUrl(WebSearchEngine engine, string query)
    {
        if (string.IsNullOrWhiteSpace(engine.UrlTemplate)) return null;
        if (string.Equals(engine.Id, LauncherSettings.CustomSearchEngine, StringComparison.Ordinal)) return null;

        var url = engine.UrlFor(query);
        if (url.Length == 0) return null;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return url + separator + "format=rss";
    }

    /// <summary>Strip markup, decode entities, collapse whitespace and trim to <paramref name="max"/>.</summary>
    private static string Clean(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = TagPattern.Replace(raw, " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length <= max) return text;

        // Trim on a word boundary when there is one near the cut, so the row does not end mid-word.
        var cut = text.LastIndexOf(' ', Math.Min(max, text.Length - 1));
        return (cut > max / 2 ? text[..cut] : text[..max]).TrimEnd() + "…";
    }

    private bool TryGetCached(string key, out IReadOnlyList<SpotlightResult> rows)
    {
        lock (gate)
        {
            if (cache.TryGetValue(key, out var entry)
                && DateTime.UtcNow - entry.CreatedUtc < CacheLifetime)
            {
                rows = entry.Rows;
                return true;
            }

            if (entry != null) cache.Remove(key);
        }

        rows = [];
        return false;
    }

    private void Store(string key, IReadOnlyList<SpotlightResult> rows)
    {
        lock (gate)
        {
            if (cache.Count >= CacheLimit)
            {
                // Oldest first: the map is tiny and a strict LRU would need bookkeeping the hit
                // rate does not justify.
                string? oldestKey = null;
                var oldestStamp = DateTime.MaxValue;
                foreach (var pair in cache)
                {
                    if (pair.Value.CreatedUtc >= oldestStamp) continue;
                    oldestStamp = pair.Value.CreatedUtc;
                    oldestKey = pair.Key;
                }

                if (oldestKey != null) cache.Remove(oldestKey);
            }

            cache[key] = new CacheEntry(DateTime.UtcNow, rows);
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };

        var client = new HttpClient(handler)
        {
            // The overlay must never wait on the network longer than a keystroke batch: a query
            // that cannot answer in this window simply contributes no rows.
            Timeout = TimeSpan.FromSeconds(4),
        };

        // The endpoint serves a browser-oriented page; an unnamed client is refused by some edges.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0 Safari/537.36 DeskCanvas/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.8");

        return client;
    }

    private sealed record CacheEntry(DateTime CreatedUtc, IReadOnlyList<SpotlightResult> Rows);
}
