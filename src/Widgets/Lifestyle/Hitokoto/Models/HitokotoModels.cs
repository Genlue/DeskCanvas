using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;

namespace Hitokoto.Models;

/// <summary>
/// Typography and layout style for the Hitokoto quote card.
/// </summary>
public enum HitokotoStyleMode
{
    /// <summary>现代引语：左对齐正文 + 左上角装饰引号水印 + 右下角署名。</summary>
    ModernQuote = 0,

    /// <summary>古典诗词：居中衬线排版 + 意境留白 + 居中署名。</summary>
    PoeticCenter = 1,

    /// <summary>极简纯粹：无装饰引号水印，清爽专注正文。</summary>
    MinimalCard = 2,
}

/// <summary>
/// Persisted widget configuration for the Hitokoto widget.
/// </summary>
public record HitokotoModel(
    List<string>? Categories = null,
    int MinLength = HitokotoModel.DefaultMinLength,
    int MaxLength = HitokotoModel.DefaultMaxLength,
    int RefreshIntervalMinutes = HitokotoModel.DefaultRefreshIntervalMinutes,
    HitokotoStyleMode StyleMode = HitokotoStyleMode.ModernQuote,
    bool IsFrameless = false,
    bool ShowCategoryTag = true,
    bool ShowAttribution = true,
    bool ShowLikeCount = true,
    bool AutoHideActions = true,
    string? FontFamily = null,
    int FontSize = HitokotoModel.DefaultFontSize,
    string? CustomApiUrl = null)
{
    public const int DefaultMinLength = 0;
    public const int DefaultMaxLength = 45;
    public const int DefaultRefreshIntervalMinutes = 30;
    public const int DefaultFontSize = 0; // 0 = auto-scale by widget tier

    public HitokotoModel() : this(
        Categories: null,
        MinLength: DefaultMinLength,
        MaxLength: DefaultMaxLength,
        RefreshIntervalMinutes: DefaultRefreshIntervalMinutes)
    {
    }

    public HitokotoModel Normalize()
    {
        var minLen = Math.Clamp(MinLength, 0, 100);
        var maxLen = Math.Clamp(MaxLength, Math.Max(5, minLen), 120);
        var interval = RefreshIntervalMinutes < 0 ? DefaultRefreshIntervalMinutes : RefreshIntervalMinutes;
        var fontSize = FontSize <= 0 ? DefaultFontSize : Math.Clamp(FontSize, 11, 32);

        List<string>? validCategories = null;
        if (Categories is { Count: > 0 })
        {
            var filtered = Categories
                .Where(c => !string.IsNullOrWhiteSpace(c) && HitokotoCategories.AllCodes.Contains(c.Trim().ToLowerInvariant()))
                .Select(c => c.Trim().ToLowerInvariant())
                .Distinct()
                .ToList();
            if (filtered.Count > 0 && filtered.Count < HitokotoCategories.All.Count)
                validCategories = filtered;
        }

        return this with
        {
            Categories = validCategories,
            MinLength = minLen,
            MaxLength = maxLen,
            RefreshIntervalMinutes = interval,
            FontSize = fontSize,
            CustomApiUrl = string.IsNullOrWhiteSpace(CustomApiUrl) ? null : CustomApiUrl.Trim()
        };
    }
}

/// <summary>
/// Official Hitokoto sentence categories (parameter <c>c</c> in v1.hitokoto.cn).
/// </summary>
public static class HitokotoCategories
{
    private static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<(string Code, string Name, string Description)> AllZh =
    [
        ("a", "动画", "Anime - 动漫经典台词"),
        ("b", "漫画", "Comic - 漫画经典语录"),
        ("c", "游戏", "Game - 游戏台词与旁白"),
        ("d", "文学", "Novel - 小说、散文与名著"),
        ("e", "原创", "Myself - 社区用户原创句子"),
        ("f", "网络", "Internet - 来自互联网的句子"),
        ("g", "其他", "Other - 其他收录句子"),
        ("h", "影视", "Film - 电影、剧集经典台词"),
        ("i", "诗词", "Poetry - 古典诗词与现代诗"),
        ("j", "网易云", "NetEase - 音乐热评与歌词"),
        ("k", "哲学", "Philosophy - 哲思与箴言"),
        ("l", "抖机灵", "Punchline - 幽默与机智妙语")
    ];

    private static readonly IReadOnlyList<(string Code, string Name, string Description)> AllEn =
    [
        ("a", "Anime", "Anime - Classic anime quotes"),
        ("b", "Comic", "Comic - Classic manga & comic quotes"),
        ("c", "Game", "Game - Video game lines & narration"),
        ("d", "Literature", "Novel - Novels, essays & classics"),
        ("e", "Original", "Myself - Original community quotes"),
        ("f", "Internet", "Internet - Quotes from the web"),
        ("g", "Other", "Other - Miscellaneous quotes"),
        ("h", "Film", "Film - Movie & TV series quotes"),
        ("i", "Poetry", "Poetry - Classical & modern poetry"),
        ("j", "Music", "NetEase - Music comments & lyrics"),
        ("k", "Philosophy", "Philosophy - Philosophical thoughts & maxims"),
        ("l", "Witty", "Punchline - Humor & witty remarks")
    ];

    public static IReadOnlyList<(string Code, string Name, string Description)> All =>
        IsChinese ? AllZh : AllEn;

    public static readonly HashSet<string> AllCodes =
        new(AllZh.Select(x => x.Code), StringComparer.OrdinalIgnoreCase);

    public static string GetDisplayName(string? code)
    {
        var fallback = IsChinese ? "一言" : "Quotes";
        if (string.IsNullOrWhiteSpace(code)) return fallback;
        var normalized = code.Trim().ToLowerInvariant();
        foreach (var item in All)
        {
            if (item.Code == normalized)
                return item.Name;
        }
        return fallback;
    }

    public static string GetCategoryName(string? code) => GetDisplayName(code);
}

/// <summary>
/// A single Hitokoto sentence entry returned by Sentence API v1 and enriched with Core API likes.
/// </summary>
public record HitokotoItem(
    [property: JsonPropertyName("id")] int Id = 0,
    [property: JsonPropertyName("uuid")] string Uuid = "",
    [property: JsonPropertyName("hitokoto")] string Hitokoto = "",
    [property: JsonPropertyName("type")] string Type = "a",
    [property: JsonPropertyName("from")] string? From = null,
    [property: JsonPropertyName("from_who")] string? FromWho = null,
    [property: JsonPropertyName("creator")] string? Creator = null,
    [property: JsonPropertyName("created_at")] string? CreatedAt = null,
    [property: JsonPropertyName("length")] int Length = 0,
    [property: JsonPropertyName("likes")] int Likes = 0,
    [property: JsonPropertyName("is_liked")] bool IsLiked = false,
    [property: JsonPropertyName("fetched_at")] DateTime FetchedAt = default)
{
    private static bool IsChinese =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

    [JsonIgnore]
    public string CategoryName => HitokotoCategories.GetDisplayName(Type);

    [JsonIgnore]
    public string FormattedAttribution
    {
        get
        {
            var hasFrom = !string.IsNullOrWhiteSpace(From);
            var hasWho = !string.IsNullOrWhiteSpace(FromWho);
            if (hasWho && hasFrom)
                return $"—— {FromWho}《{From}》";
            if (hasFrom)
                return $"—— 《{From}》";
            if (hasWho)
                return $"—— {FromWho}";
            return IsChinese ? "—— 一言" : "—— Hitokoto";
        }
    }

    [JsonIgnore]
    public string CompactAttribution
    {
        get
        {
            var hasFrom = !string.IsNullOrWhiteSpace(From);
            var hasWho = !string.IsNullOrWhiteSpace(FromWho);
            if (hasWho && hasFrom)
                return $"{FromWho} · 《{From}》";
            if (hasFrom)
                return $"《{From}》";
            if (hasWho)
                return FromWho!;
            return IsChinese ? "一言" : "Hitokoto";
        }
    }

    [JsonIgnore]
    public string WebUrl => string.IsNullOrWhiteSpace(Uuid)
        ? "https://hitokoto.cn"
        : $"https://hitokoto.cn/?uuid={Uri.EscapeDataString(Uuid)}";

    [JsonIgnore]
    public string CopyPlainText => $"{Hitokoto} {FormattedAttribution}";

    [JsonIgnore]
    public string CopyMarkdown => $"> {Hitokoto}\n>\n> {FormattedAttribution}";
}
