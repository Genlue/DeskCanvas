using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tools.Services.Translation;

public class YoudaoTranslationEngine : ITranslationEngine
{
    public string Name => "Youdao";

    private static readonly HttpClient httpClient = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public async Task<string> TranslateAsync(string text, string sourceLang, string targetLang, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var (s, t) = ResolveSourceAndTarget(sourceLang, targetLang, text);
        if (s == t) return text;

        // Youdao mobile API only supports pairs where one side is Chinese (ZH_CN <-> {Foreign}).
        // If neither source nor target is Chinese, perform two-step pivot translation via Chinese.
        if (s != "ZH_CN" && t != "ZH_CN")
        {
            var zhIntermediate = await RequestTranslateRawAsync(text, $"{s}2ZH_CN", cancellationToken);
            if (string.IsNullOrWhiteSpace(zhIntermediate))
            {
                return zhIntermediate;
            }
            return await RequestTranslateRawAsync(zhIntermediate, $"ZH_CN2{t}", cancellationToken);
        }

        return await RequestTranslateRawAsync(text, $"{s}2{t}", cancellationToken);
    }

    private static async Task<string> RequestTranslateRawAsync(string text, string type, CancellationToken cancellationToken)
    {
        var postData = new Dictionary<string, string>
        {
            { "inputtext", text },
            { "type", type }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://m.youdao.com/translate")
        {
            Content = new FormUrlEncodedContent(postData)
        };
        request.Headers.Add("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) AppleWebKit/605.1.15 Mobile/15E148 Safari/604.1");
        request.Headers.Add("Referer", "https://m.youdao.com/translate");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var html = await response.Content.ReadAsStringSafeAsync(cancellationToken);

        bool isZh = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase);

        // Extract <ul id="translateResult">...<li>result</li>...</ul>
        var match = Regex.Match(html, @"<ul\s+id=[""']translateResult[""'][^>]*>([\s\S]*?)</ul>", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException(isZh ? "未找到翻译结果" : "Translation result not found");
        }

        var listContent = match.Groups[1].Value;
        var liMatches = Regex.Matches(listContent, @"<li>([\s\S]*?)</li>", RegexOptions.IgnoreCase);
        if (liMatches.Count == 0)
        {
            throw new InvalidOperationException(isZh ? "未找到翻译文本" : "Translated text not found");
        }

        var sb = new StringBuilder();
        foreach (Match li in liMatches)
        {
            var raw = li.Groups[1].Value.Trim();
            if (!string.IsNullOrEmpty(raw))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append(WebUtility.HtmlDecode(raw));
            }
        }

        return sb.ToString();
    }

    private static (string Source, string Target) ResolveSourceAndTarget(string sourceLang, string targetLang, string text)
    {
        string s = Normalize(sourceLang);
        string t = Normalize(targetLang);

        if (t == "AUTO")
        {
            t = "ZH_CN";
        }

        if (s == "AUTO")
        {
            // Auto-detect source language from text.
            // Check Japanese Kana first because Japanese sentences often contain Chinese characters (Kanji).
            bool hasJapanese = Regex.IsMatch(text, @"[\u3040-\u30ff]");
            bool hasKorean = Regex.IsMatch(text, @"[\uac00-\ud7af]");
            bool hasRussian = Regex.IsMatch(text, @"[\u0400-\u04ff]");
            bool hasChinese = Regex.IsMatch(text, @"[\u4e00-\u9fa5]");

            if (hasJapanese)
            {
                s = "JA";
            }
            else if (hasKorean)
            {
                s = "KR";
            }
            else if (hasRussian)
            {
                s = "RU";
            }
            else if (hasChinese)
            {
                s = "ZH_CN";
            }
            else
            {
                s = "EN";
            }

            // If auto-detected source matches target, smartly invert target
            if (s == t)
            {
                t = s == "ZH_CN" ? "EN" : "ZH_CN";
            }
        }

        return (s, t);
    }

    private static string Normalize(string lang) => lang.ToLowerInvariant() switch
    {
        "zh" or "zh-cn" or "zh-hans" => "ZH_CN",
        "en" => "EN",
        "ja" => "JA",
        "ko" => "KR",
        "fr" => "FR",
        "de" => "DE",
        "es" => "ES",
        "ru" => "RU",
        _ => "AUTO"
    };
}
