using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskCanvas.Core.Services;
using Hitokoto.Models;

namespace Hitokoto.Services;

/// <summary>
/// Client for Hitokoto Sentence API (v1.hitokoto.cn) and lightweight Core RESTful v1 API (hitokoto.cn/api/restful/v1).
/// Uses <see cref="ProxySettings"/> and gracefully degrades to international mirror and curated offline sentences.
/// </summary>
public static class HitokotoService
{
    public const string PrimarySentenceEndpoint = "https://v1.hitokoto.cn/";
    public const string InternationalSentenceEndpoint = "https://international.v1.hitokoto.cn/";
    public const string CoreRestfulBaseUrl = "https://hitokoto.cn/api/restful/v1";

    /// <summary>Per-request budget for the sentence endpoints.</summary>
    private static readonly TimeSpan SentenceTimeout = TimeSpan.FromSeconds(6);

    /// <summary>Per-request budget for the Core API (likes).</summary>
    private static readonly TimeSpan LikeTimeout = TimeSpan.FromSeconds(5);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    // ---- shared HTTP client ------------------------------------------------------------------
    // Every fetch used to build (and dispose) its own client, which meant a fresh
    // SocketsHttpHandler + connection pool + DNS cache per quote — and one more set per like.
    // One process-wide client (re-created only when the configured proxy actually changes) keeps
    // the pool warm; per-request budgets are applied with a linked CTS instead of client.Timeout.
    private static readonly object ClientGate = new();
    private static HttpClient? sharedClient;
    private static string? sharedClientProxy;

    private static HttpClient GetSharedClient()
    {
        var proxy = ProxySettings.GetProxySetting();
        lock (ClientGate)
        {
            if (sharedClient != null && string.Equals(sharedClientProxy, proxy, StringComparison.Ordinal))
                return sharedClient;

            var previous = sharedClient;
            var client = ProxySettings.CreateHttpClient(proxy, Timeout.InfiniteTimeSpan);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DeskCanvas-HitokotoWidget/1.0");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            sharedClient = client;
            sharedClientProxy = proxy;
            // A proxy change is the only moment the old client is dropped; in-flight requests on it
            // are failed by the disposal and simply fall through to the backup endpoint/fallback.
            previous?.Dispose();
            return client;
        }
    }

    /// <summary>
    /// GET that applies <paramref name="timeout"/> to the request + body read only (not the whole
    /// operation), returning null on any failure. Cancellation of the caller's own token is
    /// propagated so a superseded fetch can still be distinguished from a network error.
    /// </summary>
    private static async Task<string?> TryGetStringAsync(
        HttpClient client,
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            using var response = await client.GetAsync(url, timeoutCts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Built-in curated Hitokoto sentences used for instant first-frame display and offline fallback.
    /// </summary>
    public static readonly IReadOnlyList<HitokotoItem> OfflineFallbackQuotes =
    [
        new(Id: 5708, Uuid: "255dda12-df94-4bdd-badb-2556d0b9084f", Hitokoto: "往者不可谏，来者犹可追。", Type: "i", From: "论语·微子篇", FromWho: "孔子", Creator: "a632079", Likes: 42),
        new(Id: 7338, Uuid: "75a45fd4-4f2f-45eb-80cb-6f0a7bcdfaf2", Hitokoto: "用代码表达言语的魅力，用代码书写山河的壮丽。", Type: "f", From: "一言开发者中心", FromWho: "一言", Creator: "DreamOne", Likes: 86),
        new(Id: 10948, Uuid: "36c1ab75-d502-401a-ad5c-0a89dbaebd6e", Hitokoto: "只要你不混乱，时间永远够用。", Type: "k", From: "网络", FromWho: null, Creator: "nDhnzr6r", Likes: 19),
        new(Id: 6083, Uuid: "62c12303-b3fa-4720-a7c5-2985bf049e60", Hitokoto: "我们最终都要远行，最终都要跟稚嫩的自己告别。", Type: "d", From: "海边的卡夫卡", FromWho: "村上春树", Creator: "a632079", Likes: 128),
        new(Id: 4102, Uuid: "19c8e011-8a1b-4c52-910c-8e8f9a1b2c3d", Hitokoto: "世界上只有一种真正的英雄主义，那就是在认清生活的真相后依然热爱生活。", Type: "d", From: "米开朗琪罗传", FromWho: "罗曼·罗兰", Creator: "戎歌", Likes: 215),
        new(Id: 3812, Uuid: "a1b2c3d4-e5f6-4789-8012-3456789abcde", Hitokoto: "醉后不知天在水，满船清梦压星河。", Type: "i", From: "题龙阳县青草湖", FromWho: "唐珙", Creator: "hitokoto", Likes: 164),
        new(Id: 1205, Uuid: "b2c3d4e5-f6a7-4890-9123-456789abcdef", Hitokoto: "正是因为有着不能原谅的过去，才更要去创造能够原谅自己的未来。", Type: "a", From: "银魂", FromWho: "坂田银时", Creator: "moe", Likes: 93),
        new(Id: 8419, Uuid: "c3d4e5f6-a7b8-4901-a234-56789abcdef0", Hitokoto: "万物皆有裂痕，那是光照进来的地方。", Type: "k", From: "Anthem", FromWho: "莱昂纳德·科恩", Creator: "hitokoto", Likes: 147),
        new(Id: 5192, Uuid: "d4e5f6a7-b8c9-4012-b345-6789abcdef01", Hitokoto: "山高月小，水落石出。", Type: "i", From: "后赤壁赋", FromWho: "苏轼", Creator: "hitokoto", Likes: 76),
        new(Id: 9031, Uuid: "e5f6a7b8-c9d0-4123-c456-789abcdef012", Hitokoto: "愿中国青年都摆脱冷气，只是向上走，不必听自暴自弃者流的话。", Type: "d", From: "热风", FromWho: "鲁迅", Creator: "hitokoto", Likes: 182)
    ];

    /// <summary>
    /// Fetch a random Hitokoto quote matching the model's filter settings, optionally querying
    /// the Core API for its current like count.
    /// </summary>
    public static async Task<HitokotoItem> FetchQuoteAsync(
        HitokotoModel model,
        string? excludeUuid = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = model.Normalize();
        var query = BuildQueryString(normalized);

        var endpoints = new List<string>();
        if (!string.IsNullOrWhiteSpace(normalized.CustomApiUrl))
        {
            endpoints.Add(AppendQuery(normalized.CustomApiUrl!, query));
        }
        endpoints.Add(AppendQuery(PrimarySentenceEndpoint, query));
        endpoints.Add(AppendQuery(InternationalSentenceEndpoint, query));

        var client = GetSharedClient();

        HitokotoItem? fetched = null;

        foreach (var url in endpoints)
        {
            var json = await TryGetStringAsync(client, url, SentenceTimeout, cancellationToken).ConfigureAwait(false);
            if (json == null)
                continue; // endpoint failed or timed out — try the next one

            HitokotoItem? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<HitokotoItem>(json, JsonOptions);
            }
            catch
            {
                continue; // a malformed body is the same as a failed endpoint
            }

            if (parsed != null && !string.IsNullOrWhiteSpace(parsed.Hitokoto))
            {
                var isLiked = HitokotoStore.Instance.IsLiked(parsed.Uuid);
                fetched = parsed with
                {
                    FetchedAt = DateTime.Now,
                    IsLiked = isLiked
                };
                break;
            }
        }

        if (fetched == null)
        {
            fetched = PickOfflineFallback(normalized, excludeUuid);
        }
        else if (normalized.ShowLikeCount && !string.IsNullOrWhiteSpace(fetched.Uuid))
        {
            var likes = await FetchLikeCountInternalAsync(client, fetched.Uuid, LikeTimeout, cancellationToken).ConfigureAwait(false);
            if (likes.HasValue)
            {
                var localLiked = HitokotoStore.Instance.IsLiked(fetched.Uuid);
                var count = likes.Value;
                if (localLiked && count == 0) count = 1;
                fetched = fetched with { Likes = count, IsLiked = localLiked };
            }
        }

        return fetched;
    }

    /// <summary>
    /// Query the Hitokoto Core API (<c>GET /like?sentence_uuid=...</c>) for a sentence's total likes.
    /// </summary>
    public static async Task<int?> FetchLikeCountAsync(string uuid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return null;
        try
        {
            return await FetchLikeCountInternalAsync(GetSharedClient(), uuid, LikeTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<int?> FetchLikeCountInternalAsync(
        HttpClient client,
        string uuid,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var url = $"{CoreRestfulBaseUrl}/like?sentence_uuid={Uri.EscapeDataString(uuid)}";
        var json = await TryGetStringAsync(client, url, timeout, cancellationToken).ConfigureAwait(false);
        if (json == null) return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("data", out var data) &&
                data.ValueKind == JsonValueKind.Array &&
                data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("total", out var totalProp) && totalProp.TryGetInt32(out var total))
                {
                    return Math.Max(0, total);
                }
            }
        }
        catch
        {
            // Ignore Core API telemetry errors
        }

        return null;
    }

    /// <summary>
    /// Submit a like via the Hitokoto Core API (<c>POST /like?sentence_uuid=...</c>).
    /// Returns <c>true</c> if the remote endpoint accepted or already recorded the like.
    /// </summary>
    public static async Task<bool> SubmitLikeAsync(string uuid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return false;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SentenceTimeout);
        try
        {
            var client = GetSharedClient();

            var url = $"{CoreRestfulBaseUrl}/like?sentence_uuid={Uri.EscapeDataString(uuid)}";
            using var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("sentence_uuid", uuid)
            ]);
            using var response = await client.PostAsync(url, content, timeoutCts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return true;

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            return body.Contains("200", StringComparison.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public static HitokotoItem PickOfflineFallback(HitokotoModel model, string? excludeUuid = null)
    {
        var candidates = OfflineFallbackQuotes.AsEnumerable();
        if (model.Categories is { Count: > 0 })
        {
            var catSet = new HashSet<string>(model.Categories, StringComparer.OrdinalIgnoreCase);
            var matching = OfflineFallbackQuotes.Where(q => catSet.Contains(q.Type)).ToList();
            if (matching.Count > 0)
                candidates = matching;
        }

        var list = candidates.Where(q => q.Uuid != excludeUuid).ToList();
        if (list.Count == 0)
            list = OfflineFallbackQuotes.ToList();

        var picked = list[Random.Shared.Next(list.Count)];
        var isLiked = HitokotoStore.Instance.IsLiked(picked.Uuid);
        return picked with
        {
            FetchedAt = DateTime.Now,
            IsLiked = isLiked,
            Likes = isLiked ? Math.Max(1, picked.Likes + 1) : picked.Likes
        };
    }

    private static string BuildQueryString(HitokotoModel model)
    {
        var sb = new StringBuilder("encode=json&charset=utf-8");
        if (model.Categories is { Count: > 0 })
        {
            foreach (var c in model.Categories)
            {
                if (!string.IsNullOrWhiteSpace(c))
                    sb.Append("&c=").Append(Uri.EscapeDataString(c.Trim()));
            }
        }

        if (model.MinLength > 0)
            sb.Append("&min_length=").Append(model.MinLength);
        if (model.MaxLength > 0)
            sb.Append("&max_length=").Append(model.MaxLength);

        return sb.ToString();
    }

    private static string AppendQuery(string baseUrl, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return baseUrl;
        return baseUrl.Contains('?', StringComparison.Ordinal)
            ? $"{baseUrl}&{query}"
            : $"{baseUrl}?{query}";
    }
}
